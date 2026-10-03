# P8 当前主机安装器自动验收

日期：2026-10-03。状态：134251 交付候选已通过首次安装、安装目录完整 UI、同版本升级、程序卸载及双基线用户文件保留。统一关闭入口修复已通过 Debug / AOT 各 10 次 IPC 正常退出、P3 与 Fake 两入口、实际自绘关闭按钮取消/重入/确认/清理及 PlaybackRefresh 检查。P8 当前主机本地交付验收完成；125913 / 131910 原生失败保留为历史。最终提交源码按固定流程归档，路径、大小和哈希以忽略的交付元数据为准，公开分发原生对应源码缺口仍保留。

## 输入与隔离

`scripts/test-installer.ps1 -SetupPath <本次安装器绝对路径> -PublishDirectory <本次 app 发布目录绝对路径>` 使用发布目录的 `release-manifest.json`，要求 Windows x64、Native AOT 与自包含清单。输入和全部测试产物必须位于本仓库内，拒绝重解析点及逃出边界的路径。每轮新建 `artifacts/installer-validation/<guid>/app`，日志、UI 报告与 `result.json` 放在同轮目录；不递归清理测试目录。

安装前核对进程身份的注册表 ProfileImagePath 与 USERPROFILE，避免受限账户的 HKCU 配上另一个用户的数据目录。四个 HKCU / HKLM、32 / 64 位卸载位置中，只要生产 AppId 已存在就返回 `Unsupported: ExistingMamboInstallation`，不会覆盖或卸载已有安装。运行环境不能确认用户身份、安装归属或应用窗口状态时同样报告 Unsupported。

## 验收路径

1. 以 `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /TASKS="" /CLOSEAPPLICATIONS` 安装到唯一测试目录。安装器正常退出且卸载注册项指向本轮目录后，才确认本轮安装归属；校验清单所有文件的长度和 SHA-256，以及发布清单本身。
2. 从已安装目录执行现有 UI smoke；报告路径显式指向本轮目录。该模式使用假实现或隔离的合成 HTTP 后端，不读取真实账户凭据或写入真实设置；PlaybackRefresh 验证关闭播放后页面自动重取和进度更新。只有正常退出码 0、新完整报告及除对象留存以外的全部现有功能检查通过时，失败异常才带固定 `UiOnlyRetainedFailure=true` 标记；旧包缺少新增数字字段时保持兼容，字段存在则必须为整数 50。安装脚本仅接受该特定异常标记继续独立验证升级/卸载，`installedUiSmoke` 仍为 false；该分支最终整体仍为 Failed，不能把 UI 留存失败豁免成通过。最新通过轮次全部完整 UI 检查通过，没有使用此失败分支。
3. 启动本轮安装目录的 `Mambo.exe --fake`，保存此次 Start-Process 返回的 Process 句柄与 PID，按 100ms 间隔轮询该进程窗口，截止时间为 30 秒。分别记录 WindowReady、ExitedBeforeWindow、WindowTimedOut、实际等待毫秒数和早退退出码；不再把 InputIdle 或固定 500ms 等待等同窗口就绪。再次运行同一安装器，要求旧 PID 在升级后退出且退出码为 0，全部文件再次匹配。未传 `/FORCECLOSEAPPLICATIONS`，清理时的终止操作不能算作升级通过。
4. 仅在正常安装且注册项与本轮绝对路径匹配时，调用本轮 `unins000.exe` 静默卸载。确认清单文件、清单本身、卸载器 exe/dat 和注册项已删除，验证真实 `%LOCALAPPDATA%/Mambo` 数据保留。

用户数据的两个基线只在 RAM 中保存文件路径、长度和 SHA-256，不写出路径、内容或哈希。安装前原基线及 `originalUserFilesPreserved` 的原含义继续保留，另输出原文件在卸载前和结束时的 changed/missing、新增文件等聚合数量。第一次启动本轮卸载器前，确认本轮 Mambo 进程已退出，再固定完整卸载前快照；清理重试复用该快照，不在部分卸载后重新设基线。卸载后要求全部卸载前文件的长度、哈希和文件集合相同，任何删改或新增都失败；安装前原文件若在卸载前已丢失也仍失败。日志、设置、缓存和其他文件都包含在内，没有筛除可变文件来制造通过。

`preUninstallUserFilesPreserved` 和 `uninstallUserDataPreserved` 明确衡量卸载保留；安装前发生的 byte 变化仍体现为原字段 false 和聚合计数，不直接归因于卸载，也不推断为合法应用写入。代码核对表明 `--fake` 注入内存设置、账号和图片服务，关闭时的 placement 更新仍作用于假设置，退出协调器没有真实 BackendRuntime 日志；未找到该假实现写真实用户目录的代码链路。此前报告不足以定位实际原文件变化来源。

额外创建随机命名的唯一诊断 marker，要求卸载后仍保留相同字节，最后只删除这一个本轮创建的文件。遇到用户数据重解析点或读取错误就停止为 Unsupported，不调整权限、不扫描其他用户目录。文件快照不读取 Windows 凭据条目，不能据此声明凭据管理器内容已经过运行时比对。

