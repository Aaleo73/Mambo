# P4–P6 统一接手与界面验证

日期：2026-10-03。

用户因 Claude 额度耗尽，明确把后续全部工作交给 Codex；随后要求持续工作、不再询问，并授权后续必要步骤。因此本轮 Codex 同时负责前端与后端路径，保留已确认的 P2 设计、契约边界和不推送远端的约定。原有分工表保留为历史记录，不再阻止本次明确授权的修改。

## 实现

- 详情页：继续播放、播放进度、换季与选集、人物、缺图占位、返回状态、图片转场。
- 播放层：内置画面与外部播放面板、控制层显隐、进度插值和拖动、轨道/倍速/音量、选集抽屉、错误重试、全屏及 Esc 层级、阻止屏保和焦点恢复。
- 设置：主题持久化、缓存统计/清理、日志入口、外部 MPV 显式批准与状态、实际版本和许可入口。
- 加固：账号切换清理图片解码代际，旧加载不得回填；损坏图片保持占位；隐藏首页停止轮播；淘汰重建页先补齐分页再恢复深滚动；标题栏和全局快捷键接入。
- 控件释放：播放层与 VideoSurface 的计时器解除 Tick 订阅、停止 compiled bindings、关闭菜单、释放光标与 Composition 对象。VideoSurface.Detach 保留复用能力，新增 Dispose 表示永久销毁；关闭应用先停止后端，再释放外壳与服务。

## 自动验证范围

`scripts/test-ui-lab.ps1` 启动正常 WinUI 外壳，强制使用假服务；不读取真实凭据。报告保存在 `artifacts/ui-debug.json` / `ui-aot.json`，截图在同名目录。离屏 RenderTargetBitmap 不含系统 backdrop，截图临时使用同主题底色，不能据此宣称系统亚克力实拍已验收。

`StartupTimeline` 以进程创建时刻记录外壳 Loaded 和首页内容的下一次 XAML Rendering；假数据首屏不等同于真实账号磁盘缓存冷启动。缓存首屏要求实际可见的首页卡片对象仍来自磁盘 continue/latest 快照，只恢复库列表、hero 或来自网络的新卡片均不算；网络成功替换、Reset/Clear 与 Dispose 后撤销来源。八项独立来源测试已通过。`UiPerformanceProbe` 完整加载 5000 项后采样三段真实滚动，仅用 RenderingTime 计算 UI 回调间隔，不把回调频率称作 GPU 呈现帧率；无有效时间戳时明确返回 Unsupported。

50 次假播放关闭检查包含句柄类别、进程私有内存和 PlayerOverlay / VideoSurface 的弱引用。GC 后等待 UI Dispatcher，再反复 GC 与采样；等待本身不等于已释放，零留存阈值保持不变。原生硬件 composition 的 P0 Section/Mutant 遗留项仍独立保留，不能由假播放结果覆盖。

当前完整单元测试 345 项通过，无失败或跳过；集中构建零警告、零错误。先前 NU1900 是离线还原状态；实际用户环境重新执行 locked restore 后不再出现。后续 UI、AOT、性能和安装器的实际结果继续补入此记录。

2026-10-03 原生转储定位到两个实际问题：旧退出崩溃的托管及原生栈为 `HeroCarousel.OnUnloaded → WindowContrastObserver.Dispose → ThemeSettings.remove_Changed → EnsureWindowFeatureDetached → terminate/abort`，发生在 UI 主线程。已将原生订阅集中到窗口存活期，页面只订退托管通知，窗口关闭前在 UI 线程解除唯一原生订阅。深滚动崩溃为 `AG_E_LAYOUT_CYCLE` / `0x88000FA8`；修复同步布局中的 ChangeView/分页和 SearchPage.UpdateLayout，并在 LandscapeCard.MeasureOverride 中设置自适应高度，避免冷创建/回收的第零项测量值与实际排列高度不一致。

