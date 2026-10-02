# P0：WinUI 3 + libmpv 视频技术验证

日期：2026-10-02。状态：用户在知悉下列未通过与待测项后，明确要求“提交，进入P1”，批准携遗留问题结束 P0 并进入 P1。以下验收结果保持原样，不将未通过项改记为通过。

## 环境与可复现命令

工作区为 `backend`。开工时已执行 `git merge frontend`，结果为已是最新；Qt 雏形由已有的 `qt-prototype` 标签保留。本仓库的 MiSans 原样移动到 App 的字体目录，没有使用旧 Tauri 项目的文件或资产。

- Windows 11，系统版本 `10.0.28000`；已有 VS2022 BuildTools / MSVC 与 Windows SDK 26100，仅通过命令行构建。
- 经用户授权安装 .NET SDK `10.0.401`；`global.json` 使用 `latestFeature`，不允许预览版。
- Windows App SDK `2.5.1`、CsWin32 `0.3.335`、CommunityToolkit.Mvvm `8.4.2`；集中包管理及各项目的 NuGet lock 文件已配置。
- GPU：NVIDIA GeForce RTX 4060 Laptop GPU，驱动 `32.0.16.1692`；系统另有 GameViewer Virtual Display Adapter `15.6.5.199`。
- libmpv 固定为 shinchiro release `20260610`，mpv 修订 `304426c390901436fb1d4a63efbd582ae80c88f4`。下载脚本验证压缩包和 DLL 的 SHA-256，后续运行不会自动刷新 lock。

在仓库根目录执行：

```powershell
pwsh scripts/fetch-libmpv.ps1
pwsh scripts/fetch-video-sample.ps1
dotnet build -p:Platform=x64 -bl:artifacts/p0-build.binlog
dotnet test
dotnet publish src/Mambo.App -p:Platform=x64 -p:PublishProfile=Aot -o publish/aot -bl:artifacts/p0-aot.binlog
pwsh scripts/test-video-lab.ps1
pwsh scripts/test-video-lab.ps1 -Aot
pwsh scripts/test-video-lab.ps1 -Aot -MissingLibrary
```

