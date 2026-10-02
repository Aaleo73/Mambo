# 前后端请求

需要对方修改它负责的路径时，在下表新增一条请求。处理方完成后更新"状态"。分歧无法解决时，交给用户决定。所有权见 `docs/PLAN.md` §14.1。

- **编号**：按顺序递增，格式为 R-001、R-002……
- **内容**：写清背景、期望的接口或行为、涉及的契约类型。
- **阻塞**：填"是"表示提出方在这件事完成前无法继续。
- **状态**：待处理 / 已完成 / 已拒绝（附理由）。

| 编号 | 提出方 | 日期 | 内容 | 阻塞 | 状态 |
|---|---|---|---|---|---|
| R-001 | Codex | 2026-10-02 | 用户已批准 P0 提交并进入 P1。Program / App / MainWindow 现移交 Claude；请按 PLAN §14.4 合并 backend 并验证构建。原生 DXGI 句柄增长及尚未完成的人工验收保留在 `docs/decisions/P0-video-spike.md`，不视为已通过。 | 否 | 已完成（Claude，2026-10-02）：已快进合并到 b09d539；`dotnet build -p:Platform=x64` 0 警告 0 错误，`dotnet test` 61/61 通过。P0 遗留的人工验收项已知悉，不视为已通过。 |
| R-002 | Codex | 2026-10-02 | 请 Claude 合并 backend 后评审 `src/Mambo.Core/Contracts/` 初始草案与全套假实现。重点确认页面所需模型字段、Observe 查询与取消语义、PlayRequest 的替换确认、播放关闭及消息；说明与验证见 `docs/decisions/P1a-contracts.md`。61 项测试及 Debug / AOT 双入口冒烟通过。按 PLAN §14.3，评审通过后双方记录 v1 冻结并勾选 P1a；此请求阻塞契约冻结和 P1 阶段验收。 | 是 | 已完成（Codex，2026-10-02）：用户转达 Claude 已审阅；有条件通过要求的 R-004–R-009 已修正并测试，契约 v1 冻结。R-010–R-014 增量同时完成。 |
| R-003 | Codex | 2026-10-02 | 请 Claude 在最终 App / Program / MainWindow 外壳启动中接入 `BackendServices.IsFakeMode`、`AddBackendServices(fake, IUiScheduler)` 与 UI 服务注册；目前仅 Debug/VideoLab 提供假模式入口，真实服务注册仍待 P1。XAML 使用 getter-only partial ViewModel / 投影和 x:Bind，不直接以带 init 属性的 Contracts record 为 x:DataType，否则 WinUI 生成 setter 会报 CS8852；参考 `Debug/DemoRows.cs`。播放界面只使用 `VideoSurface.Attach(IPlaybackSession)` / `Detach()`。 | 否 | 已接收（Claude）：在 P4 外壳实现时接入，通知线程按 R-009 的结论处理。Codex 补充（2026-10-02）：P1 真实服务注册已在 612ae38 完成，用户已确认登录、列库、重启恢复和凭据检查通过；P4 外壳接入仍待完成，启动/退出顺序见 backend-status。 |
| R-004 | Claude | 2026-10-02 | **稳定错误码**：在 Contracts 新增 `ErrorCodes` 常量类，前端只按这些码做分支，假实现和真实实现用同一组码。现在假实现用的是 `demo.*`，前端无法可靠识别"需要确认替换"等情况。至少包括：替换未确认、正在启动（busy）、会话已关闭、未登录或登录失效、已登录时再次登录、条目不存在、条目不可播放、图片不存在。 | 是 | 已完成（Codex，2026-10-02）：ErrorCodes 常量供假/真实服务共用；请求、认证、条目与图片分支已测试。 |
| R-005 | Claude | 2026-10-02 | **PlayAsync 的失败语义**：<br>• 只有请求本身被拒绝时才抛 `AppException`：替换未确认、正在启动、参数无效、未登录。<br>• 解析、PlaybackInfo、选源、网络等失败，一律以会话 `Phase=Failed` + `Error` 呈现，并可 `RetryAsync`。<br>• `PlayAsync` 应尽快返回 `Phase=Preparing` 的会话，让前端统一走一条显示路径。<br>• `PlayAsync` 被取消时，保证不留下新会话。<br>• 同一项目正在播放时返回现有会话、不重新开始（`StartTicks` 被忽略），这一点需写进契约。<br>• `PreviewAsync` 也要遵守替换确认，例如新增 `PreviewAsync(bool replaceCurrent, CancellationToken)` 重载。它不能像现在这样用 `ReplaceCurrent=true` 静默关掉正在进行的真实播放。 | 是 | 已完成（Codex，2026-10-02）：准备失败返回 Failed 会话并可重试；同项复用、取消不建新会话、Preview 替换确认均已测试。 |
| R-006 | Claude | 2026-10-02 | **结束原因与替换顺序**：<br>• `PlaybackSessionEventArgs` 增加 `EndReason`：UserClosed / Replaced / SeasonEnded / Failed / Logout / AppShutdown。<br>• 写明替换时的事件顺序和中间状态：旧会话 `SessionEnded(Replaced)` → 新会话 `SessionStarted`；期间 `IsStarting=true`，`Current` 可能短暂为 null（假实现就是如此）。<br>• 没有这些信息，播放层在切换时会先收起再展开，也无法区分"意外中断"提示和季末或主动关闭。<br>• 另外，只在产生了停止上报（已确认开播）时才发送 `PlaybackStopped`。现在假会话在打开阶段被关闭也会发送。 | 是 | 已完成（Codex，2026-10-02）：六种结束原因、旧 Ended → 新 Started 顺序及中间状态已写入契约；未确认开播不发 PlaybackStopped。 |
| R-007 | Claude | 2026-10-02 | **注销与播放**：<br>• 写明 `LogoutAsync` 会先关闭当前播放会话，停止上报使用旧令牌；前端负责在调用前弹确认。现在假实现注销时不处理播放。<br>• 返回值改为 `Task<LogoutResult>`，至少包含远端注销是否失败，用于显示「本地已断开，但服务器会话可能仍有效」。<br>• 这是破坏性的签名变化，必须在冻结前定下来。 | 是 | 已完成（Codex，2026-10-02）：LogoutResult 签名冻结；假/真实注销先关播放、以旧身份刷新停止记录再清本地；远端失败结果已测试。 |
| R-008 | Claude | 2026-10-02 | **图片的来源与排序**：<br>• `MediaItem.Images` 要写明排序规则：同一 Kind 内先放自身的图，再放父级或剧集的图。单集应带剧集的 Logo 和父级背景图；横版卡片使用 Thumb。<br>• 前端按"第一个匹配的 Kind"取图，再按附录 A.6 的顺序回退。<br>• 假数据请补上 Logo、Thumb、父级图片和"缺图"条目，以覆盖这些回退路径。现在所有条目只有自身的 Primary 和 Backdrop。 | 是 | 已完成（Codex，2026-10-02）：图片顺序写入模型；假数据含 Thumb/Logo/父级/缺图，真实映射补剧集图片。 |
| R-009 | Claude | 2026-10-02 | **通知必须异步投递**：<br>• 问题：`UiScheduler.TryEnqueue` 在 UI 线程上会内联执行回调，而假服务在持有内部锁时调用 Publish（例如 `ChangeActive`、`SwitchEntry`）。于是 UI 线程调用命令（如 `TogglePauseAsync`、`SelectEntryAsync`）时，`SnapshotChanged`/`Updated` 会在命令返回前、在锁内同步触发。事件处理器如果再调用命令，就会重入一次尚未完成的状态转换。<br>• 请求：`TryEnqueue` 一律异步排队、不内联执行；服务不在持锁时回调。<br>• 请在契约中写明：通知总是异步到达；UI 线程上创建观察后先读状态、再订阅，不会漏掉通知。 | 是 | 已完成（Codex，2026-10-02）：UI scheduler 一律排队；服务回调不持锁，通知 sender/非内联/释放后抑制已测试。 |
| R-010 | Claude | 2026-10-02 | **查询与资料库（增量）**：<br>• 新增悬停预取入口，例如 `ILibraryService.PrefetchDetail(string itemId)`。现在创建观察再 Dispose 会取消底层读取，无法预热。<br>• 写明 `ObserveLibraries` 只返回可播放的视频库（排除 music/photos/books 等），并保持服务器顺序。<br>• 写明 NextUp 返回附录 A.9 规则选出的目标集；剧集没有可播放的集时 `Current=null` 且 `IsInitialized=true`。现在假实现遇到空剧集会因 `episodes[0]` 越界而报成网络错误。<br>• 写明播放停止后，由后端让受影响的活动观察自动刷新，前端不必自己判断依赖关系。<br>• 写明账号切换后，旧观察不会刷新出新账号的数据（终止，或报 `session.changed`）。<br>• 写明 scopeToken 取消后观察的终止状态：后续 Refresh/LoadMore 抛取消，仍需 Dispose 才会退订。<br>• 写明有缓存时，观察可以在创建时就 `IsInitialized=true`，同时在后台刷新。<br>• `IPagedQuery` 区分"刷新第一页"和"追加下一页"（新增 `IsRefreshing` 或加载状态枚举），以便分别显示顶部和底部的加载提示。<br>• 保证所有 `ImmutableArray` 成员都不是 default。 | 否 | 已完成（Codex，2026-10-02）：PrefetchDetail、库过滤及服务器顺序、空 NextUp、停止失效、旧账号终止、缓存首屏及 IsRefreshing 已实现并测试。 |
| R-011 | Claude | 2026-10-02 | **设置与会话（增量）**：<br>• `ISettingsService` 新增原子更新重载 `UpdateAsync(Func<AppSettings, AppSettings>)`，避免播放层保存音量和设置页修改同时发生时互相覆盖。<br>• `DeviceId` 对前端只读：`UpdateAsync` 应忽略或拒绝对它的修改。<br>• 写明会话音量由后端持久化到 `AppSettings.Volume`。<br>• 写明应用启动时由谁触发会话恢复：前端外壳调用一次 `RestoreAsync`，还是后端自动恢复。 | 否 | 已完成（Codex，2026-10-02）：Func 原子更新、DeviceId 拒绝修改；真实组合根持久化音量；外壳启动调用一次 RestoreAsync，详见 P1 平台记录。 |
| R-012 | Claude | 2026-10-02 | **播放与图片模型（增量）**：<br>• `PlaybackEntry` 增加 `SeriesName`、`EpisodeName`，与预格式化的 `Title` 并存，方便新设计的布局。<br>• `PlaybackEntry` 增加 `Played` 或进度，供选集面板显示已看状态。<br>• `PlaybackEntry` 增加 `Image`，供选集面板和片尾"下一集"提示卡使用。<br>• 图片不存在时，`FetchAsync` 抛不可重试的错误（码见 R-004），前端显示占位、不再重试。<br>• 可选：提供 BlurHash 或主色，用作图片占位。 | 否 | 已完成（Codex，2026-10-02）：PlaybackEntry 增补剧/集名、UserData 和 Image；缺图稳定不可重试错误码已测试。 |
| R-013 | Claude | 2026-10-02 | **VideoSurface 的公开 API**：<br>• P0 的诊断成员（`PixelSizeRequested`、`DiagnosticError`、`BufferSize` 等）改为 internal，或标注仅供调试。<br>• 在契约文档写明外壳要做的事：在 `WM_ENTERSIZEMOVE`/`WM_EXITSIZEMOVE` 期间调用 `SetLiveResize`（可用 `Platform/WindowResizeHook`）；控制层隐藏时调用 `HideCursor`。<br>• 写明 HDR 由 VideoSurface 内部处理，前端不需要介入。 | 否 | 已完成（Codex，2026-10-02）：诊断成员 internal；ResizeHook/HideCursor/HDR 后端职责写入契约记录，真实 Attach 桥接随 P3 完成。 |
| R-014 | Claude | 2026-10-02 | **仓库与工具**：<br>• 在新工作区运行 `scripts/fetch-libmpv.ps1` 会覆盖已入库的 `include/mpv/*.h`。在 `core.autocrlf=true` 下，这会产生只有行尾差异的改动（本次已还原）。请加 `.gitattributes` 固定这些文件的行尾，或在内容相同时不覆盖。<br>• `dotnet test -p:Platform=x64 --no-build` 会"运行零个测试"并以退出码 5 结束，而 `dotnet test` 正常。建议在 AGENTS.md 写明测试命令不带平台参数，或让测试项目兼容这种写法。<br>• `src/Mambo.App/packages.lock.json` 只在 Release 下包含 `Microsoft.DotNet.ILCompiler`，因为 `PublishAot` 只在 Release 下为 true。Debug 构建一还原就会删掉这几项，工作区随之变脏（本次已还原，未提交）。建议让 Debug 和 Release 的还原图保持一致，例如 `PublishAot` 不再按配置区分（它只影响 publish），或调整锁文件策略。 | 否 | 已完成（Codex，2026-10-02）：头文件 LF、中央 PublishAot 统一 Debug/Release 还原图；测试目录限制写在后端决策文档，AGENTS 保持用户维护。 |
| R-015 | Claude | 2026-10-02 | **`dotnet test` 不运行测试**：<br>• 现象：在干净的 HEAD（fcb7687，含 backend b09d539）上，`dotnet test` 报告"运行了零个测试"并以退出码 5 结束；删掉 `tests/Mambo.Core.Tests/bin`、`obj` 重建后也一样。直接运行 `tests/Mambo.Core.Tests/bin/x64/Debug/net10.0/Mambo.Core.Tests.exe` 则 61/61 通过。<br>• 推测：`global.json` 指定了 `Microsoft.Testing.Platform` 运行器，但生成的测试程序是 xunit 自带的 in-process runner，不认 MTP 参数（`--list-tests` 报 unknown option）。命令行加 `-p:UseMicrosoftTestingPlatformRunner=true` 没有变化。<br>• 期望：`dotnet test` 能真正运行测试，例如启用 xunit v3 的 MTP 入口，或让 `global.json` 退回 VSTest。修好之前，前端提交前直接运行上面的 exe。R-014 里"`dotnet test` 正常"的说法已不成立。 | 否 | 已完成（Codex，2026-10-02）：中央 Directory.Build.props 显式启用 UseMicrosoftTestingPlatformRunner，xUnit v3 生成 MTP 入口；干净原 P1 基线 174 项、加入持久化取消测试后 175 项真正执行通过，带 Platform 的 --no-build 也通过。P3 全套测试另见 backend-status，不再需要直接运行测试 exe。 |
| R-016 | Codex | 2026-10-02 | **P3 真实播放与关闭接入**：请 P4 / P5 合并 backend 后使用 AddBackendServices(fake: false, scheduler) 的真实 IPlaybackService 和现有 VideoSurface.Attach(IPlaybackSession)；Contracts v1 签名未变，HDR、交换链、尺寸及重试后重绑由后端处理。启动/注销顺序仍按 R-003。Window.Closing 中取消本次关闭，await AppShutdownCoordinator.CloseAsync 后，应在 Closing 回调返回后排队调用最终 Window.Close，并设置防重入标志；CloseAsync 可能同步完成，直接在回调内再次 Close 曾使实际 Debug/AOT composition 回归以 C000027B 退出。Debug/VideoLab 的 CloseAsync 已通过 Task.Yield 修复自身路径。外壳仍需接入 SetLiveResize / HideCursor；实现与验收见 docs/decisions/P3-playback.md。 | 否 | 待处理（Claude 的 P4 / P5 路径） |

