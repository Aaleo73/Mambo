# P8 打包交付

日期：2026-10-03。状态：本机的打包、安装、升级和卸载验收通过。

当时含 libmpv 的二进制还不能公开分发，因为所用的 shinchiro 构建无法追溯全部对应源码。这个限制在 2026-10-05 换用 MSYS2 原生组件后解除，见 [原生组件对应源码](native-distribution.md) 和 [GitHub 托管与自动更新](github-auto-update.md)。

## 发布输出

`pwsh scripts/publish.ps1 -Version <版本>` 先 locked-mode restore，再发布 Windows x64、Release、Native AOT、.NET 与 Windows App SDK 自包含产物。版本参数同时传入产品版本、数字文件版本和程序集说明版本，界面不硬编码版本号。

每次使用新的 `publish/releases/<version>/<timestamp>/app` 目录，拒绝已有目录，不递归删除旧发布物或任何用户目录。脚本核验 EXE、PRI、XBF、WinUI / Windows App Runtime DLL、四个字体的哈希，以及 lock 中 libmpv 的 SHA-256；libmpv 固定在 `mpv/`。随包附根 LICENSE、THIRD_PARTY_NOTICES 与 LICENSES，生成逐文件字节数和 SHA-256 的 `release-manifest.json`，然后创建便携 ZIP。源码包由 `scripts/make-source-archive.ps1` 生成，组成见 [原生组件对应源码](native-distribution.md)。脚本不自动签名，也不自动发布。

## 安装器

`-Installer` 使用已安装的 ISCC.exe，可用 `-IsccPath` 显式指定；找不到编译器时保留已生成的便携包并给出中文失败原因。脚本本身不暗中安装软件。

`installer/Mambo.iss` 使用 `PrivilegesRequired=lowest`，安装到当前用户的 `%LOCALAPPDATA%/Programs/Mambo`，最低 Windows 10 22H2、Windows x64，中文界面。升级通过 Inno Setup 的 Restart Manager 请求关闭安装目录下的 Mambo.exe，不使用 `force`，不扫描或终止其他进程和外部 mpv；停止播放和保存进度由应用的有序退出负责。默认提供开始菜单快捷方式，桌面快捷方式由用户勾选。

`installer/ChineseSimplified.isl` 取自[官方 issrc 标签 is-6_7_3 的语言文件](https://github.com/jrsoftware/issrc/blob/is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl)，安装器按仓库相对路径引用，不依赖编译器是否自带简体中文翻译。原文件保留维护者归属；Inno Setup 的官方许可收录在 `LICENSES/Inno-Setup-license.txt`。

卸载只由 Inno 的安装文件记录清理程序文件，没有 UninstallDelete 用户数据规则，`%LOCALAPPDATA%/Mambo` 会保留。

依据：[PrivilegesRequired](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)、[CloseApplications](https://jrsoftware.org/ishelp/topic_setup_closeapplications.htm)、[CloseApplicationsFilter](https://jrsoftware.org/ishelp/topic_setup_closeapplicationsfilter.htm)。

## 许可

Windows App SDK 2.5.1 的 NuGet 包适用 Microsoft Software License Terms，不是 MIT；ML 子包另有条款。随包附 Microsoft、.NET、Toolkit 的完整 NOTICE、小米 MiSans 官方 PDF、GNU 与 Apache 文本，详见 `THIRD_PARTY_NOTICES.md`。

当时为什么不能公开分发：libmpv 用的是 shinchiro `20260610` 构建。它的公开 CI 在构建前执行 `ninja update`，日志只保留一天，发行资产里没有全部静态依赖的对应源码，包装配方也不生成完整的依赖修订清单。这些证据无法恢复全部原生依赖的精确修订，只凭 mpv、FFmpeg 和构建脚本三份主工程源码也补不齐。Opus DNN 模型归档内没有许可文件，其中部分生成文件和权重的许可范围无法确认。最后的处理是整体换用有匹配源码包的 MSYS2 组件，而不是继续补旧构建的证据。

## 关闭顺序故障与修复

两个早期候选在窗口关闭后以 `0xC000027B` 退出，而界面和 IPC 协议报告都显示通过。

- **根因**：`Window.Close()` 已销毁 XAML，但仍活跃的 `DispatcherQueueTimer` 继续刷新控件（定位到 `VideoLab.RefreshDiagnostics` 对 `Slider.Maximum` 的访问）。故障属于窗口销毁与清理顺序，不是 IPC 协议错误。
- **定位过程中的教训**：WER 的小 dump 缺少 stowed payload，只能看到 CoreMessaging 的 failfast 栈，读不出真正的 `0x8000FFFF`，也定位不到调用点；要用完整 dump 或 CDB 的 live first-chance CLR 栈。单独把控件改成永久 Dispose 没有解决问题。
- **修复**：统一显式的异步关闭入口，先停止计时器和回调、完成资源清理，再销毁窗口；MainWindow 的自绘关闭按钮走同一条清理路径。
- **依据**：官方 [Window.Close](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.window.close?view=windows-app-sdk-2.0) 的定义是销毁窗口；[AppWindow.Closing](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindow.closing?view=windows-app-sdk-2.0) 只针对系统关闭入口，AppWindow.Destroy 不触发它，所以不能假定直接 Close 总会先经过可等待的清理。
- **回归**：Debug 与 AOT 各 10 次正常退出，退出码 0；实际自绘关闭按钮的取消、重入、确认和清理四项通过；50 次假播放关闭后播放器与画面对象留存为 0。

判断退出是否正常要看进程退出码，不能只看界面或协议报告通过。

## 验收范围与限制

- 本机首次安装、安装目录下的完整界面回归、同版本升级（运行中的实例正常退出）、卸载及用户文件保留都通过，做法与结果见 [安装器自动验收](P8-installer-validation.md)。
- 用户于 2026-10-03 取消了专用 Windows 虚拟机验收。本机已有 SDK 和运行时，所以不宣称经过干净 Windows 10 / 11 验收。
- 打包交付通过不代表性能达标：窗口 Loaded 与缓存首页的启动目标仍未达到，见 `docs/STATUS.md`。
- 代码签名：没有做。
