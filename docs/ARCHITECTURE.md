# Mambo 架构

> 本文由原实施计划的设计章节整理而来，说明各模块的设计意图与约束。§2.1 的目录结构按当前代码核对过；其余章节描述的是设计，细节与实现不一致时，以代码和 `docs/decisions/` 中日期更新的记录为准。
>
> 功能与交互规格见 [SPEC.md](SPEC.md)，进度与未验收项见 [STATUS.md](STATUS.md)。

## 1. 背景与关键决策

- **为什么重写**：旧版 Tauri 2 + React + Rust 的内置播放器把 libmpv 渲染到 `--wid` 子 HWND 里，WebView 画不到它上面（airspace）。只好另开一个透明 WebView 窗口盖在视频上放控件，每 16ms 强行置顶，靠轮询同步位置和状态。结果既不优雅，又带来一串 bug（见附录 C）。
- **为什么不用 Qt**：评估过 Qt 6 Quick。libmpv render API 只支持 OpenGL，走旧的 `vo_gpu` 渲染器，所以：
  - 用不了 gpu-next/libplacebo；
  - 没有 HDR 直通；
  - 硬解受限于 OpenGL 互操作。
  若要把 mpv 的合成交换链放到 QML 下面，就得动 Qt 私有 API，或做没有先例的透明子窗口方案。
- **采用的方案**：mpv 0.41 新增了 `--d3d11-output-mode=composition`：
  - mpv 不创建窗口，只提供一个 DXGI 合成交换链（`display-swapchain` 属性）；
  - 把它挂到 WinUI 3 的 `SwapChainPanel` 上，XAML 控件直接叠在画面上，整个应用只有一个窗口；
  - 画质和单独运行 mpv 一样：gpu-next、HDR、d3d11va 零拷贝都在；
  - 开源播放器 nami 和 Mio 已经这样实现（见 §9）。
- **已确认的产品决定**：
  - 技术栈：WinUI 3 + C#/.NET 10，只支持 Windows；
  - 外部播放器（用户自备 mpv.exe）保留；
  - UI 重新设计，但不与原版大相径庭；视觉基准见 `docs/decisions/P9-visual-parity.md`；
  - 全新开始，不迁移旧版数据（新的 DeviceId，需要重新登录）；
  - Qt 雏形以 `qt-prototype` 标签留档；
  - 只用命令行工具链；
  - 不复用原项目资产（字体除外）。

## 2. 总体架构

### 2.1 解决方案结构

```
Mambo/
├─ global.json · Directory.Build.props · Directory.Packages.props（集中包管理）· Mambo.slnx
├─ third_party/
│  ├─ libmpv/      libmpv.lock.json · include/mpv/*.h（供对照）· bin/（运行时 DLL，由脚本获取，不入库）
│  └─ shaders/     画质模式着色器：upstream/（上游原文）· adapters/（接入修改）· runtime/（生成结果）· shaders.lock.json
├─ scripts/        fetch-libmpv.ps1 · publish.ps1 · build-update-packages.ps1 · prepare-github-release.ps1 等构建发布脚本；
│                  test-*.ps1 为本机界面/播放验收脚本；diagnostics/ 为独立诊断工程（不在解决方案内）
├─ installer/      Mambo.iss（Inno Setup）
├─ LICENSES/       第三方许可原文与原生组件来源记录，随应用分发
├─ docs/           ARCHITECTURE.md · SPEC.md · STATUS.md · decisions/ · releases/
├─ src/
│  ├─ Mambo.Core/    net10.0，不依赖 WinUI
│  │  ├─ Contracts/    界面与内部服务之间的契约：服务接口、领域模型、读取模型、消息、错误码（§7）
│  │  ├─ Networking/   EmbyApi、EmbyDtos、EmbyMapper、EmbyEpisodeReader、AuthHeader、ServerAddress、HttpErrorMapper、RequestScheduler
│  │  ├─ Session/      SessionManager、AccountContext
│  │  ├─ Data/         LibraryService（含筛选）、ObservableQuery、ObservablePagedQuery、QueryCache、QueryPersistence
│  │  ├─ Images/       ImageByteCache（内存 + 磁盘）、ImageFetcher
│  │  ├─ Playback/     PlaybackCoordinator、PlaybackTargetResolver、SeasonPlan、MediaSourceSelector、StreamCandidateBuilder、
│  │  │                StreamUrlResolver、EntryPreparer、PlaybackSession、PlaybackReporter、DeviceProfileFactory、EngineContracts（IPlayerEngine）
│  │  ├─ Reliability/  StopOutbox
│  │  ├─ Persistence/  SettingsStore、AppPaths、AtomicFile、LibraryPreferences、VideoQualityPreferences、StorageJsonContext
│  │  ├─ BulletChat/   弹幕匹配、取数、解析与缓存（DandanplayClient 等）
│  │  ├─ Updates/      GitHub 版本检查、组件更新清单与更新事务
│  │  ├─ Diagnostics/  AppLog、UrlRedactor
│  │  ├─ Fakes/        契约的假实现，供演示模式和界面开发使用（§7）
│  │  └─ BackendRuntime.cs   真实服务的组合根
│  ├─ Mambo.Player/  net10.0
│  │  ├─ LibMpv/       LibMpvNative（LibraryImport）、MpvStructs、MpvNodeReader/Builder、MpvRuntime、MpvCore、MpvSwapChain、
│  │  │                LibMpvEngine、VideoQualityController
│  │  └─ External/     ExternalMpvEngine、MpvIpcClient、MpvExecutableApproval
│  ├─ Mambo.Updater/  独立 Native AOT 更新器；等待退出、原位更新/恢复及重启，无 XAML
│  └─ Mambo.App/     net10.0-windows10.0.26100.0，WinUI 3，全部 XAML 都在这里
│     ├─ Program.cs、App.xaml、MainWindow.xaml、app.manifest（PerMonitorV2）
│     ├─ Composition/  BackendServices、UiServices、AppShutdownCoordinator、UiScheduler、UpdateBootstrap
│     ├─ Shell/        ShellView、SidebarView、PageHost、Navigator、PageFactory、DialogHost/DialogService、ToastHost/ToastService、
│     │                TitleBarService、ThemeService、PlaybackLauncher、PlayerFoldTransition、BrowseTransitionCoordinator
│     ├─ Windowing/    WindowChrome（非客户区、窗口按钮）、NonClientHook、MamboBackdrop、WindowIcon、LabWindow
│     ├─ Video/        VideoSurface（SwapChainPanel 子类）、SwapChainPanelInterop、HdrController、PlaybackVideoBridge
│     ├─ BulletChat/   弹幕合成层：BulletChatLayer、轨道规划、文字栅格化
│     ├─ Images/       ImageLoader、RemoteImage、DecodedImageCache
│     ├─ Platform/     WindowsCredentialStore、ExternalMpvPicker、PowerRequest、SingleInstanceLifetime、WindowResizeHook、AppExceptionMonitor
│     ├─ Views/        各页面（Home、Library、Recent、Search、Detail、Settings）、PlayerOverlay、BulletChatPanel
│     ├─ Views/Controls/  PosterCard、LandscapeCard、EpisodeCard、CardRail、HeroCarousel、PageHeader、StateView 等
│     ├─ ViewModels/
│     ├─ Themes/       Tokens.xaml、Typography.xaml、Controls.xaml、Icons.xaml、PlayerPanels.xaml、Motion
│     ├─ Debug/        VideoLab、FakeLab 与各项验收用的 Smoke/Probe 入口（配合 scripts/test-*.ps1）
│     └─ Assets/       Fonts/MiSans-*.ttf、AppIcon.ico
└─ tests/  Mambo.Core.Tests（xUnit）· Mambo.Player.Tests（无头真实 libmpv：vo=null、ao=null、av://lavfi:testsrc）
```

- **项目引用**：App → Core、Player；Player → Core；Tests → Core、Player。
- **XAML 只放在 Mambo.App**：类库里放 XAML 会引出 PRI 合并的麻烦。
- **依赖边界**：`Mambo.App` 的页面与 ViewModel 只依赖 `Mambo.Core.Contracts` 和 `VideoSurface` 的公开 API，不引用 Core 的实现类、DTO 或 Player 的内部类型（§7）。