下载的样片是 Jellyfin / Gnattu 的 **Test Jellyfin 4K HEVC HDR10 40M**，30 秒、3840×2160，约 141 MiB。发布方标注 CC BY-SA，出处为 [Jellyfin test-videos](https://repo.jellyfin.org/main/test-videos/)，下载使用其官方纽约镜像。SHA-256：`da108da499153ae4816b6cd50296a6dc56996ae3d30a08c7a14a1108b25a4221`。样片、原生 DLL、下载缓存、诊断报告和发布产物均被 Git 忽略。

## 已验证的管线与实现决定

采用 `vo=gpu-next`、`gpu-api=d3d11`、`gpu-context=d3d11`、`hwdec=d3d11va`，初始化前设置 `d3d11-output-mode=composition` 和面板像素尺寸。mpv 不创建播放 HWND，交换链挂到 `SwapChainPanel`；控件叠在同一个 XAML 视觉树中。

`display-swapchain` 的属性 getter 返回**借用指针**。依据固定修订的 [player/command.c](https://github.com/mpv-player/mpv/blob/304426c390901436fb1d4a63efbd582ae80c88f4/player/command.c) 与 [video/out/vo.c](https://github.com/mpv-player/mpv/blob/304426c390901436fb1d4a63efbd582ae80c88f4/video/out/vo.c)，应用在事件线程显式 `AddRef`，用自己的 `SafeHandle` 持有排队期间的引用，UI 绑定成功或放弃后释放。不能把其它内部 helper 的引用约定套用到这个属性上。

事件线程立即复制 mpv node / property 数据，不把事件缓冲区指针传到 UI。`loadfile` 使用异步 node 命令和 options map，返回 `playlist_entry_id`；属性写入为异步。关闭顺序是 UI 解绑、发送 quit、等待事件线程、后台 destroy，带超时保护，不允许 destroy 与 `mpv_wait_event` 并发。

面板像素尺寸为 DIP 尺寸乘 `RasterizationScale` 并取整。拖动窗口期间保留已提交的缓冲区尺寸，用 XAML transform 临时伸缩；拖动结束后更新 mpv，轮询 `GetDesc1`，缓冲区匹配后延迟 40 ms 提交面板布局。`SetMatrixTransform` 仅在绑定和 DPI 变化时设置。

全屏暂采用 `AppWindow.SetPresenter(FullScreen)`，退出时恢复同一个显式创建的 `OverlappedPresenter`。AOT 下从 `AppWindow.Presenter` 动态强转曾失败，现改为保存创建时的强类型实例，已重新通过 AOT 最大化 / 全屏验证。是否需要 PLAN §5.12 的无边框方案，要由人工闪烁检查决定。

`HdrController` 通过窗口所属的 `Microsoft.Graphics.Display.DisplayInformation` 监听高级颜色变化。自动模式仅在 Windows HDR 已开启时设置 PQ / BT.2020、峰值、对比度和参考白；关闭 HDR 则回到 auto，交换链输出格式保留 auto。显示器监听由窗口持有，每次播放只连接或断开 mpv。

`StreamUrlResolver` 的探测关闭自动重定向和 Cookie。同源认证只放在 `X-Emby-Token` 请求头；同源认证 query 会移除，跨 scheme / host / port 的跳转立即结束探测并移除认证头，不预先请求 CDN。跨域 URL 保留 CDN 自己的签名，仅剔除与 Emby 令牌相同的复制凭据。支持 Range → HEAD → 普通 GET 回退、5 跳限制、单次 3 秒和总计 6 秒超时。异常不保留可能包含地址的原始网络消息；P0 不接收原始 mpv 日志。

Native AOT 使用 `LibraryImport`、函数指针、JSON 源生成、partial WinRT 类型；发布显式携带 `Mambo.pri`、`App.xbf`、`MainWindow.xbf`、`Debug/VideoLab.xbf`。另有 ReadyToRun、不裁剪配置，本轮未单独发布验证。

## 自动验收结果

最新 `dotnet build -p:Platform=x64`：0 警告、0 错误。`dotnet test`：13 通过、0 失败、0 跳过，覆盖请求同源边界、重定向与认证处理，以及真实 libmpv 的探测、node 命令、事件复制和无头实例生命周期。

| 项目 | Debug JIT | Native AOT |
|---|---|---|
| 程序启动、交换链绑定 | 通过 | 通过 |
| 4K HEVC 硬解 | `d3d11va` | `d3d11va` |
| 输入格式 | P010、PQ、BT.2020 | P010、PQ、BT.2020 |
| 1100×720 / 1500×860 窗口，缓冲区等于面板像素 | 通过 | 通过 |
| 最大化进入全屏，缓冲区等于面板像素 | 通过 | 通过 |
| 创建 / 销毁 20 次无句柄增长 | **未通过** | **未通过** |
| DLL 缺失，中文错误、进程正常退出 | — | 通过 |

自动报告采集时 Windows HDR 为关闭状态；显示器接口报告支持 HDR，峰值约 497 nit，SDR 白约 80 nit，自动模式输出 Gamma 2.2 / BT.709。随后用户按人工 HDR 检查步骤开启 HDR、确认 PQ / BT.2020 与高光，再在播放中关闭 HDR 检查无需重启切回 SDR，并回复“验收通过”。HDR 高光与动态切换据此记录为人工通过；自动报告保留采集时的 SDR 状态。

最近一轮计时：

| 指标 | Debug JIT | Native AOT |
|---|---:|---:|
| 新进程启动 → Video Lab Loaded | 1065 ms | 506 ms |
| 开始创建播放器 → 首次 PlaybackRestart | 405 ms | 261 ms |

窗口计时从操作系统的进程 StartTime 开始，包含 native bootstrap。首帧使用 mpv 的 PlaybackRestart 作为代理，包含实例创建和打开片源，不是屏幕扫描输出时间，也不包含 URL 预解析。这些数值来自全新的应用进程，但系统文件缓存和 shader cache 已热；不代表重启系统后的磁盘冷启动。早期首次打开、shader cache 尚冷时观测到约 1.5 秒，旧窗口计时口径只从托管 Main 开始，不能与上表直接比较。

原始报告在本地 `artifacts/p0-debug-smoke.json`、`artifacts/p0-aot-smoke.json` 和 `artifacts/p0-aot-missing-library-smoke.json`；重复运行脚本会覆盖同名报告。

## 未通过：硬件渲染生命周期的句柄增长

每次创建 / 销毁硬件渲染实例后，当前进程约增加 1 个 Section 和 1 个 Mutant。最新 AOT 从第 1 次到第 20 次再静置 5 秒：Section 41 → 60、Mutant 18 → 37，总句柄 1030 → 1072，线程数保持 62。Private Bytes 从 360.7 MiB 到 369.9 MiB；循环期间有暂时升高，静置后回落。本轮数据不能证明内存长期无增长，而句柄增长已经足以使验收失败。

为排除 App 集成的影响，在独立临时 console 中用相同封装做原生对照，关闭音频、不加载片源、不绑定 WinUI：

| 20 次原生实例对照 | Section / Mutant 的变化 |
|---|---|
| 无头 `vo=null, ao=null` | 均不增长 |
| 硬件 `gpu-next + d3d11 composition` | 各增加 20 |
| WARP 软件渲染，关闭硬解 | 均不增长 |
| 改为旧 `vo=gpu` | 仍增长 |
| 关闭 shader cache / 字体，限制 feature level 11_0 | 仍增长 |
| 诊断性比较 libmpv release 20261002 | 仍增长；仓库 lock 仍为 20260610 |
| 直接创建并释放 D3D11 device / context，设置最大帧延迟 | 均不增长 |
| 上一项改为每次使用新的专用线程 | 均不增长 |
| 不加载 libmpv，直接创建 / 释放硬件 DXGI composition 交换链 | Section 23 → 43、Mutant 7 → 27，静置 5 秒仍不回落 |
| 上一项改为 WARP | Section 20 → 20、Mutant 2 → 2 |

最后两项是仓库内的独立诊断工具 `scripts/diagnostics/Mambo.CompositionProbe`，不引用或加载 WinUI / libmpv。它依次创建 device、IDXGIDevice1、adapter、factory、composition swapchain，反向释放所有 COM 引用；没有提交 GPU 命令或绑定资源。直接交换链对照在同一线程及每次新建线程时都复现增长。

证据将范围缩小到本机的硬件 DXGI composition / 驱动交互，仍不能断定具体驱动或系统组件。COM 引用约定修正后增长仍存在，不再通过释放借用指针尝试规避。后续应使用最小复现比较另一硬件 / 驱动环境，再决定解决方式。没有把生产配置改为 WARP，也没有复用单例 mpv 掩盖生命周期问题。

复现命令（第一条当前退出码 1，两个对照退出码 0；2 表示采样或 API 不可用）：

```powershell
dotnet run --project scripts/diagnostics/Mambo.CompositionProbe -p:Platform=x64
dotnet run --project scripts/diagnostics/Mambo.CompositionProbe -p:Platform=x64 -- --warp
dotnet run --project scripts/diagnostics/Mambo.CompositionProbe -p:Platform=x64 -- --device-only
```

该工具放在 scripts，不加入四项目应用解决方案；资源增长不会被混同为 Core 单元测试失败。

`test-video-lab.ps1` 分别报告 `PipelinePassed` 和 `ResourcesStable`；默认画面检查通过不代表完整 P0 验收通过。完整资源关卡可运行：

```powershell
pwsh scripts/test-video-lab.ps1 -Aot -RequireStableResources
```

当前预期因资源稳定性未通过而返回失败。

## 需用户完成的验收与关卡

运行 `dotnet run --project src/Mambo.App -p:Platform=x64`，或启动 `publish/aot/Mambo.exe`，在 Video Lab 选择下载的样片。也可先设置 `$env:MAMBO_VIDEO_LAB_SAMPLE` 为样片的绝对路径，让输入框自动填入；不要给交互运行加 `--smoke`。

- [x] Windows HDR 开启后，自动模式输出 PQ / BT.2020，高光和亮度正确（用户确认）。
- [x] 播放中关闭 Windows HDR，无需重启切到 SDR（用户确认）。
- [ ] 播放中重新开启 HDR 也正确。
- [ ] 100 / 150 / 200% 缩放、跨显示器拖动，面板和缓冲区像素一致，画面无异常拉伸。
- [ ] 拖动缩放、最大化 / 还原 / 全屏的视觉无闪烁；无顶部 1px 缝隙。
- [ ] 画面上方的 XAML 按钮能暂停；拖动进度条能 seek；隐藏光标后可恢复。
- [ ] 真实 Emby 直链与 302 场景，抓包确认跨域请求不携带服务器认证。地址 / 令牌只在 Video Lab 输入，不发进聊天、不写入文件或日志。
- [ ] 解决当前 DXGI composition 的句柄增长；若继续受环境阻挡，由用户决定后续验证环境与验收安排。
- [x] 用户确认 P0 关卡，批准携下述遗留项进入 P1a 契约与假实现（2026-10-02：“提交，进入P1”）。

PLAN 的 P0 进度按用户明确批准推进，并注明遗留项。进入 P1 时，`VideoSurface` 仍只有 P0 的 internal 指针绑定入口，不能作为前端依赖；公开 `Attach(IPlaybackSession)` 接口将在 P1a 形成。Program / App / MainWindow 的后续修改移交给 Claude。资源增长及尚未完成的人工检查继续跟踪，进入 P1 不代表这些检查已经通过。
