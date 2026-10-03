# 后端状态

服务实现与接入状态如下。契约定义见 `docs/PLAN.md` §14。

2026-10-03：外壳和播放器的接入请求 R-003/R-016/R-017/R-019/R-020 已处理；阶段验收仍按实际证据更新。集成记录见 [P4–P6 集成](../decisions/P4-P6-integration.md)。AppSettings.UseEpisodeGrid 为选集布局跨重启记忆的兼容增量，旧文件默认列表。

当前本地播放及音频修复候选为 `publish/releases/0.1.0/20261003-144025-553/app`，安装包为同目录上一层的 `Mambo-0.1.0-win-x64-setup.exe`。发布路径和完整校验值见 `artifacts/release-candidate-audio-fix.json`；提交后源码归档单独放在该发布目录的 `sources-final/`。旧入口 `publish/aot` 同步本次文件，避免运行旧二进制。不推送远端，不将当前 libmpv 组合包公开分发。

本次修复“有画面但无声”：正常播放器不再设置不存在的 ao=auto 驱动，改为保留 mpv 默认音频驱动选择，仅显式无声/headless 测试设置 ao=null。同一集真实验证 WASAPI、已选音轨、48000 Hz / 2 channels、非静音。357/357 单测通过，Debug/AOT 构建无警告错误；正式界面回归现在强制检查真实音频输出、所选音轨、有效输出格式、音量/静音控件原生回写及恢复推进，Debug/AOT 均通过。见 [音频输出修复](../decisions/playback-audio-output.md)。原 Windows 音量与设备设置未修改。

上一轮解决用户正式播放器“片源无法播放”：登录服务器签发的跨域下载 Location 中认证查询被误删，三个候选全部 HTTP 401 / mpv -13。改为保留已认证服务器签发的完整链接，仍清空跨域认证头并保留直接外部地址、降级、字幕后续跳转的隔离。真实保存会话复测三个候选全部 HTTP 206 / FILE_LOADED / PLAYBACK_RESTART。当时新增安全原生错误诊断和正式 Shell 真实 libmpv 验证，353 项单测通过，Debug/AOT 正式播放、视口尺寸、暂停/跳转/恢复/关闭通过；该轮没有把音频输出列为通过条件，本轮已纠正。决策及验证见 [播放跳转兼容](../decisions/playback-issued-redirect.md)。

上一轮 134251 候选构建零警告零错误，345 项单元测试全部通过。修复了直接 Window.Close 绕过清理导致 Video Lab 计时器访问已销毁 XAML 的原生崩溃，并修复主窗口自绘关闭按钮的同类路径。Debug/AOT 各 10 次 IPC 正常启停、P3 真实播放和 Fake 两入口通过。该轮 Debug/AOT/安装目录完整界面回归通过：实际关闭按钮取消/重入/确认/清理四项、生产库停止失效到三页面实际进度绑定、五项原生无障碍检查及 50 次假播放关闭零留存均通过。安装轮 `artifacts/installer-validation/b86c1004e97e4039a3b5cf3ba337e479/result.json` 完整 Passed：安装/升级清单核验、升级进程正常退出、卸载清除程序与注册项、356 个原用户文件及卸载前 357 文件含本轮 marker 的完整保留均通过。本次未改安装脚本，未重复安装/卸载。

仍未通过或未覆盖：修复前候选窗口 Loaded 881/865/774 ms、实际缓存首页 972/919/827 ms，600/800 ms 目标均未达到，本次未作启动优化或重测；GPU 呈现帧率、物理键鼠/讲述人实际朗读、P0 硬件 DXGI 资源及显示环境遗留。早前候选曾测得缓存首页 620/728/699 ms，但不以较快轮次替代最终测量。专用 Windows VM 已取消；其文件删除被工具自动审批拒绝，仍未成功清理，详见 [P8 打包记录](../decisions/P8-packaging.md)。