### 2.2 Mambo.App.csproj 要点（参考 nami 的 `Nami.csproj`）

- `OutputType=WinExe`；`TargetFramework=net10.0-windows10.0.26100.0`；`TargetPlatformMinVersion` 和 `SupportedOSPlatformVersion` 都设为 10.0.19041.0。
- `Platforms=x64`；Platform 为空时默认 x64；`RuntimeIdentifier=win-x64`。
- 打包与部署：`UseWinUI=true`、`WindowsPackageType=None`（免打包）、`WindowsAppSDKSelfContained=true`、`SelfContained=true`、`EnableMsixTooling=false`。
  - 要确认构建产物里有 `Mambo.pri`；如果没有，就改用 `EnableMsixTooling=true`。
- `ApplicationManifest=app.manifest`、`AllowUnsafeBlocks=true`。
- 定义 `DISABLE_XAML_GENERATED_MAIN`，改用自己写的 `Program.Main`。
- AOT 相关：Release 设 `PublishAot=true`；设 `IsAotCompatible=true`、`JsonSerializerIsReflectionEnabledByDefault=false`。
- `SatelliteResourceLanguages=zh-Hans;en`。
- 内容文件：`third_party\libmpv\bin\libmpv-2.dll` 复制到输出和发布目录的 `mpv\libmpv-2.dll`；NOTICES 和 LICENSES 也一并复制。
- AOT 发布会漏掉 `$(AssemblyName).pri` 和 `obj\**\*.xbf`，照 nami 的 publish target 把它们补进发布清单。
- WinUI 3 不支持单文件发布。

### 2.3 AOT 兼容规则

- Release 用 Native AOT，Debug 用 JIT；另备一份 ReadyToRun、不裁剪的发布配置，作为 AOT 受阻时的退路。
- 互操作只用 `LibraryImport` 和函数指针，**不用 `[ComImport]`**。
- JSON 一律用 System.Text.Json 源生成。
- 绑定只用 `{x:Bind}`；万一要用 `{Binding}`，必须加 `[GeneratedBindableCustomProperty]`。
- 暴露给 WinRT 的类都写成 `partial`；用 CommunityToolkit.Mvvm 8.4 的 partial `[ObservableProperty]`。
- 不写自定义泛型 WinRT 集合，所以增量加载要手写（见 §6.4）。
- Serilog 在代码里配置，不读配置文件。
- **不引入**：Microsoft.Extensions.Hosting（拖慢启动）、Polly、Serilog.Settings.Configuration（依赖反射，不兼容 AOT）。包版本以 `Directory.Packages.props` 和各项目的 `packages.lock.json` 为准，锁定后不要随意升级。

### 2.4 原生运行时与许可

- **来源**：MSYS2 官方 UCRT64 精确版本包，由 `scripts/fetch-libmpv.ps1` 按 `third_party/libmpv/libmpv.lock.json` 下载并校验 SHA-256。每个包、DLL、头文件和对应源码均固定版本与哈希；依据与核验见 `docs/decisions/native-distribution.md`。
- **版本要求**：必须 ≥ mpv 0.41，因为要用 `d3d11-output-mode=composition`、`d3d11-composition-size` 和 `display-swapchain`。更换输入时要重新验证合成画面、硬解与画质，并更新发布证据。
- **部署**：`libmpv-2.dll` 及其全部非系统 DLL 放在应用目录的 `mpv/` 下；不分发导入库或编译工具。C# 用 P/Invoke 动态加载。
- **下载缓存**：环境变量 `MAMBO_LIBMPV_CACHE` 可指定下载缓存目录，避免重复下载。
- **许可**：原生运行时包含 GPL 和 Apache-2.0 等组件，Mambo 采用 GPL-3.0-or-later。各组件逐文件保留许可与对应源码，见 `THIRD_PARTY_NOTICES.md` 与 `LICENSES/`。

## 3. 视频管线

### 3.1 加载 libmpv

- **查找顺序**：启动时、调用任何 mpv 函数之前，用 `NativeLibrary.SetDllImportResolver` 为 `"libmpv-2"` 依次尝试：
  1. `<AppContext.BaseDirectory>\mpv\libmpv-2.dll`
  2. `<base>\libmpv-2.dll`

  这个做法兼容 AOT。
- **可用性检查**：`MpvRuntime.Probe()` 依次检查：
  - 文件存在且能加载；
  - `mpv_client_api_version() >= 0x00020005`；
  - 在一个临时句柄上 `mpv_set_option_string("d3d11-output-mode","composition")` 能成功（低于 0.41 的版本会失败）。
- **预热**：首次播放时、打开设置/关于页时，以及启动约 3 秒后在后台各探测一次。DLL 有 117MB，后台预热可以把加载开销藏起来。
- **失败处理**：只禁用内置播放器，给出中文提示（如「内置播放器组件缺失（libmpv-2.dll），请重新安装或切换到外部播放器」），并在设置页引导改用外部播放器。

### 3.2 P/Invoke 接口

`[LibraryImport("libmpv-2", StringMarshalling = Utf8)]`

- **函数**：
  - 基础：`mpv_client_api_version`、`mpv_error_string`、`mpv_free`、`mpv_free_node_contents`
  - 生命周期：`mpv_create`、`mpv_initialize`、`mpv_terminate_destroy`（只在 `MpvHandle.ReleaseHandle` 里调用）
  - 选项与属性：`mpv_set_option_string`（初始化前）、`mpv_set_property_string`、`mpv_set_property`（FLAG/INT64/DOUBLE）、`mpv_set_property_async`、`mpv_get_property`（INT64/DOUBLE/FLAG/NODE）、`mpv_get_property_string`
  - 观察：`mpv_observe_property`、`mpv_unobserve_property`
  - 命令：`mpv_command`、`mpv_command_node`（`loadfile` 用它，才能拿到 `playlist_entry_id` 并传每文件选项的 NODE_MAP）、`mpv_command_async`、`mpv_command_node_async`
  - 事件：`mpv_request_log_messages`、`mpv_request_event`、`mpv_wait_event`、`mpv_wakeup`
- **不需要的**：render API、stream_cb、hooks、`mpv_set_wakeup_callback`。
- **结构体**（布局与 client.h 完全一致）：
  - `mpv_event {int id; int error; ulong reply_userdata; void* data}`
  - `mpv_event_property {byte* name; int format; void* data}`
  - `mpv_event_start_file {long playlist_entry_id}`
  - `mpv_event_end_file {int reason; int error; long playlist_entry_id; long playlist_insert_id; int playlist_insert_num_entries}`
  - `mpv_event_log_message`
  - `mpv_node {8 字节 union; int format}`，共 16 字节
  - `mpv_node_list {int num; mpv_node* values; byte** keys}`
  - `mpv_byte_array`
- **事件 id**：1 SHUTDOWN、2 LOG_MESSAGE、4 SET_PROPERTY_REPLY、5 COMMAND_REPLY、6 START_FILE、7 END_FILE、8 FILE_LOADED、17 VIDEO_RECONFIG、18 AUDIO_RECONFIG、20 SEEK、21 PLAYBACK_RESTART、22 PROPERTY_CHANGE、24 QUEUE_OVERFLOW。

### 3.3 句柄与生命周期

- `sealed class MpvHandle : SafeHandleZeroOrMinusOneIsInvalid`，释放时调用 `mpv_terminate_destroy`。
  - 所有 P/Invoke 都传 SafeHandle，封送器会在调用期间持有引用，保证 terminate 不会和其它调用并发。
  - 句柄释放后再调用会抛 `ObjectDisposedException`，而不是崩溃。
- **每个播放会话一个 mpv 实例**：开始播放时创建，关闭播放层时销毁。
  - mpv 在后台线程创建，与 PlaybackInfo 请求、URL 探测并行，不额外增加等待。

### 3.4 初始化前设置的选项

