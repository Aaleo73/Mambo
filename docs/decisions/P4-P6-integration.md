# P4–P6 界面集成与验证

日期：2026-10-03。范围：外壳与浏览页面、播放页界面、打磨与加固。实现和自动回归完成，启动性能、GPU 呈现帧率和若干人工验收还没有通过，见 `docs/STATUS.md`。

## 实现

- **详情页**：继续播放、播放进度、换季与选集、人物、缺图占位、返回状态。
- **播放层**：内置画面、控制层显隐、进度插值和拖动、轨道 / 倍速 / 音量、选集、错误重试、全屏及 Esc 层级、阻止屏保和焦点恢复。
- **设置**：主题持久化、缓存统计与清理、日志入口、外部 MPV 显式批准与状态、实际版本和许可入口。
- **加固**：账号切换时清理图片解码代际，旧加载不得回填；损坏图片保持占位；隐藏的首页停止轮播；被淘汰后重建的页面先补齐分页再恢复深滚动。
- **控件释放**：播放层与 VideoSurface 的计时器解除 Tick 订阅、停止 compiled bindings、关闭菜单、释放光标与 Composition 对象。`VideoSurface.Detach` 保留复用能力，`Dispose` 表示永久销毁；关闭应用先停止后端，再释放外壳与服务。
- **背景兜底**：Acrylic / Mica 都不可用时显示浅 / 深色实色底，高对比度使用系统 WindowColor；材质真正连接成功后隐藏兜底。

## 验证方法

`scripts/test-ui-lab.ps1` 启动正常的 WinUI 外壳并强制使用假服务，不读取真实凭据。几项口径要注意：

- **截图**：离屏 `RenderTargetBitmap` 不含系统 backdrop，截图临时使用同主题底色，不能当作系统亚克力的实拍验收。
- **启动计时**：`StartupTimeline` 从进程创建时刻算起，记录外壳的真实 Loaded 事件和首页内容的下一次 XAML Rendering。缓存首屏要求实际可见的首页卡片仍来自磁盘快照；只恢复了库列表、hero 或来自网络的新卡片都不算。新进程采样没有清空系统文件缓存，不等同于冷机启动。
- **滚动性能**：`UiPerformanceProbe` 完整加载 5000 项后采样三段真实滚动，只用 RenderingTime 计算 UI 回调间隔。这不是 GPU 呈现帧率。
- **对象留存**：50 次假播放关闭后，用弱引用检查 `PlayerOverlay` 和 `VideoSurface` 是否全部释放，要求零留存。每次 GC 后等待两个真实的 Rendering 边界，窗口为 10 秒；跨过截止时间才归零不算通过。生产代码没有为此增加强制 GC 或持续绘制。
- **停止播放后的刷新**：`Debug/PlaybackRefreshSmoke.cs` 用独立的生产 `LibraryService`、`QueryCache` 和三个真实页面，账号为随机合成值，HTTP 只响应白名单合成数据。由播放器实际的关闭按钮触发关闭，之后必须由生产的 `PlaybackStopped` 处理器自行失效并刷新查询；测试不调用 Refresh、不手动发消息、不直接改 ViewModel。
- **关闭按钮**：从实际标题栏按钮发起，取消后播放继续，重复请求不重复确认，再从确认按钮退出；清理完成后才写报告，并要求进程退出码为 0。

## 定位到的问题与修复