## R-002 评审结论（Claude，2026-10-02）

**结论：有条件通过。**
- 契约的整体方向正确，前端可以据此开工。
- R-004–R-009 是语义层面的约定，大多只需改文档或做小改动。处理完后即可冻结 v1。
- R-010–R-014 都是向后兼容的增量，可以在冻结后再追加。

**评审范围与验证**
- 已快进合并 backend（b09d539）。
- 已阅读 `docs/decisions/P1a-contracts.md`、`src/Mambo.Core/Contracts/` 的全部文件，以及 `Fakes/`、`Composition/` 和 `VideoSurface` 的实现。
- `dotnet build -p:Platform=x64`：0 警告、0 错误。
- `dotnet test`：61/61 通过。

**认可的部分**（冻结时保持不变）
- **错误分工**：读取失败写入 `Error`，并保留已有数据；`RefreshAsync`/`LoadMoreAsync` 不会因为读取失败而抛异常。命令失败则抛出带中文 `AppError` 的 `AppException`。
- **取消语义**：调用方的取消只结束自己的等待；底层工作由 scopeToken 或 Dispose 取消；Dispose 之后不再触发 `Updated`。
- **分页语义**：刷新成功后才替换第一页；追加失败时保留已有条目；并发的 LoadMore 共享同一次请求；页大小有校验。
- **数据形态**：使用不可变 record 和 `ImmutableArray`，不暴露 DTO、URL、令牌或原生句柄。`SessionSnapshot` 带 `CapturedAtUtc`，前端可以据此在两次快照之间插值显示进度。
- **播放入口**：`IPlaybackService.IsStarting` 可用来禁用播放按钮；`PlayRequest.ReplaceCurrent` 的确认流程方向正确。
- **XAML 绑定**：不直接绑定 Contracts record，改用只读投影。前端会统一使用 getter-only 的 ViewModel。

**模型字段**
- 对照附录 A，首页、资料库、详情、搜索、设置和播放页需要的字段基本齐全。
- 缺口有三处：图片的来源与排序（R-008），选集面板和"下一集"提示需要的字段（R-012），NextUp 和媒体库范围的语义（R-010）。

**查询取消**
- 主体设计可行。
- 阻塞问题：通知内联执行会造成同步重入（R-009）。
- 待补充：悬停预取目前无法实现；账号切换以及 scopeToken 取消后观察处于什么状态，契约里还没有写（R-010）。

**播放替换与关闭**
- 方向正确，但还不够前端直接使用：
  - 需要稳定的错误码来识别"需要确认替换"（R-004）；
  - `PlayAsync` 什么时候抛异常、什么时候让会话进入 Failed，边界要统一（R-005）；
  - 结束事件需要带上原因，并写明替换时的事件顺序（R-006）；
  - 注销与播放的先后关系、远端注销失败时的提示，需要定下来（R-007）。

**后续安排**
- Codex 处理完 R-004–R-009 后，在 `backend-status.md` 记录"契约 v1 冻结"，再请用户勾选 P1a。
- Claude 在此期间先推进 P2 设计稿，P4 开工前再复核一次契约。
