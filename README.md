# Nextcloud 桌面小窗

在 Windows 11 x64 上查看 Nextcloud Tasks 待办、离线编辑任务和阅读近期邮件。关闭窗口可收起到托盘，单击托盘图标即可恢复。

## 安装与连接

1. 从 [GitHub Release](https://github.com/ywyz/nextcloud-windows-widget/releases/latest) 下载签名的 [Windows x64 EXE](https://github.com/ywyz/nextcloud-windows-widget/releases/latest/download/NextcloudWidget-win-x64.exe)，也可以使用[加速下载](https://github.ywyz.tech/https://github.com/ywyz/nextcloud-windows-widget/releases/latest/download/NextcloudWidget-win-x64.exe)。
2. 将 EXE 放在自己的可写目录并双击运行。应用内含 .NET 桌面运行时。
3. 打开设置，填写 HTTPS 服务器地址，保留安装子路径。在系统浏览器中授权后返回小窗。
4. 待办来自支持 VTODO 的 CalDAV 清单。邮件复用 Nextcloud Mail 中已有的邮箱。服务器需提供 Mail 的账户、收件箱和正文 OCS 接口，建议 Mail 5.2 或以上。

## 日常使用

- 默认打开待办页。可按清单筛选，查看截止日期，在可写清单新建、修改、完成任务。离线操作保存在本机，联网后同步；出现冲突时可以对照两端版本选择。
- 邮件页列出各邮箱收件箱最近 50 封邮件，后台下载正文。可离线阅读已缓存正文；点击尚未缓存的邮件会优先下载。阅读和预取保留服务器的已读状态。
- 应用启动、恢复网络及运行期间每 5 分钟同步，也可手动刷新。邮件可见时间取决于 [Nextcloud Mail 自身的收信周期](https://docs.nextcloud.com/server/stable/admin_manual/groupware/mail.html#background-sync-interval)。
- 窗口支持置顶。关闭窗口继续托盘运行；从托盘菜单选择“退出”结束进程。
- 数据保存在当前 Windows 用户的 `%LOCALAPPDATA%\NextcloudWidget`。设备专属应用密码存入 Windows 凭据管理器。设置中的断开操作会清除本地账户缓存，包括尚未上传的修改。

## 更新

应用每天检查 GitHub 稳定 Release，也可以从设置中手动检查。点击安装后，程序使用加速器下载并在失败时尝试 GitHub 直连；校验更新清单签名、文件哈希、Authenticode 发布者和版本后替换 EXE。新版启动确认成功后移除旧版备份。请保留程序所在目录的写入权限。

## 从源码构建

需要 Windows x64 和 .NET 10 SDK。在仓库根目录执行：

```powershell
dotnet run --project src/NextcloudWidget.App
dotnet run --project tests/CoreChecks
dotnet run --project tests/UpdateChecks
dotnet publish src/NextcloudWidget.App -c Release -r win-x64 --self-contained true
```

隔离集成测试使用 WSL Ubuntu、Docker、Nextcloud Tasks、Mail 和 GreenMail。运行 `bash tests/Integration/run-live.sh /mnt/c/path/to/dotnet.exe`，传入 Windows SDK 的实际路径。测试使用临时账户与独立容器。

正式包由 `scripts/Build-Release.ps1` 在签名机器上生成。脚本要求 PowerShell 7.4+、Windows SDK SignTool，以及与程序内置公钥匹配的代码签名证书。GitHub Release 附带 EXE、校验和、更新清单及其分离签名。