- **退出崩溃**：调用栈为 `HeroCarousel.OnUnloaded → WindowContrastObserver.Dispose → ThemeSettings.remove_Changed → … → terminate`，发生在 UI 主线程。修复：原生订阅集中到窗口存活期，页面只订退托管通知，窗口关闭前在 UI 线程解除唯一的原生订阅。
- **深滚动崩溃**（`AG_E_LAYOUT_CYCLE` / `0x88000FA8`）：同步布局过程中调用了 ChangeView、分页和 `SearchPage.UpdateLayout`。修复：把这些调用移出同步布局，并在 `LandscapeCard.MeasureOverride` 中设置自适应高度，避免冷创建或回收的第零项测量值与实际排列高度不一致。
- **AOT 下资源类型转换失败**：首页和资料库构造时抛 `InvalidCastException`。同一资源在 Debug 可直接转换，在 AOT 下返回的对象类型是 `DependencyObject`，`is Style` 为假。这是原生资源的托管投影问题，不是漏发布资源。修复：`Themes/XamlResources.cs` 对 Style、DataTemplate 等使用具体类型的 `WinRT.CastExtensions.As<T>`（[源码](https://github.com/microsoft/CsWinRT/blob/master/src/WinRT.Runtime/CastExtensions.cs)），不增加反射。AOT 下的 `RenderingEventArgs` 同样要用具体的 WinRT 投影才能取到时间戳。
- **关闭播放层后焦点丢失**：`ShellView.RemovePlayer` 原先在浏览页和侧栏仍禁用时就释放播放器，之后才尝试恢复焦点，而且忽略 `Focus` 的失败结果。修复：先恢复浏览区的可见性与可用性，确认原目标已加载、可聚焦、属于当前 Shell 且不是旧播放器的后代，再恢复焦点；失败时转到导航控件。
- **播放菜单计数竞态**：Hide 的延迟 Closed 回调可能扣减后来打开的新菜单的计数。修复：按具体菜单实例增删，关闭时解除对应事件。
- **程序化关闭绕过清理**：冒烟完成后直接调用 `Window.Close()`，而清理只挂在 `AppWindow.Closing` 上，定时器随后访问已销毁的 XAML，进程以 `0xC000027B` 退出。主窗口的自绘关闭按钮有同样的问题。根因、定位方法和修复见 [P8 打包交付](P8-packaging.md) 的「关闭顺序故障与修复」。
- **诊断报告的并发读写**：Windows 上 `File.Move(overwrite: true)` 遇到仍打开的共享读取句柄会返回 `80070005`，即使读者允许 FileShare.Delete；`File.Replace` 成功。报告写入因此改用原子替换。

## 播放层对象留存的调查

50 次关闭后，`PlayerOverlay` 和 `VideoSurface` 曾不稳定地留存 2–6 个，个别 AOT 轮次 50 个全部留存。结论和限度如下：

- 托管堆转储里，这些对象的 `gcroot` 为 0 个根。但 SOS 对 `ComWrappers` 的条件根（COM 计数、ReferenceTracker 计数、pegging 状态）覆盖不全，零根不能证明没有原生条件根。
- 同一进程里，GC 之后继续经过 XAML 绘制周期，对象就能释放。WinUI 的 [UIAffinityReleaseQueue](https://github.com/microsoft/microsoft-ui-xaml/blob/main/dxaml/xcp/dxaml/lib/UIAffinityReleaseQueue.cpp) 也是通过 UI tick 分批处理释放的。所以检查协议改成 GC 后等待真实的 Rendering 边界，而不是用普通 Delay。
- 跳过进程内原生 UIA 检查的隔离轮次留存为 0。这只是线索，没有证明 UIA 缓存就是持有者。原生 UIA 的 TextBox 查询后来移到了受限的同程序子进程里，无障碍检查没有因此跳过。
- 采用新协议后，Debug、AOT 发布目录和安装目录的多轮检查都在 0.4–0.8 秒内归零。

这只证明假播放场景下托管控件能够释放，不覆盖 P0 的硬件 DXGI 资源遗留，也没有定位到具体的原生引用根。

另有一次运行在 Finalizer 的 `ComWrappers.NativeObjectWrapper.Finalize → ScrollViewer / GettingFocusEventArgs` 释放路径上检测到 `0xC0000374` 堆损坏，写坏的位置没有查明，之后没有复现，不宣称已解决。

## 性能结果

- **启动**：最近一次测量窗口 Loaded 为 881 / 865 / 774 ms，可见缓存首页为 972 / 919 / 827 ms，目标分别是 600 ms 和 800 ms，都没有达到。各轮都有 4 张实际可见的缓存卡片。更早的候选测到过 564 / 651 / 633 ms 和 620 / 728 / 699 ms，轮次之间有波动，不用较快的那次代替最终测量。
- **主要成本**：首次取得 presenter 并调用 `SetBorderAndTitleBar(true, false)` 占 250–430 ms。这个调用用于保留窗口边框并隐藏系统标题栏，是自绘标题栏行为的一部分，不能为了达标删掉它，也不能用更早的 Activated 代替 Loaded。
- **滚动**：5000 项全量加载，最多 72 个已实现的卡片，进程私有内存峰值约 275 MiB，三段 Rendering 回调间隔的 P95 为 10.35–11.66 ms。
- **GPU 呈现帧率没有验证**：本机没有 PresentMon，当前账户既不是管理员也不属于 Performance Log Users，按 [PresentMon 的权限要求](https://github.com/GameTechDev/PresentMon#user-access-denied)无法启动实时 ETW 跟踪。没有为此安装工具或修改用户组。

## 未覆盖的范围

- 启动 600 / 800 ms 目标和 GPU 呈现 60 fps。
- `docs/SPEC.md` A.10 的真实键鼠输入、讲述人实际朗读。
- 引导 / 登录页、高对比度和多 DPI 的截图核对；真实媒体和系统亚克力的观感。
- P0 的原生硬件资源遗留，不由假界面回归覆盖。