```
config=no load-scripts=no ytdl=no terminal=no input-default-bindings=no input-vo-keyboard=no osc=no
osd-level=0 osd-bar=no cursor-autohide=no idle=yes force-window=immediate keep-open=no
vo=gpu-next gpu-api=d3d11 gpu-context=d3d11 d3d11-output-mode=composition
d3d11-composition-size=<面板像素 WxH，拿不到时用 1280x720>   ← 必须在 VO 启动前就有效，否则初始化失败
hwdec=d3d11va（设置项"硬件解码：自动/关闭"）
cache=yes demuxer-max-bytes=128MiB demuxer-max-back-bytes=64MiB demuxer-readahead-secs=15 network-timeout=15
user-agent=Mambo/<版本> audio-client-name=Mambo media-controls=no sub-auto=no
slang=zh-CN,zh-Hans,chi,zho,chs,zh,eng,en
gpu-shader-cache-dir=%LOCALAPPDATA%\Mambo\mpv\shader-cache（缩短 gpu-next 首帧时间）
可选：prefetch-playlist=yes；sub-fonts-dir=<应用>\Assets\Fonts + sub-font=MiSans
```

- 初始化后调用 `mpv_request_log_messages("warn")`，接入 Serilog 的 `mpv` 分类。
- HDR 相关的设置在初始化之后应用（§3.9）。

### 3.5 事件模型

- **专用事件线程**：后台线程 `mpv-events` 循环调用 `mpv_wait_event(h, -1)`。
  - 事件数据只在下一次 wait 之前有效，所以每个事件**立即拷贝**成 `EngineEvent` 记录：`StartFile(id)`、`FileLoaded`、`PlaybackRestart`、`EndFile(id, reason, error)`、`PropertyChanged(属性, value)`、`VideoReconfig`、`Log`、`QueueOverflow`、`Shutdown`。
  - 拷贝后写入单读者的 `Channel<EngineEvent>`，作为播放会话 actor 的收件箱。
- **交换链**：收到 `VIDEO_RECONFIG` 时，事件线程用 `mpv_get_property("display-swapchain", INT64)` 重新读取。指针有变化就发出 `SwapChainChanged(ptr)`，由 `VideoSurface` 切回 UI 线程处理。
  - **不要观察 `display-swapchain`**：它不在 mpv 的变更事件表里，只会推送一次初始值。
- **队列溢出**：收到 `QUEUE_OVERFLOW` 时，会话直接重新读取关键属性。
- **UI 更新**：UI 线程从不处理原始事件。会话把最新的 `SessionSnapshot` 交给发布器；发布器只在没有待处理回调时才往 DispatcherQueue 投递一次，上限约 10Hz。
- **用户操作**：seek、暂停、改尺寸一律用 `*_async`，UI 线程从不等待 mpv。

### 3.6 观察的属性

`reply_userdata` 用枚举值区分属性，省去字符串比较。

| 属性 | 格式 | 用途 |
|---|---|---|
| time-pos、duration | DOUBLE | 进度条、上报的位置（每个条目保留最后一个值） |
| pause、paused-for-cache、seeking、core-idle | FLAG | 播放状态、缓冲提示、上报中的 IsPaused |
| idle-active、eof-reached | FLAG | 诊断，以及辅助判断季末 |
| speed、volume、mute | DOUBLE/FLAG | 界面，以及上报真实的 PlaybackRate |
| aid、sid | STRING | 轨道菜单里的当前选中项 |
| track-list | NODE | 字幕/音轨菜单（id、type、title、lang、codec、external、default、forced、ff-index） |
| video-params、video-target-params | NODE | HDR 标识、HDR 验证、诊断 |
| hwdec-current | STRING | 诊断，预期为 d3d11va |
| demuxer-cache-state | NODE | 进度条上的已缓冲区间 |
| playlist-pos、playlist-count | INT64 | 仅用于诊断；条目以 entry id 跟踪 |

### 3.7 END_FILE 的处理（细节见 §4.2）

- **ERROR(4)**：尚未开播就换下一个候选，候选用尽则跳过或报失败；已开播则上报停止并提示「播放中断」。
- **EOF(0)**：上报停止，然后自动进入下一集、准备下一集，或在季末结束。
- **STOP(2)**：上报停止，再执行用户挂起的操作（下一集、上一集、选集、关闭）。
- **QUIT(3)**：上报停止，结束会话。
- **REDIRECT(5)**：忽略，随后会收到新的 START_FILE。

### 3.8 交换链绑定、DPI 与尺寸（照搬 nami 的 `VideoView.cs` 和 `SwapChainPanelInterop.cs`）

- **创建时机**：面板加载完成、尺寸不为零之后，才创建 mpv。
  - 保留 Mio 那种有限重试：每次布局最多试一次，共 10 次，用尽后给出明确错误。
- **绑定**：
  1. 用 `((WinRT.IWinRTObject)panel).NativeObject.ThisPtr` 拿到面板的原生指针；
  2. QueryInterface 取 `ISwapChainPanelNative`（IID `63aad0b8-7c24-40ff-85a8-640d944cc325`）；
  3. 调用 vtable 第 3 项 `SetSwapChain`。

  这个调用必须在 UI 线程执行，兼容 AOT。互操作代码集中在一个文件里，以便日后 CsWinRT 3 迁移时只改一处。
- **DPI**：
  - 像素尺寸 = `round(ActualWidth × XamlRoot.RasterizationScale)`。
  - 绑定后以及每次 `XamlRoot.Changed`，都要 QueryInterface 取 `IDXGISwapChain2`（IID `a8be2ac4-199f-4946-b331-79599fb98de7`），调用 `SetMatrixTransform(1/scale, 1/scale)`。否则在高 DPI 下画面会被放大裁切。
  - 缩放比例用 `RasterizationScale`，不要用 `CompositionScaleX`。
- **改尺寸不闪烁**：
  - 面板保持缓冲区当前的实际尺寸（"已提交尺寸"），用 XAML 变换把它拉伸到宿主大小，直到 mpv 跟上。
  - 新尺寸通过写 `d3d11-composition-size` 生效。
  - 每 8ms 用 `IDXGISwapChain2::GetDesc1` 检查缓冲区实际尺寸，1 秒后放弃；尺寸到位后再等一帧（约 40ms）才提交。
  - 拖动缩放期间（`WM_ENTERSIZEMOVE` 到 `WM_EXITSIZEMOVE`）不改 mpv 的尺寸。
- **画面边界**：视频填满 `VideoViewport`，使用矩形裁剪，不添加独立黑框、视频内缩或圆角遮罩。窗口留白仍由 `PlayerOverlay` 控制。详见 [播放区域布局](decisions/player-card-frame.md)。
- **解绑**：销毁 mpv 前先 `SetSwapChain(null)`。

### 3.9 HDR（composition 模式下 mpv 看不到显示器，必须由应用告诉它）

- **显示器信息**：`Microsoft.Graphics.Display.DisplayInformation.CreateForWindowId(appWindow.Id)` → `GetAdvancedColorInfo()`，并监听 `AdvancedColorInfoChanged`。这一个 API 就能覆盖开关 HDR 和移动到另一台显示器两种情况。
- **设置项"HDR"**：自动 / 始终 HDR / 关闭。
- **HDR 显示器**（模式为"自动"且 `CurrentAdvancedColorKind == HighDynamicRange`）时设置：
  - `target-colorspace-hint=yes`、`target-trc=pq`、`target-prim=bt.2020`
  - `target-peak=<MaxLuminanceInNits，拿不到时 1000>`
  - `hdr-reference-white=<SdrWhiteLevelInNits，拿不到时 203>`
  - `target-contrast`：按最大/最小亮度计算，最小亮度为 0 时设 `inf`
  - 默认 `target-colorspace-hint-mode=target`，由 mpv 按实测峰值做色调映射。高级开关"由显示器映射"改为 `source`，即直通。
