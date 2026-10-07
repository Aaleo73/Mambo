# P1a 契约 v1 与假实现

日期：2026-10-02。状态：**契约 v1 冻结**，评审提出的阻塞修订已全部完成并通过测试。

服务契约的总览见 `docs/ARCHITECTURE.md` §7；假服务支持独立运行界面。真实实现见 [P1 Core 平台层](P1-core-platform.md) 和 [P3 播放引擎与会话](P3-playback.md)。

## 界面接入边界

- 业务类型集中在 `src/Mambo.Core/Contracts/`，命名空间为 `Mambo.Core.Contracts`。领域数据使用不可变 record 和 `ImmutableArray`，不暴露 DTO、播放 URL、令牌或原生句柄。
- 服务在 `BackendServices.AddBackendServices(fake: true, scheduler: …)` 注册。界面通过 Contracts 接口解析服务，不构造假实现。容器使用显式工厂，避免 AOT 反射激活。
- App 的 `Debug/VideoLab` 已支持 `--fake` 和 `MAMBO_FAKE=1`，启动 `FakeLab`。
- `VideoSurface.Attach(IPlaybackSession)` / `Detach()` 必须在 UI 线程调用。公开入口支持 `EngineKind.Demo` 的 Composition 纯色和真实 libmpv 会话。界面不调用内部交换链入口。
- 不把 Contracts record 直接声明为 XAML 的 `x:DataType`：当前 WinUI 编译器会为 `init` 属性生成普通 setter，导致 CS8852。使用 getter-only 的 partial ViewModel 或投影对象，再通过 `{x:Bind}` 绑定。可参考 `Debug/DemoRows.cs`；其原始 `MediaItem` 保持 internal，避免进入生成的 XAML 类型元数据。

## 契约 v1 API 与语义

| 类型 | 用法与约定 |
|---|---|
| `ISessionService` | `Current` 只暴露账号身份；状态为 Restoring / LoggedOut / LoggedIn / Unreachable；登录、恢复、退出及 `Changed`。登录输入短时使用，`ToString()` 隐藏内容。 |
| `ILibraryService` | Observe 媒体库、hero、继续观看、最新、最近播放、资料库、筛选项、详情、NextUp、季、剧集、搜索分组与各库搜索；创建观察即开始加载。 |
| `IQuery<T>` | `IsInitialized` 区分未加载与空结果；刷新保留旧值，失败写入 `Error`；`Updated` 通知 UI 读取最新快照。 |
| `IPagedQuery<T>` | `Items` 是已加载页的不可变快照；`TotalCount` 可未知；刷新成功才替换第一页；分页失败保留旧页并可重试；页大小 1–500。 |
| `IImageService` | 按账号作用域内的 `ImageRef`、像素宽度和优先级取压缩图片字节；可取消，界面自行按尺寸解码。 |
| `ISettingsService` / `ILibraryPreferences` | 不可变设置及各库排序、筛选偏好；连接默认值不含密码或令牌，诊断文本隐藏输入。 |
| `IPlaybackService` | `PlayAsync(PlayRequest)`、`Current`、开始/结束/跳集事件；切换正在播放的项目需要界面先确认，再设 `ReplaceCurrent=true`；`PreviewAsync` 启动演示。 |
| `IPlaybackSession` | 不可变 `SessionSnapshot`，包含 phase、进度、暂停、缓冲、倍速、音量、轨道和连播计划；异步命令覆盖暂停、跳转、选轨、选集、逐帧、重试与关闭。 |
| `IUiScheduler` / 消息 | 所有公开事件、查询通知及 `PlaybackStopped` / `SessionChanged` / `SessionExpired` 经 UI scheduler；`TryEnqueue=false` 表示 UI 正在关闭。消息使用 CommunityToolkit `IMessenger`。 |

观察的 `scopeToken` 归属于页面或账号生命周期，`Dispose()` 结束当前观察。并发刷新和分页复用正在执行的请求；调用者取消只结束自己的等待，底层读取由观察 scope 或 Dispose 取消。分页刷新会使旧页请求失效，迟到结果和通知不会重新写入已释放的观察。

读取失败放入 `Error`，保留已有数据；命令失败抛出 `AppException`，携带安全的中文 `AppError`。主动取消抛 `OperationCanceledException`，不覆盖读取错误。事件接收方在回调中读取最新状态，不依赖每次中间状态均产生一条通知。

播放入口仅在请求被拒绝时抛异常（参数、未登录、正在启动、替换未确认）。解析与打开失败进入返回会话的 Failed/Error，可 RetryAsync。同一项目复用当前会话并忽略 StartTicks；Preview 默认也要求替换确认。替换顺序是旧 SessionEnded(Replaced) → 新 SessionStarted，期间 IsStarting=true、Current 可为空。结束原因另含 UserClosed/SeasonEnded/Failed/Logout/AppShutdown。只有已确认开播并产生停止上报才发 PlaybackStopped。

