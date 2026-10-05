# Mambo

Windows 上的 Emby 媒体客户端，使用 WinUI 3、C#/.NET 10 和 libmpv。支持资料库浏览、搜索、筛选、内置播放、外部 mpv、弹幕和按内容记忆的画质模式。

目前处于开发阶段，支持 Windows 10 22H2 / Windows 11 x64。

## 开发

需要 .NET 10 SDK、Windows SDK 和 Visual Studio Build Tools 的 C++ Native AOT 工具链；不需要 Visual Studio IDE。SDK 与 NuGet 依赖由仓库锁定。

```powershell
pwsh scripts/fetch-libmpv.ps1
dotnet restore --locked-mode
dotnet build -p:Platform=x64
dotnet test
dotnet run --project src/Mambo.App -p:Platform=x64 -- --fake
```

正式运行时在界面中输入服务器和账号。凭据保存在 Windows 凭据管理器中，请勿将服务器信息、令牌或密码提交到 GitHub。

## 更新与发布

设置页提供“启动时自动检查更新”“检查更新”“下载并安装”和发行页面入口。自动检查延迟到启动后执行；只读取本仓库的公开稳定版 Releases。下载安装包后核对 GitHub 提供的 SHA-256，再打开安装向导完成升级。便携版通过安装包更新时会转为安装版。不会在播放中静默替换文件。演示模式不连接更新服务。

构建安装包：

```powershell
pwsh scripts/publish.ps1 -Version 0.1.1 -Installer -UpdateRepository Aaleo73/Mambo
```

安装包必须命名为 `Mambo-<版本>-win-x64-setup.exe`，对应 Release 标签为 `v<版本>`；只发布稳定版。GitHub Actions 在推送和 PR 上构建及测试，推送 `v*` 标签时构建并发布安装器、便携包、应用源码与全部原生对应源码分包。

内置播放器已改用有匹配源码、补丁、构建记录和 Rust 锁定依赖的 MSYS2 原生组件。下载入口为 [GitHub Releases](https://github.com/Aaleo73/Mambo/releases)；普通安装使用 `setup.exe`，免安装使用 `portable.zip`。源码由 `sources.zip` 和全部 `native-sources-*.zip` 组成，使用同页 `SHA256SUMS.txt` 校验。发布审查绑定两份原生 lock 的哈希，变更输入后需重新核验。详见 [原生源码与发布记录](docs/decisions/native-distribution.md)。

实施进度及待验收项见 [实施计划](docs/PLAN.md)，更新设计见 [GitHub 与自动更新](docs/decisions/github-auto-update.md)。

## 许可

Mambo 采用 GPL-3.0-or-later。第三方组件保留各自许可，见 [LICENSE](LICENSE)、[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 和 [LICENSES](LICENSES)。
