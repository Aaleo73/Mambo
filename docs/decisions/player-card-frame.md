# 播放页：圆角黑框与标题栏选集开关

日期：2026-10-05，2026-10-07 修订。范围：播放区域的画面边界、窗口留白和选集栏入口。

## 决策

1. **圆角只切自然黑边，影片保持完整矩形，选集展开与收起共用圆角。** `VideoViewport` 负责画黑底，窗口模式最大圆角为 12 DIP，全屏为 0。窗口四边保留 12 DIP 留白；选集栏展开时，右边间隔由选集栏自己的内边距提供。按影片显示比例分别计算选集展开、收起时的黑边，采用两者都能容纳的较小圆角。两种状态黑边都充足时为 12 DIP；任一种黑边较薄时一起减小；任一种恰好铺满时一起保持方角。视频矩形只在当前有黑边的方向让出共同圆角，另一方向铺满，影片原有的大小和位置不变。不另外增加边框或缩小影片。
2. **选集开关放在标题栏。** 位于最小化按钮左侧，展开和收起时位置不变；提示在「收起选集」「展开选集」间切换。只在面向播放页且有多集时显示，底栏不重复提供入口。
3. **收起、展开带淡变。** 选集栏用 `PopupTransition` 只淡变（160 / 120 ms，不位移）。收起时先淡出再折叠，画面随后变宽；展开时画面先让位，选集栏淡入。全屏、最大化等呈现切换直接落终态。

这取代 `P9-visual-parity.md` 的窗口上角裁剪方案及 `VideoSurface.SetViewportClip`，不改变其余播放页交互。

## 原因

