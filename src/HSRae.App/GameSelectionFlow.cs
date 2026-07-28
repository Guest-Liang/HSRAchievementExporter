using HSRae.App.Infrastructure;

namespace HSRae.App;

internal static class GameSelectionFlow
{
    private const string ExpectedPublisher = "miHoYo";
    private const string ExpectedProduct = "崩坏：星穹铁道";

    public static GameSelection? Select(string? configuredGamePath)
    {
        if (configuredGamePath is not null)
        {
            ApplicationLog.WriteInfo("游戏路径来源：命令行 --game");
            return ResolveAndValidateGame(configuredGamePath);
        }

        var registryGamePath = GameLocator.TryFindChinaGameExecutable();
        if (Console.IsInputRedirected)
        {
            if (registryGamePath is not null)
            {
                ApplicationLog.WriteInfo("游戏路径来源：注册表（非交互启动）");
                return CreateGameSelection(registryGamePath);
            }

            throw MissingRegistryGamePath();
        }

        var selectedOption = registryGamePath is null ? 1 : 0;
        while (true)
        {
            ApplicationLog.WriteInfo("请选择游戏路径获取方式（↑/↓ 选择，Enter 确认）：");
            var isAdministrator = ElevationManager.IsAdministrator();
            var options = new[]
            {
                registryGamePath is null ? "从注册表读取游戏路径（未检测到有效路径）" : "从注册表读取游戏路径",
                isAdministrator ? "手动粘贴游戏目录 / StarRail.exe" : "手动粘贴或拖入游戏目录 / StarRail.exe",
                "退出 HSRae",
            };
            var selected = ConsoleSelectionMenu.Read(
                options,
                selectedOption,
                CancellationToken.None,
                escapeSelection: options.Length - 1
            );
            selectedOption = selected;
            ApplicationLog.WriteInfo($"已选择：{options[selected]}");

            if (selected == 0)
            {
                registryGamePath = GameLocator.TryFindChinaGameExecutable();
                if (registryGamePath is not null)
                {
                    try
                    {
                        var selection = CreateGameSelection(registryGamePath);
                        ApplicationLog.WriteInfo("游戏路径来源：注册表");
                        return selection;
                    }
                    catch (Exception exception) when (IsGamePathValidationException(exception))
                    {
                        ApplicationLog.WriteWarning($"注册表中的游戏路径无效：{exception.Message}");
                        ApplicationLog.WriteDebug($"注册表中的游戏路径校验异常：{exception}", writeToConsole: false);
                        registryGamePath = null;
                        selectedOption = 1;
                        if (!WaitForReturnToPathMenu())
                        {
                            return null;
                        }

                        continue;
                    }
                }

                ApplicationLog.WriteWarning("未检测到有效的游戏注册表路径，请重新选择");
                selectedOption = 1;
                if (!WaitForReturnToPathMenu())
                {
                    return null;
                }

                continue;
            }

            if (selected == 1)
            {
                if (isAdministrator)
                {
                    ApplicationLog.WriteInfo(
                        "当前窗口具有管理员权限，Windows 会阻止从普通权限资源管理器拖入；请复制并粘贴完整路径"
                    );
                }
                else
                {
                    ApplicationLog.WriteInfo("可以把游戏目录或 StarRail.exe 拖入当前窗口，也可以粘贴完整路径");
                }

                Console.Write("游戏路径（直接按 Enter 取消）：");
                var enteredPath = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(enteredPath))
                {
                    return null;
                }

                try
                {
                    var selection = ResolveAndValidateGame(enteredPath);
                    ApplicationLog.WriteInfo("游戏路径来源：交互输入");
                    return selection;
                }
                catch (Exception exception) when (IsGamePathValidationException(exception))
                {
                    ApplicationLog.WriteWarning($"手动指定的游戏路径无效：{exception.Message}");
                    ApplicationLog.WriteDebug($"手动指定的游戏路径校验异常：{exception}", writeToConsole: false);
                    selectedOption = 1;
                    if (!WaitForReturnToPathMenu())
                    {
                        return null;
                    }
                }

                continue;
            }

            return null;
        }
    }

    private static GameSelection ResolveAndValidateGame(string configuredPath)
    {
        ApplicationLog.WriteInfo($"待校验的游戏路径：{configuredPath}", writeToConsole: false);
        var executablePath = GameLocator.ResolveChinaGameExecutable(configuredPath);
        return CreateGameSelection(executablePath);
    }

    private static GameSelection CreateGameSelection(string executablePath)
    {
        return new GameSelection(executablePath, ValidateChinaProductionBuild(executablePath));
    }

    private static bool IsGamePathValidationException(Exception exception)
    {
        return exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException;
    }

    private static bool WaitForReturnToPathMenu()
    {
        Console.Write("按 Enter 返回路径选择菜单...");
        var canContinue = Console.ReadLine() is not null;
        Console.WriteLine();
        return canContinue;
    }

    private static FileNotFoundException MissingRegistryGamePath()
    {
        return new FileNotFoundException(
            """
            没有在注册表找到国服游戏路径；
            非交互启动时请使用 --game 指定游戏目录或 StarRail.exe 完整路径
            """
        );
    }

    private static string ValidateChinaProductionBuild(string gameExecutablePath)
    {
        var gameDirectory =
            Path.GetDirectoryName(gameExecutablePath) ?? throw new InvalidDataException("无法确定游戏安装目录");
        var gameAssemblyPath = Path.Combine(gameDirectory, "GameAssembly.dll");
        if (!File.Exists(gameAssemblyPath))
        {
            throw new FileNotFoundException("游戏目录缺少 GameAssembly.dll", gameAssemblyPath);
        }

        var appInfoPath = Path.Combine(gameDirectory, "StarRail_Data", "app.info");
        if (!File.Exists(appInfoPath))
        {
            throw new FileNotFoundException("游戏目录缺少 StarRail_Data\\app.info，无法确认游戏产品", appInfoPath);
        }

        var appInfo = File.ReadAllLines(appInfoPath);
        if (
            appInfo.Length < 2
            || !appInfo[0].Trim().Equals(ExpectedPublisher, StringComparison.Ordinal)
            || !appInfo[1].Trim().Equals(ExpectedProduct, StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException("app.info 与国服《崩坏：星穹铁道》不匹配");
        }

        var configPath = Path.Combine(gameDirectory, "config.ini");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("游戏目录缺少 config.ini，无法确认渠道和版本", configPath);
        }

        var general = ReadIniSection(configPath, "General");
        if (
            !general.TryGetValue("channel", out var channel)
            || channel != "1"
            || !general.TryGetValue("sub_channel", out var subChannel)
            || subChannel != "1"
        )
        {
            throw new InvalidDataException("config.ini 的 channel/sub_channel 不是 HSRae 支持的国服正式渠道");
        }

        if (!general.TryGetValue("game_version", out var gameVersion) || string.IsNullOrWhiteSpace(gameVersion))
        {
            throw new InvalidDataException("config.ini 缺少有效的 game_version");
        }

        var executableInfo = new FileInfo(gameExecutablePath);
        var gameAssemblyInfo = new FileInfo(gameAssemblyPath);
        ApplicationLog.WriteInfo(
            $"游戏渠道：hkrpg_cn（channel={channel}, sub_channel={subChannel}）",
            writeToConsole: false
        );
        ApplicationLog.WriteDebug(
            $"游戏文件：{executableInfo.FullName}；"
                + $"大小 {executableInfo.Length} bytes；"
                + $"最后写入 UTC {executableInfo.LastWriteTimeUtc:O}",
            writeToConsole: false
        );
        ApplicationLog.WriteDebug(
            $"GameAssembly：{gameAssemblyInfo.FullName}；"
                + $"大小 {gameAssemblyInfo.Length} bytes；"
                + $"最后写入 UTC {gameAssemblyInfo.LastWriteTimeUtc:O}",
            writeToConsole: false
        );

        return gameVersion.Trim();
    }

    private static Dictionary<string, string> ReadIniSection(string path, string expectedSection)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inSection = false;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                inSection = line[1..^1].Trim().Equals(expectedSection, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return values;
    }
}

internal sealed record GameSelection(string ExecutablePath, string Version);