所有启动均使用 Hidden。finally 只清理保存了句柄的本轮进程，重核其实际可执行路径，优先请求关闭；若必须终止，就在报告中记录 `cleanupForcedOwnedProcess`，不能把该轮标成 Passed。不按进程名扫描或停止进程，不递归删除任何目录。正常安装后发生失败时，仍只在本轮注册归属已确认的情况下卸载，并在删除 marker 前核验程序文件、注册项、两个 RAM 数据基线和 marker 保留；单独记录清理是否完成、是否核验及失败原因。清理复核失败或异常必须撤销 `LifecyclePassed`，不把原本的整轮失败改成通过。安装未正常完成时，即使发现指向本轮目录的半成品注册项，也只报告 `unconfirmedInstallationLeft`，不擅自尝试卸载；残留会阻止后续安装预检，须由 root 单独处理。

## 已实测的本地候选

交付元数据 `artifacts/release-candidate-delivery.json` 已切换到 `publish/releases/0.1.0/20261003-134251-380/`，本轮原始元数据另保留在 `release-candidate-close-entry.json`。安装器为 102,978,152 字节，SHA-256 `94448c608046533a30988e98be4ba5eec7c945711bf3abf096e0a687f8ba2b53`，与 `InstallerSha256` 及实际安装报告匹配；release-manifest 自身 SHA-256 为 `817797f7b242cb1735ab95f3b0cc07a7062e3a999952d35c161c48fcd5931b79`。

归档校验 `artifacts/release-archive-validation-close-entry.json` 为 Passed：437 个便携项及生成时源码 ZIP 的 413 项与各自清单匹配，整体 ZIP 元数据、原生 lock 匹配，缺失、额外、重复、不安全及禁止目录项均为 0。生成时快照包含 408 个 Mambo 文件、3 个原生归档与 2 份说明，核验当时 408 项全与当前工作区匹配，但来源清单记录 dirty 工作区，不冒充最终已提交源码。最终源码流程固定为：本地提交后只在同一 release 根的 `sources-final/` 归档已提交源码、更新忽略的交付元数据，再核验 `source-manifest.json` 的逐项长度、SHA-256、原生 lock 和提交标识。最终 `SourceZip`、`SourceBytes`、`SourceSha256` 及对应校验结果以该元数据为准；生成时源码 ZIP 保留为历史快照，本文不嵌最终源码 ZIP 自身哈希。

最新实际安装轮次 `artifacts/installer-validation/b86c1004e97e4039a3b5cf3ba337e479/result.json` 的 `status=Passed`、`LifecyclePassed=true`，同目录 `ui-smoke.json` 为 Passed，RunId `eae600a5ff95416194a2913132a707a4`：

- 首次安装及升级后的 436 个清单文件、清单自身都与发布物匹配。
- 安装目录 UI 的 `SessionsClosed`、`FocusRestoresSucceeded`、`OpenedFrameCycles`、`ClosedFrameCycles` 均为 50，`RetainedPlayers=0`、`RetainedSurfaces=0`，未加载 libmpv。
- UIA 5 项通过；425ms 对象收集样本经过 4 个清理帧，player / surface 留存为 0 / 0。四项 `CaptionCloseCancelled`、`CaptionCloseReentryIgnored`、`CaptionCloseConfirmed`、`CaptionCloseCleanupCompleted` 全 true，实际关闭按钮取消、重入和确认后必须经统一异步清理再报告。
- `PlaybackRefresh.Passed=true`、`CleanupPassed=true`，17 项检查均通过：实际关闭按钮触发合成后端保存真实停止位置，详情、最近播放和资料库自动重取并更新进度/继续播放提示；仅合成 HTTP，页面历史与全局页状态保持，资源清理通过。
- 本轮升级实例 PID 55276 在 703ms 检测到窗口；升级请求使该实例正常退出，退出码 0，没有强制清理。
- 卸载程序正常结束；发布文件、清单、unins000.exe/dat 和注册项全部清除，本轮 marker 在卸载后保持相同字节，最后已删除。
- 安装前 356 个原用户文件在卸载前和结束时均无删改，`originalUserFilesPreserved=true`。卸载前固定 357 文件快照包含本轮 marker；卸载后全部文件长度、SHA-256 和集合相同，changed/missing/added 均为 0，`preUninstallUserFilesPreserved=true`、`uninstallUserDataPreserved=true`。
- 没有失败后的卸载清理、强制进程清理或半成品注册残留。`cleanupVerificationAttempted=false`、`cleanupUninstallVerified=false` 表示没有进入失败复核分支，不是主验收失败；正常流程的卸载及数据保留布尔项已全为 true。

