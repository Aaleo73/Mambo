# P1a 契约 v1 与假实现

日期：2026-10-02。状态：**契约 v1 冻结**。用户转达 Claude 已审阅；条件通过所要求的 R-004–R-009 已实现并通过测试，R-010–R-014 增量同时完成。

用户已批准 P0 提交并进入 P1。按 PLAN §14.3，先交付契约和全套假服务，供前端并行开发。真实平台实现与本地验收工具见 [P1 Core 平台层](P1-core-platform.md)，P1 真实 Emby 登录、列库、重启恢复及凭据检查已由用户确认通过；原生播放会话桥接属 P3。

## 前端接入边界

- 业务类型集中在 `src/Mambo.Core/Contracts/`，命名空间为 `Mambo.Core.Contracts`。领域数据使用不可变 record 和 `ImmutableArray`，不暴露 DTO、播放 URL、令牌或原生句柄。
- 服务在 `BackendServices.AddBackendServices(fake: true, scheduler: …)` 注册。前端通过 Contracts 接口解析服务，不构造假实现。容器使用显式工厂，避免 AOT 反射激活。
- App 的 `Debug/VideoLab` 已支持 `--fake` 和 `MAMBO_FAKE=1`，启动 `FakeLab`。Program / App / MainWindow 仍由 Claude 拥有，最终外壳入口的 DI 接入见 R-003。
- `VideoSurface.Attach(IPlaybackSession)` / `Detach()` 必须在 UI 线程调用。当前公开入口支持 `EngineKind.Demo`，使用 Composition 纯色画面；真实会话的内部桥接在 P3 接入。前端不调用 P0 的内部交换链入口。
- 不把 Contracts record 直接声明为 XAML 的 `x:DataType`：当前 WinUI 编译器会为 `init` 属性生成普通 setter，导致 CS8852。使用 getter-only 的 partial ViewModel 或投影对象，再通过 `{x:Bind}` 绑定。可参考 `Debug/DemoRows.cs`；其原始 `MediaItem` 保持 internal，避免进入生成的 XAML 类型元数据。

## 契约 v1 API 与语义

| 类型 | 用法与约定 |
|---|---|
| `ISessionService` | `Current` 只暴露账号身份；状态为 Restoring / LoggedOut / LoggedIn / Unreachable；登录、恢复、退出及 `Changed`。登录输入短时使用，`ToString()` 隐藏内容。 |
| `ILibraryService` | Observe 媒体库、hero、继续观看、最新、最近播放、资料库、筛选项、详情、NextUp、季、剧集、搜索分组与各库搜索；创建观察即开始加载。 |
| `IQuery<T>` | `IsInitialized` 区分未加载与空结果；刷新保留旧值，失败写入 `Error`；`Updated` 通知 UI 读取最新快照。 |
| `IPagedQuery<T>` | `Items` 是已加载页的不可变快照；`TotalCount` 可未知；刷新成功才替换第一页；分页失败保留旧页并可重试；页大小 1–500。 |
| `IImageService` | 按账号作用域内的 `ImageRef`、像素宽度和优先级取压缩图片字节；可取消，前端自行按尺寸解码。 |
| `ISettingsService` / `ILibraryPreferences` | 不可变设置及各库排序、筛选偏好；连接默认值不含密码或令牌，诊断文本隐藏输入。 |
| `IPlaybackService` | `PlayAsync(PlayRequest)`、`Current`、开始/结束/跳集事件；切换正在播放的项目需要前端先确认，再设 `ReplaceCurrent=true`；`PreviewAsync` 启动演示。 |
| `IPlaybackSession` | 不可变 `SessionSnapshot`，包含 phase、进度、暂停、缓冲、倍速、音量、轨道和连播计划；异步命令覆盖暂停、跳转、选轨、选集、逐帧、重试与关闭。 |
| `IUiScheduler` / 消息 | 所有公开事件、查询通知及 `PlaybackStopped` / `SessionChanged` / `SessionExpired` 经 UI scheduler；`TryEnqueue=false` 表示 UI 正在关闭。消息使用 CommunityToolkit `IMessenger`。 |

观察的 `scopeToken` 归属于页面或账号生命周期，`Dispose()` 结束当前观察。并发刷新和分页复用正在执行的请求；调用者取消只结束自己的等待，底层读取由观察 scope 或 Dispose 取消。分页刷新会使旧页请求失效，迟到结果和通知不会重新写入已释放的观察。

读取失败放入 `Error`，保留已有数据；命令失败抛出 `AppException`，携带安全的中文 `AppError`。主动取消抛 `OperationCanceledException`，不覆盖读取错误。事件接收方在回调中读取最新状态，不依赖每次中间状态均产生一条通知。