- **SDR 显示器**：`target-colorspace-hint=no`，其余 `target-*` 全部恢复 `auto`，由 gpu-next 做色调映射。
- **其它**：`d3d11-output-format` 保持 `auto`。
- **何时应用**：创建 mpv 后、`AdvancedColorInfoChanged` 时、用户改设置时，都在运行时直接设置属性。
- **验证方法**：
  - 片源的 `video-params/gamma` = pq；
  - 输出的 `video-target-params` = pq / bt.2020 / 峰值；
  - 高光测试图显示正确；
  - 播放中关闭 Windows HDR，不重启就切到 SDR 映射。

### 3.10 全屏、光标、屏保、材质

- **全屏**：用 `AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)`，同时收起标题栏和侧栏两行。
  - 若从最大化进入全屏时出现还原闪烁，改用 Mio 的做法：保持 Overlapped presenter，设 `IsResizable=false`、`SetBorderAndTitleBar(false,false)`，再 `MoveAndResize` 到显示器边界。当前实现用的是 FullScreen presenter。
  - 还要检查顶部是否有 1px 缝隙。
- **光标**：控制层隐藏时，用 `ProtectedCursor` 换成空光标（参考 nami 的 `EmptyCursor`）。
- **屏保**：mpv 在 composition 模式下不会阻止屏保，由应用在"播放中且未暂停"时调用 `SetThreadExecutionState(ES_CONTINUOUS|ES_DISPLAY_REQUIRED|ES_SYSTEM_REQUIRED)`。
- **材质**：画面上方**不能用亚克力**（XAML 亚克力采样不到视频交换链，全屏时还会闪白）。播放器控件和弹出菜单都用半透明纯色加渐变。

### 3.11 关闭顺序

1. **会话结束**：
   1. 发送 `stop`，最多等 2 秒的 END_FILE（等不到就用最后已知位置）；
   2. 停止记录先写入发件箱，再上报；
   3. 转码会话调用清理接口。
2. **解绑**：UI 线程调用 `VideoSurface.Detach()`，即 `SetSwapChain(null)`。
3. **销毁 mpv**（后台执行）：
   1. `quit`，最多等 3 秒，等事件线程收到 SHUTDOWN 退出；
   2. `MpvHandle.Dispose()` 销毁句柄，设 5 秒看门狗，超时就记日志并放弃句柄。

   事件线程可能还在 `mpv_wait_event` 里时，绝不销毁句柄。
4. **关闭应用**：拦截 `AppWindow.Closing` 并取消关闭，交给 `AppShutdownCoordinator`：
   1. 按上述流程结束会话（HTTP 上报预算 1.5 秒，记录此前已落盘）；
   2. 保存查询缓存快照、设置和窗口位置；
   3. `Log.CloseAndFlush()`；
   4. 真正关闭。

   任何收尾工作都不交给终结器。

### 3.12 兜底方案

`SwapChainPanel` 绑定已验证可行并在使用。若日后失效，备选是 `ICompositorInterop::CreateCompositionSurfaceForSwapChain` + SpriteVisual，通过 `ElementCompositionPreview.SetElementChildVisual` 挂到元素上。仍然在 WinUI 的合成树里，仍然没有 airspace 问题。

## 4. 播放会话

### 4.1 组件（除引擎外都在 Core）

- **`PlaybackCoordinator.PlayAsync(item, startTicks?)`**：解析目标 → 生成连播计划 → 创建引擎和会话。
- **`PlaybackTargetResolver`**：规则见 SPEC A.9。
- **`SeasonPlan`**：规则见 SPEC A.9。生成失败**不再阻断播放**，降级为单集播放。
- **`EntryPreparer`**：
  - **每次都调用 PlaybackInfo**，不保留原版"用本地 PlaySessionId 走捷径"的做法；
  - 然后依次经过 `MediaSourceSelector`、`StreamCandidateBuilder`、`StreamUrlResolver`；
  - 产出 `PreparedEntry`：候选列表（URL、播放方式、请求头或空）、PlaySessionId、MediaSourceId、LiveStreamId、外挂字幕列表、标题、起始 ticks（后续追加的集为 0）。
- **`PlaybackSession`**：单线程 actor。
  - 唯一的读循环处理单读者收件箱通道：引擎事件、用户命令、计时器、异步结果；
  - 网络工作以 Task 形式运行，完成后把结果投回收件箱；
  - 上报经会话内的有序队列发出，保证 Playing → Progress → Stopped 的顺序；
  - 可以用 FakeEngine + `FakeTimeProvider` 测试。

### 4.2 状态与转换

- **会话状态**：Preparing → Opening（候选 k；20 秒后标记"加载较慢"）→ Playing（附带 Paused/Buffering/Seeking 标志）→ Interstitial（两集之间准备下一集）→ Failed / Closing / Closed。
- **条目状态**：Prepared → Loading(k) → Confirmed → Ended(reason)。条目用 `loadfile` 返回的 `playlist_entry_id` 跟踪，**不依赖 playlist-pos**。

| 事件 | 条件 | 动作 → 下一状态 |
|---|---|---|
| Start | — | 解析并生成计划；并行创建引擎、绑定画面；准备选中的集；`loadfile url0 replace -1 {opts}`，记录 entryId → Opening |
| START_FILE(id) | id 已知 | 该条目进入 Loading(k)（或表示 mpv 已自动切到下一集） |
| FILE_LOADED(id) | — | 对每条外挂字幕执行 `sub-add <url> auto <title> <lang>`；恢复音量设置 |
| FILE_LOADED 后首次 PLAYBACK_RESTART | 未确认 | 标记 Confirmed；上报 Playing；启动 10 秒进度计时；开始准备下一集 → Playing |
| END_FILE(ERROR) | 未确认 | 还有候选 k+1：`loadfile url[k+1] replace` → Opening。候选用尽：首集 → Failed（「重试」/「关闭」）；连播中的集 → Toast「有一集无法加入连播，已跳过」并准备下一集，最多连续跳 3 集 → Interstitial |
| END_FILE(ERROR) | 已确认 | 按最后位置上报停止；Toast「播放中断」。下一集已追加就等它开始，否则 → Failed（「从断点重试」） |
| END_FILE(EOF) | — | 上报停止。下一集已追加：等它 START_FILE；没有下一集或不是连播：→ Closing（季末）；否则 → Interstitial，准备好后 `loadfile replace` |
| END_FILE(STOP/QUIT) | — | 上报停止；执行挂起的用户操作 |
| 下一集准备就绪 | 当前集已确认 | `loadfile url append -1 {opts}`，记录 entryId |
| 用户 下一集/上一集/选集(e) | — | e 是已追加的下一集：`playlist-next`；否则先准备 e，再 `loadfile replace`（途中会上报当前集的停止） |
| 暂停/继续/拖动 | Playing | 调用引擎；立即上报一次 Progress（EventName = Pause / Unpause / TimeUpdate） |
| 10 秒计时 | 已确认且未结束 | 上报 Progress |
| 关闭 | — | `stop`，最多等 2 秒 END_FILE，再按 §3.11 收尾 → Closed |
| 引擎意外退出 | — | 完成当前集的停止上报 → Failed（「播放器已退出」） |

### 4.3 连播：内置保留"当前集 + 下一集"，外置提供后续剧集列表

2026-10-05 用户修正外部播放器行为：外置 mpv 加载自己的配置、脚本和控件，Mambo 留在浏览页面。外置模式在当前集开播后依次追加后续剧集，保留已播条目供 mpv 原生列表返回；重播使用新的上报生命周期。以下“当前集 + 下一集”的裁剪策略只适用于内置模式。见 `docs/decisions/P7-external-handoff.md`。

- **追加时机**：当前集**确认开播后**才追加下一集。
  - 首集加载失败时 mpv 处于 idle，候选回退可以放心用 replace；
  - mpv 仍然会在片尾自动切到下一集。