双基线修改已通过脚本 AST、diff 检查及 6 个只使用内存合成数据的计数/只捕获一次检查，并完成上述真实安装验收。PlaybackRefresh、统一关闭入口和独立原生退出的 Debug / AOT 回归也已通过，当前主机本地安装交付验收完成；旧候选成功与失败记录继续独立保留。

## 原生退出根因与修复回归

125913 与永久 Dispose 改动后的 131910 均出现同类原生退出失败；131910 元数据为 `artifacts/release-candidate-lab-dispose.json`。永久 Dispose 单独未解决，没有将该候选标为最终通过。WER 小 dump 缺少 stowed payload，只能看到 CoreMessaging failfast 栈，无法读出 `0x8000FFFF`；该 HRESULT 来自后来约 562MB 的完整 dump 中的 STOWED0。精确定位则来自更后续的 CDB live first-chance CLR 栈 `artifacts/native-exit-helper/ipc-refresh-after-close-proof.txt` / `.json` 与同一二进制反汇编：UI 线程 `VideoLab.RefreshDiagnostics+0x226` 对应 `Slider.Maximum`，Window.Close 已销毁 XAML，但仍活跃的 DispatcherQueueTimer 继续访问控件，随后发生 `0xC000027B`。该轮 UI / 协议报告通过且二进制哈希未变，进程退出仍失败；没有将故障归因于 IPC 协议或未执行的新 fixture。

官方 [Window.Close](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.window.close?view=windows-app-sdk-2.0) 会销毁窗口；[AppWindow.Closing](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindow.closing?view=windows-app-sdk-2.0) 是系统关闭入口的事件，AppWindow.Destroy 不触发它，直接 Close 不能被当作保证先经过异步清理的入口。134251 已统一显式异步关闭，先停止计时器/回调并清理资源，再销毁窗口，同时修复 MainWindow 自绘关闭按钮的同类路径。

`close-regression-debug/summary.json` 与 `close-regression-aot/summary.json` 均为 Passed，各 10 次 IPC 正常退出码 0，P3、Fake 命令行和环境变量入口均通过。`ui-debug-close-entry.json`（RunId `0d176b3e9b36419f8a278e929169bf46`）与 `ui-aot-close-entry.json`（RunId `878fb055ece44f8abde542060c4bf6b5`）也均 Passed，对象收集样本为 439ms / 419ms、4 个清理帧、留存 0 / 0；四个 CaptionClose 布尔值全 true，UIA 5 项、50 次关闭及 PlaybackRefresh 17 检查全通过。报告位于 `artifacts/`，安装目录相同检查结果见上述最新安装轮次；不以旧候选的协议 Passed 替代实际正常退出与完整清理。

## 历史候选与失败

旧 125913 元数据已保留为 `artifacts/release-candidate-125913.json`，`ee9d0e81ce2b40368f3e0782012e12fe` 安装轮次仍为 Passed：570ms 正常升级、356 个原文件及卸载前 357 文件全保留。但独立原生退出回归失败，与 131910 的失败和 first-chance 定位证据都保留为历史，不替代新 134251 的完整通过证据。

123901 候选及其 `34b4b28678d94440b24a52898c867665` 安装轮次已通过当时 UI 与生命周期，窗口就绪为 546ms，但不含后加入的 PlaybackRefresh；它们由 `release-candidate-verified.json` 与 `release-archive-validation-verified.json` 保留。

`artifacts/release-candidate-final.json` 对应更早的 112620 候选，历史安装轮次 `artifacts/installer-validation/211fcecd26bf41a9b98f6355fcdb8a6c/result.json` 及同目录 UI 报告仍保留原 Failed：首次安装、升级正常退出和程序卸载已成功，但 player/surface 各留存 2；安装前原文件数量为 21，`originalUserFilesPreserved=false`、`LifecyclePassed=false`。该轮没有卸载前快照，原比对覆盖整个 UI/启动/升级过程，不能证明变化由卸载造成，也不表示 21 个文件全部改变。新通过轮次不补写这份旧报告缺失的计数，也不将旧 Failed 追认成通过。

## 结果边界

报告只有 Passed / Failed / Unsupported 和安全原因码、公开发布文件哈希、计数、本轮 PID 及布尔验收项。写出报告后，Failed / Unsupported 会以不含私人路径的固定错误让调用方失败；仅 Passed 正常返回。Passed 证明当前主机上的上述自动流程；它不证明真实 Emby 播放时的退出上报。用户已于 2026-10-03 取消专用干净 Windows 10/11 虚拟机验收，本机结果不会扩展为干净系统验证结论。当前原生对应源码缺口仍阻止公开分发，见 `P8-packaging.md`。

P8 安装交付通过不表示性能或所有阶段通过。新候选单实例轮次 `b19a513633c247c3947ff282097a1694` 为 Passed，但真实启动轮次 `e24cf88c865e4cafa666c4b14cc16cb0` 为 ResultsFailed：窗口 881 / 865 / 774ms、缓存首页 972 / 919 / 827ms，三轮均未达到 600 / 800ms 目标；对应状态由 PLAN 和 P4–P6 记录维护。
