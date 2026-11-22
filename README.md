# CodexQuotaTray

Windows 11 的任务栏托盘额度组件。左键打开面板，右键通过 **显示来源** 切换 Codex、火山引擎 Agent Plan 或 Antigravity。组件会记住上次选择的来源。

## 下载和运行

1. 在 [Cloudflare 下载页](https://codex-quota-tray-aver58.pages.dev/) 下载 Windows x64 便携包并解压。
2. 双击 `CodexQuotaTray.exe`。便携包已包含 .NET 运行时。
3. 在系统托盘中左键单击图标查看额度。按住面板可拖动，点击其他窗口或面板的关闭按钮即可收起。要退出程序，在托盘右键菜单中选择 **退出**。

换电脑时，在目标电脑登录对应服务，或在 **设置** 配置火山引擎。需要登录 Windows 后自动启动时，将程序快捷方式放入 `shell:startup` 文件夹。

## 自动更新

v0.3.0 起，组件通过 HTTPS 匿名读取 Cloudflare 上的稳定版清单，在启动时及每 6 小时检查更新。右键托盘选择 **检查更新** 可立即执行，也可关闭 **自动更新**。用户无需安装 GitHub CLI 或登录 GitHub。旧版用户可以退出组件，用新 EXE 替换原程序一次。

客户端按版本号选择更新，核对 ZIP 大小、SHA-256 和 EXE 版本，在原位置替换并重启。程序目录需要允许当前 Windows 用户写入。若新版启动失败，助手尝试恢复旧程序。

## 每个用户配置自己的账号

- **火山引擎**：右键托盘，选择 **显示来源 → 火山引擎 · Agent Plan**。首次使用会打开设置，在 **Access Key ID** 和 **Secret Access Key** 填入自己的 IAM 密钥并保存。下次打开设置将隐藏已有密钥，勾选「更换已保存的密钥」可更新。额度查询需要 Ark Agent Plan 读取权限，账户余额需要 `billing:QueryBalanceAcct`。
- **Codex 与 Antigravity**：在这台电脑上登录各自客户端。Antigravity 的实时额度需要桌面端正在运行。
- **存储与传输**：个人设置存于 `%LOCALAPPDATA%\CodexQuotaTray\settings.json`。Access Key ID 以明文保存，Secret 由当前 Windows 用户的 DPAPI 加密。火山引擎请求在本机签名；Cloudflare 更新站仅提供版本清单和程序包。分享时只拷贝便携 ZIP 或 EXE。

## 可以查看什么

| 来源 | 额度面板 | 数据来源 |
| --- | --- | --- |
| Codex | 5 小时和周剩余额度、重置时间、今日及近 7 天 token。缺少 5 小时额度且有重置卡数据时显示重置卡数量。 | 使用本机 Codex 登录查询 `wham/usage`。实时读取失败时尝试使用本地会话记录，按本地日期统计 token。 |
| 火山引擎个人 Agent Plan | 5 小时、日、周、月 AFP 额度及重置时间，可用余额与现金余额。右侧用量图显示本机 Codex 的 token。 | 在设置中填写具有 Ark 用量读取权限的 IAM Access Key。余额查询还需要 `billing:QueryBalanceAcct` 权限。节省计划余额可在火山引擎控制台查看。 |
| Antigravity | Gemini 和 Claude + GPT 两组的 5 小时与周已用比例、剩余比例和重置时间，以及本机最近 7 天逐日 token 数。 | 实时额度取自已登录且正在运行的 Antigravity 桌面端。每日 token 汇总自本机 Antigravity 会话数据库。 |

在 **设置** 可设置刷新间隔（默认 15 秒）、Codex 主目录和托盘提示使用的重置窗口（5 小时或周额度）。当前用户配置保存在 `%LOCALAPPDATA%\CodexQuotaTray\settings.json`。火山引擎 Secret Access Key 通过 Windows DPAPI 加密并绑定当前用户。

## 从源码构建

在 Windows 上安装 .NET 8 SDK 或兼容的更新版本，在仓库根目录运行：

```powershell
dotnet build .\CodexQuotaTray.sln -c Release
.\scripts\Build-Release.ps1
```

`dist/` 中会生成仅含 EXE 的便携 ZIP 和 `latest.json` 清单。维护者先上传带版本号的 ZIP，再更新清单。Cloudflare Pages 通过 R2 绑定读取专用桶，站点配置位于 `release-site/`。独立部署的维护者需将 `ReleaseUpdater.cs` 中的站点地址改为自己的 HTTPS 域名。

| 目录 | 内容 |
| --- | --- |
| `CodexQuotaTray/` | WPF 面板、托盘菜单和设置窗口 |
| `CodexQuotaCore/` | 三种来源的额度读取与本地用量统计 |
| `CodexQuotaSmoke/` | 在终端读取并显示 Codex 额度的诊断工具 |

本项目源码采用 [MIT 许可证](LICENSE)。
