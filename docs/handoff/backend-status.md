# 后端状态（Codex 维护）

前端根据这张表决定何时从假实现切换到真实服务。契约定义见 `docs/PLAN.md` §14.3。

| 契约服务 | 状态（未开始 / 假 / 真 / 已测试） | 已知限制 | 更新日期 |
|---|---|---|---|
| `ISessionService` | 真 | 登录、恢复、过期及注销通过合成测试；用户确认真实 Emby 登录、重启恢复及 Windows 凭据检查通过 | 2026-10-02 |
| `ILibraryService` | 真 | 真实端点、库过滤、分页/筛选/搜索/详情/NextUp、账号隔离、SWR/磁盘首屏/停止失效已测试；用户确认真实视频库列表通过，其余端点暂以合成测试验证 | 2026-10-02 |
| `IImageService` | 真 | 缓存/优先级/合并/取消/重定向/缺图已测试；前端解码与 RemoteImage 控件由 Claude 接入 | 2026-10-02 |
| `ISettingsService` | 真 | 原子更新/备份恢复/只读 DeviceId/清理错误已测试，音量经组合工具恢复；外部播放器验证在 P7 | 2026-10-02 |
| `ILibraryPreferences` | 真 | 文件持久化、账号/库隔离、通知和重启恢复已测试 | 2026-10-02 |
| `IPlaybackService` / `IPlaybackSession` | 假 | 演示及修订的失败/重试/替换/注销已测试；真实请求返回 Preparing→Failed，引擎接入待 P3 | 2026-10-02 |
| `VideoSurface`（`Attach` / `Detach`） | 假 | 公开 `Attach(IPlaybackSession)` 已支持 Demo Composition 纯色并通过 Debug / AOT；真实会话桥接待 P3，P0 内部交换链入口仅供探针 | 2026-10-02 |

## P1a 契约与假服务

2026-10-02：契约草案、全部假服务与 `AddBackendServices(fake: true)` 已实现。`--fake` / `MAMBO_FAKE=1` 当前在 Debug/VideoLab 启动假数据演示。构建零警告零错误、61 项测试通过；Debug / AOT 的两种入口均通过分页、生成图片解码、12 集切集、画面挂接与关闭冒烟，且未加载 libmpv。

**契约 v1 冻结**：用户转达 Claude 已审阅，条件通过所要求的 R-004–R-009 已修正并通过回归，R-010–R-014 增量也已完成；requests 无未处理的阻塞请求，PLAN 的 P1a 已勾选。API、取消与生命周期、运行命令及 XAML 限制见 [P1a 契约 v1](../decisions/P1a-contracts.md)。前端通过 Contracts 接口开发，不依赖实现类或 DTO。

## P1 平台实现

真实平台层及 AddBackendServices(fake: false) 已接入，174 项 Core 测试通过。Debug / Native AOT 组合工具验证合成登录、库列表、缓存、设置、音量、发件箱、注销、日志与令牌不落盘。账号退出和清缓存使用删除屏障，旧请求/写入不会覆盖新代状态。实现、命令与限制见 [P1 Core 平台层](../decisions/P1-core-platform.md)。

**P1 人工验收通过**（2026-10-02）：用户在真实登录、列库、重启恢复及凭据检查步骤后回复“好了”，PLAN 的 P1 已勾选。验收工具为 publish/core-smoke/Mambo.CoreSmoke.exe --login，退出后运行 --restore；凭据目标为 Mambo:emby-session:v1，敏感输入仅在本机。自动测试仍只用内存凭据，不读取或修改用户的 Windows 凭据。本次确认不扩展到 P0 遗留项或 P3 真实播放验收。

R-003 外壳交接：启动解析 AppShutdownCoordinator（安装 WinUI 异常记录），再调用一次 RestoreAsync；关闭时 await coordinator.CloseAsync 后再销毁 DI。真实播放待 P3；前端可继续以假模式开发页面。

## P0 骨架与视频验证

2026-10-02：WinUI 3 / .NET 10 四项目骨架、libmpv 下载锁、Video Lab、HDR / DPI / 交换链互操作、认证 URL 预解析及 AOT 配置已实现。`dotnet build -p:Platform=x64` 零警告零错误，`dotnet test` 13 项通过；Debug 与 AOT 的 4K HEVC `d3d11va` 播放、缓冲区尺寸同步、最大化 / 全屏及 AOT DLL 缺失中文错误已验证。

**用户批准携遗留项进入 P1**（2026-10-02：“提交，进入P1”）：硬件渲染每次创建 / 销毁约增加一个 Section 和一个 Mutant；不加载 WinUI / libmpv、直接调用 DXGI composition 的独立工具也复现，详见 `scripts/diagnostics/Mambo.CompositionProbe`。用户已确认 HDR 高光及播放中关闭 Windows HDR 切回 SDR 通过。DPI / 多显示器、按钮 / 光标 / 闪烁和真实 Emby 抓包仍需人工验收。详见 [P0 视频技术验证](../decisions/P0-video-spike.md)。这些验收结果不变；Program / App / MainWindow 的后续修改现移交 Claude。

复测：`pwsh scripts/fetch-libmpv.ps1`、`pwsh scripts/fetch-video-sample.ps1`，构建后执行 `pwsh scripts/test-video-lab.ps1`。AOT 先用 `dotnet publish src/Mambo.App -p:Platform=x64 -p:PublishProfile=Aot -o publish/aot`，再执行 `pwsh scripts/test-video-lab.ps1 -Aot`；加 `-RequireStableResources` 才将资源检查作为脚本失败条件，当前会失败。

## 契约变更记录

2026-10-02：初始草案经 Claude 有条件通过，R-004–R-009 完成后冻结 v1。破坏性变更在冻结前收敛：LogoutResult 返回值、结束原因/关闭重载、Preview 替换重载；同批完成稳定错误码、图片回退、异步通知、预取、分页刷新状态和原子设置更新。