| 契约服务 | 状态（未开始 / 假 / 真 / 已测试） | 已知限制 | 更新日期 |
|---|---|---|---|
| `ISessionService` | 真 | 登录、恢复、过期及注销通过合成测试；用户确认真实 Emby 登录、重启恢复及 Windows 凭据检查通过 | 2026-10-02 |
| `ILibraryService` | 真 | 真实端点、库过滤、分页/筛选/搜索/详情/NextUp、账号隔离、SWR/磁盘首屏/停止失效已测试；用户确认真实视频库列表通过，其余端点暂以合成测试验证 | 2026-10-02 |
| `IImageService` | 已测试 | 缓存/优先级/合并/取消/重定向/缺图已测试；前端解码、RemoteImage、换账号代际隔离及损坏图片回退已接入 | 2026-10-03 |
| `ISettingsService` | 已测试 | 原子更新/备份恢复/主题/缓存统计/日志目录已测试；外部 MPV 显式批准、指纹复验、变化后撤销、真实 exe 自动验证和设置页均已接入，人工 Emby 后台观察未补做 | 2026-10-03 |
| `ILibraryPreferences` | 真 | 文件持久化、账号/库隔离、通知和重启恢复已测试 | 2026-10-02 |
| `IPlaybackService` / `IPlaybackSession` | 已测试 | 准备/候选/连播/重试/上报/转码清理已测试，真实 libmpv Debug / AOT 组合冒烟通过；用户确认 P3 真实服务器验收通过 | 2026-10-02 |
| `VideoSurface`（`Attach` / `Detach`） | 真 | 公开入口支持 Demo 和真实 composition，会话重试后重绑、HDR/尺寸、销毁前 UI 解绑均已接入；实际 Debug / AOT 冒烟通过，人工显示检查沿用 P0 遗留项 | 2026-10-02 |

## P1a 契约与假服务

2026-10-02：契约草案、全部假服务与 `AddBackendServices(fake: true)` 已实现。`--fake` / `MAMBO_FAKE=1` 当前在 Debug/VideoLab 启动假数据演示。构建零警告零错误、61 项测试通过；Debug / AOT 的两种入口均通过分页、生成图片解码、12 集切集、画面挂接与关闭冒烟，且未加载 libmpv。

**契约 v1 冻结**：评审有条件通过所要求的 R-004–R-009 已修正并通过回归，R-010–R-014 增量也已完成；requests 无未处理的阻塞请求，PLAN 的 P1a 已勾选。API、取消与生命周期、运行命令及 XAML 限制见 [P1a 契约 v1](../decisions/P1a-contracts.md)。前端通过 Contracts 接口开发，不依赖实现类或 DTO。

## P1 平台实现

真实平台层及 AddBackendServices(fake: false) 已接入，174 项 Core 测试通过。Debug / Native AOT 组合工具验证合成登录、库列表、缓存、设置、音量、发件箱、注销、日志与令牌不落盘。账号退出和清缓存使用删除屏障，旧请求/写入不会覆盖新代状态。实现、命令与限制见 [P1 Core 平台层](../decisions/P1-core-platform.md)。

**P1 人工验收通过**（2026-10-02）：用户在真实登录、列库、重启恢复及凭据检查步骤后回复“好了”，PLAN 的 P1 已勾选。验收工具为 publish/core-smoke/Mambo.CoreSmoke.exe --login，退出后运行 --restore；凭据目标为 Mambo:emby-session:v1，敏感输入仅在本机。自动测试仍只用内存凭据，不读取或修改用户的 Windows 凭据。本次确认不扩展到 P0 遗留项或 P3 真实播放验收。

R-003 外壳接入：启动解析 AppShutdownCoordinator（安装 WinUI 异常记录），再调用一次 RestoreAsync；关闭时 await coordinator.CloseAsync 后再销毁 DI，并在 Window.Closing 返回后排队调用最终 Window.Close（R-016）。真实服务均可接入；假模式仍可用于页面开发。

## P3 播放实现

2026-10-02：真实播放准备、候选回退、外部字幕、转码清理、actor 会话、真实倍速上报、StopOutbox 和缓存失效已接入。LibMpvEngine 使用真实 entryId、原生确认及类型化事件，新增六项无头原生测试；VideoSurface 内部桥接负责交换链、HDR、尺寸与重试后重绑，Contracts v1 无破坏性改动。实现及验收命令见 [P3 播放引擎与会话](../decisions/P3-playback.md)。

