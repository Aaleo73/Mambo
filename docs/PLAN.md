# Mambo 实施计划：WinUI 3 + C# + libmpv（composition 交换链）

> 这是一份可以独立执行的完整计划。执行者有两个：Codex（GPT）负责后端，Claude 负责前端（见 §14）。动手前请先读第 0、10、14 节；附录 A 是功能与交互的依据。

## 0. 执行约定

1. **按阶段推进，按分工执行**：P0 → P8。**后端由 Codex（GPT）负责，前端由 Claude 负责**，各自只修改自己拥有的路径；所有权、契约和协作流程见 §14。P2 设计稿可以与 P0、P1、P3 并行。每个阶段做完都要逐条跑完验收，全部通过后在文末"进度"打勾，再进入下一阶段。
2. **两个关卡必须停下等用户确认**：
   - ① P0 结束：汇报视频技术验证的结果；
   - ② P2 结束：交付设计稿。
   P4、P5 的界面必须照确认后的设计稿实现。
3. **只用命令行工具链**：.NET SDK + VS2022 BuildTools（MSBuild/MSVC）+ Windows SDK，不依赖 Visual Studio IDE。
4. **资产约束（硬性）**：
   - 不复用原项目 `D:\MAKISEV\emby-mpv-player` 的任何资产，包括图标、图片、配置、脚本、锁文件、本地缓存、许可证文件、源代码文件。
   - 唯一例外是字体：使用本仓库 `assets/fonts/MiSans-*.ttf`。
   - 原项目只能作**只读的行为参考**。附录 A 已整理出所需的行为规格，一般不必再去读原项目。
5. **需要用户参与的事项**（文中标【需用户】）：
   - 安装 .NET SDK，或授权执行 winget；
   - 提供测试用的 Emby 服务器和账号（运行时在界面里输入，绝不写入仓库）；
   - 提供本地测试片源，含 4K HEVC HDR10 样片；
   - HDR、多显示器、DPI 的人工检查；
   - 两个关卡的确认。
6. **网络**：`dotnet restore` 和下载 libmpv 都需要联网。
7. **安全**：服务器地址、令牌、密码不得出现在仓库、日志、测试数据或提交信息里。日志一律经 `UrlRedactor` 脱敏。
8. **语言**：界面文案和错误文案用简体中文，代码标识符用英文；提交信息用中文 conventional commits（例如 `feat(player): …`）；不推送远端。
9. **从第一天起遵守 AOT 兼容规则**（见 §4.3）。

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
  - 开源播放器 nami 和 Mio 已经这样实现（见附录 D）。
- **已确认的产品决定**：
  - 技术栈：WinUI 3 + C#/.NET 10，只支持 Windows；
  - 外部播放器（用户自备 mpv.exe）保留，放在 P7；
  - UI 借机重新设计，但不能与原版大相径庭：先出设计稿，用户确认后再实现；
  - 全新开始，不迁移旧版数据（新的 DeviceId，需要重新登录）；
  - 在当前仓库里替换实现，Qt 雏形打 tag 留档；
  - 只用命令行工具链；
  - 不复用原项目资产（字体除外）。

## 2. 环境与前置条件

- **本机**：
  - Windows 11；
  - VS2022 BuildTools（MSVC 14.44 + MSBuild）；
  - Windows SDK 10.0.26100；
  - 已有 .NET 10 运行时，但**还没有 SDK**；
  - NVIDIA GPU；
  - 系统自带的 `C:\Windows\System32\tar.exe`（bsdtar 3.8.4），已实测能读 `.7z`。
- **需要安装**【需用户】：.NET SDK 10.0.4xx（`winget install Microsoft.DotNet.SDK.10`），并用 `global.json` 锁定版本（`rollForward: latestFeature`）。
- **建议包版本**（实施时以 NuGet 最新稳定版为准，锁定后不要随意升级）：

  | 包 | 版本 |
  |---|---|
  | Microsoft.WindowsAppSDK | 2.5.1 |
  | Microsoft.Windows.CsWin32（PrivateAssets=all） | 0.3.x |
  | CommunityToolkit.Mvvm | 8.4.x |
  | Microsoft.Extensions.DependencyInjection | 10.0.x |
  | Microsoft.Extensions.Logging.Abstractions | 10.0.x |
  | Serilog + Serilog.Extensions.Logging + Serilog.Sinks.File | — |
  | xunit.v3 + xunit.runner.visualstudio + Microsoft.NET.Test.Sdk | — |
  | Microsoft.Extensions.TimeProvider.Testing | — |

  **不引入**：Microsoft.Extensions.Hosting（拖慢启动）、Polly、Serilog.Settings.Configuration（依赖反射，不兼容 AOT）。
- **libmpv**：
  - 来源：shinchiro/mpv-winbuild-cmake 的 GitHub Releases，包名 `mpv-dev-x86_64-<日期>-git-<修订>.7z`。
  - 版本要求：**必须 ≥ mpv 0.41**，因为要用 `d3d11-output-mode=composition`、`d3d11-composition-size` 和 `display-swapchain`。
  - 建议用 release `20260610`（git-304426c）：已核实这个修订的文档包含上面三项。若该 release 已下架，就用最新的包，并在 P0 重新验证。
  - 包内文件：`libmpv-2.dll`（约 117MB）、`include/mpv/*.h`、`libmpv.dll.a`（MSVC 用不上）。
  - C# 用 P/Invoke 动态加载，不需要导入库。
- **许可**：libmpv 是 GPL-2.0-or-later，shinchiro 构建还带有 Apache-2.0 组件，两者只能在 GPLv3 下共存。因此 Mambo 采用 **GPL-3.0-or-later**。NOTICES 在 P8 自行撰写，GPL 文本从 gnu.org 获取。

## 3. 仓库约定

- **当前状态**：`D:\MAKISEV\qt-mambo`，master 分支只有一个提交 808d4dd（Qt 雏形）。
- **分支与工作区**：前后端各用一个 git worktree 并行开发，准备步骤见 §14.4。
- **P0 第一步**（Codex 在 `backend` 工作区执行）：
  1. 删除 `CMakeLists.txt`、`qml/`、`src/app/`
  2. 把 `assets/fonts/MiSans-*.ttf` 移到 `src/Mambo.App/Assets/Fonts/`
  3. 把 `.gitignore` 换成 .NET 版本，至少包含 `bin/ obj/ .vs/ *.user publish/ artifacts/ third_party/libmpv/bin/ third_party/libmpv/download/`
- 仓库目录名暂时不改。

## 4. 总体架构

### 4.1 解决方案结构

```
qt-mambo/
├─ global.json · Directory.Build.props · Directory.Packages.props（集中包管理）· Mambo.slnx
├─ third_party/libmpv/   libmpv.lock.json（本仓库自行生成）· include/mpv/*.h（可入库，供对照）· bin/（忽略）
├─ scripts/              fetch-libmpv.ps1 · publish.ps1 · make-installer.ps1
├─ installer/Mambo.iss   design/（HTML 原型 + tokens.css）  docs/（PLAN.md、decisions/、handoff/）
├─ src/
│  ├─ Mambo.Core/    net10.0，不依赖 WinUI
│  │  ├─ Contracts/   前后端契约：服务接口、领域模型、读取模型、消息（§14.3）
│  │  ├─ Emby/        EmbyHttp、EmbyApi、AuthHeader、ServerAddress、DeviceProfileFactory、ImageUrlBuilder、Dto/*、EmbyJsonContext
│  │  ├─ Session/     SessionManager、StoredSession、ISecretStore、AccountScope
│  │  ├─ Errors/      AppError、AppErrorKind、ErrorCodes、ErrorText（中文）、AppException、HttpErrorMapper
│  │  ├─ Net/         RequestScheduler
│  │  ├─ Caching/     QueryCache、QueryKey、QueryPersistence
│  │  ├─ Images/      ImageRequest、ImageKey、ImageByteCache（内存 + 磁盘）、ImageFetcher
│  │  ├─ Library/     LibraryQuery、FilterOptions、LibraryPreferencesStore、VideoViewFilter
│  │  ├─ Playback/    PlaybackCoordinator、PlaybackTargetResolver、SeasonPlan、MediaSourceSelector、StreamCandidateBuilder、
│  │  │               StreamUrlResolver、EntryPreparer、PlaybackSession（+States）、SessionInput、SessionSnapshot、IPlayerEngine、EngineEvent
│  │  ├─ Reporting/   PlaybackReporter、ReportPayloads、StopOutbox、StopReportRecord、ReportFailureClassifier
│  │  ├─ Settings/    AppSettings、SettingsStore、AppPaths、AtomicFile
│  │  ├─ Diagnostics/ UrlRedactor、StartupTimeline
│  │  └─ Fakes/       契约的假实现，供演示模式和前端开发使用（§14.3）
│  ├─ Mambo.Player/  net10.0
│  │  ├─ LibMpv/      LibMpvNative（LibraryImport）、MpvStructs、MpvHandle（SafeHandle）、MpvNodeReader/Builder、MpvRuntime、MpvCore、LibMpvEngine
│  │  └─ External/    ExternalMpvEngine、MpvIpcClient、MpvExecutableApproval（P7）
│  └─ Mambo.App/     net10.0-windows10.0.26100.0，WinUI 3，全部 XAML 都在这里
│     ├─ Program.cs、App.xaml、MainWindow.xaml、app.manifest（PerMonitorV2）、NativeMethods.txt（CsWin32）
│     ├─ Composition/ BackendServices（Codex）、UiServices（Claude）、AppShutdownCoordinator、UiScheduler
│     ├─ Shell/       ShellPage、TitleBar、Sidebar、PageHost、Navigator、NavEntry、ToastHost、DialogService、ShortcutService
│     ├─ Window/      WindowChrome（非客户区、窗口按钮）、MamboBackdrop、FullscreenController、PowerRequest、EmptyCursor
│     ├─ Video/       VideoSurface（SwapChainPanel 子类）、SwapChainPanelInterop、HdrController
│     ├─ Images/      ImageLoader、RemoteImage（模板控件）、DecodedImageCache
│     ├─ Platform/    WindowsCredentialStore（CredWrite）、Pickers
│     ├─ Views/ 与 Views/Controls/   各页面、PlayerOverlay、MediaCard、LandscapeCard、CardRail、HeroCarousel、FilterBar、
│     │                              SeasonPills、EpisodeRail、PeopleRail、Skeleton
│     ├─ Debug/       VideoLab 调试页（Codex）
│     ├─ ViewModels/
│     ├─ Themes/      Tokens.xaml、Typography.xaml、Controls.xaml、Player.xaml（深色）
│     └─ Assets/      Fonts/MiSans-*.ttf、AppIcon.ico（P2 新设计）
└─ tests/  Mambo.Core.Tests（xUnit）· Mambo.Player.Tests（无头真实 libmpv：vo=null、ao=null、av://lavfi:testsrc）
```

- **项目引用**：App → Core、Player；Player → Core；Tests → Core、Player。
- **XAML 只放在 Mambo.App**：类库里放 XAML 会引出 PRI 合并的麻烦。

