using System.Text.Json;
using System.Text.Json.Serialization;
using HSRae.Core.Achievements;

namespace HSRae.Formats.Uiaf;

public static class UiafExporter
{
    private const long UnknownCompletionTimestamp = 253_402_271_999;
    private const uint MinimumObservedStatus = 1;
    private const uint MaximumObservedStatus = 3;

    public static string Serialize(AchievementSnapshot snapshot, uint uid, IReadOnlySet<uint> knownAchievementIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(knownAchievementIds);
        ArgumentOutOfRangeException.ThrowIfZero(uid);

        var document = new UiafDocument
        {
            Info = new UiafInfo
            {
                ExportApp = "HSRae",
                UiafVersion = "v1.2",
                ExportTimestamp = snapshot.CapturedAt.ToUnixTimeSeconds(),
            },
            Hkrpg = new UiafHkrpgData
            {
                Uid = uid,
                List = snapshot
                    .Records.Where(record => knownAchievementIds.Contains(record.Id) && IsObservedStatus(record.Status))
                    .OrderBy(static record => record.Id)
                    .Select(static record => new UiafHkrpgAchievement
                    {
                        Id = record.Id,
                        Current = record.Progress ?? 0,
                        Status = MapStatus(record),
                        Timestamp =
                            AchievementTimestamp.Normalize(record.FinishTimestamp)?.ToUnixTimeSeconds()
                            ?? UnknownCompletionTimestamp,
                    })
                    .ToArray(),
            },
        };

        return JsonSerializer.Serialize(document, UiafJsonContext.Default.UiafDocument);
    }

    private static uint MapStatus(AchievementRecord record)
    {
        // A before/after capture of metadata-known achievements confirmed:
        // 1 = unfinished, 2 = finished with reward unclaimed, 3 = reward claimed.
        // This is derived from observed records and finish-time evidence, not
        // from any historical native QuestStatus declaration. The values map
        // directly to the proposed hkrpg UIAF status range.
        return IsObservedStatus(record.Status)
            ? record.Status!.Value
            : throw new InvalidOperationException("UIAF hkrpg 只接受本次样本实际观察到的状态值 1、2、3");
    }

    private static bool IsObservedStatus(uint? status)
    {
        return status is >= MinimumObservedStatus and <= MaximumObservedStatus;
    }
}

internal sealed class UiafDocument
{
    [JsonPropertyName("info")]
    public required UiafInfo Info { get; init; }

    [JsonPropertyName("hkrpg")]
    public required UiafHkrpgData Hkrpg { get; init; }
}

internal sealed class UiafInfo
{
    [JsonPropertyName("export_timestamp")]
    public required long ExportTimestamp { get; init; }

    [JsonPropertyName("export_app")]
    public required string ExportApp { get; init; }

    [JsonPropertyName("uiaf_version")]
    public required string UiafVersion { get; init; }
}

internal sealed class UiafHkrpgData
{
    [JsonPropertyName("uid")]
    public required uint Uid { get; init; }

    [JsonPropertyName("list")]
    public required UiafHkrpgAchievement[] List { get; init; }
}

internal sealed class UiafHkrpgAchievement
{
    [JsonPropertyName("id")]
    public required uint Id { get; init; }

    [JsonPropertyName("current")]
    public required ulong Current { get; init; }

    [JsonPropertyName("status")]
    public required uint Status { get; init; }

    [JsonPropertyName("timestamp")]
    public required long Timestamp { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UiafDocument))]
internal sealed partial class UiafJsonContext : JsonSerializerContext;