全套 237/237 测试通过，无失败或跳过（Core 231、Player 6）；Debug 构建 / Native AOT 发布零警告零错误，本地 composition 播放冒烟均正常退出，并验证切集/重试后的三次停止保持 1.5 倍速。2026-10-02 用户回复“验收通过，继续”，确认 P3 真实服务器的位置/倍速、强制转码与停止清理、整季连播及断网补发通过，P3 已勾选。Video Lab 可“恢复 Emby 会话”后按 itemId 播放，人工输入不落盘。R-015 的测试发现问题同批修复；最终外壳关闭要求见 R-016。

## P7 外部播放器后端与设置增量

2026-10-02：ExternalMpvEngine 已接入真实播放组合根，文件选择后的显式版本探测（3 秒、最低 0.38.0）、指纹批准/只读复验、JSON IPC 超时、类型化事件及本次进程的有限清理均已实现。文件变化会撤销批准、切回内置并通知设置页；再次启用须用户重新验证。只有明确选择文件时才执行版本探测，普通启动/复验不执行未知 exe；假模式保持 Demo。

326/326 测试通过，构建与 Native AOT 发布零警告零错误；Debug/AOT 假管道冒烟实际执行服务端 PID 的 LibraryImport 及 JSON 编解码，本地真实 libmpv composition 的 P3 回归仍通过。新增手动选集回归同时修复 Replace 后误删当前条目的 P3 问题。P7 前端面板和真实 exe 的进程终止/替换人工验收尚待完成，详见 [P7 外部 MPV](../decisions/P7-external-player.md) 和 R-019。

R-017 后端设置增量已完成：AppSettings.ThemeMode 的类型为 SettingsThemeMode，旧文件默认 System；GetCacheSizeAsync 返回缓存字节数；LogDirectory 返回日志路径，假模式为空。版本仍按原请求放在 P8。R-018 已完成：假模式组合根默认读取两项环境参数，显式 options 优先。Contracts v1 只有兼容增量，前端无须引用实现类型。

## P0 骨架与视频验证

2026-10-02：WinUI 3 / .NET 10 四项目骨架、libmpv 下载锁、Video Lab、HDR / DPI / 交换链互操作、认证 URL 预解析及 AOT 配置已实现。`dotnet build -p:Platform=x64` 零警告零错误，`dotnet test` 13 项通过；Debug 与 AOT 的 4K HEVC `d3d11va` 播放、缓冲区尺寸同步、最大化 / 全屏及 AOT DLL 缺失中文错误已验证。

**用户批准携遗留项进入 P1**（2026-10-02：“提交，进入P1”）：硬件渲染每次创建 / 销毁约增加一个 Section 和一个 Mutant；不加载 WinUI / libmpv、直接调用 DXGI composition 的独立工具也复现，详见 `scripts/diagnostics/Mambo.CompositionProbe`。用户已确认 HDR 高光及播放中关闭 Windows HDR 切回 SDR 通过。DPI / 多显示器、按钮 / 光标 / 闪烁和真实 Emby 抓包仍需人工验收。详见 [P0 视频技术验证](../decisions/P0-video-spike.md)。

复测：`pwsh scripts/fetch-libmpv.ps1`、`pwsh scripts/fetch-video-sample.ps1`，构建后执行 `pwsh scripts/test-video-lab.ps1`。AOT 先用 `dotnet publish src/Mambo.App -p:Platform=x64 -p:PublishProfile=Aot -o publish/aot`，再执行 `pwsh scripts/test-video-lab.ps1 -Aot`；加 `-RequireStableResources` 才将资源检查作为脚本失败条件，当前会失败。

## 契约变更记录

2026-10-02：初始草案评审有条件通过，R-004–R-009 完成后冻结 v1。破坏性变更在冻结前收敛：LogoutResult 返回值、结束原因/关闭重载、Preview 替换重载；同批完成稳定错误码、图片回退、异步通知、预取、分页刷新状态和原子设置更新。

2026-10-02 P7：新增 SettingsThemeMode/ThemeMode、ExternalMpvApproval，以及 ISettingsService 的缓存统计/日志目录默认成员；外部批准仍使用既有 ValidateExternalPlayerAsync。旧设置及旧接口实现兼容，指纹记录由后端维护。