### 4.2 Mambo.App.csproj 要点（参考 nami 的 `Nami.csproj`）

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

### 4.3 AOT 兼容规则

- Release 用 Native AOT，Debug 用 JIT；另备一份 ReadyToRun、不裁剪的发布配置，作为 AOT 受阻时的退路。
- 互操作只用 `LibraryImport` 和函数指针，**不用 `[ComImport]`**。
- JSON 一律用 System.Text.Json 源生成。
- 绑定只用 `{x:Bind}`；万一要用 `{Binding}`，必须加 `[GeneratedBindableCustomProperty]`。
- 暴露给 WinRT 的类都写成 `partial`；用 CommunityToolkit.Mvvm 8.4 的 partial `[ObservableProperty]`。
- 不写自定义泛型 WinRT 集合，所以增量加载要手写（见 §8.4）。
- Serilog 在代码里配置，不读配置文件。

## 5. 视频管线（核心风险，P0 先验证）

### 5.1 加载 libmpv

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

### 5.2 P/Invoke 接口

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

### 5.3 句柄与生命周期

- `sealed class MpvHandle : SafeHandleZeroOrMinusOneIsInvalid`，释放时调用 `mpv_terminate_destroy`。
  - 所有 P/Invoke 都传 SafeHandle，封送器会在调用期间持有引用，保证 terminate 不会和其它调用并发。
  - 句柄释放后再调用会抛 `ObjectDisposedException`，而不是崩溃。
- **每个播放会话一个 mpv 实例**：开始播放时创建，关闭播放层时销毁。
  - mpv 在后台线程创建，与 PlaybackInfo 请求、URL 探测并行，不额外增加等待。

### 5.4 初始化前设置的选项

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
- HDR 相关的设置在初始化之后应用（§5.9）。

### 5.5 事件模型

- **专用事件线程**：后台线程 `mpv-events` 循环调用 `mpv_wait_event(h, -1)`。
  - 事件数据只在下一次 wait 之前有效，所以每个事件**立即拷贝**成 `EngineEvent` 记录：`StartFile(id)`、`FileLoaded`、`PlaybackRestart`、`EndFile(id, reason, error)`、`PropertyChanged(PropId, value)`、`VideoReconfig`、`Log`、`QueueOverflow`、`Shutdown`。
  - 拷贝后写入单读者的 `Channel<EngineEvent>`，作为播放会话 actor 的收件箱。
- **交换链**：收到 `VIDEO_RECONFIG` 时，事件线程用 `mpv_get_property("display-swapchain", INT64)` 重新读取。指针有变化就发出 `SwapChainChanged(ptr)`，由 `VideoSurface` 切回 UI 线程处理。
  - **不要观察 `display-swapchain`**：它不在 mpv 的变更事件表里，只会推送一次初始值。
- **队列溢出**：收到 `QUEUE_OVERFLOW` 时，会话直接重新读取关键属性。
- **UI 更新**：UI 线程从不处理原始事件。会话把最新的 `SessionSnapshot` 交给发布器；发布器只在没有待处理回调时才往 DispatcherQueue 投递一次，上限约 10Hz。
- **用户操作**：seek、暂停、改尺寸一律用 `*_async`，UI 线程从不等待 mpv。

### 5.6 观察的属性

`reply_userdata` 用 `PropId` 枚举值区分属性，省去字符串比较。

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

### 5.7 END_FILE 的处理（细节见 §6.2）

- **ERROR(4)**：尚未开播就换下一个候选，候选用尽则跳过或报失败；已开播则上报停止并提示「播放中断」。
- **EOF(0)**：上报停止，然后自动进入下一集、准备下一集，或在季末结束。
- **STOP(2)**：上报停止，再执行用户挂起的操作（下一集、上一集、选集、关闭）。
- **QUIT(3)**：上报停止，结束会话。
- **REDIRECT(5)**：忽略，随后会收到新的 START_FILE。

### 5.8 交换链绑定、DPI 与尺寸（照搬 nami 的 `VideoView.cs` 和 `SwapChainPanelInterop.cs`）

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
- **圆角**：视频区的圆角在宿主视觉上用 `CompositionGeometricClip` + `CompositionRoundedRectangleGeometry` 实现。
- **解绑**：销毁 mpv 前先 `SetSwapChain(null)`。

### 5.9 HDR（composition 模式下 mpv 看不到显示器，必须由应用告诉它）

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

### 5.10 全屏、光标、屏保、材质

- **全屏**：用 `AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)`，同时收起标题栏和侧栏两行。
  - 若从最大化进入全屏时出现还原闪烁，改用 Mio 的做法：保持 Overlapped presenter，设 `IsResizable=false`、`SetBorderAndTitleBar(false,false)`，再 `MoveAndResize` 到显示器边界。在 P0 定方案。
  - 还要检查顶部是否有 1px 缝隙。
- **光标**：控制层隐藏时，用 `ProtectedCursor` 换成空光标（参考 nami 的 `EmptyCursor`）。
- **屏保**：mpv 在 composition 模式下不会阻止屏保，由应用在"播放中且未暂停"时调用 `SetThreadExecutionState(ES_CONTINUOUS|ES_DISPLAY_REQUIRED|ES_SYSTEM_REQUIRED)`。
- **材质**：画面上方**不能用亚克力**（XAML 亚克力采样不到视频交换链，全屏时还会闪白）。播放器控件和弹出菜单都用半透明纯色加渐变。

### 5.11 关闭顺序

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

### 5.12 兜底方案

如果 `SwapChainPanel` 绑定在 P0 走不通，改用 `ICompositorInterop::CreateCompositionSurfaceForSwapChain` + SpriteVisual，通过 `ElementCompositionPreview.SetElementChildVisual` 挂到元素上。仍然在 WinUI 的合成树里，仍然没有 airspace 问题。

## 6. 播放会话

### 6.1 组件（除引擎外都在 Core）

- **`PlaybackCoordinator.PlayAsync(item, startTicks?)`**：解析目标 → 生成连播计划 → 创建引擎和会话。
- **`PlaybackTargetResolver`**：规则见附录 A.9。
- **`SeasonPlan`**：规则见附录 A.9。生成失败**不再阻断播放**，降级为单集播放。
- **`EntryPreparer`**：
  - **每次都调用 PlaybackInfo**，不保留原版"用本地 PlaySessionId 走捷径"的做法；
  - 然后依次经过 `MediaSourceSelector`、`StreamCandidateBuilder`、`StreamUrlResolver`；
  - 产出 `PreparedEntry`：候选列表（URL、播放方式、请求头或空）、PlaySessionId、MediaSourceId、LiveStreamId、外挂字幕列表、标题、起始 ticks（后续追加的集为 0）。
- **`PlaybackSession`**：单线程 actor。
  - 唯一的读循环处理 `Channel<SessionInput>`：引擎事件、用户命令、计时器、异步结果；
  - 网络工作以 Task 形式运行，完成后把结果投回收件箱；
  - 上报经会话内的有序队列发出，保证 Playing → Progress → Stopped 的顺序；
  - 可以用 FakeEngine + `FakeTimeProvider` 测试。

### 6.2 状态与转换

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
| 关闭 | — | `stop`，最多等 2 秒 END_FILE，再按 §5.11 收尾 → Closed |
| 引擎意外退出 | — | 完成当前集的停止上报 → Failed（「播放器已退出」） |

### 6.3 连播：mpv 播放列表里只保留"当前集 + 下一集"

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

### 6.4 选源、候选与 URL 解析

- **`MediaSourceSelector`**：资格与优先级见附录 A.9。与原版的差别是支持带 `RequiredHttpHeaders` 的片源，把这些请求头按文件传给 mpv。
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

### 6.5 上报

- **载荷字段与发送节奏**：见附录 A.9。
- **失败分类**（`ReportFailureClassifier`）：
  - 401/403：等待重新登录；
  - 408/425/429/5xx 或网络错误：重试；
  - 其它 4xx：死信，记日志后丢弃。
- **转码清理**：转码会话停止后调用 `DELETE /Videos/ActiveEncodings?DeviceId&PlaySessionId`。
- **缓存失效**：停止后通过 `WeakReferenceMessenger` 广播 `PlaybackStopped(itemId, seriesId, seasonId)`，让相关缓存失效（§7.6）。

### 6.6 停止发件箱（`StopOutbox`）

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

### 6.7 引擎抽象与外部播放器（P7）

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

## 7. Emby 客户端、缓存与数据

### 7.1 网络

- **两个 `SocketsHttpHandler`**，公共设置：
  - `AllowAutoRedirect=false`、`UseCookies=false`、`AutomaticDecompression=All`
  - `ConnectTimeout=10s`、`PooledConnectionLifetime=5min`
  - `HttpClient.Timeout=Infinite`，改用 linked CTS 给每个请求设截止时间：默认 30 秒，`/Items/Filters` 和探测请求 3 秒
  - 启用 HTTP/2（`RequestVersionOrLower`）
- **API 客户端**：从不跟随重定向。
- **图片客户端**：手动跟随重定向，最多 5 跳，只允许同主机同端口，协议不变或 http → https。
  - 必须手动处理的原因：.NET 跟随重定向时会去掉 `Authorization`，但**不会**去掉 `X-Emby-Token`。

### 7.2 认证头、地址与端点

- **认证头**：`Authorization: Emby UserId="<uid>", Client="Mambo", Device="Windows", DeviceId="<guid>", Version="<程序集版本>"`，另加 `X-Emby-Token: <token>`。
  - `AuthenticateByName` 请求不带 UserId 和令牌。
  - 客户端不在 URL 中自行添加 api_key；§6.4 中保留登录服务器签发的下载 Location 是明确例外。
- **`ServerAddress.Normalize`**：规则和错误文案见附录 A.1。
- **端点、字段集、DeviceProfile**：见附录 A.1。DeviceProfile 在原版基础上，为所有字幕格式加上 `Embed`（交给 mpv 自己读内封字幕）。

### 7.3 DTO

- 全部用 `sealed record`，成员可空，忽略未知字段。
- 一个 `EmbyJsonContext : JsonSerializerContext`：`PropertyNameCaseInsensitive`、`WhenWritingNull`、`AllowReadingFromString`，再加一个 `NameOrStringConverter`，兼容 `/Items/Filters` 的值为字符串或 `{Name}` 对象两种形态。
- DTO 映射成领域模型（`MediaItem`、`ImageRef`、`PlaybackState` 等）后再交给 ViewModel，ViewModel 不接触原始 DTO。

### 7.4 错误模型

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
- **ViewModel**：通过 `RunGuarded(...)` 把错误转成错误态或 Toast。

### 7.5 请求调度（`RequestScheduler`）

- **通道**：`metadata` 并发 4，其中保留 1 个给前台；`reliability` 并发 2。
- **优先级**：Foreground / Visible / Background，用 `PriorityQueue<(priority, seq)>` 排队。
- **截止时间**：从入队算起，前台 10 秒，其余 20 秒。
- **重试**：幂等的 GET 遇到可重试错误时重试一次（截止时间内）。
- **取消**：`scopeToken` 绑定页面或账号作用域，离开页面、切换账号时取消排队中和进行中的请求。