- `SwapChainPanel` 的交换链属于外部内容：WinUI 将它交给系统，在界面绘制层下方合成。界面层裁剪改变的是透出视频的区域，不能保证下面的矩形交换链也被裁成圆角。窗口亚克力同样是外部内容。见 [Visual layer overview：External content](https://learn.microsoft.com/en-us/windows/uwp/composition/visual-layer#external-content)。
- 父级圆角在不透明背景上看起来有效，不能证明视频已被裁剪；启用系统背景后，原本被界面像素遮住的方角会露出。[microsoft-ui-xaml #9516](https://github.com/microsoft/microsoft-ui-xaml/issues/9516) 记录了 WebView2 这一类外部内容的相同现象。
- 纯色角块不能复现窗口的薄亚克力与白纱，背景变化时会出现色差。当前正式 SDK 的 `SystemBackdropElement` 只提供外凸圆角，不能做圆角外侧的内凹遮盖；自定形状的 `ContentExternalBackdropLink` 仍属 Experimental，本实现不依赖它。
- 本项目使用锁定哈希的官方 mpv 包，不自编译。mpv 0.41 的 composition 路径跳过透明设置，创建交换链时也未设置透明的 `AlphaMode`；WinUI 文档明确说明 `SwapChainPanel` 不支持透明。因此不能让视频四角透明后露出亚克力。见 [mpv context.c](https://github.com/mpv-player/mpv/blob/v0.41.0/video/out/d3d11/context.c#L491-L521)、[d3d11_helpers.c](https://github.com/mpv-player/mpv/blob/v0.41.0/video/out/gpu/d3d11_helpers.c#L602-L635)、[SwapChainPanel 文档](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.swapchainpanel#remarks)。
- 不改为独立的系统合成层承载视频：那需要重写播放层。当前方案只依赖矩形布局，不依赖外部内容圆角、透明交换链或硬件叠加层的偶然表现。
- 只按当前视口计算会使选集展开、收起后的圆角不同。两种布局共用较小值，使切换后的轮廓保持一致，同时继续容纳影片原有尺寸；代价是当前黑边充足时，也可能因另一布局接近铺满而采用较小圆角。

## 实现与边界

- `VideoFrameFit.Compute` 是不依赖 WinUI 的纯计算。输入、输出都是物理整像素：先按显示比例求自然黑边，每侧预留半个像素以容纳 mpv 取整，每种布局允许的圆角为 `min(maxRadiusPx, max(0, floor(barPx - 0.5)))`。`ComputeSharedRadius` 取两种布局的较小值，再按当前布局求视频矩形。没有选集时只按当前视口计算；最大圆角为 0 或比例无效时使用完整矩形和 0 圆角。
- `VideoSurface.SetMaxCornerRadius` 设置最大值，`SetCornerRadiusReferenceWidths` 接收同一高度下两种布局的 DIP 宽度，`EffectiveCornerRadius` / `EffectiveCornerRadiusChanged` 提供当前 DIP 圆角。`PlayerOverlay` 从不随选集开关改变的根布局宽度推算两种视口，避免混用切换前后的尺寸。宿主尺寸、参考宽度、DPI、会话或比例变化都会重算，平移与缩放使用同一个 `CompositeTransform`，`PixelSize` 直接来自计算结果。保留「先拉伸、缓冲区到位再提交」及拖动期间推迟 mpv 改尺寸的流程。视频实验窗口默认没有圆角。
- `LibMpvEngine` 从已观察的 `video-params` 中读取 `dw / dh / rotate`；90°、270° 对调宽高，队列溢出后重读。`PlaybackVideoBridge` 把最新有效比例切回界面线程，切集和重试期间沿用上次比例，首次默认 16:9，不改 Core 契约。参数暂时不可用时保留旧值；若比例尚未取得或不准确，影片可能略小，但矩形仍在圆角以内，不露方角。
- `PlayerOverlay` 用实际圆角绘制黑底，并对整个 `VideoViewport` 设置合成裁剪，只约束界面绘制的控制条渐变、弹幕和状态层。`VideoHost` 不再铺一层方形黑底。演示画面由界面层绘制，可以铺满并直接使用最大圆角；它不能代替真实交换链的验收。
- 冻结退场前取消圆角事件订阅，移除原生画面后静态退场面使用最大圆角；裁剪资源在最终 `Dispose` 释放。
- 任一选集布局恰好铺满时，两种布局均保持方角；全屏始终铺满、方角。上下有黑边时，位于黑边内的字幕会整体上移最多约 12 DIP，具体位置仍由 mpv 字幕排版决定。
- 选集栏 `Visibility` 回调统一更新窗口留白。冻结前先让选集动画落终态再注销回调；标题居中时让出「选集栏宽度减去左侧留白」。标题栏按钮登记为 Passthrough，不抢播放器焦点，窗口模式另保留 `E` 快捷键。

## 验证与未验收项

- 几何测试覆盖宽银幕、窄画面、恰好铺满、薄黑边以及 1 / 1.25 / 1.5 / 1.75 / 2 倍缩放，并扫描比例和尺寸，检查四角包含关系及影片自然尺寸。共同圆角覆盖任一布局黑边较薄、任一布局恰好铺满及两者均充足。比例解析测试覆盖显示尺寸、旋转、缺失和无效参数。
- `test-native-overlay.ps1 -Screenshot` 用本地真实片源和正式播放层检查交换链尺寸、变换归一、四角包含关系、近似铺满时影片尺寸、全屏及恢复；黑边充足和近似铺满两种窗口尺寸都检查展开、收起后的圆角一致。它只通过 `PrintWindow` 截诊断窗口自身，留存选集展开、控制条、选集收起、近似铺满的展开与收起，以及全屏画面。几何断言不能替代截图检查。
- 最小实现的真实画面截图中，黑框四角为圆角，影片宽度与改动前基线一致。完整自动验证结果统一登记在 `docs/STATUS.md`，不在此记录逐轮流水。
- **【需用户】** 真实 Emby 片源下四角观感；覆盖选集展开 / 收起、近似铺满、黑边中的字幕，以及不同 DPI、HDR 显示器和背景。自动化与本机截图不代表这些环境已验收。
