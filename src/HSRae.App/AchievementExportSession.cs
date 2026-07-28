using HSRae.App.Infrastructure;
using HSRae.Core.Achievements;
using HSRae.Core.Profiles;
using HSRae.Protocol.Achievements;
using HSRae.Protocol.Identity;
using HSRae.Protocol.Metadata;

namespace HSRae.App;

internal static class AchievementExportSession
{
    private static readonly TimeSpan UidWaitAfterSnapshot = TimeSpan.FromSeconds(30);

    private static readonly AchievementProtocolProfile CurrentProtocolHint = new()
    {
        FullSnapshotCommandId = 978,
        RecordFieldPath = "$.13[]",
        IdFieldNumber = 14,
        StatusFieldNumber = 15,
        FinishTimestampFieldNumber = 2,
        ProgressFieldNumber = 1,
        PackedVarintFieldNumbers = [3],
    };

    public static async Task<int> RunAsync(
        string gamePath,
        string gameVersion,
        string hookPath,
        AchievementCatalog catalog
    )
    {
        using var game = SuspendedGameProcess.Start(gamePath);
        await using var pipe = new HookPipeServer(game.ProcessId);

        game.Resume();
        RemoteHookInjector.Inject(game, hookPath);

        ApplicationLog.WriteInfo(
            "游戏已启动且 Hook 已加载。请正常登录并打开成就页面，" + "HSRae 会在取得完整成就快照和当前 UID 后导出"
        );
        ApplicationLog.WriteInfo("按 Ctrl+C 可取消");

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var captured = await WaitForSnapshotAsync(
                pipe,
                game,
                catalog,
                gameVersion,
                CurrentProtocolHint,
                cancellation.Token
            );
            var snapshot = captured.Snapshot;
            var completedCount = snapshot.Records.Count(static record => record.IsCompleted);
            var uiafRecordCount = snapshot.Records.Count(record =>
                catalog.Ids.Contains(record.Id) && record.Status is >= 1 and <= 3
            );
            var knownCompletedCount = snapshot.Records.Count(record =>
                catalog.Ids.Contains(record.Id) && record.IsCompleted
            );

            Console.WriteLine();
            ApplicationLog.WriteInfo(
                $"快照获取完成：UID {captured.Uid}，"
                    + $"识别 {snapshot.Records.Count} 条成就记录，"
                    + $"其中已完成 {completedCount} 条；"
                    + $"元数据命中 {snapshot.CatalogMatchCount} 条，"
                    + $"未知 ID {snapshot.UnknownIdCount} 条"
            );

            ApplicationLog.WriteInfo("正在关闭本次由 HSRae 启动的游戏...");
            try
            {
                game.Terminate(0);
                ApplicationLog.WriteInfo("游戏已关闭");
            }
            catch (Exception exception)
            {
                ApplicationLog.WriteWarningException("快照已取得，但主动关闭游戏失败", exception);
                ApplicationLog.WriteWarning($"警告：快照已取得，但主动关闭游戏失败：{exception.Message}");
                ApplicationLog.WriteWarning("仍可继续导出；HSRae 退出时会再次尝试关闭游戏");
            }

            var target = ExportSelectionFlow.Select(cancellation.Token);
            var output = await AchievementExportWriter.WriteAsync(
                snapshot,
                captured.Uid,
                catalog,
                target,
                cancellation.Token
            );

            Console.WriteLine();
            var exportSummary = target switch
            {
                ExportTarget.AchievementBackup =>
                    $"导出完成：保留服务端返回的 {snapshot.Records.Count} 条成就记录，其中 {completedCount} 条有完成时间证据",
                ExportTarget.Liyin =>
                    $"导出完成：写入元数据内 {knownCompletedCount} 条具有完成时间证据的成就 ID；完整快照共 {snapshot.Records.Count} 条",
                ExportTarget.UiafExperimental =>
                    $"导出完成：写入元数据内且状态为 1/2/3 的 {uiafRecordCount} 条成就记录；"
                        + $"完整快照中的其余 {snapshot.Records.Count - uiafRecordCount} 条只保留在备份",
                _ => throw new ArgumentOutOfRangeException(nameof(target), target, "未知导出目标"),
            };
            ApplicationLog.WriteInfo(exportSummary);
            ApplicationLog.WriteInfo($"{output.DisplayName}：{output.Path}");

            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<CapturedAchievementSnapshot> WaitForSnapshotAsync(
        HookPipeServer pipe,
        SuspendedGameProcess game,
        AchievementCatalog catalog,
        string gameVersion,
        AchievementProtocolProfile protocolHint,
        CancellationToken cancellationToken
    )
    {
        var gameExit = game.WaitForExitAsync(CancellationToken.None);
        var connection = pipe.WaitForConnectionAsync(cancellationToken);

        if (await Task.WhenAny(connection, gameExit) == gameExit)
        {
            throw new InvalidOperationException("游戏在 Hook 建立连接前退出");
        }

        await connection;
        ApplicationLog.WriteDebug("游戏内 Hook 已连接命名管道", writeToConsole: false);

        var decoder = new AchievementSnapshotDecoder(catalog, gameVersion, protocolHint);
        var packetDiagnostics = new PacketCaptureDiagnostics();
        var ready = false;
        var packetCount = 0;
        AchievementSnapshot? pendingSnapshot = null;
        DateTimeOffset? uidDeadline = null;
        uint currentUid = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CancellationTokenSource? uidWaitCancellation = null;
            var readCancellationToken = cancellationToken;
            if (pendingSnapshot is not null && currentUid == 0)
            {
                var remaining = uidDeadline!.Value - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new InvalidOperationException(
                        "已经取得完整成就快照，但当前游戏 UID 在 30 秒内仍未从登录响应中取得；"
                            + BuildCaptureDiagnostics(packetDiagnostics, decoder)
                    );
                }

                uidWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                uidWaitCancellation.CancelAfter(remaining);
                readCancellationToken = uidWaitCancellation.Token;
            }

            HookMessage message;
            try
            {
                var read = pipe.ReadMessageAsync(readCancellationToken);
                if (await Task.WhenAny(read, gameExit) == gameExit)
                {
                    var stage = pendingSnapshot is null ? "取得完整成就快照" : "取得当前游戏 UID";
                    throw new InvalidOperationException(
                        $"游戏在{stage}前退出；此前共收到并检查 {packetCount} 个完整明文包；"
                            + BuildCaptureDiagnostics(packetDiagnostics, decoder)
                    );
                }

                message = await read;
            }
            catch (OperationCanceledException)
                when (pendingSnapshot is not null && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "已经取得完整成就快照，但当前游戏 UID 在 30 秒内仍未从登录响应中取得；"
                        + BuildCaptureDiagnostics(packetDiagnostics, decoder)
                );
            }
            catch (EndOfStreamException exception)
            {
                var reason = gameExit.IsCompleted ? "游戏已经退出" : "游戏内 Hook 提前关闭了通信通道";
                var missing = pendingSnapshot is null
                    ? "尚未确认完整成就快照"
                    : "完整成就快照已经确认，但尚未取得当前游戏 UID";
                throw new InvalidOperationException(
                    $"{reason}；此前共收到并检查 {packetCount} 个完整明文包，{missing}；"
                        + BuildCaptureDiagnostics(packetDiagnostics, decoder),
                    exception
                );
            }
            finally
            {
                uidWaitCancellation?.Dispose();
            }