### 7.6 SWR 缓存、持久化与失效

- **`QueryCache`**：
  - 键为 `QueryKey(AccountScope, Kind, Args)`；
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

### 7.7 图片管线

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
  - 显示：字节 → `InMemoryRandomAccessStream` → `BitmapImage { DecodePixelType=Logical, DecodePixelWidth=<DIP 宽> }` → `SetSourceAsync`，即按显示尺寸解码。
  - 复用：`DecodedImageCache` 以弱引用缓存 `BitmapImage`，键为（缓存键, 解码宽度），回到页面时不用重新解码。
  - `RemoteImage` 控件：显示占位 → 图片就绪后淡入 140ms；在 Unloaded 时和列表容器回收时（`ContainerContentChanging` 且 `InRecycleQueue`）取消加载。
- **预取**：hero 预取后两张幻灯片（Hero 优先级）；卡片悬停 300ms 后预取详情数据（后台优先级）。

### 7.8 凭据、设置、路径、日志

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

## 8. 界面架构

### 8.1 MVVM、依赖注入与启动

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

### 8.2 窗口、标题栏、背景

- **窗口**：`OverlappedPresenter`，`SetBorderAndTitleBar(true,false)`，`ExtendsContentIntoTitleBar=true`，`SetTitleBar(拖动区域)`。
- **标题栏**：自绘 40px 高，含后退/前进按钮，以及自绘的 46×40 窗口按钮。
  - **不用** WinAppSDK 自带的 `TitleBar` 控件：它只有 32/48px 两种高度。
- **非客户区**（`InputNonClientPointerSource`）：
  - Caption 区 = 标题栏减去其中的可交互控件，在尺寸、DPI、可见性变化时重新计算；
  - Maximize 区 = 自绘的最大化按钮，鼠标悬停时出现 **Snap Layouts**；
  - 对最大化按钮的点击会以 `HTMAXBUTTON` 送达，用窗口子类化吞掉 `WM_NCLBUTTONDOWN/UP/DBLCLK`，自己切换最大化（参考 nami 的 `NonClientHook`）；
  - 悬停视觉用 `InputNonClientPointerSource.PointerEntered/Exited` 实现。
- **背景**：自定义 `SystemBackdrop` + `DesktopAcrylicController`，强制 `IsInputActive=true`，窗口失焦时也保持亚克力；不支持时回退到 Mica。
  - tint 不追求复刻原版：原版在 Win11 上的 tint 实际被系统忽略。以系统默认亚克力为起点，在设计稿阶段调整。
- **尺寸**：默认 1500×860 居中；最小 1100×720（`PreferredMinimumWidth/Height`），DPI 变化时重新设置；记住窗口位置。
- **屏保**：`PowerRequest` 见 §5.10。

### 8.3 导航（自建 Navigator + PageHost，不用 Frame）

- **记录与栈**：
  - 每条记录为 `NavEntry(Route, Param, Key, ViewState)`，ViewState 保存滚动位置、搜索词等；
  - 后退栈、前进栈各最多 12 条，超出时丢弃最旧的；
  - 导航到新页面时清空前进栈；其余规则见附录 A.11。
- **`PageHost`**：
  - 用 LRU 保留最多 4 个活的页面实例，首页常驻；不活跃的页面折叠隐藏；
  - 返回活页面是瞬时的，滚动位置、图片和布局都在；
  - 被淘汰的页面从缓存的 ViewModel 重建，等足够的项实现后用 `ChangeView(..., disableAnimation:true)` 恢复滚动位置。
- **页面接口**：页面实现 `INavigable`：`OnNavigatedTo(entry, mode)`；`OnNavigatingFrom` 保存 ViewState。
- **转场**：用 Composition 动画做淡入加位移。
- **播放层打开时**：`CanGoForward=false`；后退、Esc、鼠标后退键改为关闭播放层。
- **侧栏**：只显示可播放的视频库（规则见附录 A.1）；侧栏搜索框与当前搜索记录的搜索词双向同步。

### 8.4 列表与虚拟化

- **资料库和最近播放**：`GridView` + `ItemsWrapGrid`，在 `SizeChanged` 时重算条目宽度，让各列两端对齐。
- **横向卡片行**（继续观看、最新、剧集、演职人员）：水平 `ListView`（`ItemsStackPanel`），鼠标滚轮映射为横向滚动。
- **容器设置**：`SelectionMode=None`、`IsItemClickEnabled=true`，用精简的 `ListViewItemPresenter` 样式。
- **增量加载**：监听 `ScrollViewer.ViewChanged`，距末尾 1.5 屏以内时加载下一页。不用 `ISupportIncrementalLoading`，以规避 AOT 下泛型 WinRT 集合的问题。
- **渲染开销**：在 `ContainerContentChanging` 里分阶段加载图片；卡片上不用 `ThemeShadow` 和亚克力。
- **为什么选 GridView/ListView 而不是 ItemsView**：成熟，有 `ScrollIntoView`，并且自带 ConnectedAnimation 辅助方法。

### 8.5 动效

- **卡片 → 详情**：`PrepareConnectedAnimation("poster", item, "PosterImage")` → 导航 → 详情页在图片就绪后 `TryStart`；返回时用 `TryStartConnectedAnimationAsync`，它会先把卡片滚动到可见。
- **hero 轮播**：两层图片交叉淡化，7 秒自动切换；页面不可见或窗口最小化时暂停。
- **时长与缓动**：来自 `Tokens.xaml`；系统关闭动画（`UISettings.AnimationsEnabled`）时动画瞬间完成。

### 8.6 播放层（外壳里的一层覆盖，不是导航页面；底下的页面一直活着）

```
PlayerOverlay（Grid，RequestedTheme=Dark，IsTabStop=True，持有焦点）
├─ VideoHost（黑底、圆角裁剪）→ VideoSurface（SwapChainPanel）
├─ InputSurface（透明）：单击 250ms 后切换暂停 / 双击最大化；指针移动唤出控制层；隐藏时换空光标
├─ TopBar（渐变）：标题、关闭
├─ 状态层：打开中（ProgressRing + 20 秒"加载较慢"提示 + 关闭）| 缓冲胶囊 | 失败（重试 / 关闭）
├─ BottomBar（渐变 + 半透明纯色面板）：带已缓冲区间的进度条、上一集 / 下一集、播放 / 暂停、时间、
│     倍速菜单、轨道菜单（字幕 / 音轨 / 关闭字幕）、悬停展开的音量、全屏、最大化
└─ 选集面板：布局按设计稿（原版是侧栏位置的 208px 网格）
```

- 画面上方**不用亚克力**，弹出菜单的 presenter 也要改成半透明纯色背景。
- 按钮设 `AllowFocusOnInteraction=False`，键盘焦点始终留在播放层上。
- 按键在 `PreviewKeyDown` 里处理（包括已被标记为处理过的事件）。按键表、自动隐藏规则见附录 A.10。
- 支持假数据模式，供设置页的「预览播放页」使用。

### 8.7 全局快捷键

用外壳上的 `KeyboardAccelerator` 实现，具体按键见附录 A.11。

### 8.8 主题、字体、图标

- **主题**：`Tokens.xaml` 的取值来自确认后的设计稿；外壳用浅色主题，播放层用深色。
- **MiSans**：每个字重一个 `FontFamily` 资源（如 `ms-appx:///Assets/Fonts/MiSans-Medium.ttf#MiSans`），回退到 Segoe UI Variable。免打包应用里 `ms-appx` 也能解析。
- **图标**：以 Segoe Fluent Icons 为主（Win10 回退到 Segoe MDL2 Assets），特殊图标用 `PathIcon`。
- **应用图标**：P2 重新设计，P4 生成多尺寸 `.ico`。

### 8.9 通用组件

- 基于 `ContentDialog` 的确认对话框（`DialogService`，请求排队）
- `ToastHost`（右下角，最多 3 条）
- `Skeleton`（Composition 实现的微光效果）
- 离线横幅：服务器不可达、但仍在显示缓存数据时出现

## 9. 设计稿（P2，与 P0、P1、P3 并行；用户确认后才写页面）

- **负责**：Claude。可以在 P0 进行期间就开始，因为它只依赖本计划，不依赖代码。
- **交付形式**：`design/` 目录下的 HTML 交互原型，基于 `design/tokens.css`，用户用浏览器打开查看（Claude 也可以另外发布为私有 Artifact，方便预览）。
- **覆盖范围**：
  - 外壳：1500×860 与最小尺寸 1100×720 两种；
  - 页面：引导/登录、首页、最近播放、资料库（筛选/排序）、详情（电影、剧集）、搜索、设置；
  - 播放页：打开中、缓冲、失败、控制层显示/隐藏、倍速与轨道菜单、选集、全屏；
  - 通用：对话框、Toast，以及空、错、加载三种状态；
  - 新的应用图标。
- **约束**：原型只用 XAML 能实现的效果：画面上方不做背景模糊，阴影少用。
- **保留**（与原版接近，参考附录 B）：
  - 无边框亚克力玻璃外壳，浅色调；
  - 40px 标题栏（后退/前进 + 居中标题）、208px 侧栏结构；
  - MiSans 字体、主色 #0c68b8；
  - 海报 150×220、横版 300×169 的卡片比例；
  - 首页 hero 轮播 + 横向卡片行；详情页 hero + 季/集 + 演职人员。
- **拟调整**（在设计稿里逐项标出，供用户确认）：
  - 最大化按钮支持 Snap Layouts；
  - 控件统一为 Fluent 风格；
  - 播放页"全屏优先"：选集可以呼出，新增片尾"下一集"提示；
  - 浏览 ↔ 播放的 3D 翻折改为缩放或淡入；
  - 卡片 → 详情用 ConnectedAnimation；
  - 评估深色模式。
- **确认之后**：把 token 落成 `Tokens.xaml`，并把确认结论记到 `docs/decisions/P2-design.md`。

## 10. 分阶段实施

### P0 工具链、骨架与视频技术验证（关卡 ①）

**负责**：Codex（GPT），在 `backend` 工作区完成。P0 结束后，`Program.cs`、`App.xaml`、`MainWindow` 移交给 Claude；`Video/`、`Debug/` 仍归 Codex（见 §14.1）。

**任务**
1. `dotnet --list-sdks` 检查 SDK；没有就请用户安装（或经用户同意执行 winget）【需用户】。
2. 仓库整理（§3；工作区准备见 §14.4）。
3. 解决方案骨架：
   - `global.json`
   - `Directory.Build.props`：LangVersion latest、Nullable、ImplicitUsings、Deterministic、AnalysisLevel latest-recommended
   - `Directory.Packages.props`
   - `Mambo.slnx`
   - Core、Player、App、Core.Tests 四个项目
4. `scripts/fetch-libmpv.ps1`：
   1. 按 `third_party/libmpv/libmpv.lock.json` 里的 release URL 下载到 `third_party/libmpv/download/`；
   2. 首次运行时计算压缩包和 DLL 的 SHA-256 并写入 lock 文件，之后每次都校验；
   3. 用 `tar.exe -xf <包> libmpv-2.dll include/mpv` 解压；
   4. 把 DLL 放进 `third_party/libmpv/bin/`。
