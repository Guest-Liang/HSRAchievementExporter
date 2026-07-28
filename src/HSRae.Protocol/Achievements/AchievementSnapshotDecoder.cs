using System.Globalization;
using HSRae.Core.Achievements;
using HSRae.Core.Profiles;
using HSRae.Protocol.Capture;
using HSRae.Protocol.Metadata;
using HSRae.Protocol.Protobuf;

namespace HSRae.Protocol.Achievements;

public sealed record AchievementCandidateDiagnostic
{
    public required uint CommandId { get; init; }

    public required string RecordFieldPath { get; init; }

    public required uint IdFieldNumber { get; init; }

    public required uint? StatusFieldNumber { get; init; }

    public required uint? FinishTimestampFieldNumber { get; init; }

    public required uint? ProgressFieldNumber { get; init; }

    public required int RecordCount { get; init; }

    public required int CatalogMatchCount { get; init; }

    public required int UnknownIdCount { get; init; }

    public required int CompletionEvidenceCount { get; init; }

    public required bool IsAccepted { get; init; }

    public required string Decision { get; init; }

    public string FormatForLog()
    {
        return $"命令 {CommandId}，路径 {RecordFieldPath}，ID 字段 {IdFieldNumber}，"
            + $"状态字段 {DisplayField(StatusFieldNumber)}，完成时间字段 {DisplayField(FinishTimestampFieldNumber)}，"
            + $"进度字段 {DisplayField(ProgressFieldNumber)}；记录 {RecordCount} 条，"
            + $"元数据命中 {CatalogMatchCount} 条，未知 ID {UnknownIdCount} 条，"
            + $"完成时间证据 {CompletionEvidenceCount} 条；{Decision}";
    }

    private static string DisplayField(uint? fieldNumber)
    {
        return fieldNumber?.ToString(CultureInfo.InvariantCulture) ?? "未识别";
    }
}

public sealed class AchievementSnapshotDecoder
{
    private const int MinimumVerifiedRecordCount = 3;
    private const int MinimumDiscoveredRecordCount = 20;
    private const int MaximumTraversalDepth = 5;
    private const int MaximumFieldsPerRecord = 64;

    private readonly AchievementCatalog _catalog;
    private readonly string _gameVersion;
    private readonly AchievementProtocolProfile _profile;