            switch (message)
            {
                case HookReadyMessage hookReady:
                    if (ready)
                    {
                        throw new InvalidDataException("Hook 重复发送了就绪确认");
                    }

                    ready = true;
                    ApplicationLog.WriteInfo("Hook 已就绪");
                    ApplicationLog.WriteDebug(
                        "Hook 定位详情：明文解析器 "
                            + $"RVA 0x{hookReady.ParserRva:X}，"
                            + $"定位版本 {hookReady.ParserLocatorVersion}",
                        writeToConsole: true
                    );
                    break;

                case HookPacketMessage packet:
                    if (!ready)
                    {
                        throw new InvalidDataException("Hook 在就绪确认前发送了数据包");
                    }

                    packetCount++;
                    packetDiagnostics.Observe(packet.Packet);
                    if (packetCount == 1)
                    {
                        ApplicationLog.WriteInfo("已收到第一个包");
                        ApplicationLog.WriteDebug(
                            $"第一个包详情：命令 {packet.Packet.CommandId}，包体 {packet.Packet.Body.Length} bytes",
                            writeToConsole: true
                        );
                    }

                    if (
                        currentUid == 0
                        && PlayerIdentityDecoder.TryDecode(packet.Packet, out var decodedUid, out var uidFieldNumber)
                    )
                    {
                        currentUid = decodedUid;
                        ApplicationLog.WriteInfo($"已从登录响应取得当前游戏 UID：{currentUid}");
                        ApplicationLog.WriteDebug(
                            $"UID 识别详情：命令 {packet.Packet.CommandId}，字段 {uidFieldNumber}",
                            writeToConsole: true
                        );
                        if (pendingSnapshot is not null)
                        {
                            return new CapturedAchievementSnapshot(pendingSnapshot, currentUid);
                        }
                    }
                    else if (
                        currentUid == 0
                        && packet.Packet.CommandId == PlayerIdentityDecoder.PlayerGetTokenScRspCommandId
                    )
                    {
                        ApplicationLog.WriteDebug(
                            $"UID 候选诊断：命令 {packet.Packet.CommandId} 的 "
                                + $"{packet.Packet.Body.Length} bytes 响应中没有唯一可信的九位 UID 字段",
                            writeToConsole: true
                        );
                    }

                    if (
                        pendingSnapshot is null
                        && decoder.TryDecode(packet.Packet, out var snapshot)
                        && snapshot is not null
                    )
                    {
                        ApplicationLog.WriteInfo("已确认完整成就快照结构");
                        ApplicationLog.WriteDebug(
                            "成就记录结构详情："
                                + $"命令 {snapshot.SourceCommandId}，"
                                + $"路径 {snapshot.RecordFieldPath}，"
                                + $"ID/状态/完成时间/进度字段 "
                                + $"{snapshot.IdFieldNumber}/"
                                + $"{DisplayField(snapshot.StatusFieldNumber)}/"
                                + $"{DisplayField(snapshot.FinishTimestampFieldNumber)}/"
                                + $"{DisplayField(snapshot.ProgressFieldNumber)}，"
                                + $"已出现的 packed varint 字段 "
                                + $"{DisplayFields(snapshot.PackedVarintFieldNumbers)}，"
                                + $"记录 {snapshot.Records.Count} 条，"
                                + $"元数据命中 {snapshot.CatalogMatchCount} 条，"
                                + $"未知 ID {snapshot.UnknownIdCount} 条",
                            writeToConsole: true
                        );

                        if (currentUid != 0)
                        {
                            return new CapturedAchievementSnapshot(snapshot, currentUid);
                        }

                        pendingSnapshot = snapshot;
                        uidDeadline = DateTimeOffset.UtcNow + UidWaitAfterSnapshot;
                        ApplicationLog.WriteInfo("成就快照已取得；登录响应中的 UID 尚未捕获，继续等待最多 30 秒...");
                    }

                    if (pendingSnapshot is null && packetCount % 100 == 0)
                    {
                        ApplicationLog.WriteDebug(
                            $"已检查 {packetCount} 个包，继续等待完整成就快照；" + packetDiagnostics.FormatForLog(),
                            writeToConsole: true
                        );
                        ApplicationLog.WriteDebug(
                            decoder.BestCandidate is null
                                ? "成就候选诊断：尚未发现至少 3 条且元数据命中率达到 60% 的记录组"
                                : $"成就候选诊断：{decoder.BestCandidate.FormatForLog()}",
                            writeToConsole: true
                        );
                    }

                    break;

                case HookErrorMessage error:
                    throw new InvalidOperationException($"游戏内 Hook 报错：{error.Error}");

                default:
                    throw new InvalidDataException("收到无法识别的 Hook 消息");
            }
        }
    }

    private static string BuildCaptureDiagnostics(
        PacketCaptureDiagnostics packetDiagnostics,
        AchievementSnapshotDecoder decoder
    )
    {
        var candidate = decoder.BestCandidate is null
            ? "成就候选：未发现至少 3 条且元数据命中率达到 60% 的记录组"
            : $"最佳成就候选：{decoder.BestCandidate.FormatForLog()}";
        return $"{packetDiagnostics.FormatForLog(limit: 12)}；{candidate}";
    }

    private static string DisplayField(uint? fieldNumber)
    {
        return fieldNumber?.ToString() ?? "未识别";
    }

    private static string DisplayFields(IReadOnlyList<uint> fieldNumbers)
    {
        return fieldNumbers.Count == 0 ? "无" : string.Join(',', fieldNumbers);
    }
}

internal sealed record CapturedAchievementSnapshot(AchievementSnapshot Snapshot, uint Uid);