5. Player：`LibMpvNative`、结构体、`MpvHandle`、`MpvRuntime`、`MpvCore`（选项、事件线程、观察属性、node 读写、用 `command_node` 执行 `loadfile`）。
6. App：
   - `Program.Main`；
   - `MainWindow`：最小宿主窗口，用系统标题栏即可（自绘标题栏、非客户区、亚克力在 P4 由 Claude 实现）；
   - `VideoSurface`：SwapChainPanel、互操作、DPI 与尺寸处理；
   - `HdrController`；
   - 调试页 **Video Lab**（放在 `Debug/` 下）：
     - 打开本地文件或 URL；
     - 输入服务器和令牌（只放内存，不保存）；
     - 播放/暂停、拖动、HDR 模式、全屏/最大化；
     - 诊断面板：hwdec-current、video-params、video-target-params、显示器信息、`GetDesc1` 得到的缓冲区尺寸、缩放比例。
7. 一个最小可用的 `StreamUrlResolver`，用来验证 302 场景。
8. AOT 发布配置，补齐 pri/xbf。
9. 把结论写进 `docs/decisions/P0-video-spike.md`：全屏方案、HDR 结果、冷启动和首帧时间、遗留问题。

**验收**
- 不装 Visual Studio，`dotnet build -p:Platform=x64` 能成功。
- 【需用户】本地 4K HEVC HDR10 样片：`hwdec-current=d3d11va`。
- 【需用户】在 HDR 显示器上选"自动"：`video-target-params` 为 pq/bt.2020，高光正确；播放中关闭 Windows HDR，不重启就切到 SDR。
- 【需用户】Emby 直链能播；302 服务器能正确解析，且令牌不发往跨域地址（看日志和抓包）。
- 拖动缩放、最大化、还原之后，缓冲区尺寸等于面板像素。
- 【需用户】在 100/150/200% 缩放下、在显示器之间拖动时，画面都是 1:1 显示。
- 从最大化进入全屏没有闪烁，或已改用备选方案。
- 画面上的 XAML 按钮可以点击；光标可以隐藏。
- 创建/销毁 20 次，句柄数和内存不增长。
- 把 DLL 改名后，给出中文提示而不是崩溃。
- AOT 发布出来的程序能启动。

**关卡**
- 向用户汇报验收结果，用户确认后才进入 P1。
- 如果 SwapChainPanel 不可行：先试 §5.12 的兜底方案；仍然不行就停下来和用户讨论。

### P1 Core 平台层

**负责**：Codex（GPT）。

**P1a 契约与假实现（最先做）**
- 按 §14.3 编写 `src/Mambo.Core/Contracts/` 和 `src/Mambo.Core/Fakes/`，并让 App 支持 `--fake` 启动（`AddBackendServices(fake: true)`）。
- 建立 `docs/handoff/backend-status.md`，全部服务先标为"假"。
- 完成后按里程碑合并（§14.4），请 Claude 评审；评审通过即冻结为契约 v1。

**任务**
- `ServerAddress`、`AuthHeader`、`SessionManager` + `WindowsCredentialStore`
- `SettingsStore` / `AppPaths` / `AtomicFile`
- 错误模型与 `ErrorText`
- `EmbyApi` + DTO + `EmbyJsonContext`（覆盖附录 A.1 的全部端点）
- `RequestScheduler`、`QueryCache` + `QueryPersistence`
- `ImageByteCache` / `ImageFetcher`
- Serilog + `UrlRedactor`、`StopOutbox`

**验收**
- 契约 v1 已经过 Claude 评审并冻结，`docs/handoff/requests.md` 里没有未处理的阻塞请求。
- §11 列出的 Core 测试全部通过。
- 【需用户】命令行冒烟测试（一个临时的 console 或测试工具）：登录 → 列出媒体库 → 重启后会话仍在（凭据管理器里能看到 `Mambo:emby-session:v1`）。

### P2 设计稿（关卡 ②）

**负责**：Claude。

**任务**：见 §9。

**验收**：【需用户】确认设计稿；token 落成 `Tokens.xaml`。

### P3 播放引擎与会话

**负责**：Codex（GPT）。

**任务**
- `PlaybackCoordinator`、`PlaybackTargetResolver`、`SeasonPlan`（预取下一集）、`EntryPreparer`
- `MediaSourceSelector`、`StreamCandidateBuilder`、`StreamUrlResolver`
- `PlaybackSession`、`PlaybackReporter` + `StopOutbox` 接入
- 外挂字幕、转码清理、上报真实倍速、停止后缓存失效
- `LibMpvEngine`（实现 `IPlayerEngine`）
- Video Lab 增加"按 itemId 播放"
- 新增 `Mambo.Player.Tests`

**验收**
- FakeEngine 事件脚本测试覆盖：候选回退、连播跳集、季末、中途关闭、mpv 自动切集、上报顺序。
- 无头 libmpv 测试通过。
- 【需用户】真实服务器：
  - Emby 后台看到的 Playing/Progress/Stopped 位置和倍速正确；
  - 强制转码能播放，停止后会被清理；
  - 一季能连续播完；
  - 播放中断网再恢复，发件箱会补发。

### P4 外壳与浏览页面（设计稿确认后）

**负责**：Claude。先基于假实现开发；`backend-status.md` 把某项服务标为"真"后，切换到真实服务验证。

**任务**
- 外壳：`WindowChrome`（从 P0 移来：自绘 40px 标题栏、非客户区与 Snap Layouts、亚克力背景）、`Navigator` / `PageHost`、侧栏
- 页面：引导/登录、首页、资料库（筛选器 + 偏好记忆）、最近播放、搜索、详情、设置（含关于、许可、第三方声明）
- 组件与基础设施：图片组件、Toast、对话框、骨架屏、全局快捷键、应用图标

全部按附录 A 的行为、按设计稿的视觉实现。

**验收**
- 每页与设计稿截图对照。
- 前进/后退各 12 条；每一页（包括搜索）返回时都恢复滚动位置。
- 冷启动 800ms 内显示缓存的首页。
- 5000 项的资料库滚动保持 60fps，解码内存受控。

### P5 播放页 UI

**负责**：Claude。会话或引擎层面的问题通过 `requests.md` 交给 Codex。

**任务**
- `PlayerOverlay` 的内容区和全屏两种模式
- 控件、按键、自动隐藏、光标
- 轨道、倍速、音量、选集、上一集/下一集
- 错误态与"加载较慢"提示
- 阻止屏保、焦点处理、Esc 的语义

**验收**（按附录 A.10 逐条手动测试）
- 所有按键都生效；单击与双击能区分。
- 全屏时不残留外壳；Esc 先退出全屏，再关闭播放层。
- 关闭播放后，详情、最近播放、资料库的进度已刷新。

### P6 打磨与加固

**负责**：双方各自打磨自己的部分。Claude：动效、无障碍、界面性能；Codex：启动、内存、日志、AOT。

**任务**
- ConnectedAnimation、hero 预取、悬停预取
- 启动调优、内存分析
- 如果改尺寸时的闪烁明显，参考 nami 的 `BridgeEraseHook` 处理
- 无障碍：控件名称、键盘顺序
- 日志审查
- 定期做 AOT 冒烟

**验收**
- AOT 构建达到启动目标。
- 连续 50 次播放会话没有泄漏。
- 讲述人能读出主要控件。

### P7 外部播放器

**负责**：Codex（引擎、校验、进程管理）；Claude（设置页切换、「正在外部播放」面板）。

**任务**
- `ExternalMpvEngine`：带超时的 JSON IPC
- mpv.exe 选择与校验：用 `Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id)` 选择，用 `--version` 校验（3 秒超时），记录路径、SHA-256、大小、修改时间；文件变化后需要重新批准
- 进程生命周期管理
- "正在外部播放"面板
- 设置页的播放方式切换

**验收**
- 用假 IPC 服务器跑同一套事件脚本测试。
- 【需用户】播放中杀掉 mpv.exe，仍然会上报 Stopped；替换 mpv.exe 后会要求重新批准。

### P8 打包发布

**负责**：Codex（发布脚本、安装器、NOTICES）；Claude（应用图标资源、关于页内容）。

**任务**
- `publish.ps1`：AOT、自包含、带版本号，libmpv 放在 `mpv\` 下
- Inno Setup 安装器 `installer/Mambo.iss`：按用户安装，升级时关闭正在运行的实例；Inno Setup 本身用 `winget install JRSoftware.InnoSetup` 安装【需用户】
- 便携版 zip
- 自行撰写 `THIRD_PARTY_NOTICES.md`，列出 libmpv/FFmpeg 及其组件、WinAppSDK、.NET、CommunityToolkit、Serilog、MiSans 的许可；附 `LICENSES\`
- GPL 源码说明：仓库地址、mpv 修订、shinchiro 构建脚本的出处
- 可选：代码签名

**验收**
- 在当前 Windows 主机验证安装、运行、升级关闭实例和卸载保留数据。用户于 2026-10-03 明确取消专用 Windows 10/11 虚拟机验收并要求删除相关环境与脚本，不将本机结果记作干净系统验收。清理状态见 P8 打包记录。
- 卸载时保留 `%LOCALAPPDATA%\Mambo`，或询问用户是否删除。
- 记录安装包体积。

## 11. 测试（xUnit）

测试依据附录 A 的规格编写，不复制原项目的任何测试文件。

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
- **会话**：用 FakeEngine 回放事件脚本（§6.2 表中的每一行）。
- **Player**：在无头环境下运行真实 libmpv（vo=null、ao=null、`av://lavfi:testsrc`）。DLL 不存在时跳过。

## 12. 端到端验证

- **每次改动后**：
  - `dotnet build -p:Platform=x64` 成功，`dotnet test` 全部通过；
  - `dotnet run --project src/Mambo.App -p:Platform=x64` 启动后，在 Video Lab 和各页面做手动检查。
- **真实服务器**【需用户】，至少覆盖以下场景，并在 Emby 后台核对会话、进度和已播放标记：
  - 直链播放
  - 302 到 CDN
  - 强制转码
  - 带外挂字幕的片源
  - 一整季连播
  - 断网后恢复
- **显示**【需用户】：HDR 开/关；100/150/200% 缩放；在两台显示器之间拖动；最大化 ↔ 全屏。
- **发布前**：AOT 发布冒烟；在当前 Windows 主机完成安装、升级与卸载验证（专用虚拟机验收已按用户要求取消）。

## 13. 风险与对策