- **界面数据来源**：选集面板和上一集/下一集按钮由 `SeasonPlan` 驱动，不读 mpv 的播放列表。
- **每文件选项**：用 `MpvNodeBuilder` 构造 NODE_MAP，值都是字符串。这些选项在每个文件结束后自动失效，不会带到下一集。

  | 键 | 值 |
  |---|---|
  | `http-header-fields` | 多个请求头用 `,` 连接；转义规则：`\` → `\\`、`,` → `\,`。只有同源（同协议、主机、端口，且路径在服务器基路径下）的 URL 才带 `X-Emby-Token: …`；另加 Emby 返回的 `RequiredHttpHeaders`。跨域 URL 设为 `""` |
  | `force-media-title` | `{剧名} S{季:02}E{集:02} - {集名}` |
  | `start` | 起始秒数，保留 3 位小数，只在大于 0 时设置 |

- **外挂字幕**：在 FILE_LOADED 时 `sub-add`。URL 优先用 PlaybackInfo 返回的 `MediaStream.DeliveryUrl`，否则用 `/Videos/{id}/{mediaSourceId}/Subtitles/{index}/Stream.{format}`。

### 4.4 选源、候选与 URL 解析

- **`MediaSourceSelector`**：资格与优先级见 SPEC A.9。与原版的差别是支持带 `RequiredHttpHeaders` 的片源，把这些请求头按文件传给 mpv。
- **`StreamCandidateBuilder`**：
  - 只有 `SupportsDirectPlay` 或 `SupportsDirectStream` 为真时，才生成静态路由 `/Videos/{id}/stream[.{container}]?DeviceId&MediaSourceId&Static=true[&PlaySessionId]`；
  - 两者都为假时，`TranscodingUrl` 排在第一位（修复"转码不可达"）；
  - 同源 URL 里的认证查询参数（api_key/access_token/token）要去掉。
- **`StreamUrlResolver`**（取代原版无效的 `curl-max-redirects`）：
  - **凡是要带令牌的候选，都先探测一次**：带认证头发 `GET` + `Range: bytes=0-0`，不自动跟随重定向，`ResponseHeadersRead`，3 秒超时，拿到响应头立即释放。
    - 2xx/206：直接使用这个 URL；
    - 同源 30x：对新地址继续探测；
    - 跨域 30x：清空认证头和 RequiredHttpHeaders，把已认证登录服务器明确签发的完整 Location 交给 mpv；不自行追加认证查询参数。下载网关可能要求 Location 自带的参数（其值可能等于账号令牌），不能删除。直接跨域候选、非原服务器的跳转和 HTTPS 降级不获得此信任，见 `docs/decisions/playback-issued-redirect.md`；
    - 最多 5 跳，总计 6 秒。
  - 服务器拒绝 Range 请求时，依次退回到 HEAD、普通 GET。
  - **这样做的原因**：ffmpeg 每次重定向都会重发自定义请求头，Emby 302 到 CDN/网盘直链的部署就会泄露令牌。
  - 不带令牌的跨域候选不探测，失败时由 END_FILE(ERROR) 触发换下一个候选。

### 4.5 上报

- **载荷字段与发送节奏**：见 SPEC A.9。
- **失败分类**：
  - 401/403：等待重新登录；
  - 408/425/429/5xx 或网络错误：重试；
  - 其它 4xx：死信，记日志后丢弃。
- **转码清理**：转码会话停止后调用 `DELETE /Videos/ActiveEncodings?DeviceId&PlaySessionId`。
- **缓存失效**：停止后通过 `WeakReferenceMessenger` 广播 `PlaybackStopped(itemId, seriesId, seasonId)`，让相关缓存失效（§5.6）。

### 4.6 停止发件箱（`StopOutbox`）

- **文件**：`%LOCALAPPDATA%\Mambo\outbox\stop-reports.json`，`{"version":1,"records":[…]}`。
- **记录字段**：
  - 作用域：`serverId`、`serverBase`、`userId`
  - `idempotencyId`：本地播放会话 id
  - 条目信息：`itemId`、`mediaSourceId?`、`liveStreamId?`、`playSessionId?`、`playbackStartTimeTicks`、`positionTicks`
  - 投递状态：`queuedAt`、`lastAttemptAt?`、`attemptCount`、`state` = `pending | awaiting_reauth`

  不保存令牌和标题。
- **语义**：
  - **先落盘再发送**：写临时文件 → flush → 替换原文件。
  - **只发送作用域完全匹配当前账号的记录**；未登录时什么也不做。
  - 每次尝试前先记下尝试时间和次数。
  - 成功的记录在整轮发送结束后统一确认，保证至少一次投递。
  - 401/403：标记 `awaiting_reauth` 并中止本轮；可重试的错误：保留并中止本轮；死信：删除并记日志。
  - 同一时间只允许一轮 flush；每轮有时间上限（停止时 3 秒，退出时 1.5 秒）。
- **触发时机**：停止播放、切换播放、季末、意外中断、登录、恢复会话、注销前（用旧令牌），外加低频定时重试（例如每 5 分钟）。
- **不要做**：多类隔离文件、保留预算、版本迁移。

### 4.7 引擎抽象与外部播放器

```csharp
public interface IPlayerEngine : IAsyncDisposable {
  EngineKind Kind { get; }                       // Embedded | External
  ChannelReader<EngineEvent> Events { get; }
  ValueTask<long> LoadAsync(string url, LoadMode mode, IReadOnlyList<KeyValuePair<string,string>> fileOptions, CancellationToken ct);
  ValueTask CommandAsync(ReadOnlyMemory<string> args, CancellationToken ct);   // seek / stop / playlist-next / sub-add / frame-step
  ValueTask SetAsync(string property, MpvValue value, CancellationToken ct);
}
```

- **`LibMpvEngine`** 另外提供 `SwapChainChanged`、`SetCompositionSize(w,h)`、`ApplyHdr(...)`，只给 App 用。
- **`ExternalMpvEngine`**：
  - 通过命名管道 `\\.\pipe\mambo-{guid}` 走 mpv JSON IPC；事件和 `loadfile` 选项与内置引擎同构。
  - 每个请求都带 `request_id` 和超时：默认 3 秒，`loadfile` 5 秒，连接管道 5 秒。进程退出映射为 Shutdown。
  - URL、令牌、起始位置都不放在命令行上。
- 会话、上报、发件箱两种引擎完全复用。

## 5. Emby 客户端、缓存与数据

### 5.1 网络

- **两个 `SocketsHttpHandler`**，公共设置：
  - `AllowAutoRedirect=false`、`UseCookies=false`、`AutomaticDecompression=All`
  - `ConnectTimeout=10s`、`PooledConnectionLifetime=5min`
  - `HttpClient.Timeout=Infinite`，改用 linked CTS 给每个请求设截止时间：默认 30 秒，`/Items/Filters` 和探测请求 3 秒
  - 启用 HTTP/2（`RequestVersionOrLower`）
- **API 客户端**：从不跟随重定向。
- **图片客户端**：手动跟随重定向，最多 5 跳，只允许同主机同端口，协议不变或 http → https。
  - 必须手动处理的原因：.NET 跟随重定向时会去掉 `Authorization`，但**不会**去掉 `X-Emby-Token`。

### 5.2 认证头、地址与端点

- **认证头**：`Authorization: Emby UserId="<uid>", Client="Mambo", Device="Windows", DeviceId="<guid>", Version="<程序集版本>"`，另加 `X-Emby-Token: <token>`。
  - `AuthenticateByName` 请求不带 UserId 和令牌。
  - 客户端不在 URL 中自行添加 api_key；§4.4 中保留登录服务器签发的下载 Location 是明确例外。
- **`ServerAddress.Normalize`**：规则和错误文案见 SPEC A.1。
- **端点、字段集、DeviceProfile**：见 SPEC A.1。DeviceProfile 在原版基础上，为所有字幕格式加上 `Embed`（交给 mpv 自己读内封字幕）。

### 5.3 DTO

- 全部用 `sealed record`，成员可空，忽略未知字段。
- 一个 `EmbyJsonContext : JsonSerializerContext`：`PropertyNameCaseInsensitive`、`WhenWritingNull`、`AllowReadingFromString`，再加一个 `NameOrStringConverter`，兼容 `/Items/Filters` 的值为字符串或 `{Name}` 对象两种形态。
- DTO 映射成领域模型（`MediaItem`、`ImageRef`、`PlaybackState` 等）后再交给 ViewModel，ViewModel 不接触原始 DTO。

### 5.4 错误模型

- **结构**：`AppError(Kind, Code, Message, Retryable, Stage?, Status?, DiagnosticId?)`。
  - Kind：auth / network / server / contract / cancelled / player / persistence。
  - 跨 I/O 边界时用 `AppException` 携带。
- **`HttpErrorMapper` 映射规则**：

  | 情况 | 映射为 |
  |---|---|
  | 传输失败、超时 | Network（可重试） |
  | 主动取消 | Cancelled |
  | 401/403 | Auth（并发出 SessionExpired） |
  | 408/425/429/5xx | Server（可重试） |
  | 其它 4xx | Server/Contract |
  | JSON 解析失败 | Contract |

- **文案**：所有面向用户的文字集中在中文模板 `ErrorText` 里，例如「加载媒体库失败：无法连接服务器，请检查网络或服务器地址」。错误消息里不得出现完整 URL。
- **ViewModel**：把错误转成错误态或 Toast。

### 5.5 请求调度（`RequestScheduler`）

- **通道**：`metadata` 并发 4，其中保留 1 个给前台；`reliability` 并发 2。
- **优先级**：Foreground / Visible / Background，用 `PriorityQueue<(priority, seq)>` 排队。
- **截止时间**：从入队算起，前台 10 秒，其余 20 秒。
- **重试**：幂等的 GET 遇到可重试错误时重试一次（截止时间内）。
- **取消**：`scopeToken` 绑定页面或账号作用域，离开页面、切换账号时取消排队中和进行中的请求。

### 5.6 SWR 缓存、持久化与失效

- **`QueryCache`**：
  - 键为 `QueryKey(Scope, Kind, Args)`；
  - `Observe<T>(key, fetch, staleAfter=60s)`：先返回缓存，过期则后台刷新，合并重复请求，通过 `IUiScheduler` 推送更新；
  - ViewModel 按 Id 原地更新 `ObservableCollection`，列表不闪烁。
- **`QueryPersistence`**（冷启动秒开）：
  - 按账号持久化白名单：媒体库列表、首页各行、hero 候选、各库默认排序下的第一页；
  - 文件 `%LOCALAPPDATA%\Mambo\cache\query\{sha256(server|user)}.json`；
  - 静止 2 秒后写入；在恢复会话、首次渲染之前读取；
  - 保留 7 天，总量 20MB，注销时删除。
- **失效**：收到 `PlaybackStopped` 后，把下列缓存标记为过期，正在显示的页面自动重新获取：
  - 该条目的详情
  - 继续观看行、最近播放页
  - 该剧集的 NextUp、本季剧集
  - 各资料库页
- **F5**：使当前页的缓存键失效。

### 5.7 图片管线

- **Core（`ImageByteCache` / `ImageFetcher`）**：
  - **缓存键**：`server|item|type|tag|maxW|maxH|index|quality`，取 SHA-256。
  - **内存层**：压缩字节的 LRU，共 64MB，单条最大 16MB。
  - **磁盘层**：共 512MB，**只缓存带 Tag 的图**（Tag 不变则内容不变，无需重新验证）。
    - 文件为 `cache/images/xx/<hash>.img`，带魔数和版本头，先写临时文件再替换；
    - 命中时更新修改时间；
    - 每写入 32MB 检查一次，超限后按最旧优先删除，直到低于 90%；
    - 注销时清空。
  - **下载**：并发 6；优先级 Hero > Visible > Prefetch；合并重复请求；可取消。
  - **请求尺寸**：MaxWidth = DIP 宽 × 缩放比例，向上取到 {160, 240, 320, 480, 640, 960, 1280, 1920, 2560} 中的一档；Quality 取 90。
- **App（`ImageLoader`）**：
  - 显示：字节 → `InMemoryRandomAccessStream` → `BitmapImage { DecodePixelType=Physical, DecodePixelWidth=ceil(DIP 宽 × XamlRoot.RasterizationScale) }` → `SetSourceAsync`。下载与解码使用同一个物理像素宽度，流解码不依赖隐式 DPI 推断。
  - 复用：`DecodedImageCache` 以弱引用缓存 `BitmapImage`，键为（图片引用, 物理像素解码宽度）；不同 DPI 所需的分辨率分开缓存，相同物理尺寸可以共享。
  - `RemoteImage` 控件：显示占位 → 图片就绪后淡入 140ms；在 Unloaded 时和列表容器回收时（`ContainerContentChanging` 且 `InRecycleQueue`）取消加载。
  - 卡片宽度、解码宽度提示或 XamlRoot 缩放率增大时升级图片；同轮布局通知合并，升级期间保留海报与文字，失败保留旧图。卸载时解除 XamlRoot 订阅，换绑后拒绝旧请求回填。见 `docs/decisions/P6-image-resolution.md`。
- **预取**：hero 预取后两张幻灯片（Hero 优先级）；卡片悬停 300ms 后预取详情数据（后台优先级）。

### 5.8 凭据、设置、路径、日志

- **凭据**：通过 CsWin32 调用 `CredWriteW` / `CredReadW` / `CredDeleteW` / `CredFree`，封装为 `WindowsCredentialStore : ISecretStore`。
  - 目标名 `Mambo:emby-session:v1`，类型 `CRED_TYPE_GENERIC`，持久化方式 `CRED_PERSIST_LOCAL_MACHINE`；
  - 内容是 UTF-8 JSON `{ServerAddress, ServerId, UserId, UserName, AccessToken}`；
  - **不保存密码**。
- **数据目录** `%LOCALAPPDATA%\Mambo\`：
  - `settings.json`（schema v1；先写临时文件再 `File.Move(overwrite)`，并保留 `.bak`）
  - `cache\`、`outbox\`、`logs\`、`mpv\shader-cache`
- **设置内容**：
  - DeviceId（首次运行时生成新的 GUID）
  - 播放方式；外部 mpv 路径及其指纹
  - HDR 模式、硬件解码、音量
  - 窗口位置
  - 上次的服务器地址和用户名（用于预填表单）
  - 各库偏好：按 `server|user|library` 存排序和筛选
- **日志**：
  - Serilog 写 `logs\mambo-.log`，按天滚动，保留 7 份，每份最大 10MB；
  - 全部经 `UrlRedactor`：去掉 token/api_key 等查询参数的值和 `X-Emby-Token:` 请求头的值，URL 只记脱敏后的形式；
  - mpv 自己的 `log-file` 默认关闭；
  - 记录 `UnhandledException`、`AppDomain.UnhandledException`、`UnobservedTaskException`。
- **`UrlRedactor` 规则**：
  - 以下查询参数的值替换为 `<redacted>`：api_key、token、password、authorization、signature、sig、expires、policy、key-pair-id、x-amz-*、x-oss-*，以及任何名字里含 token、password、authorization、signature 的参数；
  - 自由文本里的 `key=` / `key:` 值同样处理；
  - 以 `/play/` 开头的路径替换为 `/play/<redacted>`。

## 6. 界面架构

### 6.1 MVVM、依赖注入与启动

- **MVVM**：CommunityToolkit.Mvvm 8.4，用 partial `[ObservableProperty]`、`[RelayCommand]`；应用事件（`SessionChanged`、`PlaybackStopped`、`SessionExpired`）走 `WeakReferenceMessenger`。
- **依赖注入**：普通的 `ServiceCollection`，Debug 下构建时校验。Core 服务为单例；ViewModel 为瞬时，或按导航记录缓存。
- **`Program.Main`**（参考 nami 的 `Program.cs`）依次：
  1. `ComWrappersSupport.InitializeComWrappers()`
  2. 用 `AppInstance.FindOrRegisterForKey("Mambo")` 保证单实例，重复启动时把激活转给已有实例
  3. 启动日志
  4. 设置 `DispatcherQueueSynchronizationContext`
  5. 同步读取设置和凭据，异步开始读取缓存快照
  6. 显示带骨架屏的窗口
  7. 快照到达后立即填充内容
  8. 在后台校验会话、flush 发件箱、刷新数据、预热 libmpv
- **启动目标**：窗口在 600ms 内出现，首页缓存内容在 800ms 内显示；用 `StartupTimeline` 记录。

### 6.2 窗口、标题栏、背景

- **窗口**：`OverlappedPresenter`，`SetBorderAndTitleBar(true,false)`；完整自绘标题栏由 `InputNonClientPointerSource` 登记 Caption/Passthrough/Maximize 区域，不混用 `ExtendsContentIntoTitleBar` / `SetTitleBar` 的系统窗口按钮路径。切换原因与本轮证据见 `docs/decisions/P9-visual-parity.md`。
- **标题栏**：自绘 40px 高，含后退/前进按钮，以及自绘的 46×40 窗口按钮。
  - **不用** WinAppSDK 自带的 `TitleBar` 控件：它只有 32/48px 两种高度。
- **非客户区**（`InputNonClientPointerSource`）：
  - Caption 区 = 标题栏减去其中的可交互控件，在尺寸、DPI、可见性变化时重新计算；
  - Maximize 区 = 自绘的最大化按钮，鼠标悬停时出现 **Snap Layouts**；
  - 对最大化按钮的点击会以 `HTMAXBUTTON` 送达，用窗口子类化吞掉 `WM_NCLBUTTONDOWN/UP/DBLCLK`，自己切换最大化（参考 nami 的 `NonClientHook`）；
  - 悬停视觉用 `InputNonClientPointerSource.PointerEntered/Exited` 实现。
- **背景**：自定义 `SystemBackdrop` + `DesktopAcrylicController`，强制 `IsInputActive=true`，窗口失焦时也保持亚克力；不支持时回退到 Mica。
  - tint 不追求复刻原版：原版在 Win11 上的 tint 实际被系统忽略。以系统默认亚克力为起点。
- **尺寸**：默认 1500×860 居中；最小 1100×720（`PreferredMinimumWidth/Height`），DPI 变化时重新设置；记住窗口位置。
- **屏保**：`PowerRequest` 见 §3.10。

### 6.3 导航（自建 Navigator + PageHost，不用 Frame）

- **记录与栈**：
  - 每条记录为 `NavEntry(Route, Param, Key, ViewState)`，ViewState 保存滚动位置、搜索词等；
  - 后退栈、前进栈各最多 12 条，超出时丢弃最旧的；
  - 导航到新页面时清空前进栈；其余规则见 SPEC A.11。
- **`PageHost`**：
  - 用 LRU 保留最多 4 个活的页面实例，首页常驻；不活跃的页面折叠隐藏；
  - 返回活页面是瞬时的，滚动位置、图片和布局都在；
  - 被淘汰的页面从缓存的 ViewModel 重建，等足够的项实现后用 `ChangeView(..., disableAnimation:true)` 恢复滚动位置。
- **页面接口**：页面实现 `INavigablePage`：`OnNavigatedTo(entry, mode, created)`、`OnNavigatedFrom(entry)`、`Refresh()`；离开时保存 ViewState，`created` 为真时从 entry 恢复。
- **转场**：用 Composition 动画做淡入加位移。
- **播放层打开时**：`CanGoForward=false`；后退、Esc、鼠标后退键改为关闭播放层。
- **侧栏**：只显示可播放的视频库（规则见 SPEC A.1）；侧栏搜索框与当前搜索记录的搜索词双向同步。

### 6.4 列表与虚拟化

- **资料库和最近播放**：`ScrollViewer` 内的 `ItemsRepeater` + `UniformGridLayout`，按可用宽度分配列数与条目宽度。
- **横向卡片行**（继续观看、最新、剧集、演职人员）：`CardRail`，内部是 `ItemsRepeater` + 水平 `StackLayout`，鼠标滚轮映射为横向滚动。
- **增量加载**：`GridLoader` 监听滚动，距末尾不足 1.5 屏时加载下一页，并负责返回时的滚动位置恢复。不用 `ISupportIncrementalLoading`，以规避 AOT 下泛型 WinRT 集合的问题。
- **筛选胶囊**：自写的 `WrapPanel` 按行排列、放不下就换行。
- **渲染开销**：图片按显示尺寸解码、容器回收时取消请求；卡片上不用阴影和亚克力。

### 6.5 动效

- **现行规范**：用户确认「克制、有辨识度」，浏览↔播放翻折是唯一强动效；本节取代原版的旧动效参数，完整决定见 `docs/decisions/P9-visual-parity.md`。
- **页面与详情**：普通导航 240ms 原位交叉淡变；详情前景整体进入 240ms/Y4→0、退出 120ms/Y0→4，缓存返回只淡变。库首屏未就绪保留旧页；删除封面 ConnectedAnimation/克隆残影和逐块、逐卡错峰。
- **hero**：背景由唯一宿主 400ms 纯淡变，整组文字 240ms；图片、文字、圆点与点击目标原子提交。同图往返复用 surface；7 秒自动轮播在交接落稳/暂停恢复后重新计时。
- **播放**：480ms 中心 Y 轴翻折，1400 透视、.18 遮罩峰值；半程换面，快速反向从当前值接续。关闭先解绑释放原生表面，再短暂保留无活动资源的冻结 XAML，终态释放退场树。
- **轻反馈**：筛选占位 240ms；排序、确认卡片、Toast 与控制栏 160ms 进入、120ms 退出；卡片 hover Y−2、按下 Y−1，普通按钮无缩放。逻辑关闭立即禁输入，物理卸载等真实完成；父级转场不叠冷图入场。
- **时长与缓动**：Press/Exit/Feedback/Content/Image/Mode = 80/120/160/240/400/480ms，来自 `Tokens.xaml`，与 CSS 同步。自定义曲线仅 EaseOut/EaseIn/Symmetric；平台刷色、按距离滚动和状态周期另行标明。
- **收尾**：每窗口唯一 `UISettings.AnimationsEnabledChanged` 订阅；离页、非活动、禁动画和释放直接落最新有效终态，恢复仅影响后续动作。页面/背景最多两层，过期完成不得覆盖新目标。

### 6.6 播放层（外壳里的一层覆盖，不是导航页面；底下的页面一直活着）

```
PlayerOverlay（Grid，RequestedTheme=Dark，IsTabStop=True，持有焦点）
├─ VideoHost（黑底、圆角裁剪）→ VideoSurface（SwapChainPanel）+ BulletChatLayer（弹幕合成层，见 `docs/decisions/bullet-chat.md`）
├─ InputSurface（透明）：单击 250ms 后切换暂停 / 双击最大化；指针移动唤出控制层；隐藏时换空光标
├─ TopBar（渐变）：标题、关闭
├─ 状态层：打开中（ProgressRing + 20 秒"加载较慢"提示 + 关闭）| 缓冲胶囊 | 失败（重试 / 关闭）
├─ BottomBar（渐变 + 半透明纯色面板）：带已缓冲区间的进度条、上一集 / 下一集、播放 / 暂停、时间、
│     模式（首位，显示当前模式名称），其余为图标：倍速、弹幕面板（开关 / 样式 / 匹配状态 / 搜索）、独立字幕面板（轨道 / 时间 / 样式）和音轨面板，同一套实底样式；
│     音量（Chrome 内独立覆盖层向上展开竖向滑块，不改变控制条布局）、全屏
└─ 选集面板：布局与收起入口见 `docs/decisions/P9-visual-parity.md`、`player-card-frame.md`
```

- 画面上方**不用亚克力**，弹出菜单的 presenter 也要改成半透明纯色背景。
- 按钮设 `AllowFocusOnInteraction=False`，键盘焦点始终留在播放层上。
- 按键在 `PreviewKeyDown` 里处理（包括已被标记为处理过的事件）。按键表、自动隐藏规则见 SPEC A.10。
- 支持假数据模式，供设置页的「预览播放页」使用。

### 6.7 全局快捷键

用外壳上的 `KeyboardAccelerator` 实现，具体按键见 SPEC A.11。

### 6.8 主题、字体、图标

- **主题**：`Tokens.xaml` 的取值以原版为基准，强调色为中性色（见 `docs/decisions/P9-visual-parity.md`、`P9-neutral-accent.md`）；外壳用浅色主题，播放层用深色。
- **MiSans**：每个字重一个 `FontFamily` 资源（如 `ms-appx:///Assets/Fonts/MiSans-Medium.ttf#MiSans`），回退到 Segoe UI Variable。免打包应用里 `ms-appx` 也能解析。
- **图标**：以 Segoe Fluent Icons 为主（Win10 回退到 Segoe MDL2 Assets），特殊图标用 `PathIcon`。
- **应用图标**：由 `scripts/build-app-icon.ps1` 生成多尺寸 `.ico`，见 `docs/decisions/app-icon.md`。

