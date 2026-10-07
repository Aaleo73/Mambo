# P0：WinUI 3 + libmpv 视频技术验证

日期：2026-10-02。状态：用户在知悉下列未通过与待测项后，明确要求“提交，进入P1”，批准携遗留问题结束 P0 并进入 P1。以下验收结果保持原样，不将未通过项改记为通过。

## 验证环境

- Windows 11（`10.0.28000`），VS2022 BuildTools / MSVC 与 Windows SDK 26100，只用命令行构建。
- GPU：NVIDIA GeForce RTX 4060 Laptop GPU，驱动 `32.0.16.1692`；系统另有 GameViewer Virtual Display Adapter。
- 当时的 libmpv 是 shinchiro release `20260610`（mpv 修订 `304426c390901436fb1d4a63efbd582ae80c88f4`）；发布用的运行时后来换成 MSYS2 包，见 [原生组件对应源码](native-distribution.md)。
- 样片：Jellyfin / Gnattu 的 **Test Jellyfin 4K HEVC HDR10 40M**（30 秒、3840×2160，CC BY-SA），由 `scripts/fetch-video-sample.ps1` 下载并校验 SHA-256。样片、原生 DLL 和下载缓存都被 Git 忽略。
- 复测：`pwsh scripts/test-video-lab.ps1`，AOT 加 `-Aot`，DLL 缺失场景加 `-MissingLibrary`。

## 已验证的管线与实现决定

采用 `vo=gpu-next`、`gpu-api=d3d11`、`gpu-context=d3d11`、`hwdec=d3d11va`，初始化前设置 `d3d11-output-mode=composition` 和面板像素尺寸。mpv 不创建播放 HWND，交换链挂到 `SwapChainPanel`；控件叠在同一个 XAML 视觉树中。