| 风险 | 对策 |
|---|---|
| 交换链出现的时机、`SetSwapChain` 调用过早而失败 | `force-window=immediate`；每次 VIDEO_RECONFIG 重新读取；面板有尺寸后才创建 mpv；有限次重试；3 秒拿不到交换链就给出带诊断信息的错误 |
| DPI 与缩放 | 照搬 nami 的 VideoView；`SetMatrixTransform` 只在绑定时和 DPI 变化时调用；P0 做压力测试 |
| 改尺寸/全屏闪烁、顶部 1px 缝隙 | 已提交尺寸 + 变换；拖动期间不改尺寸；全屏方案在 P0 定；必要时用 `BridgeEraseHook` |
| HDR 是否正确 | 由 `DisplayInformation` 驱动参数；用 `video-target-params` 和测试图验证；默认按实测峰值映射，直通作为可选项 |
| 命令行构建 WinUI（无热重载、XAML 编译器报错信息简略） | PRI 生成和 XAML 编译器都来自 NuGet；排错用 `-bl` 二进制日志；接受约 15 秒的重建循环 |
| AOT/CsWinRT 的坑 | 只用 `{x:Bind}`、源生成 JSON、partial 类型；锁定 WinAppSDK 2.5.x；定期 AOT 冒烟；备好 R2R 退路 |
| 令牌泄露 | 预解析重定向；令牌只放在每文件选项里；日志脱敏；默认不开 mpv 详细日志 |
| 大量图片的网格性能 | 按显示尺寸解码、请求尺寸分档、容器回收时取消、分阶段渲染、卡片上不用阴影和亚克力 |
| 不同 Emby 服务器的行为差异 | DTO 宽松解析；候选阶梯 + END_FILE 回退；P3 至少在直链、302、强制转码三种服务器上实测 |
| mpv 关闭时卡死或竞态 | SafeHandle；quit → 等待线程 → destroy 的顺序；看门狗超时就放弃句柄，不崩溃 |

## 14. 分工与协作（Codex 写后端，Claude 写前端）

### 14.1 所有权

各自只修改自己拥有的路径。需要对方改动时不要直接改，而是在 `docs/handoff/requests.md` 写请求（背景、期望的接口或行为、是否阻塞）。唯一的例外：对方的改动导致整个解决方案编译失败，而修复只是一两行的明显小错（例如类型改名后的引用），可以直接修，并在 requests.md 记一笔。

| 路径 | 负责 |
|---|---|
| 构建基础设施：`global.json`、`Directory.Build.props`、`Directory.Packages.props`、`Mambo.slnx`、`.gitignore` | Codex |
| `scripts/`、`third_party/`、`installer/` | Codex |
| `src/Mambo.Core/**`（含 `Contracts/`、`Fakes/`）、`src/Mambo.Player/**`、`tests/**` | Codex |
| `src/Mambo.App/Video/**`（VideoSurface、SwapChainPanelInterop、HdrController） | Codex |
| `src/Mambo.App/Platform/**`（凭据存储等 Win32 实现）、`src/Mambo.App/Debug/**`（Video Lab） | Codex |
| `src/Mambo.App/Composition/BackendServices.cs`、`UiScheduler.cs`、`AppShutdownCoordinator.cs` | Codex |
| `design/**` | Claude |
| `src/Mambo.App/**` 的其余部分：`Program.cs`、`App.xaml`、`MainWindow`、`Shell/`、`Window/`、`Images/`、`Views/`、`ViewModels/`、`Themes/`、`Assets/`、`Composition/UiServices.cs` | Claude（P0 期间由 Codex 搭建，P0 结束后移交） |
| `docs/handoff/backend-status.md` | Codex |
| `docs/handoff/requests.md`、`docs/decisions/` | 双方 |
| `docs/PLAN.md`、`AGENTS.md`、`CLAUDE.md` | 用户；agent 只在用户要求时修改（勾选"进度"除外） |

- 共享热点文件（`Mambo.slnx`、`Directory.Packages.props`、`Directory.Build.props`）只由 Codex 修改。Claude 需要新的 NuGet 包或项目设置时，在 requests.md 提出。
- `Program.cs` 只做组装：调用 `AddBackendServices(fake)`（Codex）和 `AddUiServices()`（Claude）。Codex 新增后端服务时只改 `BackendServices.cs`。

### 14.2 阶段分工与并行

| 阶段 | 负责 |
|---|---|
| P0 工具链、骨架与视频技术验证 | Codex（含最小宿主窗口和 Video Lab） |
| P1a 契约与假实现 | Codex 编写，Claude 评审 |
| P1 Core 平台层 | Codex |
| P2 设计稿 | Claude（可与 P0 同时开始） |
| P3 播放引擎与会话 | Codex |
| P4 外壳与浏览页面 | Claude（先用假实现） |
| P5 播放页 UI | Claude；会话和引擎的问题由 Codex 修复 |
| P6 打磨与加固 | 各自负责自己的部分 |
| P7 外部播放器 | Codex（引擎、校验）+ Claude（设置、面板） |
| P8 打包发布 | Codex（脚本、安装器、NOTICES）+ Claude（图标、关于页） |

```
Codex : P0 ──► P1a ──► P1 ──► P3 ──► P7 ──► P8
Claude: P2（P0 期间即可开始）──► 评审契约 ──► P4（假实现 → 真实服务）──► P5 ──► P7/P8 界面部分
双方  : P6 在各自的线上收尾
```

### 14.3 契约（P1a，前后端之间唯一的接口）

- **位置**：`src/Mambo.Core/Contracts/`。前端代码只能依赖这里的类型和 `VideoSurface` 的公开 API，不得引用 Core 的实现类、DTO 或 Player 的内部类型。
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
- **错误约定**：读取错误放进 `Error` 属性；命令失败抛 `AppException`（带 `AppError`，`Message` 为中文）。
- **VideoSurface**：`PlayerOverlay` 只需在 XAML 里放置 `VideoSurface`，调用 `Attach(IPlaybackSession)` / `Detach()`，不接触 mpv 或 DXGI。
- **假实现**（`src/Mambo.Core/Fakes/`）：
  - 确定性的示例数据：若干媒体库（其中一个约 5000 项，用于性能测试）、多季多集的剧集、演职人员；
  - 图片由程序生成（渐变色块 + 标题文字），不使用任何外部图片；
  - 假播放会话：模拟打开中 → 播放、进度推进、缓冲、失败、12 集连播、轨道列表；画面区域显示纯色；
  - 可配置延迟和错误率，用来调试加载态和错误态。
- **演示模式**：用 `Mambo.exe --fake`（或环境变量 `MAMBO_FAKE=1`）启动时注册全部假实现。设置页的「预览播放页」也使用假播放会话。
- **变更规则**：
  - 契约 v1 经 Claude 评审后冻结，之后的变更走 `requests.md`；
  - 只做向后兼容的修改（新增成员或类型）；破坏性修改必须在 requests.md 标明，并由双方同步完成；
  - Codex 每完成一项真实实现，就在 `backend-status.md` 把对应服务从"假"改为"真"，并注明已知限制。

### 14.4 Git 与工作区

- **准备**（由用户执行，或经用户同意后由 agent 执行）：
  1. 把 `docs/`、`AGENTS.md`、`CLAUDE.md` 提交到 master；
  2. `git tag qt-prototype 808d4dd`；
  3. `git worktree add D:\MAKISEV\mambo-backend -b backend master`：Codex 在这个目录工作，从 P0 开始；
  4. 在 `D:\MAKISEV\qt-mambo` 执行 `git switch -c frontend`：Claude 在这里工作，可以立即开始 P2 设计稿。
- **同步**：
  - 开工前先合并对方分支：Claude 执行 `git merge backend`；Codex 在需要时（读取 requests.md、联调）执行 `git merge frontend`。两边路径不重叠，合并一般不会冲突；
  - 合并后先阅读 `docs/handoff/` 下的文件；
  - P0 骨架建好之后，每次提交前都要保证 `dotnet build -p:Platform=x64` 和 `dotnet test` 通过。
- **集成分支** `winui3`：在里程碑（P0、P1a、P3、P4、P5……）处，由 Claude 合并 `backend` 并验证构建后，执行 `git branch -f winui3 frontend` 推进；也可以由用户执行。最终以 `winui3` 为准。
- **libmpv**：两个工作区都要各自运行一次 `scripts/fetch-libmpv.ps1`。脚本支持用环境变量 `MAMBO_LIBMPV_CACHE` 指定共享的下载缓存目录，避免重复下载。

### 14.5 交接文件（`docs/handoff/`）

- `backend-status.md`（Codex 维护）：每个契约服务的状态（假 / 真 / 已测试）、已知限制、最近的契约变更。
- `requests.md`（双方）：每条请求包含编号、提出方、日期、内容、是否阻塞、状态（待处理 / 已完成 / 已拒绝及理由）。
- 无法在 requests.md 中达成一致时，交给用户决定。
- 用户的角色是在两个会话之间传话，例如告诉 Codex"Claude 在 requests.md 提了新请求"，或告诉 Claude"后端某项服务已完成，可以切到真实服务"。

---

## 附录 A　行为规格（功能与信息架构的依据；视觉以设计稿为准）

### A.1 Emby API

**地址规范化**
- 规则：去掉首尾空白，长度 ≤ 2048 字节；没写协议时补 `https://`，只接受 http/https；必须有主机名；拒绝 userinfo、查询参数和片段；去掉末尾的 `/`；允许带路径（如 `https://host/emby`）。
- 错误文案：「请输入服务器地址」「服务器地址过长」「服务器地址格式无效」「服务器地址仅支持 HTTP 或 HTTPS」「服务器地址缺少主机名」「服务器地址不能包含用户名或密码」「服务器地址不能包含查询参数或片段」。
- 其它输入上限：用户名 ≤ 256 字符，密码 ≤ 4096 字节，id ≤ 256 字节，搜索词 ≤ 256 字符，分页 limit 在 1–500 之间。

**端点**

| 方法与路径 | 参数 / 说明 |
|---|---|
| POST /Users/AuthenticateByName | body `{"Username","Pw"}`，返回 `{AccessToken, User{Id,Name}, ServerId}`；401/403 →「用户名或密码错误」 |
| GET /Users/{uid} | 恢复会话时校验，只看状态码 |
| POST /Sessions/Logout | 2xx、401、403 都视为成功 |
| GET /Users/{uid}/Views | 媒体库列表 |
| GET /Users/{uid}/Items（浏览） | Fields=ITEM_FIELDS；ParentId、IncludeItemTypes、SortBy、SortOrder、StartIndex、Limit、Recursive、Filters、Genres（`\|` 分隔）、Years（`,` 分隔）、OfficialRatings（`\|` 分隔） |
| GET /Users/{uid}/Items（继续观看） | Recursive=true、SortBy=DatePlayed、SortOrder=Descending、Filters=IsResumable、IncludeItemTypes=Movie,Episode,Video、Fields=RESUME_FIELDS、Limit、StartIndex |
| GET /Users/{uid}/Items/Latest | ParentId、Limit、Fields=LATEST_FIELDS；返回数组 |
| GET /Users/{uid}/Items（搜索） | SearchTerm（去首尾空白）、Recursive=true、IncludeItemTypes 默认 Movie,Series,Video、Limit、StartIndex、ParentId、Fields=ITEM_FIELDS |
| GET /Users/{uid}/Items/{id} | Fields=DETAIL_FIELDS |
| GET /Shows/NextUp | UserId、SeriesId、Limit=1，用精简字段 |
| GET /Users/{uid}/Items（季） | ParentId=剧集 id、IncludeItemTypes=Season、SortBy=IndexNumber、SortOrder=Ascending、Limit=100 |
| GET /Users/{uid}/Items（集） | ParentId=季 id、IncludeItemTypes=Episode、Recursive=true、SortBy=ParentIndexNumber,IndexNumber,SortName、SortOrder=Ascending、StartIndex、Limit，用精简字段 |
| GET /Items/Filters | UserId、ParentId、IncludeItemTypes；超时 3 秒；值可能是字符串或 `{Name}`。失败时退回为完整分页扫描（Fields=Genres,ProductionYear,OfficialRating&EnableImages=false&EnableUserData=false），或只用已加载的卡片推导（原版只读前 500 条，结果不完整） |
| POST /Items/{id}/PlaybackInfo?UserId= | body `{UserId, StartTimeTicks, IsPlayback:true, AutoOpenLiveStream:true, MaxStreamingBitrate:140000000, DeviceProfile}` |
| POST /Sessions/Playing、/Sessions/Playing/Progress、/Sessions/Playing/Stopped | 都加 `?reqformat=json`；载荷见 A.9 |
| DELETE /Videos/ActiveEncodings?DeviceId&PlaySessionId | 转码会话停止后调用 |
| GET /Items/{id}/Images/{type}?Tag&MaxWidth&MaxHeight&Index&Quality | 类型：Primary/Backdrop/Thumb/Logo；参数按这个顺序拼接 |
| 字幕 | 优先用 `MediaStream.DeliveryUrl`，否则用 `/Videos/{id}/{mediaSourceId}/Subtitles/{index}/Stream.{format}` |

