# Nextcloud 桌面小窗

在 Windows 11 x64 上查看 Nextcloud Tasks 待办、离线编辑任务和阅读近期邮件。关闭窗口可收起到托盘，单击托盘图标即可恢复。

## 安装与连接

1. 从 [GitHub Release](https://github.com/ywyz/nextcloud-windows-widget/releases/latest) 下载签名的 [Windows x64 EXE](https://github.com/ywyz/nextcloud-windows-widget/releases/latest/download/NextcloudWidget-win-x64.exe)，也可以使用[加速下载](https://github.ywyz.tech/https://github.com/ywyz/nextcloud-windows-widget/releases/latest/download/NextcloudWidget-win-x64.exe)。
2. 将 EXE 放在自己的可写目录并双击运行。应用内含 .NET 桌面运行时。
3. 打开设置，填写 HTTPS 服务器地址，保留安装子路径。在系统浏览器中授权后返回小窗。
4. 待办来自支持 VTODO 的 CalDAV 清单。邮件复用 Nextcloud Mail 中已有的邮箱，需要 Mail 的账户、收件箱和正文 OCS 接口。标记已读和移动邮件还需要服务端允许已授权应用调用 Mail 的写入接口。已验证 Nextcloud 32.0.15 与 Mail 5.13.3。

## 日常使用

- 待办页可按清单筛选。在输入框写下标题、选好可写清单并按 Enter，即可添加待办。需要备注或截止日期时再打开编辑。离线改动会在联网后同步，发生冲突时可对照两端版本选择。
- 邮件页缓存每个收件箱最近 50 封邮件。打开正文会标记已读，后台预取保留未读状态。逐封勾选或全选后，可批量标记已读，也可确认后移到各自邮箱的废纸篓。离线操作会在联网后提交。邮箱需要有可识别的废纸篓才能执行移动。
- 应用启动、恢复网络及运行期间每 5 分钟同步，也可手动刷新。邮件可见时间取决于 [Nextcloud Mail 自身的收信周期](https://docs.nextcloud.com/server/stable/admin_manual/groupware/mail.html#background-sync-interval)。
- 主窗可拖动顶部、调整大小、置顶或收起到托盘，并记住日夜模式。正常使用时，收起、关闭主窗或从托盘退出都会在桌面创建或复用「Nextcloud 小窗」快捷方式。双击快捷方式即可恢复已收起的小窗，或在退出后重新启动。
- 数据保存在当前 Windows 用户的 `%LOCALAPPDATA%\NextcloudWidget`。设备专属应用密码存入 Windows 凭据管理器。设置中的断开操作会清除本地账户缓存，包括尚未上传的修改。

## 翻译邮件

在设置中打开「配置翻译模型」，填写兼容 OpenAI Chat Completions 的 API 基址、模型名称和目标语言。默认目标为简体中文。远程服务使用 HTTPS，本机服务可填写 `http://127.0.0.1:端口/v1`。服务要求 API Key 时可填写，密钥保存在 Windows 凭据管理器。

打开一封邮件后点「翻译」，小窗会将该封邮件的正文发送给所选服务。可以切换原文和译文，也可以取消正在进行的翻译。

## 更新

应用每天检查 GitHub 稳定 Release，也可以从设置中手动检查。默认通过 `github.ywyz.tech` 检查和下载。设置中会显示当前线路、下载量、总大小和速度。加速器传输中断时会先重试，支持分段请求时从断点继续，加速线路仍不可用时才切换 GitHub 直连。可以取消下载后再试。

下载完成后，程序会校验安装包的 SHA-256、Authenticode 发布者和版本，再替换 EXE。新版确认启动成功后移除旧版备份。请保留程序所在目录的写入权限。

## 从源码构建

需要 Windows x64 和 .NET 10 SDK。在仓库根目录执行：

```powershell
dotnet run --project src/NextcloudWidget.App
dotnet run --project tests/CoreChecks
dotnet run --project tests/TranslationChecks
dotnet run --project tests/UpdateChecks
dotnet publish src/NextcloudWidget.App -c Release -r win-x64 --self-contained true
```

隔离集成测试使用 WSL Ubuntu、Docker、Nextcloud Tasks、Mail 和 GreenMail。运行 `bash tests/Integration/run-live.sh /mnt/c/path/to/dotnet.exe`，传入 Windows SDK 的实际路径。测试使用临时账户与独立容器。

在 Windows 桌面运行 `./tests/UiChecks/Test-Widget.ps1 -Executable 路径/NextcloudWidget-win-x64.exe` 可验证快速新增、主题切换、邮件批量操作和翻译。此项测试需要 PowerShell 7.4+ 和 Python 3，使用独立的离线账户及本机模拟翻译服务。

正式包由 `scripts/Build-Release.ps1` 在签名机器上生成。脚本要求 PowerShell 7.4+、Windows SDK SignTool，以及与程序内置公钥匹配的代码签名证书。GitHub Release 附带 EXE、校验和、更新清单及其分离签名。
