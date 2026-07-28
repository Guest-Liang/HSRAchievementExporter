# HSRae

HSRae 是 Windows x64 下的《崩坏：星穹铁道》国服成就导出工具。

## 免责声明与风险提示
>[!Warning]
>本项目是与米哈游及《崩坏：星穹铁道》官方无关的第三方开源工具，仅供个人成就数据备份与迁移使用。工具运行时会向游戏进程加载临时 Hook；此类行为可能违反游戏用户协议、运营规则或被反作弊系统识别，并可能导致账号警告、限制、封禁，以及数据或其他损失。
>
>使用者应在使用前自行了解并遵守所在地法律法规、游戏用户协议及相关规则，自行判断和承担全部风险。项目作者及贡献者不对因下载、安装、运行、修改或传播本工具而产生的账号处罚、封禁、数据丢失、财产损失或其他直接、间接损失承担责任。若无法接受上述风险，请勿使用本工具。

## 使用方法

1. 确认《崩坏：星穹铁道》已完全退出。
2. 运行构建产物 `HSRae_v<版本>_Release.exe`。
3. 选择注册表中检测到的路径，或手动指定游戏目录 / `StarRail.exe`。
4. 接受 Windows 管理员权限请求。
5. 在由 HSRae 启动的游戏中正常登录。
6. HSRae 捕获登录响应中的 UID 和完整任务快照后，会关闭本次启动的游戏并让你选择导出格式。

可选命令行参数：

```powershell
.\HSRae.exe --game "D:\Program Files\miHoYo\Star Rail\Game"
```

工具只接受国服正式渠道：注册表路径为 `HKCU\Software\miHoYo\HYP\1_1\hkrpg_cn`，并同时校验 `StarRail.exe`、`GameAssembly.dll`、`StarRail_Data\app.info` 以及 `config.ini` 中的渠道和版本字段。

## 导出格式

### 成就数据备份

`HSRae-achievements-<日期>.json`。备份保留：

- UID、游戏版本、捕获时间与元数据版本；
- 服务端返回的星铁成就记录；
- 成就 ID、任务状态、进度、完成时间；
- 用于兼容未来字段的原始 protobuf varint 映射；
- 本次识别使用的命令号、字段路径和字段号。

### 由 HSRae 生成的 Liyin 格式

`HSRae-liyin-<日期>.json`。这是 HSRae 生成、可供 Liyin 导入的文件。

### 实验性 UIAF v1.2

`HSRae-uiaf-<日期>.json`。现行 UIAF v1.1 只正式定义原神成就，本文件依据 [UIAF v1.2 多游戏讨论提案](https://github.com/orgs/UIGF-org/discussions/18) 做实验性支持。

## 构建

```powershell
.\build.ps1
```

默认同时生成：

- `artifacts\build\HSRae_v1.0.0_Release.exe`
- `artifacts\build\HSRae_v1.0.0_Debug.exe`

也可只构建一个配置：

```powershell
.\build.ps1 -Configuration Release
```

脚本会单独发布 NativeAOT Hook、将其嵌入单文件主程序，并校验最终文件名和 Windows 版本资源。Debug 构建会输出更详细的定位与包识别日志，但仍不会输出包正文。

## 许可证

本项目基于 `ZZZAchievementExporter` 的实现演化，使用[GNU General Public License v3.0](LICENSE)。

星铁成就元数据
[`src/HSRae.Protocol/Metadata/AchievementInfo.json`](src/HSRae.Protocol/Metadata/AchievementInfo.json)
取自 [liyin.space 的 `src/jsons/AchievementInfo.json`](https://github.com/Ticca-Liyin/liyin.space/blob/master/src/jsons/AchievementInfo.json)。