`display-swapchain` 的属性 getter 返回**借用指针**。依据固定修订的 [player/command.c](https://github.com/mpv-player/mpv/blob/304426c390901436fb1d4a63efbd582ae80c88f4/player/command.c) 与 [video/out/vo.c](https://github.com/mpv-player/mpv/blob/304426c390901436fb1d4a63efbd582ae80c88f4/video/out/vo.c)，应用在事件线程显式 `AddRef`，用自己的 `SafeHandle` 持有排队期间的引用，UI 绑定成功或放弃后释放。不能把其它内部 helper 的引用约定套用到这个属性上。

事件线程立即复制 mpv node / property 数据，不把事件缓冲区指针传到 UI。`loadfile` 使用异步 node 命令和 options map，返回 `playlist_entry_id`；属性写入为异步。关闭顺序是 UI 解绑、发送 quit、等待事件线程、后台 destroy，带超时保护，不允许 destroy 与 `mpv_wait_event` 并发。

面板像素尺寸为 DIP 尺寸乘 `RasterizationScale` 并取整。拖动窗口期间保留已提交的缓冲区尺寸，用 XAML transform 临时伸缩；拖动结束后更新 mpv，轮询 `GetDesc1`，缓冲区匹配后延迟 40 ms 提交面板布局。`SetMatrixTransform` 仅在绑定和 DPI 变化时设置。

全屏暂采用 `AppWindow.SetPresenter(FullScreen)`，退出时恢复同一个显式创建的 `OverlappedPresenter`。AOT 下从 `AppWindow.Presenter` 动态强转曾失败，现改为保存创建时的强类型实例，已重新通过 AOT 最大化 / 全屏验证。是否需要无边框的备选方案（`docs/ARCHITECTURE.md` §3.10），要由人工闪烁检查决定。

`HdrController` 通过窗口所属的 `Microsoft.Graphics.Display.DisplayInformation` 监听高级颜色变化。自动模式仅在 Windows HDR 已开启时设置 PQ / BT.2020、峰值、对比度和参考白；关闭 HDR 则回到 auto，交换链输出格式保留 auto。显示器监听由窗口持有，每次播放只连接或断开 mpv。

`StreamUrlResolver` 的探测关闭自动重定向和 Cookie。同源认证只放在 `X-Emby-Token` 请求头；同源认证 query 会移除，跨 scheme / host / port 的跳转立即结束探测并移除认证头，不预先请求 CDN。跨域 URL 保留 CDN 自己的签名，仅剔除与 Emby 令牌相同的复制凭据。支持 Range → HEAD → 普通 GET 回退、5 跳限制、单次 3 秒和总计 6 秒超时。异常不保留可能包含地址的原始网络消息；P0 不接收原始 mpv 日志。跨域跳转的处理后来有调整，见 [播放跳转兼容](playback-issued-redirect.md)。

Native AOT 使用 `LibraryImport`、函数指针、JSON 源生成、partial WinRT 类型；发布显式携带 `Mambo.pri`、`App.xbf`、`MainWindow.xbf`、`Debug/VideoLab.xbf`。另有 ReadyToRun、不裁剪配置，本轮未单独发布验证。

## 自动验收结果

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

COM 引用约定修正后增长仍存在，不再通过释放借用指针尝试规避。没有把生产配置改为 WARP，也没有复用单例 mpv 掩盖生命周期问题。

### 定性（2026-10-07）：NVIDIA App 的注入模块，接受为已知限制

用同一工具继续缩小范围。本机为 RTX 4060 Laptop，驱动 32.0.16.1714，NVIDIA App 的 `nvspcap64.dll` 版本 11.0.9.251。

| 对照 | 结果 |
|---|---|
| 每次新建设备并创建 composition 交换链，500 次 | Section +500、Mutant +500，线性增长；Private Bytes 平均每次 +90 KB |
| 只建设备、不建交换链，500 次 | 句柄不增长；Private Bytes 平均每次 +17 KB |
| 同一个设备上创建 / 释放 20 次或 500 次交换链 | 总共只多 1 个 Section 和 1 个 Mutant |
| 释放交换链后先 `ClearState` + `Flush` | 仍各增加 20，不是 flip 模型的延迟销毁 |

- **增长按设备计，不按交换链计**：每个创建过 composition 交换链的硬件设备留下一对句柄。应用里每个播放会话一个 mpv 实例、一个设备，所以表现为每次播放一对。
- **新增句柄指向同一对具名对象**：`\Sessions\<n>\BaseNamedObjects\{2627E361-24E2-4F14-99ED-A20D0685D8DD}_v22`（Section）和同名加 `_0` 的 Mutant。探针进程加载的全部模块里，只有 `C:\Windows\System32\nvspcap64.dll`（NVIDIA App 的「NVIDIA Game Proxy」，NVIDIA 签名）含这个 GUID。也就是说，这个注入模块在每个新设备创建交换链时再打开一次自己的共享内存和互斥量，设备销毁后没有关闭。
- **结论**：不是 Mambo、WinUI 或 libmpv 的引用泄漏。这对句柄由注入本进程的第三方模块自己持有，应用不应该替它关闭。
- **影响**：每次播放多 2 个句柄；探针里带交换链比只建设备每次多约 70 KB 私有内存。播放 1000 次约 2000 个句柄、几十 MB，重启应用清空，离进程句柄上限（千万量级）很远。
- **决定**：接受为已知限制，不为它改生产配置，也不引入跨会话共用设备或单例 mpv。`test-video-lab.ps1 -RequireStableResources` 在装有 NVIDIA App 的机器上会继续失败，不再作为阻塞项。
- **没有验证**：关闭 NVIDIA App 的游戏内叠加层后增长是否消失（要改用户的系统设置，没有做）；AMD、Intel 显卡和没装 NVIDIA App 的机器。

复现命令（第一条当前退出码 1，后两个对照退出码 0；2 表示采样或 API 不可用）：

```powershell
dotnet run --project scripts/diagnostics/Mambo.CompositionProbe -p:Platform=x64
dotnet run --project scripts/diagnostics/Mambo.CompositionProbe -p:Platform=x64 -- --warp
dotnet run --project scripts/diagnostics/Mambo.CompositionProbe -p:Platform=x64 -- --device-only
```

`--cycles N` 改循环次数，`--shared-device` 在同一个设备上反复创建交换链，`--flush` 在释放后清状态并 Flush，`--names` 列出新增 Section / Mutant 的对象名。

该工具放在 scripts，不加入四项目应用解决方案；资源增长不会被混同为 Core 单元测试失败。

`test-video-lab.ps1` 分别报告 `PipelinePassed` 和 `ResourcesStable`；默认画面检查通过不代表完整 P0 验收通过。完整资源关卡可运行：

```powershell
pwsh scripts/test-video-lab.ps1 -Aot -RequireStableResources
```

当前预期因资源稳定性未通过而返回失败。

## 需用户完成的验收与关卡

在 Video Lab 选择下载的样片检查；交互运行不要加 `--smoke`。

- [x] Windows HDR 开启后，自动模式输出 PQ / BT.2020，高光和亮度正确（用户确认）。
- [x] 播放中关闭 Windows HDR，无需重启切到 SDR（用户确认）。
- [x] 播放中重新开启 HDR 也正确。
- [x] 100 / 150 / 200% 缩放、跨显示器拖动，面板和缓冲区像素一致，画面无异常拉伸。
- [x] 拖动缩放、最大化 / 还原 / 全屏的视觉无闪烁；无顶部 1px 缝隙。
- [x] 画面上方的 XAML 按钮能暂停；拖动进度条能 seek；隐藏光标后可恢复。
- [x] 真实 Emby 直链与 302 场景，抓包确认跨域请求不携带服务器认证。地址 / 令牌只在 Video Lab 输入，不发进聊天、不写入文件或日志。
- [x] DXGI composition 的句柄增长：已定性为 NVIDIA App 注入模块的行为，接受为已知限制，见上文「定性」。
- [x] 用户确认 P0 关卡，批准携下述遗留项进入 P1a 契约与假实现（2026-10-02：“提交，进入P1”）。

上面五项人工检查在 2026-10-07 由用户确认全部通过；用户给的是整体结论，没有逐项记录。