### 6.9 通用组件

- 基于 `ContentDialog` 的确认对话框（`DialogService`，请求排队）
- `ToastHost`（右下角，最多 3 条）
- `Skeleton`（Composition 实现的微光效果）
- 离线横幅：服务器不可达、但仍在显示缓存数据时出现

## 7. 服务契约与集成

- **位置**：`src/Mambo.Core/Contracts/`。`Mambo.App` 的界面代码只能依赖这里的类型和 `VideoSurface` 的公开 API，不得引用 Core 的实现类、DTO 或 Player 的内部类型。
- **组装**：`Program.cs` 调用 `AddBackendServices(fake)` 和 `AddUiServices()` 完成服务注册。
- **领域模型**（不可变 record）：`MediaItem`、`ImageRef`、`MediaLibrary`、`UserDataState`、`SeasonInfo`、`PersonInfo`、`FilterOptions`、`LibraryQuery`（排序与筛选）、`SearchGroup`、`PlaybackEntry`、`TrackInfo`、`SessionSnapshot`（含 `PlayerPhase`）、`AppError`。
- **读取模型**：
  - 可缓存的读取返回 `IQuery<T>`：`Current`、`Error`、`IsRefreshing`、`Updated` 事件、`RefreshAsync()`；
  - 分页读取返回 `IPagedQuery<T>`：`Items`、`HasMore`、`IsLoading`、`Error`、`LoadMoreAsync()`、`Updated` 事件。