    public AchievementSnapshotDecoder(
        AchievementCatalog catalog,
        string gameVersion,
        AchievementProtocolProfile profile
    )
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _gameVersion = string.IsNullOrWhiteSpace(gameVersion) ? "unknown" : gameVersion;
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));

        // Decoding can discover moved fields, but a malformed built-in profile
        // should still fail deterministically at startup.
        _ = ParseRecordPath(profile.RecordFieldPath);
        if (
            profile.PackedVarintFieldNumbers.Any(static fieldNumber => fieldNumber == 0)
            || profile.PackedVarintFieldNumbers.Distinct().Count() != profile.PackedVarintFieldNumbers.Count
        )
        {
            throw new ArgumentException("packed varint 字段号必须非零且不能重复", nameof(profile));
        }
    }

    public AchievementCandidateDiagnostic? BestCandidate { get; private set; }

    public bool TryDecode(CapturedPacket packet, out AchievementSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(packet);
        snapshot = null;

        if (packet.Body.Length < 8 || !ProtoWire.TryParse(packet.Body, out var root) || root is null)
        {
            return false;
        }

        var collections = new List<RecordCollection>();
        CollectRecordCollections([root], [], depth: 0, collections);

        SnapshotCandidate? best = null;
        foreach (var collection in collections)
        {
            var candidate = EvaluateCollection(packet, collection);
            if (candidate is not null && (best is null || IsBetter(candidate, best)))
            {
                best = candidate;
            }
        }

        if (best is null)
        {
            return false;
        }

        RememberDiagnostic(ToDiagnostic(best));
        if (!best.IsAccepted)
        {
            return false;
        }

        snapshot = new AchievementSnapshot
        {
            CapturedAt = packet.CapturedAt,
            GameVersion = _gameVersion,
            SourceCommandId = packet.CommandId,
            RecordFieldPath = best.RecordFieldPath,
            IdFieldNumber = best.IdFieldNumber,
            StatusFieldNumber = best.StatusFieldNumber,
            FinishTimestampFieldNumber = best.FinishTimestampFieldNumber,
            ProgressFieldNumber = best.ProgressFieldNumber,
            PackedVarintFieldNumbers = best
                .Records.SelectMany(static record => record.RawPackedVarints.Keys)
                .Distinct()
                .Order()
                .ToArray(),
            CatalogMatchCount = best.CatalogMatchCount,
            UnknownIdCount = best.UnknownIdCount,
            Records = best.Records,
        };
        return true;
    }

    private SnapshotCandidate? EvaluateCollection(CapturedPacket packet, RecordCollection collection)
    {
        SnapshotCandidate? best = null;
        var possibleIdFields = collection.Rows.SelectMany(static row => row.Keys).Distinct().Order().ToArray();

        foreach (var idFieldNumber in possibleIdFields)
        {
            var candidate = EvaluateIdField(packet, collection, idFieldNumber);
            if (candidate is not null && (best is null || IsBetter(candidate, best)))
            {
                best = candidate;
            }
        }

        return best;
    }

    private SnapshotCandidate? EvaluateIdField(CapturedPacket packet, RecordCollection collection, uint idFieldNumber)
    {
        var rowsWithId = collection.Rows.Where(row => row.TryGetValue(idFieldNumber, out _)).ToArray();
        if (rowsWithId.Length < MinimumVerifiedRecordCount)
        {
            return null;
        }

        var plausibleRows = rowsWithId
            .Where(row => row[idFieldNumber] <= uint.MaxValue && LooksLikeAchievementId((uint)row[idFieldNumber]))
            .ToArray();
        if (plausibleRows.Length < MinimumVerifiedRecordCount)
        {
            return null;
        }

        // GetQuestDataScRsp is a mixed quest snapshot rather than an
        // achievement-only collection. Main, side, daily, and achievement
        // quest IDs therefore coexist in the same field, so the proportion of
        // 4xxxxxx values among every quest ID is not a useful safety check.
        // Validate the filtered achievement subset against the bundled
        // catalog instead.
        var knownRows = plausibleRows.Count(row => _catalog.Ids.Contains((uint)row[idFieldNumber]));
        if (knownRows < MinimumVerifiedRecordCount || knownRows * 5 < plausibleRows.Length * 3)
        {
            return null;
        }

        var recordFieldPath = FormatRecordPath(collection.Path);
        var usesKnownRecordShape = idFieldNumber == _profile.IdFieldNumber;
        var isExactKnownProfile =
            packet.CommandId == _profile.FullSnapshotCommandId
            && string.Equals(recordFieldPath, _profile.RecordFieldPath, StringComparison.Ordinal)
            && usesKnownRecordShape;
        var finishTimestampFieldNumber = InferFinishTimestampField(
            plausibleRows,
            idFieldNumber,
            packet.CapturedAt,
            usesKnownRecordShape
        );
        var statusFieldNumber = InferStatusField(
            plausibleRows,
            idFieldNumber,
            finishTimestampFieldNumber,
            usesKnownRecordShape
        );
        var progressFieldNumber = InferProgressField(
            plausibleRows,
            idFieldNumber,
            finishTimestampFieldNumber,
            statusFieldNumber,
            usesKnownRecordShape
        );
        var records = BuildRecords(
            plausibleRows,
            idFieldNumber,
            statusFieldNumber,
            finishTimestampFieldNumber,
            progressFieldNumber,
            preserveProfilePackedVarints: isExactKnownProfile
        );
        if (records.Count < MinimumVerifiedRecordCount)
        {
            return null;
        }

        var catalogMatches = records.Count(record => _catalog.Ids.Contains(record.Id));
        var unknownIds = records.Count - catalogMatches;
        var completionEvidence = records.Count(static record => record.FinishTimestamp is > 0);
        var minimumRecordCount = isExactKnownProfile ? MinimumVerifiedRecordCount : MinimumDiscoveredRecordCount;

        string decision;
        var isAccepted = false;
        if (records.Count < minimumRecordCount || catalogMatches < minimumRecordCount)
        {
            decision = isExactKnownProfile
                ? $"候选不足 {MinimumVerifiedRecordCount} 条"
                : $"自发现候选不足 {MinimumDiscoveredRecordCount} 条";
        }
        else if (catalogMatches * 5 < records.Count * 3)
        {
            decision = "元数据命中率低于 60%";
        }
        else if (finishTimestampFieldNumber is null || completionEvidence == 0)
        {
            decision = "未找到可信的完成时间字段，拒绝生成可能为空的导出";
        }
        else
        {
            decision = isExactKnownProfile ? "通过内置结构提示和元数据校验" : "通过元数据驱动的协议结构发现";
            isAccepted = true;
        }

        return new SnapshotCandidate(
            packet.CommandId,
            recordFieldPath,
            idFieldNumber,
            statusFieldNumber,
            finishTimestampFieldNumber,
            progressFieldNumber,
            records,
            catalogMatches,
            unknownIds,
            completionEvidence,
            isExactKnownProfile,
            isAccepted,
            decision
        );
    }

    private uint? InferFinishTimestampField(
        IReadOnlyList<RecordRow> rows,
        uint idFieldNumber,
        DateTimeOffset capturedAt,
        bool useKnownHint
    )
    {
        uint? bestField = null;
        long bestScore = long.MinValue;
        foreach (var fieldNumber in rows.SelectMany(static row => row.Keys).Distinct())
        {
            if (fieldNumber == idFieldNumber || !TryScoreTimestampField(rows, fieldNumber, capturedAt, out var score))
            {
                continue;
            }

            if (useKnownHint && fieldNumber == _profile.FinishTimestampFieldNumber)
            {
                // The hint breaks otherwise ambiguous ties, but a newly observed
                // partial timestamp still outranks a hinted field present everywhere.
                score += 100_000_000L;
            }

            if (score > bestScore || score == bestScore && fieldNumber < bestField)
            {
                bestField = fieldNumber;
                bestScore = score;
            }
        }

        return bestField;
    }

    private static bool TryScoreTimestampField(
        IReadOnlyList<RecordRow> rows,
        uint fieldNumber,
        DateTimeOffset capturedAt,
        out long score
    )
    {
        score = 0;
        var observed = 0;
        var positive = 0;

        foreach (var row in rows)
        {
            if (!row.TryGetValue(fieldNumber, out var rawValue))
            {
                continue;
            }

            observed++;
            if (rawValue == 0)
            {
                continue;
            }

            positive++;
            if (rawValue > long.MaxValue || !IsPlausibleTimestamp((long)rawValue, capturedAt))
            {
                score = 0;
                return false;
            }
        }

        if (positive == 0)
        {
            return false;
        }

        // A finish timestamp normally exists only on completed rows. Prefer that
        // shape over timestamps (for example an accept time) present on every row.
        var partialCompletionBonus = positive < rows.Count ? 1_000_000_000L : 0L;
        score = partialCompletionBonus + positive * 1_000L + observed;
        return true;
    }

    private uint? InferStatusField(
        IReadOnlyList<RecordRow> rows,
        uint idFieldNumber,
        uint? finishTimestampFieldNumber,
        bool useKnownHint
    )
    {
        if (finishTimestampFieldNumber is null)
        {
            return null;
        }

        uint? bestField = null;
        long bestScore = long.MinValue;
        foreach (var fieldNumber in rows.SelectMany(static row => row.Keys).Distinct())
        {
            if (fieldNumber == idFieldNumber || fieldNumber == finishTimestampFieldNumber)
            {
                continue;
            }

            var observed = 0;
            var values = new uint[rows.Count];
            var valid = true;
            for (var index = 0; index < rows.Count; index++)
            {
                if (!rows[index].TryGetValue(fieldNumber, out var rawValue))
                {
                    values[index] = 0;
                    continue;
                }

                observed++;
                var maximumValue =
                    useKnownHint && fieldNumber == _profile.StatusFieldNumber ? uint.MaxValue : byte.MaxValue;
                if (rawValue > maximumValue)
                {
                    valid = false;
                    break;
                }

                values[index] = (uint)rawValue;
            }

            if (!valid || observed == 0)
            {
                continue;
            }

            var distinct = values.Distinct().Count();
            if (distinct is < 2 or > 16)
            {
                continue;
            }

            var correctlySeparated = values
                .Select(
                    (value, index) =>
                        new
                        {
                            Value = value,
                            Completed = rows[index].TryGetValue(finishTimestampFieldNumber.Value, out var rawTimestamp)
                                && rawTimestamp > 0,
                        }
                )
                .GroupBy(static item => item.Value)
                .Sum(static group =>
                    Math.Max(group.Count(static item => item.Completed), group.Count(static item => !item.Completed))
                );
            if (correctlySeparated * 10 < rows.Count * 8)
            {
                continue;
            }

            var score = correctlySeparated * 10_000L + observed * 10L - distinct;
            if (useKnownHint && fieldNumber == _profile.StatusFieldNumber)
            {
                // This is only a field-position hint. Numeric values are
                // preserved as observed and interpreted by each output format.
                score += 100_000_000L;
            }

            if (score > bestScore || score == bestScore && fieldNumber < bestField)
            {
                bestField = fieldNumber;
                bestScore = score;
            }
        }

        return bestField;
    }

    private uint? InferProgressField(
        IReadOnlyList<RecordRow> rows,
        uint idFieldNumber,
        uint? finishTimestampFieldNumber,
        uint? statusFieldNumber,
        bool useKnownHint
    )
    {
        if (
            useKnownHint
            && _profile.ProgressFieldNumber != idFieldNumber
            && _profile.ProgressFieldNumber != finishTimestampFieldNumber
            && _profile.ProgressFieldNumber != statusFieldNumber
            && rows.Any(row => row.ContainsKey(_profile.ProgressFieldNumber))
        )
        {
            return _profile.ProgressFieldNumber;
        }

        // Progress cannot be inferred safely from value shape alone. The complete
        // raw varint map remains in the backup for later protocol confirmation.
        return null;
    }

    private static IReadOnlyList<AchievementRecord> BuildRecords(
        IReadOnlyList<RecordRow> rows,
        uint idFieldNumber,
        uint? statusFieldNumber,
        uint? finishTimestampFieldNumber,
        uint? progressFieldNumber,
        bool preserveProfilePackedVarints
    )
    {
        var byId = new Dictionary<uint, AchievementRecord>();

        foreach (var row in rows)
        {
            if (
                !row.TryGetValue(idFieldNumber, out var rawId)
                || rawId > uint.MaxValue
                || !LooksLikeAchievementId((uint)rawId)
            )
            {
                continue;
            }

            var finishTimestamp = ReadInt64(row, finishTimestampFieldNumber);
            var record = new AchievementRecord
            {
                Id = (uint)rawId,
                IsCompleted = finishTimestamp is > 0,
                Status = ReadUInt32(row, statusFieldNumber, defaultWhenMissing: true),
                Progress = ReadUInt64(row, progressFieldNumber, defaultWhenMissing: true),
                FinishTimestamp = finishTimestamp,
                RawVarints = new Dictionary<uint, ulong>(row),
                RawPackedVarints = preserveProfilePackedVarints
                    ? row.PackedVarints.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray())
                    : new Dictionary<uint, ulong[]>(),
            };

            if (!byId.TryGetValue(record.Id, out var previous) || Prefer(record, previous))
            {
                byId[record.Id] = record;
            }
        }

        return byId.Values.OrderBy(static record => record.Id).ToArray();
    }

    private void CollectRecordCollections(
        IReadOnlyList<ProtoMessage> containers,
        IReadOnlyList<uint> pathPrefix,
        int depth,
        ICollection<RecordCollection> output
    )
    {
        if (depth >= MaximumTraversalDepth)
        {
            return;
        }

        var childrenByField = new Dictionary<uint, List<ProtoMessage>>();
        foreach (var container in containers)
        {
            foreach (var field in container.Fields)
            {
                if (
                    field.WireType != ProtoWireType.LengthDelimited
                    || !ProtoWire.TryParse(field.Bytes, out var child)
                    || child is null
                )
                {
                    continue;
                }

                if (!childrenByField.TryGetValue(field.Number, out var children))
                {
                    children = [];
                    childrenByField.Add(field.Number, children);
                }

                children.Add(child);
            }
        }

        foreach (var pair in childrenByField.OrderBy(static pair => pair.Key))
        {
            var path = AppendPath(pathPrefix, pair.Key);
            var rows = new List<RecordRow>();
            foreach (var child in pair.Value)
            {
                if (TryCreateRecordRow(child, out var row))
                {
                    rows.Add(row);
                }
            }

            if (rows.Count >= MinimumVerifiedRecordCount)
            {
                output.Add(new RecordCollection(path, rows));
            }

            CollectRecordCollections(pair.Value, path, depth + 1, output);
        }
    }

    private static uint[] AppendPath(IReadOnlyList<uint> prefix, uint fieldNumber)
    {
        var path = new uint[prefix.Count + 1];
        for (var index = 0; index < prefix.Count; index++)
        {
            path[index] = prefix[index];
        }

        path[^1] = fieldNumber;
        return path;
    }

    private bool TryCreateRecordRow(ProtoMessage message, out RecordRow row)
    {
        row = new RecordRow();
        if (message.Fields.Count is < 1 or > MaximumFieldsPerRecord)
        {
            return false;
        }

        foreach (var field in message.Fields)
        {
            if (field.WireType == ProtoWireType.Varint)
            {
                // RawVarints intentionally stores one value per field. Repeated
                // scalar fields do not invalidate the other useful row fields.
                row.TryAdd(field.Number, field.Varint);
            }
            else if (
                field.WireType == ProtoWireType.LengthDelimited
                && _profile.PackedVarintFieldNumbers.Contains(field.Number)
                && ProtoWire.TryParsePackedVarints(field.Bytes, out var values)
            )
            {
                // Wire type 2 is also used by strings, bytes, and submessages.
                // Only fields confirmed by the current protocol profile are
                // decoded as packed varints.
                if (row.PackedVarints.TryGetValue(field.Number, out var previous))
                {
                    row.PackedVarints[field.Number] = [.. previous, .. values];
                }
                else
                {
                    row.PackedVarints.Add(field.Number, values);
                }
            }
        }

        return row.Count != 0;
    }

    private void RememberDiagnostic(AchievementCandidateDiagnostic diagnostic)
    {
        if (
            BestCandidate is null
            || diagnostic.IsAccepted && !BestCandidate.IsAccepted
            || diagnostic.IsAccepted == BestCandidate.IsAccepted
                && (
                    diagnostic.CatalogMatchCount > BestCandidate.CatalogMatchCount
                    || diagnostic.CatalogMatchCount == BestCandidate.CatalogMatchCount
                        && diagnostic.RecordCount > BestCandidate.RecordCount
                    || diagnostic.CatalogMatchCount == BestCandidate.CatalogMatchCount
                        && diagnostic.RecordCount == BestCandidate.RecordCount
                        && diagnostic.CompletionEvidenceCount > BestCandidate.CompletionEvidenceCount
                )
        )
        {
            BestCandidate = diagnostic;
        }
    }

    private static AchievementCandidateDiagnostic ToDiagnostic(SnapshotCandidate candidate)
    {
        return new AchievementCandidateDiagnostic
        {
            CommandId = candidate.CommandId,
            RecordFieldPath = candidate.RecordFieldPath,
            IdFieldNumber = candidate.IdFieldNumber,
            StatusFieldNumber = candidate.StatusFieldNumber,
            FinishTimestampFieldNumber = candidate.FinishTimestampFieldNumber,
            ProgressFieldNumber = candidate.ProgressFieldNumber,
            RecordCount = candidate.Records.Count,
            CatalogMatchCount = candidate.CatalogMatchCount,
            UnknownIdCount = candidate.UnknownIdCount,
            CompletionEvidenceCount = candidate.CompletionEvidenceCount,
            IsAccepted = candidate.IsAccepted,
            Decision = candidate.Decision,
        };
    }

    private static bool IsBetter(SnapshotCandidate candidate, SnapshotCandidate previous)
    {
        if (candidate.IsAccepted != previous.IsAccepted)
        {
            return candidate.IsAccepted;
        }

        if (candidate.CatalogMatchCount != previous.CatalogMatchCount)
        {
            return candidate.CatalogMatchCount > previous.CatalogMatchCount;
        }

        if (candidate.Records.Count != previous.Records.Count)
        {
            return candidate.Records.Count > previous.Records.Count;
        }

        if (candidate.CompletionEvidenceCount != previous.CompletionEvidenceCount)
        {
            return candidate.CompletionEvidenceCount > previous.CompletionEvidenceCount;
        }

        return candidate.IsExactKnownProfile && !previous.IsExactKnownProfile;
    }

    private static bool Prefer(AchievementRecord candidate, AchievementRecord previous)
    {
        if (candidate.IsCompleted != previous.IsCompleted)
        {
            return candidate.IsCompleted;
        }

        if (candidate.FinishTimestamp.HasValue != previous.FinishTimestamp.HasValue)
        {
            return candidate.FinishTimestamp.HasValue;
        }

        if (candidate.Status.HasValue != previous.Status.HasValue)
        {
            return candidate.Status.HasValue;
        }

        if (candidate.RawVarints.Count != previous.RawVarints.Count)
        {
            return candidate.RawVarints.Count > previous.RawVarints.Count;
        }

        return candidate.RawPackedVarints.Sum(static pair => pair.Value.Length)
            > previous.RawPackedVarints.Sum(static pair => pair.Value.Length);
    }

    private static uint? ReadUInt32(IReadOnlyDictionary<uint, ulong> row, uint? fieldNumber, bool defaultWhenMissing)
    {
        if (fieldNumber is null)
        {
            return null;
        }

        if (!row.TryGetValue(fieldNumber.Value, out var value))
        {
            return defaultWhenMissing ? 0U : null;
        }

        return value <= uint.MaxValue ? (uint)value : null;
    }

    private static ulong? ReadUInt64(IReadOnlyDictionary<uint, ulong> row, uint? fieldNumber, bool defaultWhenMissing)
    {
        if (fieldNumber is null)
        {
            return null;
        }

        return row.TryGetValue(fieldNumber.Value, out var value) ? value
            : defaultWhenMissing ? 0UL
            : null;
    }

    private static long? ReadInt64(IReadOnlyDictionary<uint, ulong> row, uint? fieldNumber)
    {
        return fieldNumber is not null && row.TryGetValue(fieldNumber.Value, out var value) && value <= long.MaxValue
            ? (long)value
            : null;
    }

    private static bool IsPlausibleTimestamp(long value, DateTimeOffset capturedAt)
    {
        const long earliestSeconds = 1_262_304_000;
        var latestSeconds = capturedAt.AddYears(5).ToUnixTimeSeconds();

        return value >= earliestSeconds && value <= latestSeconds
            || value >= earliestSeconds * 1_000 && value <= latestSeconds * 1_000
            || value >= earliestSeconds * 1_000_000 && value <= latestSeconds * 1_000_000;
    }

    private static bool LooksLikeAchievementId(uint value)
    {
        return value is >= 4_000_000 and <= 4_999_999;
    }

    private static string FormatRecordPath(IReadOnlyList<uint> path)
    {
        return "$."
            + string.Join('.', path.Select(static fieldNumber => fieldNumber.ToString(CultureInfo.InvariantCulture)))
            + "[]";
    }

    private static uint[] ParseRecordPath(string path)
    {
        if (
            string.IsNullOrWhiteSpace(path)
            || !path.StartsWith("$.", StringComparison.Ordinal)
            || !path.EndsWith("[]", StringComparison.Ordinal)
        )
        {
            throw new ArgumentException("成就记录路径必须采用 $.字段.字段[] 格式", nameof(path));
        }

        var segments = path[2..^2].Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new uint[segments.Length];
        if (result.Length == 0)
        {
            throw new ArgumentException("成就记录路径不能为空", nameof(path));
        }

        for (var index = 0; index < segments.Length; index++)
        {
            if (
                !uint.TryParse(segments[index], NumberStyles.None, CultureInfo.InvariantCulture, out result[index])
                || result[index] == 0
            )
            {
                throw new ArgumentException($"成就记录路径包含无效字段：{segments[index]}", nameof(path));
            }
        }

        return result;
    }

    private sealed class RecordRow : Dictionary<uint, ulong>
    {
        public Dictionary<uint, ulong[]> PackedVarints { get; } = [];
    }

    private sealed record RecordCollection(IReadOnlyList<uint> Path, IReadOnlyList<RecordRow> Rows);

    private sealed record SnapshotCandidate(
        uint CommandId,
        string RecordFieldPath,
        uint IdFieldNumber,
        uint? StatusFieldNumber,
        uint? FinishTimestampFieldNumber,
        uint? ProgressFieldNumber,
        IReadOnlyList<AchievementRecord> Records,
        int CatalogMatchCount,
        int UnknownIdCount,
        int CompletionEvidenceCount,
        bool IsExactKnownProfile,
        bool IsAccepted,
        string Decision
    );
}