**字段集**
- `ITEM_FIELDS` = Genres,CommunityRating,OfficialRating,RunTimeTicks,PremiereDate,DateCreated,ProductionYear,BackdropImageTags,PrimaryImageItemId,PrimaryImageTag,ParentBackdropItemId,ParentBackdropImageTags,SortName
- `DETAIL_FIELDS` = ITEM_FIELDS + Overview,MediaSources,People,ParentLogoItemId,ParentLogoImageTag
- `RESUME_FIELDS` = Overview,Genres,MediaSources,BackdropImageTags,PrimaryImageItemId,PrimaryImageTag,ParentLogoItemId,ParentLogoImageTag,ParentBackdropItemId,ParentBackdropImageTags
- `LATEST_FIELDS` = RESUME_FIELDS 去掉 MediaSources
- 剧集列表和 NextUp 用精简字段，不要 MediaSources；播放前由 PlaybackInfo 获取媒体源（原版有过度获取的问题）。

**DeviceProfile**
- **DirectPlayProfiles**：一个 Video 配置，容器为 `mp4,mkv,avi,mov,wmv,m4v,ts,m2ts,webm,flv,ogm,ogv,mpg,mpeg,3gp`，不限制编码。
- **TranscodingProfiles**：两个，HLS 与 HTTP，容器都是 ts。共同参数：
  - VideoCodec=h264
  - AudioCodec=aac,mp3,ac3,eac3,flac,opus,vorbis
  - MaxAudioChannels="6"、MinSegments="1"
  - BreakOnNonKeyFrames=true、Context=Streaming
- **SubtitleProfiles**：srt/ass/ssa/sub/vtt 为 External；新增所有格式的 Embed。

**媒体库分类**
- **可播放库**：`CollectionFolder`，且 CollectionType 不是 books/boxsets/channels/folders/livetv/music/photos/playlists。
  - 首页"最新"各行、搜索分组、侧栏都只用可播放库（原版侧栏把所有库都列出来了，是 bug）。
- **资料库请求的 IncludeItemTypes**：movies → Movie,Video；tvshows → Series；其它 → Movie,Series,Video。客户端丢弃 Movie/Video/Series 以外的项。

### A.2 登录、恢复、注销

**登录表单**
- 字段：
  - 「服务器地址」：placeholder `https://your-emby-server`，提示「未填写协议时将使用 HTTPS。仅在可信网络中明确填写 http://。」
  - 「用户名」
  - 「密码」：可以为空
- 按钮「连接」，进行中显示「连接中...」；地址和用户名都不为空时才可用。
- 用上次的服务器地址和用户名预填。

**登录流程**
1. 规范化地址；
2. AuthenticateByName；
3. 保存凭据；
4. 进入已登录状态；
5. 在后台 flush 发件箱；
6. 跳转到首页。

- 已登录时拒绝再次登录。
- 认证错误显示「用户名或密码错误」，其它错误显示中文模板文案。

**恢复会话**
1. 读取凭据；
2. 先用缓存快照渲染界面；
3. 在后台 `GET /Users/{uid}`：
   - 401/403：删除凭据，转为未登录；
   - 网络错误或 5xx：保留凭据，分别隔 1 秒、2 秒、4 秒重试，全部失败后首页显示「无法连接服务器」和「重试」。
4. 成功后 flush 发件箱。

**已登录状态**：显示用户名，以及一个按钮：平时显示「已连接」，悬停时显示「断开连接」；操作进行中显示「断开中...」或「恢复中...」。

**注销**
1. 如果正在播放，先确认：标题「注销并结束播放？」，正文「正在播放。注销会结束当前播放并保存进度，是否继续？」，按钮「注销」。
2. 停止播放，并 flush 发件箱（此时仍用旧令牌）。
3. 删除凭据，清理该账号的查询缓存和图片缓存。
4. `POST /Sessions/Logout`；失败时只提示「本地已断开，但服务器会话可能仍有效」。

**账号**：同一时间只有一个活动会话；换账号就是注销后重新登录；缓存按账号隔离。

### A.3 首页

**状态顺序**（依次判断，命中即停）
1. 恢复会话中：空白；
2. 恢复失败：「无法连接服务器」+「重试」；
3. 未登录：引导页；
4. 全部加载中：骨架屏；
5. 媒体库列表加载失败：「首页加载失败」+「重试」；
6. 没有任何内容：空白；
7. 以上都不是：显示内容。

**Hero 轮播**
- **数据**：GET Items（Recursive=true、IncludeItemTypes=Movie,Series,Video、SortBy=Random、Limit=32）→ 只保留有图的项 → 打乱 → 按"有 backdrop > 有 thumb > 只有 primary"排序 → 取前 8 个。
- **显示**：
  - logo（放在 360×130 的区域内），没有 logo 或加载失败时显示标题；
  - 元数据行：★ 评分、年份、最多 3 个类型（用 / 连接）、分级徽标；
  - 简介，最多 2 行；
  - 整块可点击，进入详情页。
- **切换**：每 7 秒自动切换。以下情况暂停：鼠标悬停或获得焦点、hero 在视口内不足 15%、窗口隐藏或失焦、鼠标悬停在标题栏分页点上、系统关闭了动画。
- **分页点**：幻灯片 ≥ 2 张时，标题栏中间显示分页点。
- **预取**：预取后续幻灯片的背景和 logo。

**「最近播放」行**
- **数据**：继续观看，Limit=16，按剧集去重（每部剧只留一条），只保留进度在 0–100% 之间且未看完的。
- **卡片**：横版卡片；单集用剧名作标题，副标题为「S01E02 · 集名」；带进度条和"从这里播放"按钮。
- **交互**：点击卡片进入详情；点击行标题旁的箭头进入最近播放页。
- **刷新**：播放结束后刷新这一行。

**每个可播放库一行「最新」**
- **数据**：每个库请求 Latest，Limit=16，最多 4 个库并发。
- **卡片**：海报卡片，副标题为年份或集号，带评分徽标。
- **出现顺序**：按侧栏顺序逐行出现。
- **失败**：某一行失败时，只在这一行显示错误和「重试」，重试也只针对这一行。
- **交互**：点击行标题进入该库。

**卡片行通用交互**
- 左右箭头每次滚动行宽的 82%；支持鼠标滚轮横向滚动。
- 前 4 张图片优先加载。
- 卡片悬停 300ms 后预取详情。

**未登录引导**：两行大号可点击文字「前往连接你的 / Emby 服务器」，点击后进入设置页。

### A.4 最近播放页

- **页头**：标题「最近播放」，旁边显示「N 项」（已加载数）。
- **网格**：虚拟化的横版卡片网格，列数随窗口宽度变化；每张卡都有播放按钮。
- **分页**：接近末尾时自动加载下一页（继续观看接口分页，Limit=120），按 id 去重。
- **错误**：首屏失败显示「最近播放加载失败」+「重试」；后续页失败显示「重试加载」。

### A.5 资料库页

**页头**
- 标题为库名，右侧是筛选栏，并显示服务器返回的 TotalRecordCount（「N 项」）。
- 切换库时，旧内容一直保留到新库的首屏数据到达。

**筛选面板**
- 三组：类型（genres）、年份（按年代显示，如「2020年代」，请求时展开成具体年份）、分级。
- 组内是"或"，组间是"与"。
- 每组都有「全部」，选中即清空该组；只要有任何筛选，就出现「重置」。

**排序**
- 添加日期：DateCreated 降序，默认
- 名称：SortName 升序
- 评分：CommunityRating 降序
- 年份：ProductionYear 降序
- 时长：Runtime 降序，只在电影库出现

**网格**
- 海报卡片：标题、「年份 · 进度%」、评分徽标、进度条。
- 首屏卡片错开淡入。
- 接近末尾自动加载下一页，Limit=60。
- 空状态：有筛选时显示「当前筛选没有内容」+「重置筛选」，否则显示「暂无内容」。
- 排序或筛选改变时，旧结果保留到新结果到达，然后滚回顶部。

**筛选选项与偏好**
- 筛选选项：`/Items/Filters`（5 分钟内视为新鲜）与已加载卡片推导出的选项合并，让筛选立即可用。
- 偏好：排序和筛选按 `server|user|library` 记忆。

### A.6 详情页

**背景图**：优先级为 自身 backdrop → 父级 backdrop → （单集）所属剧集的 backdrop（需要额外获取剧集）→ 剧集 primary → 自身 primary。

**文字区**
- logo 或标题，加元数据行；
- 单集额外显示一行「第 N 季 · 第 M 集 · 集名」；
- 简介最多 2 行，选中某一集时显示该集的简介。

**播放按钮**：72px 圆形；有续播位置时显示白色进度环；启动中呈脉动状态。

**剧集区**（剧集或单集详情才有）
- **季胶囊**：「第 N 季」或季名，配左右箭头。
- **剧集横向列表**：
  - 卡片内容：「N. 集名」、「yyyy-MM-dd · 45 分钟」、进度条；
  - 点击卡片选中这一集：hero 简介和播放目标随之改变，卡片显示描边；
  - 每张卡片自带播放按钮；
  - 接近末尾加载下一页（Limit=30）；会自动翻页直到目标集加载出来，再把它滚动到可见位置。
- **季的选择**：NextUp 所在的季优先，否则第一季；用户手动选的季在数据刷新后保留；换季时清除选中的集。

**演职人员**
- 标题「演职人员」；横向列表，卡片宽 132px，头像比例 3:4，有圆角，无头像时显示剪影。
- 卡片显示名字，加角色名；没有角色名时按类型显示 导演/编剧/制片/演员/演职员。

**错误文案**：「详情加载失败」「剧集加载失败」「无法确定接下来播放的剧集：…」「无法加载剧集季：…」都带「重试」；后续页失败显示「重试加载」。

