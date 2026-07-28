using HSRae.App.Infrastructure;

namespace HSRae.App;

internal static class ExportSelectionFlow
{
    public static ExportTarget Select(CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
        {
            ApplicationLog.WriteInfo("标准输入不可交互，默认导出成就数据备份");
            return ExportTarget.AchievementBackup;
        }

        Console.WriteLine();
        ApplicationLog.WriteInfo("请选择导出格式（↑/↓ 选择，Enter 确认）：");
        var options = new[]
        {
            "成就数据备份（保留服务端返回的记录、状态、进度、完成时间和原始字段）",
            "导出为 Liyin JSON（由 HSRae 生成，可供 Liyin 导入）",
            "实验性 UIAF v1.2（非官方，按多游戏提案扩展 hkrpg）",
        };
        var selected = ConsoleSelectionMenu.Read(options, 0, cancellationToken);
        ApplicationLog.WriteInfo($"已选择：{options[selected]}");

        var target = (ExportTarget)selected;
        if (target == ExportTarget.UiafExperimental)
        {
            ApplicationLog.WriteWarning(
                "提示：现行 UIAF（v1.1）尚未正式定义星铁；该文件按待讨论的多游戏提案扩展生成，不保证与任何第三方工具兼容"
            );
        }

        return target;
    }
}

internal enum ExportTarget
{
    AchievementBackup,
    Liyin,
    UiafExperimental,
}