- **服务接口**：

  | 接口 | 职责 |
  |---|---|
  | `ISessionService` | 当前会话；状态（Restoring / LoggedOut / LoggedIn / Unreachable）；`LoginAsync`、`RestoreAsync`、`LogoutAsync`；`Changed` |
  | `ILibraryService` | 媒体库列表；首页（hero、继续观看、各库最新）；最近播放；资料库分页与筛选项；详情、NextUp、季、剧集分页；按库搜索 |
  | `IImageService` | 按 `ImageRef` + 像素宽度 + 优先级取图片字节，可取消 |
  | `ISettingsService`、`ILibraryPreferences` | 设置读写与变更通知；各库的排序与筛选偏好 |
  | `IPlaybackService` | `PlayAsync(PlayRequest)`；`Current`（`IPlaybackSession?`）；会话开始/结束、跳集事件 |
  | `IPlaybackSession` | `Snapshot` + `SnapshotChanged`；命令：暂停切换、跳转、倍速、音量、静音、选音轨/字幕、上一集/下一集/选集、逐帧、重试、`CloseAsync` |
  | `IUiScheduler` | 把回调投递到 UI 线程 |

- **消息**：`PlaybackStopped`、`SessionChanged`、`SessionExpired`，经 CommunityToolkit.Mvvm 的 `IMessenger` 发送。
- **线程约定**：所有事件和 `Updated` 回调都经 `IUiScheduler` 在 UI 线程触发，ViewModel 可以直接绑定。
- **XAML 绑定**：不要直接以带 `init` 属性的 Contracts record 作 `x:DataType`，WinUI 生成的 setter 会报 CS8852；用 getter-only 的 partial ViewModel 或投影类型。
- **错误约定**：读取错误放进 `Error` 属性；命令失败抛 `AppException`（带 `AppError`，`Message` 为中文）。
- **VideoSurface**：`PlayerOverlay` 只需在 XAML 里放置 `VideoSurface`，调用 `Attach(IPlaybackSession)` / `Detach()`，不接触 mpv 或 DXGI。
- **假实现**（`src/Mambo.Core/Fakes/`）：
  - 确定性的示例数据：若干媒体库（其中一个约 5000 项，用于性能测试）、多季多集的剧集、演职人员；
  - 图片由程序生成（渐变色块 + 标题文字），不使用任何外部图片；
  - 假播放会话：模拟打开中 → 播放、进度推进、缓冲、失败、12 集连播、轨道列表；画面区域显示纯色；
  - 可配置延迟和错误率，用来调试加载态和错误态。