**原版没有的功能**：收藏、标记已看、相似推荐、媒体信息。可以在设计稿里评估是否新增。

### A.7 搜索

- **输入**：
  - 搜索框在侧栏：按 Enter 或点搜索图标触发，有清除按钮；
  - 未登录时禁用，悬停提示「连接服务器后可用」；
  - 页面标题为「搜索」。
- **规范化**：做 NFKC 规范化，去掉标点和符号后至少剩 1 个字符才搜索。
- **分组**：每个可播放库一组，按库的顺序排列。
  - 最多 4 组并发，前 2 个库的请求是前台优先级；
  - 每组请求 Limit=24，「加载更多」按钮翻页，不自动加载。
- **结果处理**：只搜 Movie,Series,Video；单集折叠成所属剧集（用剧集的 id 和剧名）；空组隐藏；某组失败时只在该组显示错误和「重试」。
- **返回**：返回搜索页时恢复滚动位置；侧栏搜索框与当前搜索词保持同步。

### A.8 设置

- **「服务器配置」**：见 A.2。
- **「播放器设置」**：
  - 「播放方式」二选一：「内置画面」（默认）/「外部窗口」（只有在自定义 mpv.exe 已通过校验时可用，P7 实现）。
  - 「MPV 路径」：输入框 +「选择文件」（只显示 .exe）。
  - 「预览播放页」：用假数据打开播放层。
  - 状态行，四种之一：校验中 / 已批准 / 当前使用内置播放器 / 路径无效、已改用内置。
  - 新增：「HDR」（自动/始终/关闭）、「硬件解码」（自动/关闭）。
- **外部 mpv 路径规则**：
  - 去掉首尾空白，不能为空，长度 ≤ 32767 个 UTF-16 单元；
  - 必须是名为 mpv.exe 的文件，或包含 mpv.exe 的目录；要规范化成绝对路径；
  - 编辑后防抖 400ms 再校验；
  - 无效时提示「未找到该路径下的 MPV 可执行文件，将使用内置播放器」，并切回内置播放。
- **「关于」**：Mambo、版本号（从程序集读取）、一句简介、许可与第三方声明入口、日志目录入口、「清除缓存」。

### A.9 播放

**启动**
- **入口**：首页「最近播放」行的播放按钮、最近播放页卡片的按钮、详情页 hero 的播放按钮、剧集卡片的播放按钮。
- **防重复**：启动中或替换确认中，再次点击无效；同一个项目正在播放时，点击也无效。
- **替换确认**：已有内容在播放时弹出确认：标题「切换播放？」，正文「正在播放其他项目。切换播放会结束当前播放并保存进度，是否继续？」，危险按钮「切换」。取消则保持原状态。
- **失败**：Toast「播放失败：{原因}」，带「重试」；播放层显示失败态。
- **关闭窗口时正在播放**：确认「退出应用？」/「正在播放。关闭应用会结束播放并保存进度，是否退出？」/「退出」，然后停止播放再退出。

**目标解析**
- Movie / Episode / Video：用显式起点，否则用 `UserData.PlaybackPositionTicks`，没有阈值。
- Series：先取 NextUp；没有时在全部剧集（recursive，最多 500）里依次找：续播位置 ≥ 30 秒的第一集 → 第一个未看的集 → 第一集。
- Season：在本季剧集（最多 300）里按同样规则找。
- 其它类型：返回契约错误。

**标题**
- 单集的媒体标题：`{SeriesName} S{季:02}E{集:02} - {Name}`。
- 播放层顶栏标题：「剧名 · 第3集 标题」。

**连播计划**
- 只针对有 SeasonId 的 Episode；取该季全部剧集，最多 500，按 ParentIndexNumber、IndexNumber、SortName 升序。
- 只保留 Type=Episode 且 SeasonId 匹配的项（没有 SeasonId 的行也接受）；选中的集必须在其中。
- 任何失败都降级为单集播放。

**媒体源资格**
- "显式 URL"指 DirectStreamUrl、TranscodingUrl 或 http(s) 的 Path。
- 以下情况拒绝该媒体源：
  - 没有显式 URL，且 MediaStreams 为空或没有视频流；
  - 没有显式 URL，且容器是 iso/dvd/bluray/bdmv；
  - 显式 URL、SupportsDirectStream、SupportsDirectPlay 全都没有。
- 带 `RequiredHttpHeaders` 的源**允许使用**。

**媒体源优先级**
- 顺序：0 DirectStreamUrl；1 http Path；2 SupportsDirectStream/DirectPlay；3 TranscodingUrl。
- 同级时依次比较：最大视频高度（降序）、码率（降序）、服务器返回的顺序。

**候选 URL**（按排名）

| 排名 | 来源 | 播放方式 | 说明 |
|---|---|---|---|
| 1 | direct_stream_url | DirectStream | 转成绝对地址，缺 PlaySessionId 时补上 |
| 2 | http_path | DirectPlay | |
| 3 | `/Videos/{id}/stream.{container}?DeviceId&MediaSourceId&Static=true[&PlaySessionId]` | DirectPlay | 仅当 SupportsDirectPlay/DirectStream 为真 |
| 4 | 同上，不带扩展名 | DirectPlay | 同上 |
| 5 | transcoding_url | Transcode | |

- 有多个媒体源时，先按类别排，再按源的排名排，并按 URL 去重。
- 拼进路径的 id 要做路径编码；空字符串、`.`、`..` 一律拒绝。

**上报载荷**
- **Playing / Progress**：
  - 条目：ItemId、MediaSourceId?、PositionTicks、PlaySessionId?、LiveStreamId?、PlayMethod、PlaybackStartTimeTicks（开始时刻的 Unix 纪元 100ns ticks）
  - 状态：EventName（Progress 用 TimeUpdate / Pause / Unpause，Playing 不带）、IsPaused、IsMuted、VolumeLevel（默认 100）、PlaybackRate（真实倍速）
  - 播放列表：PlaylistIndex / PlaylistLength（在连播计划中的位置）、NowPlayingQueue=[]
  - 固定值：MaxStreamingBitrate=2147483647、RepeatMode="RepeatNone"、SubtitleOffset=0、CanSeek=true、Shuffle=false
  - 可选：AudioStreamIndex / SubtitleStreamIndex，由 track-list 的 ff-index 映射到 Emby MediaStream.Index
- **Stopped**：ItemId、MediaSourceId?、PositionTicks、PlaySessionId?、LiveStreamId?、PlaybackStartTimeTicks、Failed=false。

**上报节奏**
- 首次确认开播时发 Playing；之后至少间隔 10 秒发一次 Progress；暂停、继续、拖动时立即发 Progress；结束时发 Stopped。
- 从未确认开播的条目：不上报，也不进发件箱。
- 发 Stopped 前，如果 Playing 还没成功，先尽力补发一次 Playing。

**换算**：1 秒 = 10^7 ticks；秒数保留 3 位小数；续播位置大于 0 才设置 `start`。

**mpv 控制映射**

| 操作 | mpv 命令或属性 | 取值 |
|---|---|---|
| 暂停 | pause | — |
| 跳转 | `seek <秒> absolute` | 秒数 ≥ 0 |
| 音量 | volume | 0–100 |
| 静音 | mute | — |
| 倍速 | speed | 0.25–4 |
| 音轨 / 字幕 | aid / sid | 轨道 id，或 `no` 表示关闭 |
| 逐帧 | `frame-step` / `frame-back-step` | 以该修订的文档为准 |
| 下一集 | `playlist-next` | — |

**轨道菜单**
- 最多 32 条，文本截断到 96 个字符，不暴露 external-filename。
- 标签格式「标题 · 语言」，缺省时显示「字幕 N」/「音轨 N」。

**Toast 文案**：跳过某集时「有一集无法加入连播，已跳过」；意外中断时「播放意外中断，已保存最新进度」（显示 8 秒）。

### A.10 播放页交互

**状态**
- 打开中：加载指示 +「正在打开」；20 秒后追加「片源响应较慢，仍在等待画面」+「关闭播放页」。
- 失败：错误信息（缺省为「这部片子暂时打不开」）+「重试」/「关闭」。
- 缓冲：paused-for-cache 为真时显示「缓冲中…」胶囊。
- 暂停：控制层可见时，中央显示大播放按钮。

**底栏**
- **进度条**：拖动时显示时间提示，松手后提交绝对 seek；显示已缓冲区间。
- **左侧**：
  - 「上一集」（不是第一集时显示）
  - 播放/暂停
  - 「下一集」（不是最后一集时显示）
  - 时间：「m:ss / m:ss」，超过 1 小时用「h:mm:ss」
- **右侧**：
  - 倍速：按钮显示「倍速」或当前值如「1.25×」；选项 0.5/0.75/1/1.25/1.5/2，允许范围 0.25–4
  - 轨道菜单：「字幕」（含「关闭字幕」）、「音轨」；没有轨道时显示「这个文件没有可选轨道」
  - 音量：静音按钮，悬停时展开滑块
  - 全屏
  - 最大化/还原

**自动隐藏**
- 控制层可见的条件：鼠标按住中，或焦点在控制层内，或（指针在画面区域内且未到空闲时间）。
- 空闲时间 3000ms；画面区域内的指针移动、按下以及任何按键都会重新计时。
- 鼠标按住或焦点在控制层内时，不会因空闲而隐藏。
- 打开时默认隐藏，直到指针移动。
- 显示和隐藏的淡入淡出为 240ms；系统关闭动画时瞬间完成。
- 光标只在"播放中且控制层隐藏"时隐藏。
- **只有画面区域内的指针移动才算数**：在选集面板上移动不会唤出控制层。

**鼠标**
- 单击：250ms 后切换播放/暂停（给双击留出判断时间）；点在按钮或滑块上的单击忽略。
- 双击：切换最大化。
- 两者都只在播放或暂停状态下生效。

**按键**（除 F11、Esc 外，只在播放或暂停状态下生效）

| 按键 | 动作 |
|---|---|
| Space | 播放/暂停 |
| M | 静音 |
| `[` / `]` | 倍速降一档 / 升一档 |
| C | 循环切换字幕（循环中包含"关闭"） |
| V | 循环切换音轨 |
| `,` / `.` | 逐帧后退 / 前进 |
| ← / → | 后退 / 快进 5 秒（按住可连发） |
| ↑ / ↓ | 音量 ±5 |
| F11 | 全屏 |
| Esc | 全屏时退出全屏；否则关闭播放层并停止播放 |
| Alt+← | 关闭播放层 |
| 鼠标后退键 | 关闭播放层 |
| 鼠标前进键 | 忽略 |

**选集**
- 标题「选集」；每集一个方形按钮，显示集号，原版为 4 列。
- 集号解析：先取 episodeLabel 末尾的数字；没有则从标题的 SxxEyy 里取集号；再没有就用序号 + 1。
- 当前集高亮；只有在播放或暂停状态下才能点击。
- 连播条目多于 1 个，或第一个条目有集号时，才显示选集。

**结束与全屏**
- 季末自动关闭播放层。
- 全屏时隐藏标题栏和侧栏；关闭播放层时如果处于全屏，先退出全屏。