Debug 完整实际回归 `artifacts/ui-native-recheck.json`（RunId `2a5e374bffa0408ca7bdc727157a6606`，2026-10-03 UTC 02:48:59–02:49:31）全部通过并正常退出：五轮真实 AutomationPeer/原生 UIA 模式/焦点检查零缺失；资料库返回、最近播放前进、搜索替换后的返回/前进均经历缓存淘汰重建与超出初页的深滚动，恢复偏差均 0 DIP；错误页创建/进入/离开异常、重试与首页动作通过；播放共享输入/按钮/菜单/焦点循环回归通过，七次会话关闭；另 50 次关闭后 PlayerOverlay/VideoSurface 残留均为 0，未加载 libmpv。5000 项全量加载、最多 72 个 realized cards、进程私有内存峰值约 275 MiB，三段 Rendering 回调间隔 P95 为 10.35–11.66 ms，仍不等同于 GPU 60fps。

此外一次运行曾在 Finalizer 的 `ComWrappers.NativeObjectWrapper.Finalize → ScrollViewer/GettingFocusEventArgs` 释放中检测到 `0xC0000374` 堆损坏，写坏位置尚未证明。跳过原生 UIA 的隔离轮次明确保持未通过；上述完整回归已恢复全部原生检查。单次 debuggee 的堆验证启动后确认 verifier 未启用，未执行界面测试，报告为 Unsupported；不能根据释放发现点归因或宣称问题已解决。

后续 Debug 压力轮次出现最后几个播放层与表面对象仍存活（3–6 个），保留失败结果，未放宽零留存要求。2026-10-03 UTC 03:32:05 结束的一轮诊断正常退出，仍有 4/4 个留存；本机 CDB 在 Process.Start 遇到 Windows 错误 5（Access denied），未生成新转储，该轮没有引用根证据。界面新增下一轮可用的导航失败阶段/HResult 与报告写入错误类型/HResult，均不记录异常文本或媒体信息。播放器自建的控制层动画与 easing 已明确停止并释放，未据此认定它就是留存根因。

2026-10-03 UTC 04:04:56–04:06:33 的 Debug 假数据轮（`artifacts/ui-managed-retained-43c3062757104d0cbfb4bc4a4c90c212.json`）正常退出、没有强制清理，50 次关闭后仍记录 4/4 个对象存活。官方 `dotnet-dump` 在本轮暂停期间采集托管堆；PID、进程创建时刻、父 PID、程序绝对路径、HWND 所属、RunId、报告开始时刻、六类 Fake 服务和未完成的留存暂停状态全部核验通过。离线按精确类型和 MethodTable 枚举，4 个 `PlayerOverlay`、4 个 `VideoSurface` 的 `gcroot` 均为 0 个唯一根；同堆唯一 `MainWindow` 对照找到 3 个强根，包括窗口 Hook 和 `TimerQueue → UiLabSmoke.RunAsync` 状态机路径。分析进程均正常退出，没有 DAC、内存读取或其他分析警告，证据保存在 `artifacts/native-exit-helper/retained-dotnet/36a317b3e5a14aa59db19d3adbdb02a3/roots.json`。