- **演示模式**：用 `Mambo.exe --fake`（或环境变量 `MAMBO_FAKE=1`）启动时注册全部假实现。设置页的「预览播放页」也使用假播放会话。
- **变更规则**：
  - 契约 v1 经评审后冻结，优先采用向后兼容的修改（新增成员或类型）；破坏性修改必须同步更新所有调用方与测试。
  - 契约的增量变更记在 `docs/decisions/P1a-contracts.md` 的「契约变更记录」。


## 8. 测试（xUnit）

测试依据 [SPEC.md](SPEC.md) 的规格编写，不复制原项目的任何测试文件。

- **Core**：
  - 地址规范化与错误文案
  - 认证头格式
  - 目标解析
  - 媒体源资格与优先级
  - 候选生成与排序（含转码可达）
  - `StreamUrlResolver` 的重定向场景（用 stub HttpMessageHandler）
  - 连播计划
  - 上报载荷、节奏、失败分类
  - 发件箱：原子写、作用域、幂等、至少一次投递、死信
  - Filters 值的两种形态
  - 图片缓存键与淘汰
  - `UrlRedactor`
  - 搜索词规范化与单集折叠为剧集
  - 年代展开为年份
  - 继续观看按剧集去重
  - 集号解析与标题格式
  - 自动隐藏规则
  - 导航历史规则
  - 分页校验
  - `RequestScheduler`、`QueryCache`（用 FakeTimeProvider）
- **会话**：用 FakeEngine 回放事件脚本（§4.2 表中的每一行）。
- **Player**：在无头环境下运行真实 libmpv（vo=null、ao=null、`av://lavfi:testsrc`）。DLL 不存在时跳过。

## 9. 参考资料

- **nami**（WinUI 3 + libmpv composition，C#/.NET 10）：https://github.com/subdiox/nami（commit 5e902dd）
  - `src/Nami/` 下：`Mpv/LibMpv.cs`、`Mpv/MpvPlayer.cs`、`Controls/VideoView.cs`、`Interop/SwapChainPanelInterop.cs`、`Player/HdrController.cs`、`Interop/DisplayInfo.cs`、`Interop/EmptyCursor.cs`、`Interop/NonClientHook.cs`、`Interop/BridgeEraseHook.cs`、`MainWindow.xaml.cs`、`App.xaml`、`Program.cs`、`Nami.csproj`
  - 仓库根目录下：`scripts/publish.ps1`、`installer/Nami.iss`
- **Mio**：https://github.com/AobaRino/Mio（commit 1854a76）
  - `Player/MpvPlayer.cs`、`Services/SwapChainBinder.cs`、`Services/FullscreenService.cs`
- **mpv（修订 304426c）**：https://github.com/mpv-player/mpv/tree/304426c390901436fb1d4a63efbd582ae80c88f4
  - `DOCS/man/options.rst`：d3d11-output-mode、d3d11-composition-size、target-colorspace-hint(-mode)、hdr-reference-white
  - `DOCS/man/input.rst`：display-swapchain；`loadfile <url> [<flags> [<index> [<options>]]]`；`sub-add`；playlist_entry_id
  - `include/mpv/client.h`
  - 维护者答复（交换链何时可用）：https://github.com/mpv-player/mpv/discussions/17124
- **Microsoft 文档**：
  - Windows App SDK 2.0 发行说明
  - 自包含部署
  - DisplayInformation / DisplayAdvancedColorInfo（Microsoft.Graphics.Display）
  - NonClientRegionKind / InputNonClientPointerSource
  - OverlappedPresenter
  - IDXGISwapChain2::SetMatrixTransform
  - DirectX 与 XAML 互操作（SwapChainPanel）
  - Credential Manager（CredWriteW）