### A.11 外壳、导航、快捷键、通用组件

**窗口**：默认 1500×860 居中，最小 1100×720；无边框加亚克力，圆角 8。

**标题栏（40px）**
- **左侧**：后退、前进按钮（28px）；播放层打开时，后退改为关闭播放层。
- **中间**，按场景显示：
  - 详情页：剧名或片名；
  - 播放层：「剧名 · 第3集 标题」；
  - 首页且 hero 有 2 张以上：分页点（6px，当前项拉长到 22px）。
- **右侧**：窗口按钮，关闭按钮悬停时显示危险色。
- 空白处可以拖动窗口，双击最大化。

**侧栏（208px）**
- 从上到下：搜索框、「首页」、「最近播放」（未登录时禁用）、可播放库列表（图标按 movies/tvshows/其它区分）、底部「设置」。
- 列表可滚动；条目有激活、悬停、按下三种状态。

**导航**
- 路由：home | recent | library(libraryId) | detail(itemId) | search(query) | settings。
- 规则：
  - 重复导航到当前页（或当前库）不做任何事；
  - 已在搜索页时再次搜索，替换当前记录；
  - 从详情进入另一个详情，正常入栈；
  - 历史不持久化。
- 后退的输入：标题栏按钮、Alt+←、Esc（焦点不在输入框里，且事件没被处理过）、鼠标后退键。
- 前进的输入：标题栏按钮、Alt+→、鼠标前进键。
- 滚动位置在首页、资料库（按库分别记）、最近播放、搜索恢复；详情页换了条目就回到顶部。

**全局快捷键**
- Ctrl+F，或 `/`（焦点不在输入框里时）：聚焦侧栏搜索框
- Ctrl+,：打开设置
- Ctrl+1…9：打开第 n 个库
- F5 或 Ctrl+R：刷新当前页数据

**对话框**
- 模态；包含标题、正文、「取消」和「确认」（文字可自定义，危险操作时确认按钮为红色）。
- Esc 或点击背景等同于取消；打开时焦点限制在对话框内，关闭后恢复到原处。
- 多个请求排队依次显示。

**Toast**
- 显示在右下角，最多 3 条；已满时丢弃最旧的非错误 Toast。
- 错误 Toast 需要手动关闭；其余默认 3 秒消失（部分为 6 秒或 8 秒）；鼠标悬停时暂停计时。
- 可以带「重试」按钮。

**错误兜底页**：「出了点问题」+ 错误信息 +「返回首页」/「重试」。

## 附录 B　原版视觉参考值（用于让新设计贴近原版，可在设计稿中调整）

- **颜色**：
  - 玻璃：底 rgba(246,247,249,.055)，强调底 rgba(255,255,255,.12)，边框 rgba(255,255,255,.14)，阴影 rgba(15,23,42,.16)；
  - 文字：主要 rgba(24,24,24,.9)，次要 rgba(30,30,30,.72)，弱化 rgba(38,38,38,.55)；
  - 主色 #0c68b8，悬停 #095a9f，浅色 rgba(12,104,184,.16)；
  - 危险 #c42b1c，悬停 #a52315；评分 #e5a00d；
  - 中性填充：黑 .035 / .06；按下填充：黑 .1；
  - 侧栏：激活黑 .12，悬停 .04，按下 .08；
  - 内容区边框：黑 .1；
  - 输入框：边框黑 .2，底黑 .025。
- **外壳**：
  - 玻璃面板：135° 白色渐变（.075→.025），1px 边框，圆角 8，顶部内高光 .18、底部 .05；
  - 内容区：距标题栏 12，顶部两角圆角 12。
- **弹出面**（对话框、Toast、菜单）：底 rgba(252,253,255,.86)，边框白 .72，圆角 18，阴影 0 18 44 rgba(15,23,42,.16)。
- **控件**：
  - 圆角 12；
  - 按钮高 42，文字 13px 半粗体、次要色，按下缩放到 .97；
  - 禁用时透明度 .45；
  - 焦点环 rgba(12,104,184,.72)，2px。
- **字体**：MiSans 400/500/600/700，回退 Segoe UI Variable。

  | 用途 | 字号与字重 | 其它 |
  |---|---|---|
  | 页面标题 | 34 粗体 | — |
  | 眉标 | 12 半粗体 | 大写，字距 .18em |
  | 分区标题 | 24 粗体 | — |
  | 正文 | 13 | — |
  | 卡片标题 | 14 中等 | 行高 17 |
  | 卡片副标题 | 12 | — |
  | hero 标题 | 42 半粗体 | — |
  | hero 简介 | 14 中等 | 行高 24 |

- **卡片**：
  - 尺寸：横版 300×169，圆角 10；海报 150×220，圆角 14；文字区高 50（海报）/ 47（横版）；
  - 图片内描边：黑 .14，悬停 .30，聚焦 .34，选中为主色加 3px 白环；
  - 动效：悬停上移 6px（220ms）；按下上移 2px 并缩放到 .995；
  - 进度条：内缩 8，高 3，白 .96；
  - 卡片上的播放按钮：36px，深色玻璃 rgba(16,18,22,.42)；
  - 评分徽标：黑 .75，11px；
  - 间距：海报网格 20/24，横版网格 12/24；
  - 人物卡：宽 132，圆角 16。
- **hero 与详情**：hero 高度为窗口高度的 62%，限制在 500–600 之间；logo 区 360×130；详情页播放按钮 72px。
- **动效**：
  - 缓动曲线：enter [0,0,.2,1]、standard [.4,0,.2,1]、fluid [.2,.8,.2,1]、exit [.4,0,1,1]、settle [.22,1,.36,1]；
  - 时长（ms）：micro 80、interaction 160、imageReady 140、surface 240、route 280、heroContent 320、heroTransition 480、backdropSettle 500、shellFold 560、coverTransition 220。
- **播放层**：
  - 底栏渐变：黑 .9 → 透明；
  - 菜单：黑 .85 底，白 .15 边框，圆角 12，宽 224，最高 288；
  - 滑块：轨道 4px（悬停时 6px），白色；
  - 缓冲胶囊：黑 .65。

## 附录 C　原版缺陷（新实现必须避免；括号内为对应阶段）

**播放与后端**
1. 登录凭据从未持久化：keyring 缺少 Windows 后端，落到了内存 mock。（P1）
2. IPC 控制没有超时，可能永久挂起。（P7）
3. 片源打不开时卡在"正在打开"：原版忽略了 END_FILE(error)。（P3）
4. 转码实际不可达。（P3）
5. 进度每 3 秒才更新、操作后回弹，靠 400ms 轮询维持。（P3/P5）
6. 外挂字幕从未加载；带 RequiredHttpHeaders 的源被直接拒绝。（P3）
7. 令牌可能随 302 泄露（`curl-max-redirects` 并不是 mpv 选项）。（P3）
8. 连播计划失败会让整次播放失败。（P3）
9. 倍速固定上报 1.0；暂停和拖动不上报；转码会话不清理。（P3）
10. 退出时的停止上报依赖析构函数；发件箱没有定时重试。（P1/P3）
11. Release 版日志丢失。（P1）
12. 错误文案中英混杂；筛选项回退只读前 500 条。（P1）
13. 锁内做阻塞 I/O。新实现改为 actor 加异步 I/O。（P3）
14. 用本地生成的 PlaySessionId 走捷径，服务器从未登记这个会话。新实现每次都调用 PlaybackInfo。（P3）

**界面**
- B1：状态回弹，时钟每 3 秒才跳一次。（P3/P5）
- B2：倍速和"缓冲中"不显示。（P5）
- B3：全屏时仍显示外壳；Esc 直接停止播放；关闭播放层不退出全屏。（P5）
- B4：播放结束后，详情、最近播放、资料库的进度不刷新。（P3/P5）
- B5：搜索页滚动位置不恢复。（P4）
- B6：关于页的版本号和链接是占位。（P4）
- B7：在选集面板上移动鼠标也会唤出控制层。（P5）
- B8：播放时前进按钮仍可用，会去导航被遮住的页面。（P5）
- B9：侧栏列出了音乐、照片等非视频库。（P4）
- B10：返回后侧栏搜索框的内容与当前搜索不同步。（P4）
- B11：高频轮询（每秒约 5 次 IPC 调用）。（P3/P5）

## 附录 D　参考资料

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

## 进度

- [x] P0 工具链、骨架与视频技术验证（Codex；关卡 ①：2026-10-02 用户批准携遗留项进入 P1；未通过与待测项见 `docs/decisions/P0-video-spike.md`）
- [x] P1a 契约与假实现（Codex；2026-10-02 Claude 条件通过，R-004–R-009 已修正并测试，契约 v1 冻结；增量 R-010–R-014 完成）
- [x] P1 Core 平台层（Codex；2026-10-02 平台实现与 174 项测试完成；Debug/AOT 组合验证通过，用户确认真实登录、列库、重启恢复与凭据检查通过）
- [x] P2 设计稿（Claude；关卡 ②：2026-10-02 用户确认定稿；应用图标按用户决定暂缓；结论见 `docs/decisions/P2-design.md`）
- [x] P3 播放引擎与会话（Codex；2026-10-02 实现、237 项测试与 Debug/AOT composition 冒烟完成；用户确认真实服务器验收通过，见 `docs/decisions/P3-playback.md`）
- [ ] P4 外壳与浏览页面（Codex 统一接手；实现、345 项单元测试、Debug/AOT 界面与深滚动恢复通过；最新交付候选实际缓存首页 972/919/827 ms，800 ms 目标未通过；GPU 呈现 60fps 尚未验收，当前令牌没有实时跟踪权限，见 `docs/decisions/P4-P6-integration.md`）
- [ ] P5 播放页 UI（实现及附录 A.10 共享事件/真实按钮自动回归通过；停止播放后详情/最近/资料库实际 XAML 进度刷新在 Debug/AOT/安装目录通过；真实键鼠、光标与系统效果尚未人工确认，不将自动化当作人工观察）
- [ ] P6 打磨与加固（程序化关闭绕过清理的原生崩溃与自绘关闭按钮已修复；Debug/AOT/安装目录完整界面回归、实际关闭按钮四项、五项原生 UIA、50 次假播放关闭零留存通过；最新窗口 Loaded 为 881/865/774 ms，600 ms 目标未通过；讲述人实际朗读及 P0 原生硬件资源遗留保留）
- [x] P7 外部播放器（后端、设置与面板接入完成；Debug/AOT IPC、真实外部进程终止/停止补报/文件替换重新批准及界面自动回归通过；按用户统一接手和后续全授权采用本机真实进程自动验证，未把先前暂缓的人工 Emby 后台观察记成通过，见 `docs/decisions/P7-external-process-smoke.md`）
- [x] P8 本地打包交付（Native AOT、自包含便携/安装/源码包与许可记录完成；最终候选首次安装、安装 UI、升级正常关闭及卸载保留全部用户文件通过；专用 VM 已按用户要求取消。此项不代表已公开发布；完整原生对应源码缺口及清理受阻见 `docs/decisions/P8-packaging.md`）
