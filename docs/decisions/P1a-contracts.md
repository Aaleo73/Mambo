# P1a 契约草案与假实现

日期：2026-10-02。状态：**草案已实现、待 Claude 评审；尚未冻结为 v1**。

用户已批准 P0 提交并进入 P1。按 PLAN §14.3，先交付契约和全套假服务，供前端并行开发。真实 Emby 平台层仍属后续 P1 工作，原生播放会话桥接属 P3；本记录不代表这些阶段已完成。

## 前端接入边界

- 业务类型集中在 `src/Mambo.Core/Contracts/`，命名空间为 `Mambo.Core.Contracts`。领域数据使用不可变 record 和 `ImmutableArray`，不暴露 DTO、播放 URL、令牌或原生句柄。
- 服务在 `BackendServices.AddBackendServices(fake: true, scheduler: …)` 注册。前端通过 Contracts 接口解析服务，不构造假实现。容器使用显式工厂，避免 AOT 反射激活。
- App 的 `Debug/VideoLab` 已支持 `--fake` 和 `MAMBO_FAKE=1`，启动 `FakeLab`。Program / App / MainWindow 仍由 Claude 拥有，最终外壳入口的 DI 接入见 R-003。
- `VideoSurface.Attach(IPlaybackSession)` / `Detach()` 必须在 UI 线程调用。当前公开入口支持 `EngineKind.Demo`，使用 Composition 纯色画面；真实会话的内部桥接在 P3 接入。前端不调用 P0 的内部交换链入口。
- 不把 Contracts record 直接声明为 XAML 的 `x:DataType`：当前 WinUI 编译器会为 `init` 属性生成普通 setter，导致 CS8852。使用 getter-only 的 partial ViewModel 或投影对象，再通过 `{x:Bind}` 绑定。可参考 `Debug/DemoRows.cs`；其原始 `MediaItem` 保持 internal，避免进入生成的 XAML 类型元数据。

## 草案 API 与语义

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

## 假数据与生命周期

- 目录确定性生成三个资料库：5000 部电影、3 部剧集（各 3 季、每季 12 集）、24 段短片；包含原创标题、演职人员、图片标识及播放进度。继续观看按剧集去重，搜索进行 Unicode 规范化并将单集折叠为剧集。
- 图片由托管代码生成 BMP 渐变与原创点阵标题，无外部图片或旧项目资产。海报宽度上限 960、横图 2560；未知中文用 Unicode 编码显示。图片优先级、磁盘缓存与真实网络调度留待 P1。
- 假播放模拟打开、播放时钟、缓冲、失败重试、跳集、12 集连播及季末关闭；使用 `TimeProvider`，测试无需真实等待。关闭幂等，停止开播和计时并退订，压制迟到通知。
- 假会话启动时已有演示账号；设置、连接默认值和资料库偏好只保存在内存，均未接入 Windows 凭据管理器或真实外部播放器。日期排序在演示数据中使用确定性的首映日期作为添加日期的替身。
- `AppShutdownCoordinator` 当前负责关闭假播放并重置 messenger；真实上报、发件箱、设置及缓存落盘将在 P1 / P3 扩展。
- 默认延迟 120 ms、错误率 0、固定随机种子。调试入口可通过 `MAMBO_FAKE_DELAY_MS`（0–10000）和 `MAMBO_FAKE_FAILURE_RATE`（0–1）模拟加载和失败；构造假服务也可传入 `FakeOptions` 与受控时钟。

## 验证

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

R-002 请求 Claude 合并 backend 后评审契约字段、查询取消语义、播放替换与关闭行为、前端页所需数据，以及只读 XAML 投影方式。评审通过后双方冻结 v1，才能勾选 PLAN 的 P1a 进度。R-003 记录最终外壳启动及 DI 接入。

后续 P1 真实平台层包括地址/认证、凭据与设置持久化、Emby API/源生成 JSON、请求调度/查询缓存/持久化、图片缓存、脱敏日志及停止发件箱；真实登录与重启恢复仍需用户参与验收。