上述零根结果只表示 SOS 在该 Debug 转储中没有找到它所识别的根，不能据此断言目标对象没有原生条件根，也不支持把问题归为仅采样延迟。对同一已核验转储执行离线 `gchandles`，4458 条 RefCounted handle 全部指向 `System.Runtime.InteropServices.ComWrappers+ManagedObjectWrapperHolder`，SOS 报告的 RefCount 全为 0，分析正常退出且无警告。[官方 DAC 实现](https://github.com/dotnet/runtime/blob/main/src/coreclr/debug/daccess/daccess.cpp#L6770) 的 `GetRefCountedHandleInfo` 只读取传统 `ComCallWrapper` 的活动状态，未识别到该 wrapper 时返回 0 / false；而 [ComWrappers 的条件根](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/ComWrappers.cs#L184) 还依赖 COM 计数、ReferenceTracker 计数与 pegging 状态。普通 MainWindow 强根对照验证了基本枚举，未验证这一特殊状态的覆盖；目前没有把任何具体 RefCounted handle 认定为播放器的实际留存根。

检查收尾在 STA `await` 之后补 GC，AOT 的 `artifacts/ui-aot-lifetime-final.json` 仍有 4/4 个留存。随后 Debug 的 `artifacts/ui-debug-collection-series.json` 从 632 ms 至 10433 ms 的 22 轮采样均为 3/3，否定仅需多静置 2.5 秒的解释；所有功能通过，但总体仍未通过。当前使用非泛型 `WeakReference.IsAlive`，[.NET 10 实现](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/WeakReference.cs#L104) 只查询内部弱 handle，不调用 `Target` 的 COM 重建路径，因此没有证据把留存归为采样自身重投影。此前关闭原生 UIA 的隔离轮次留存为 0，但无障碍验收未通过；这只是定位线索，尚未证明 UIA 缓存或其他原生组件持有这些对象。继续保留播放器和表面的零留存要求，亦不将本次托管分析扩展为整个产品无泄漏或上述原生堆损坏已解决。

关闭焦点路径随后发现可直接修复的顺序缺陷：`ShellView.RemovePlayer` 原先在浏览页和侧栏仍禁用时释放、移除播放器，之后才尝试恢复焦点；旧目标只检查 `IsLoaded`，也忽略 `Focus` 的失败结果。现先恢复浏览区的可见性与可用性，验证原目标已加载、可聚焦、属于当前 XamlRoot 与 Shell，且祖先可见可用、不是旧播放器后代，再恢复焦点；失败时转到实际导航控件。资源释放、清除 PlayerSlot 和导航解锁保留有序 `finally`。新增诊断只记录焦点恢复成功及最终仍位于 Shell 内的布尔值，不记录元素名称或输入内容。

修复后的 Debug 完整实测 `artifacts/ui-debug-focus-release.json`（RunId `6d41863045c749ea851cb0c891660666`，2026-10-03 UTC 04:25:11–04:25:59）正常退出、总体通过：50 次关闭的焦点恢复均成功且最终位于 Shell 内，五轮原生 UIA/AutomationPeer、播放控制、导航深恢复及错误页恢复均通过；PlayerOverlay/VideoSurface 最终留存均为 0，句柄变化 −105，进程私有内存变化 −38,715,392 字节。收尾前 17 轮采样（612–7911 ms）仍为 4/4，第 18 轮（8286 ms）才降为 0/0。这是一次完整 Debug 回归的成功证据，不能表述为焦点修复立即消除了留存，也未证明具体原生引用根；仍可能涉及 WinUI/UIA 条件根的异步释放。对应 AOT 验证结果见下文，不据此宣称历史堆损坏或所有原生资源问题已解决。

普通主题在 Acrylic/Mica 均不可用时显示按设计浅/深色的实色底，高对比度使用系统 WindowColor；材质真正连接成功后隐藏兜底。单实例自动验证也已通过：同假模式第二次启动重定向，无第二窗口，首次窗口正常关闭退出码 0；诊断入口按进程隔离。

## Native AOT 资源投影修复

2026-10-03 集中 AOT 验证发现首页和资料库构造时的 `InvalidCastException`。27 个发布 XBF、PRI 与 Release 输出逐文件哈希匹配，生成代码校验和及 LibraryPage 的 25 个连接 ID 也一致。假模式专用探针实测 `SkeletonBlockStyle` 的返回对象类型为 `Microsoft.UI.Xaml.DependencyObject`，`is Style` 为 false；同一资源在 Debug 可直接转换。因而问题是原生资源的托管投影，不是遗漏发布资源。

新增 `Themes/XamlResources.cs`，对 Style、DataTemplate 及缓冲模板 Border 使用具体类型的 `WinRT.CastExtensions.As<T>`，将资源查询与所需原生接口显式关联；不增加反射、全程序集保留或运行时泛型。依据 [C#/WinRT CastExtensions 源码](https://github.com/microsoft/CsWinRT/blob/master/src/WinRT.Runtime/CastExtensions.cs)，此方法在托管转换失败时查询对象原生接口，也是 XAML 编译器的连接代码所用方式。

修复后的 `artifacts/ui-aot-resource-fixed.json`（2026-10-03 UTC 03:50:37–03:51:11）正常退出，首页、资料库、详情、搜索、主题、淘汰恢复、错误页恢复、五轮无障碍和完整播放控制全部通过，没有页面构造错误。50 次关闭仍有 2 个播放层与表面对象存活，所以总体仍为未通过，继续核对引用根；不将这些功能结果扩展为内存验收通过。

最终候选的完整 AOT UI 轮 `artifacts/ui-aot-final.json`（RunId `29684b5922e849178d13fbee6b919db0`，2026-10-03 UTC 04:30:27–04:31:09）正常退出，功能、导航、错误页恢复、五轮原生无障碍与播放控制均通过，50 次焦点恢复也全部成功；但 23 轮收尾采样（528–10164 ms）的播放层/表面留存始终为 50/50，总体未通过。句柄为 1747→2075，其中 Timer 为 146→311、WaitCompletionPacket 为 130→296。相同候选安装目录的 `artifacts/installer-validation/211fcecd26bf41a9b98f6355fcdb8a6c/ui-smoke.json`（UTC 04:32:03–04:32:46）也正常退出且所有功能与 50 次焦点恢复通过，留存却为 2/2、句柄为 1742→1688，UI 结果仍未通过。差异说明留存不稳定，计时器句柄增长也可能是对象留存的结果，未证明它就是根因；安装生命周期的独立结果见 P8 记录。

仅用于隔离的 `artifacts/ui-aot-no-native-isolation.json`（RunId `b9179d1e3aa7405c93a392386998117a`，UTC 04:34:07–04:34:38）跳过原生 UIA 后，50 次关闭留存为 0/0、句柄为 1633→1529；报告中四轮 Shell 无障碍明确失败，原生模式状态为 `DiagnosticNativeUiaSkipped`，因此总体仍未通过。这是调查同进程原生 UIA 与 WinRT 条件根的线索，不是原生 UIA 缓存已被证明为根因，也不能用跳过检查完成验收。原生 UIA 子进程验证方案正在开发，尚待实际验证。

设置页宽度和详情评分星已按设计修正，新 Debug 浅/深色设置与详情截图复核通过。IPC 生命周期测试使用可控时钟，避免无关的实际磁盘延迟消耗停止预算；生产 3 秒预算保持不变，独立预算与重试用例保留。最终全套单元测试重新运行 345/345 通过，locked restore 成功。

## 后续集成验证与剩余范围

### 停止播放后的页面进度刷新

新增 `Debug/PlaybackRefreshSmoke.cs`，作为完整 UI 回归的必要条件。固定演示目录不会保存播放位置，因此测试使用独立的生产 `LibraryService`、`QueryCache` 和三个实际详情/最近/资料库页面；它们只挂到当前假窗口的独立容器，不替换全局服务、导航或静态图片入口。账号和凭据为随机合成值且仅驻留内存，自定义 HTTP handler 只处理白名单合成响应，没有网络回退；缓存使用本轮新目录，持久化写入为空操作。

先断言三页真正加载的进度环/进度条和文字显示 100 秒的位置，再通过原假播放会话暂停、跳到 10 分钟，确认页面尚未自行变化。由播放器实际 Close 按钮的 InvokeProvider 触发关闭，在 Closed 快照通知中将合成响应更新为实际最终位置；随后必须由生产 `PlaybackStopped` 处理器自行失效并刷新查询。测试不调用 Refresh、不手动发 Stopped、不直接修改页面 ViewModel。验证三个端点重新请求、实际 XAML 绑定与继续播放文字更新、卡片数据对象替换、全局页面和历史不变，以及所有自建页面/查询/订阅释放；任意失败均影响 UI 总结果，不能归类为“仅回收失败”。

Debug `artifacts/ui-debug-playback-refresh-2.json`（RunId `c1602424a5cb47b5ae54d65efcc20ddf`）完整 Passed，17 项刷新检查及清理全部通过，原五项无障碍、导航、播放器控制和 50 次关闭检查保持通过，910 ms/四个清理帧后留存为 0/0。前一轮 `ui-debug-playback-refresh.json` 的刷新检查也通过，但读取运行中报告时发生 File.Replace `80070497`，导致导航进度回调中断，整体结果仍记失败。重跑等进程退出后读取，没有删去任何功能断言，也没有放宽失败条件。

同代码的交付候选 `publish/releases/0.1.0/20261003-125913-482/app` 已完成 AOT 实测：`artifacts/ui-aot-delivery.json`（RunId `727e723b29e0498ebe04624404547e5e`）完整 Passed，17 项刷新检查及清理全部通过，50 次关闭后 460 ms/四个边界内归零，句柄 1649→1573。此处“交付”指本地候选，不代表全部性能及人工显示验收已完成。

### 生命周期与 AOT 回归

2026-10-03 后续完整回归已将原生 UIA TextBox 查询移入受限的同程序子进程；父子 PID、创建时刻、执行文件、实际父进程、HWND、nonce 和协议版本均核验，六类 Fake 服务检查保留，原生模式验收没有跳过。子进程正常退出也记录在报告中。AOT 的 RenderingEventArgs 改用具体 WinRT 投影后能够取得时间戳，仍只衡量 XAML Rendering 回调。

播放菜单修复了一个确定的竞态：Hide 的延迟 Closed 回调原先可能扣减后来打开的新菜单计数；现在按具体菜单实例增删，关闭时解除对应事件。永久释放同时精确解除 20 个主树按钮和选集按钮的实际 Click 及其他 XAML 事件，停止绑定并移除 Content。50 轮验收各经过挂接后和关闭后的 Rendering，OpenedFrameCycles / ClosedFrameCycles / FocusRestoresSucceeded 均要求 50；常规零留存要求保持。

`artifacts/ui-debug-render-cycles-1.json` 功能全部通过、正常退出，但最后索引 45–49 的播放层与表面在 10 秒内始终为 5/5，句柄 1799→1758。相同代码的 AOT 两轮 `ui-aot-render-cycles-1.json` / `ui-aot-render-cycles-2.json` 功能全部通过、正常退出；第一轮从 3/3 于 4197 ms 降至 0/0，总体通过，句柄 1625→1542；第二轮仍留存索引 47–49 共 3/3，句柄 1609→1561，总体失败。故不能把一次通过扩展为稳定无泄漏。

只作调度干预的 `artifacts/ui-debug-driven-collection.json` 在原 10 秒回收窗口持续订阅 Rendering，首个 585 ms 采样已有 34 次回调、留存 0/0；报告 CollectionFramesDriven=true 且 Passed=false，不算常规验收。后续探针改成先完整保留原 10 秒常规结果，只有仍有留存才另做最多 2 秒的驱动对照，另存 DrivenCollectionSamples，不覆盖原指标。生产代码没有增加强制 GC 或持续绘制。

同进程对照 `artifacts/ui-debug-collection-same-process.json`（RunId `062a31d3b2d548ad817256ea90b5897c`）正常退出：前 681–10592 ms 的全部采样仍为尾部 4/4，随后独立驱动段在 509 ms、13 次 Rendering 回调后降至 0/0，原失败结果未被覆盖。这证明本轮对象在 GC 后继续经过 XAML 绘制周期可以释放，不能据此追认所有旧失败或原生堆损坏已解决。[WinUI 的 UIAffinityReleaseQueue](https://github.com/microsoft/microsoft-ui-xaml/blob/main/dxaml/xcp/dxaml/lib/UIAffinityReleaseQueue.cpp) 也明确通过 UI tick 分批处理释放；源码只说明机制，不当作本机特定 native 根证据。

据此修正标准检查的清理协议：每次 GC 后等待两个真实 Rendering 边界，第二个边界确保前一帧已完成；保留原 200 ms 调度时间、第二次 GC、10 秒窗口和零留存阈值。每轮累计记录 CleanupFrameCycles，不再用普通 Delay 代替清理帧。一轮取样包含四个明确等待的边界；最终通过还明确要求至少一份完整样本，且零留存样本在 10 秒内，不能把跨过截止时间才归零记为通过。一次性持续绘制实验和环境开关已从源码删除。此改动仅让诊断完成必要的 WinUI 清理周期，生产路径没有增加 GC 或持续绘制。

新标准已完成四轮独立进程验证，全部正常退出、功能检查通过。Debug 的 `ui-debug-cleanup-frames-1.json` / `ui-debug-cleanup-frames-2.json` 分别在 771 / 729 ms、四个清理帧边界后达到 0/0，句柄分别为 1760→1694、1799→1728。`publish/releases/0.1.0/20261003-123901-977/app` 的 Native AOT 轮 `artifacts/ui-aot-verified-1.json`（RunId `ac262503dd134f8fb90e334c224631bf`）在 590 ms 达到 0/0；同包安装目录的 `artifacts/installer-validation/34b4b28678d94440b24a52898c867665/ui-smoke.json`（RunId `c3f627527dd541d992623bb9b0717bb2`）在 461 ms 达到 0/0。每轮均保留五项原生 UIA 检查，50 次会话关闭、挂接帧、关闭帧和焦点恢复全部通过；以上只证明此假播放场景的托管控件释放，不覆盖 P0 的硬件 DXGI 资源遗留。

该 AOT 包随后通过 P3 真实 libmpv 播放/连播/上报/解绑、假模式命令行和环境变量入口、P7 IPC 及单实例回归。单实例报告为 `artifacts/single-instance-validation/46d1af21397449bb85cb8dba007f5f59/result.json`，首个实例与重定向进程均正常退出。实际安装/升级/卸载整体 Passed，证据见 P8 安装记录。

真实 Loaded 事件计时已改为在 Activate 前订阅、回调内即时记录，窗口关闭或 30 秒超时后解订；首页时间只在可见卡片、持久缓存来源和几何记录全部采集成功后公布。新候选的 `artifacts/startup-validation/2e252a0e6e68403cb519da37b2bf7c5f/summary.json` 三轮窗口为 1394 / 883 / 864 ms，缓存首页为 1506 / 977 / 958 ms，每轮四张可见持久缓存卡片、19 个恢复快照。三个进程均正常退出且未强制清理，但均未达到 600 / 800 ms，结果保持 ResultsFailed。与此前曾达到 800 ms 的轮次一起保留，不选择性只报告较快结果。

125913 候选的最终三轮启动为 `artifacts/startup-validation/2385254a27274c90b0c07fd983dd7486/summary.json`：窗口真实 Loaded 为 564 / 651 / 633 ms，可见持久缓存首页为 620 / 728 / 699 ms；每轮四张可见缓存卡片、19 个恢复快照，进程退出码均为 0，无强制清理。缓存首页三轮全部达到 800 ms，窗口只有第一轮达到 600 ms，整轮仍为 ResultsFailed。新旧轮次有波动，不把这次较快采样描述为新增诊断修复了启动性能，也不把新进程等同于清空系统文件缓存后的冷机启动。

最新启动采样 `artifacts/startup-validation/7b86ed0632a54d43bbfc2e70e03540da/summary.json` 三个新进程正常退出，窗口 Loaded 为 735 / 704 / 701 ms，实际可见的持久缓存首页为 749 / 721 / 721 ms，每轮 4 张缓存卡片、19 个恢复快照。缓存首页 800 ms 已在三轮达到，窗口 Loaded 600 ms 仍未达到，整体 ResultsFailed。分项确认 presenter 获取和投影约 0 ms，SetBorderAndTitleBar(true,false) 为 276 / 276 / 246 ms；保留必需调用与现有顺序，不以更早的 Activated 代替 Loaded 宣称通过。

2026-10-03 UTC 04:02:57–04:03:02 已完成首次真实账号启动采样（`artifacts/startup-validation/086895ce020c4acbab30783aad3676d9/summary.json`）。三个新进程均正常退出，窗口 Loaded 为 1372 / 927 / 936 ms，首页首内容为 1910 / 943 / 958 ms；恢复快照数为 1 / 3 / 3，但首内容帧中来自磁盘快照的可见首页卡片数均为 0。按原定 600 / 800 ms 和可见缓存条件，结果为 ResultsFailed；不能用恢复了任意快照替代首页缓存命中。主要耗时集中在 MainWindow 构造与 Loaded 之前，继续定位。

最新真实账号采样 `artifacts/startup-validation/4168ddfb18004c2aa804425edc469914/summary.json`（UTC 04:33:47–04:33:51）三个新进程均正常退出且无需强制清理：窗口 Loaded 为 1009 / 929 / 785 ms，首页首内容为 1026 / 950 / 806 ms；每轮实际可见且仍来自持久快照的首页卡片均为 4 张，恢复快照数为 19。`run1.json` 的首帧几何记录确认页面与 viewport 为约 1276×798.67 DIP，四张已加载、有 Item 的卡片位于 Y=566 DIP，尺寸均约 300×214.67 DIP，可见裁剪宽度分别为 300 / 300 / 300 / 288 DIP，均无折叠、透明或未加载祖先。这轮已证实实际可见缓存卡片，但三个进程仍均未达到窗口 600 ms、缓存首页 800 ms，结果保持 ResultsFailed；新进程采样也没有清空操作系统文件缓存。

三个进程从 `WindowBasicServicesResolved` 到 `SystemTitleBarHidden` 的阶段间隔分别为 477 / 291 / 303 ms，覆盖首次取得 presenter 并调用 `SetBorderAndTitleBar(true, false)`；这是当前启动耗时的明确观测，尚未分离原生初始化内部成本。该调用用于保留窗口边框并隐藏系统标题栏，是已确认自绘标题栏行为的一部分，不能直接删除必要调用或略去其耗时来宣称达到启动目标。

诊断报告的并发读写也已独立复现：Windows 上 `File.Move(overwrite: true)` 遇到仍打开的共享读取句柄会返回 `80070005`，即使读者允许 FileShare.Delete。相同合成文件/读取句柄下 `File.Replace` 成功，UiLabSmoke 据此改用原子替换；报告读者仍需处理替换期间的短暂读取失败，避免诊断工具打断应用。

剩余范围：启动 600/800 ms 尚未稳定达标；5000 项滚动已采集 XAML 回调与进程私有内存，但尚无 GPU 呈现 60fps 的验证；P5 附录 A.10 真实键鼠输入、P6 讲述人实际朗读、P0/P6 原生硬件 50 次播放资源遗留均未由假界面回归覆盖。P8 当前主机安装、升级关闭实例和卸载保留数据已通过。P7 真实进程终止和文件替换自动验证见专门记录。2026-10-03 用户明确取消专用 Windows 虚拟机验收，已停止相关工作；删除被工具自动审批拦截，清理状态见 P8 打包记录。授权不等于验收已通过。用户暂缓应用图标的决定保持有效。

呈现工具的有界调查确认本机已有 WPR/WPA/xperf 10.0.19041.1，PATH、常见安装目录及本项目工具缓存未找到 PresentMon；当前令牌不是管理员，也不属于 Performance Log Users。[PresentMon 官方权限要求](https://github.com/GameTechDev/PresentMon#user-access-denied)说明这两种权限都不具备时无法启动实时 ETW 跟踪。未下载或安装新性能工具、未修改用户组、未启动图形 ETW 会话，GPU 呈现目标保持未验收。PresentMon 的 PID 参数过滤输出，不将它描述成系统底层严格只收集单个进程。

本轮检查了 `artifacts/ui-aot-verified-1/` 的九张离屏截图：首页、资料库、最近播放、搜索、详情、浅/深色设置、普通与全屏播放器。卡片与自适应列、标题栏和侧栏、圆形继续播放、设置行、深色控件及播放层未见布局溢出或控件遮挡；全屏截图不残留浏览侧栏与普通标题栏。没有把合成图片或离屏底色当作真实媒体和系统亚克力验收，也未把此范围扩展到引导/登录、高对比及多 DPI。

## 程序化关闭绕过清理的修正

125913 候选在 IPC 协议报告全部通过后，以 `0xC000027B` 异常退出。补齐 VideoLab/FakeLab 的永久 Surface.Dispose 和计时器退订后，Debug 连续五轮正常退出，但 131910 AOT 候选仍复现，故不能把永久释放单独认定为修复。两份失败报告保留在 `artifacts/p7-external-ipc-delivery-native-exit.json`、`artifacts/p7-external-ipc-dispose-native-exit.json`。

仅对本次合成 IPC 的自有进程启动 CDB 并核对 PID、父进程、路径、启动时间；没有读取真实账户。完整转储中的 stowed HRESULT 为 `8000FFFF`，只指向 CoreMessaging，不能单独定位应用。随后捕获托管 first-chance 异常，证明 UI 线程的 `VideoLab.RefreshDiagnostics+0x226` 经 WinRT ThrowExceptionForHR 失败；同一 AOT 二进制反汇编确认该位置是 `PositionSlider.Maximum` setter。安全栈及运行记录保留在 `artifacts/native-exit-helper/ipc-refresh-after-close-proof.txt` / `.json`；异常发生后的调试器结束不算正常退出通过。

根因是冒烟完成后直接调用 `Window.Close()`，却把 VideoLab 清理只挂在 `AppWindow.Closing`，定时器随后仍访问已销毁的 XAML。官方 [Window.Close 文档](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.window.close)只保证销毁与 Closed；[公开 WinUI CloseImpl 源](https://github.com/microsoft/microsoft-ui-xaml/blob/winui3/release/1.8.6/src/dxaml/xcp/dxaml/lib/DesktopWindowImpl.cpp#L389)直接走 Shutdown/DestroyWindow，不经过 WM_CLOSE。公开源码版本用来解释 API 行为，不冒充当前运行库的逐字反编译。

修正统一系统关闭和程序化完成入口：明确等待异步清理、停计时器并退订、释放画面与窗口钩子后，再排队调用最终 Close。主窗口自绘关闭按钮也有相同的直接 Close 问题，一并纳入修正与真实按钮回归；此前先 CloseForSmokeAsync 再 Close 的 UI 冒烟未覆盖该入口。新的 AOT、安装及按钮回归结果随后追加，不追认旧候选退出失败为通过。

修复后构建零警告零错误，提交前再次执行 345 项单元测试全部通过。`artifacts/close-regression-debug/summary.json` 与 `artifacts/close-regression-aot/summary.json` 均 Passed：各 10 次 IPC 完整进程正常退出，中间穿插 P3 真实 libmpv 播放、Fake 命令行和环境变量入口，全部正常退出。AOT 对应 `publish/releases/0.1.0/20261003-134251-380/app`，Mambo.exe SHA-256 为 `d846bda50d22b206de12507f1e85959655bfaa953b58b5e4fc4c4fa68c7ffaef`。没有关闭窗口诊断、吞掉计时器异常或改用无头流程避开原问题。

完整 UI 回归新增四个严格条件 `CaptionCloseCancelled`、`CaptionCloseReentryIgnored`、`CaptionCloseConfirmed`、`CaptionCloseCleanupCompleted`：从实际标题栏按钮发起，取消后播放继续，重复请求不重复确认，再从真实危险确认按钮退出；真实清理完成后才写报告，脚本继续要求进程退出码 0。该关窗会话独立于原 50 个释放样本，未改变其计数和阈值。

| 运行位置 | 报告 | 四项关闭 / UIA / 进度刷新 | 50 次释放首个零留存样本 |
|---|---|---|---|
| Debug | `artifacts/ui-debug-close-entry.json`，Run `0d176b3e9b36419f8a278e929169bf46` | 全通过 / 5 项 / 17 项 | 439 ms，4 个清理帧，player/surface 0/0 |
| AOT 发布目录 | `artifacts/ui-aot-close-entry.json`，Run `878fb055ece44f8abde542060c4bf6b5` | 全通过 / 5 项 / 17 项 | 419 ms，4 个清理帧，0/0 |
| 安装目录 | `artifacts/installer-validation/b86c1004e97e4039a3b5cf3ba337e479/ui-smoke.json`，Run `eae600a5ff95416194a2913132a707a4` | 全通过 / 5 项 / 17 项 | 425 ms，4 个清理帧，0/0 |

对应安装完整 Passed：436 个清单文件、升级正常退出（窗口就绪 703 ms）、卸载清除程序与注册项、安装前 356 个原文件及卸载前含 marker 的 357 个文件全部保留，未强制结束进程。此轮关闭入口缺陷已通过明确调用链修正与 Debug/AOT/安装回归收束；启动目标、GPU/物理输入/显示与 P0 原生资源遗留仍按前述实际证据保留。

134251 最终候选单实例回归 `artifacts/single-instance-validation/b19a513633c247c3947ff282097a1694/result.json` Passed。最后启动测量 `artifacts/startup-validation/e24cf88c865e4cafa666c4b14cc16cb0/summary.json` 三个新进程均正常退出、无强制清理，实际可见持久缓存卡片各 4 张、恢复快照各 19 份；窗口 Loaded 881/865/774 ms，缓存首页 972/919/827 ms，600/800 ms 目标均未通过，结果保留 ResultsFailed。SetBorderAndTitleBar 分项 420/430/391 ms，仍是主要原生成本。旧候选较快采样保留为历史，不能代替本轮验收；没有删除计划要求的窗口行为或改动测量起点来获得通过。