LogoutAsync 返回 LogoutResult，先关闭播放并用旧身份处理停止记录，再清本地；远端失败与本地断开分开反馈。所有通知始终异步，UI 创建观察后可以先读状态、再订阅。账号切换或 scopeToken 取消后旧观察终止，仍需 Dispose；有缓存时创建观察即可已初始化。图片同 Kind 先自身再父级，缺图不可重试；PlaybackEntry 新增剧名、集名、UserData 和 Image。设置支持 Func 原子更新，DeviceId 不可修改；真实组合根保存会话音量，外壳启动调用一次 RestoreAsync。

VideoSurface 的诊断成员为 internal。外壳用 WindowResizeHook（或等效 WM_ENTERSIZEMOVE/WM_EXITSIZEMOVE）调用 SetLiveResize；控制层隐藏时调用 HideCursor。HDR 目标参数由 VideoSurface 内部处理，界面不参与显示器探测或 mpv 参数设置。

## 假数据与生命周期

- 目录确定性生成三个资料库：5000 部电影、3 部剧集（各 3 季、每季 12 集）、24 段短片；包含原创标题、演职人员、图片标识及播放进度。继续观看按剧集去重，搜索进行 Unicode 规范化并将单集折叠为剧集。
- 图片由托管代码生成 BMP 渐变与原创点阵标题，无外部图片或旧项目资产。海报宽度上限 960、横图 2560；未知中文用 Unicode 编码显示。真实图片优先级、磁盘缓存与网络调度见 P1 平台记录。
- 假播放模拟打开、播放时钟、缓冲、失败重试、跳集、12 集连播及季末关闭；使用 `TimeProvider`，测试无需真实等待。关闭幂等，停止开播和计时并退订，压制迟到通知。
- 假会话启动时已有演示账号；设置、连接默认值和资料库偏好只保存在内存，均未接入 Windows 凭据管理器或真实外部播放器。日期排序使用生成的 DateCreatedUtc，不依赖首映日期代替添加日期。
- `AppShutdownCoordinator` 负责关闭播放并重置 messenger；P1 已扩展发件箱、设置、查询快照和异步 I/O 清理，P3 已接入真实播放状态上报。
- 默认延迟 120 ms、错误率 0、固定随机种子。调试入口可通过 `MAMBO_FAKE_DELAY_MS`（0–10000）和 `MAMBO_FAKE_FAILURE_RATE`（0–1）模拟加载和失败；构造假服务也可传入 `FakeOptions` 与受控时钟。

## 外壳接入要求

- 启动时解析 `AppShutdownCoordinator`（同时安装 WinUI 异常记录），再调用一次 `ISessionService.RestoreAsync`。
- 退出时先 `await coordinator.CloseAsync`，再销毁服务容器。
- 在 `Window.Closing` 中取消本次关闭并等待 `CloseAsync`；最终的 `Window.Close` 要在 Closing 回调返回之后排队调用，并设防重入标志。`CloseAsync` 可能同步完成，直接在回调里再次 Close 曾让 Debug / AOT 的 composition 回归以 `C000027B` 退出。
- 假模式的延迟和失败率由组合根读取 `MAMBO_FAKE_DELAY_MS`、`MAMBO_FAKE_FAILURE_RATE`，界面不引用 `Mambo.Core.Fakes.FakeOptions`。
- 复验假模式：`pwsh scripts/test-fake-lab.ps1`（可加 `-EnvironmentMode`、`-Aot`）。

## 契约变更记录

冻结之后只做兼容增量；破坏性修改必须同步更新所有调用方与测试。

- **2026-10-02 冻结前**：`LogoutAsync` 返回 `LogoutResult`；会话结束带原因（UserClosed / Replaced / SeasonEnded / Failed / Logout / AppShutdown）；`PreviewAsync` 增加替换确认重载。同批完成稳定错误码 `ErrorCodes`、图片回退顺序、通知一律异步、`PrefetchDetail`、分页的 `IsRefreshing`、设置的原子更新。
- **2026-10-02 外部播放器与设置**：新增 `SettingsThemeMode` / `ThemeMode`、`ExternalMpvApproval`，`ISettingsService` 增加缓存统计 `GetCacheSizeAsync` 和 `LogDirectory`（假模式为空）。旧设置文件默认 System。
- **2026-10-03 选集布局**：`AppSettings` 增加 `UseEpisodeGrid`、`EpisodePanelCollapsed`，缺失字段由 `SettingsStore` 显式迁移；`VideoSurface` 增加公开的 `SetViewportClip`。
- **2026-10-05 弹幕**：新增 `IBulletChatService` 与 `BulletChat*` 模型；`AppSettings` 增加 `BulletChat`，`PlaybackEntry` 增加 `ProductionYear`。随后 `BulletChatSettings` 增加 `DefaultsVersion`，`Area` 默认值改为 0.25，仍是旧默认值 0.85 的已存设置在加载时迁移一次。
- **2026-10-05 画质模式**：新增 `VideoQualityMode`、`IPlaybackSession.SetVideoQualityModeAsync`（默认实现返回不支持），`SessionSnapshot` 增加 `VideoQualityMode`、`IsVideoQualityChanging`、`VideoQualityError`。
- **2026-10-06 组件更新**：`IAppUpdateService` 增加组件清单、准备更新和启动更新器的成员。