播放入口仅在请求被拒绝时抛异常（参数、未登录、正在启动、替换未确认）。解析与打开失败进入返回会话的 Failed/Error，可 RetryAsync。同一项目复用当前会话并忽略 StartTicks；Preview 默认也要求替换确认。替换顺序是旧 SessionEnded(Replaced) → 新 SessionStarted，期间 IsStarting=true、Current 可为空。结束原因另含 UserClosed/SeasonEnded/Failed/Logout/AppShutdown。只有已确认开播并产生停止上报才发 PlaybackStopped。

LogoutAsync 返回 LogoutResult，先关闭播放并用旧身份处理停止记录，再清本地；远端失败与本地断开分开反馈。所有通知始终异步，UI 创建观察后可以先读状态、再订阅。账号切换或 scopeToken 取消后旧观察终止，仍需 Dispose；有缓存时创建观察即可已初始化。图片同 Kind 先自身再父级，缺图不可重试；PlaybackEntry 新增剧名、集名、UserData 和 Image。设置支持 Func 原子更新，DeviceId 不可修改；真实组合根保存会话音量，外壳启动调用一次 RestoreAsync。

VideoSurface 的诊断成员为 internal。外壳用 WindowResizeHook（或等效 WM_ENTERSIZEMOVE/WM_EXITSIZEMOVE）调用 SetLiveResize；控制层隐藏时调用 HideCursor。HDR 目标参数由 VideoSurface 内部处理，前端不参与显示器探测或 mpv 参数设置；真实会话桥接随 P3 接入。

## 假数据与生命周期

- 目录确定性生成三个资料库：5000 部电影、3 部剧集（各 3 季、每季 12 集）、24 段短片；包含原创标题、演职人员、图片标识及播放进度。继续观看按剧集去重，搜索进行 Unicode 规范化并将单集折叠为剧集。
- 图片由托管代码生成 BMP 渐变与原创点阵标题，无外部图片或旧项目资产。海报宽度上限 960、横图 2560；未知中文用 Unicode 编码显示。真实图片优先级、磁盘缓存与网络调度见 P1 平台记录。
- 假播放模拟打开、播放时钟、缓冲、失败重试、跳集、12 集连播及季末关闭；使用 `TimeProvider`，测试无需真实等待。关闭幂等，停止开播和计时并退订，压制迟到通知。
- 假会话启动时已有演示账号；设置、连接默认值和资料库偏好只保存在内存，均未接入 Windows 凭据管理器或真实外部播放器。日期排序使用生成的 DateCreatedUtc，不依赖首映日期代替添加日期。
- `AppShutdownCoordinator` 负责关闭播放并重置 messenger；P1 已扩展发件箱、设置、查询快照和异步 I/O 清理，真实播放状态上报随 P3 接入。
- 默认延迟 120 ms、错误率 0、固定随机种子。调试入口可通过 `MAMBO_FAKE_DELAY_MS`（0–10000）和 `MAMBO_FAKE_FAILURE_RATE`（0–1）模拟加载和失败；构造假服务也可传入 `FakeOptions` 与受控时钟。

## 初始验证（修订前）

2026-10-02：`dotnet build -p:Platform=x64` 零警告、零错误；`dotnet test` 61 项通过（13 项 P0、48 项 P1a）。新增测试覆盖分页合并、取消与刷新竞态、错误保留、筛选和搜索、会话与设置生命周期、UI 通知退订、播放时钟、频繁音量更新、关闭与队列关闭、生成图片格式和确定性。

Debug 和 Native AOT 均分别验证命令行与环境变量入口。四份报告均通过：第一页 60 项、追加后 120 / 5000 项，生成 BMP 被实际 WinUI `BitmapImage` 解码，12 集预览可切到第二集，Composition 画面挂接成功，关闭后 `Current` 为空，进程未加载 libmpv。报告和构建日志位于忽略的 `artifacts/`，不提交二进制结果。

另以 AOT 运行一次原生 Video Lab 创建 / 播放 / 关闭回归，画面管线通过，记录为 `artifacts/p1a-native-video-regression.json`。单次回归不改变 P0 的 20 次硬件 composition 句柄增长结论，原 P0 报告已保留。

```powershell
dotnet run --project src/Mambo.App -p:Platform=x64 -- --fake
pwsh scripts/test-fake-lab.ps1
pwsh scripts/test-fake-lab.ps1 -EnvironmentMode
dotnet publish src/Mambo.App -p:Platform=x64 -p:PublishProfile=Aot -o publish/aot
pwsh scripts/test-fake-lab.ps1 -Aot
pwsh scripts/test-fake-lab.ps1 -Aot -EnvironmentMode
```

## 评审与后续

R-002 条件通过的六项阻塞修订已完成，契约 v1 冻结，PLAN 的 P1a 已勾选。R-003 仍由 Claude 在 P4 接入最终外壳与 DI：启动时解析 AppShutdownCoordinator 并调用一次 RestoreAsync，退出先 await CloseAsync 再销毁服务容器。

P1 真实平台层已实现，自动验证见 P1 平台记录；真实登录、列库、重启恢复及凭据检查已于 2026-10-02 经用户确认通过。原生播放、媒体源和真实播放状态上报在 P3 完成。
