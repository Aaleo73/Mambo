# P9：视觉基准与统一动效

日期：2026-10-03。范围：WinUI 前端的视觉基准与播放页交互；不改变 WinUI 3 + C#/.NET 10 + libmpv 技术路线，也不重新定义 `docs/PLAN.md` 的阶段验收。

## 决策

布局、配色、内容层级与交互识别度继续以原 Tauri 应用为参考；动效按用户随后确认的「克制、有辨识度」统一，不再逐项复刻旧参数。旧项目只作只读参照，不复制源码、图片或其他资产；字体继续使用本仓库 MiSans。假数据图片仍由本仓库生成。

`design/` 保留 P2 原型，便于追溯。其布局不再是当前验收标准；对应的公共 token 仍与 `Themes/Tokens.xaml` 同步。

### 被取代的 P2 结论

| 原结论 | 当前决策 |
|---|---|
| D2 控件尺寸 | 按原版恢复控件高度、留白、字体层级和圆角，不继续使用 P2 紧凑尺寸作为基准。 |
| D5 播放页 | 保留应用主题标题栏；黑色画面上留 12 DIP、上角半径 12；右侧为 208 DIP、跟随主题的选集面板。控制层恢复白色线性图标、渐变底栏、玻璃暂停按钮和黑色菜单。 |
| D6 转场 | 恢复绕中心 Y 轴的双面 3D 翻折，而不是将原版翻折替换为普通页面淡入。 |
| D7 设置页 | 恢复分区与分隔线布局，不使用 P2 的图标卡片分区。 |
| Q2 选集抽屉 | 改为非模态固定右栏：默认四列集号，可切换紧凑文字列表；不是覆盖视频的缩略图抽屉。 |

新增的深色主题、Windows 贴靠布局、真正全屏、重试、即将播放、缓冲区间和外部播放控制仍保留，不为外观回退而删除功能。

### 统一动效语言

浏览↔播放的中心 Y 轴翻折是唯一强动效，其余动作不缩放、不回弹、不逐块排演。本轮不重设计布局、配色或资产。

| 语义 | 时长 | 消费边界 |
|---|---:|---|
| Press | 80 ms | 卡片按下反馈。 |
| Exit | 120 ms | 详情前景、弹层、Toast、控制栏退出。 |
| Feedback | 160 ms | 卡片 hover、刷色、冷图就绪、弹层及控制栏进入。 |
| Content | 240 ms | 页面交接、详情前景、Hero 文字、筛选占位。 |
| Image | 400 ms | 唯一共享 Hero 背景的纯淡变。 |
| Mode | 480 ms | 仅浏览↔播放翻折。 |

自有透明度/位移只使用 EaseOut `(.2,.8,.2,1)`、EaseIn `(.4,0,1,1)`、Symmetric `(1/3,0,2/3,1)`；后者等价于 `H(t)=t²(3−2t)`。平台 BrushTransition 和按距离滚动是明确例外。7 秒轮播、Toast 驻留、单击识别和可见状态指示周期不是有限动效 token。

- **一个操作，一个主 owner。** PageHost 负责旧新页交接与输入隔离；详情前景整体进退，不保留封面克隆、来源卡片坐标或逐块错峰。列表没有逐卡揭示，冷图仅在父级没有转场时淡入；缓存、缺图、回收和迟到完成不得补播或串图。
- **逻辑目标不等于呈现对象。** 页面与背景各最多两层，第三目标只保留最新请求；同两面反向从当前值接续，不先落旧端点。库首屏等待保留旧页，空/错误可提交，不等待全部图片。
- **背景只有一个宿主。** BrowseTransitionCoordinator/HeroBackdropPresenter 维护 owner、generation 和有界返回上下文。同 ImageRef、像素足够的 Home→详情→Back 复用实际 surface，不重复取流/解码。不同图以不透明旧图托底，避免交叉变透明露黑。
- **Hero 整项提交。** 背景就绪或明确失败后，文字、圆点、可访问名称与点击目标一起交接；等待时旧项仍可点击。Logo 迟到只填匹配项，不重播前景。交接落稳或暂停恢复后重新计满 7 秒。
- **轻反馈统一。** 卡片 hover/键盘焦点 Y−2 DIP、按下 Y−1 DIP；普通按钮只刷色/描边。筛选用 240 ms 连续占位和裁剪，终点才提交布局；排序、确认卡片、Toast 使用 160/120 ms 与 4 DIP，不缩放。逻辑关闭立即禁输入，实际退出结束才卸载；Dialog 的焦点围栏与队列也保持到此时。
- **生命周期是收尾边界。** 每窗口一个原生动画设置订阅；离页、非活动、关闭动画和释放落最新有效终态，恢复仅影响后续动作。回收清除旧 hover/按下与图片动画，rail 释放捕获，Toast 业务删除/Action 不等待退场。

`design/` 只迁移公共 token 和现有消费者、清除退休效果；历史 HTML 播放容器仍使用 Content，不重建 WinUI 翻折或完整路由/弹层状态机。其浏览器兼容结果不能替代真实 WinUI 动态验收。

### 六项已选优化

1. 首页主视觉随可用宽度增长，受最小/最大高度约束。
2. 海报宽度随容器自适应，保持比例和确定的布局尺寸。
3. 横向内容轨道的箭头在到达首尾时淡出。
4. 选集面板允许收起，收起状态跨重启保存；全屏临时隐藏不修改该偏好。
5. 进度条悬停显示指针所在时间；悬停本身不跳转，拖动仍只在提交时跳转。
6. 音量、静音、倍速、快进/快退、音轨与字幕快捷键只显示约 1.2 秒的顶部提示，不因此唤出整个控制栏。

不做两项：不增加“已看完”角标，避免增加原版没有的卡片标记；不改为输入即搜索，保留用户选择的 Enter 提交节奏。

## 播放页实现与边界

- `PlayerOverlay` 的 `VideoViewport` 和 `EpisodePanel` 分列布局；多集队列才显示面板。鼠标在面板上移动不触发视频控制栏。列表行高 36 DIP，集号四列、行高 40 DIP、间距 6 DIP。
- 标题按实际视频视口居中。真正全屏隐藏外壳标题栏和选集栏，在视频顶部显示标题和关闭按钮；窗口模式通过外壳返回退出播放。
- `VideoSurface.SetViewportClip(radius, topOnly)` 是公开画面 API：窗口模式上角半径 12，全屏半径 0。只改变合成裁剪，不改变交换链的物理像素尺寸。
- 进度条保持缓冲区间；悬停与拖动预览互不覆盖。浮动提示在边缘钳制，离开后隐藏；键盘跳转仍走既有会话契约。
- 高对比度使用系统颜色，不使用硬编码品牌色或玻璃透明度替代系统可读性。
- 前端只消费 `Mambo.Core.Contracts` 与 `VideoSurface` 公开 API。未增加 NuGet 包。

### 设置兼容

`AppSettings.UseEpisodeGrid` 的新用户默认值为 `true`；新增 `EpisodePanelCollapsed`，默认 `false`。旧文档显式保存的 `UseEpisodeGrid=false` 必须保留，不能以“恢复默认”为由改回集号。

源生成 JSON 反序列化会给缺失的 init-only 布尔属性赋 `false`，因此 `SettingsStore.Load` 显式识别缺失的布局字段并迁移。面板状态写入沿用 `ISettingsService.UpdateAsync`，保留设备标识、音量及其他偏好；关闭播放会等待已提交的布局写入。缺失布局、显式列表、显式集号三种旧文档均有持久化回归测试。

### 翻折与生命周期

`Shell/PlayerFoldTransition.cs` 使用 Composition 标量动画和表达式矩阵：480 ms、smoothstep、透视距离 1400、遮罩峰值 0.18。标题栏不参与翻折；统一进度 p 在 .5 换面。模型时钟用于反向起点与一次性换面计时，真正 batch 完成才允许挂接；模型进度不是 GPU 读回，也没有逐帧托管矩阵计算。

转入完成前不挂接原生交换链。关闭先停放焦点，并在首个 await 前解绑、释放和移除 VideoSurface；短暂保留的退场面是无命令、订阅、timer 或原生表面的冻结 XAML，不截取视频，也不再使用整面黑色占位。关闭中到来新会话，沿当前 p 反向，旧 generation 不能清理或补挂新会话。尺寸/全屏变化、非活动、系统关闭动画和释放直接落最新目标，最终清空退场树、导航锁并恢复有效焦点。

外壳的导航锁覆盖整个转场，`Root` 仍为两行两列，`PlayerSlot` 仍是直接子元素。冒烟等待转场终态，而不是仅等待播放器对象创建或消失。

### 回归与报告读取契约

- 集号按钮显式拉伸到各自的网格单元，避免只给数字宽度绘制当前集背景。
- 悬停监听接收 Slider 已处理的指针事件；气泡按文字、内边距和边框测量，不把上一次定位用的 `Margin` 算进宽度。连续移动后的气泡仍须覆盖指针位置，有界面回归检查及真实鼠标实拍。
- `NativeOverlaySmoke` 的全屏检查必须等待交换链高度实际变化后再核对视口，不能在呈现器刚切换、布局尚未更新时用旧尺寸判为通过。
- `UiLabSmoke` 的运行中检查点改为原文件写入，不反复 `File.Replace`。仓库内的验收读取者均等进程退出后解析终报；运行中的文件可能尚未写完，不能用作完整验收结果。写入失败仍记录并抛出，不重试或忽略。这不改变真实用户设置、停止上报等持久化数据的原子写入约定。

初轮 Debug 与 AOT 导航检查曾因报告替换失败返回 `IOException / 80070497`，不是滚动偏移断言失败。独立复现中，允许 Write、未共享 Delete 的读取句柄会阻止替换/移动而不阻止原文件写入；未识别本机当次持句柄的具体进程，不把它归因于某个后台软件。

原生播放首轮也曾在起播阶段返回 `playback.failed`（`artifacts/native-overlay-validation/29a0e7c709ab4fccad2927706f6e5755/app-report.json`）；随后 Debug、AOT 均实际起播、关闭成功。该次原生失败原因未定位，不能据此宣称修复了原生引擎缺陷。临时诊断代码已移除。

## 浏览页补充调整

- 侧栏隐藏滚动条，保留媒体库长列表的纵向滚动。
- 设置页移除 1024 DIP 最大宽度，分区与分隔线铺满右侧可用宽度，保留页面内边距。
- 详情页剧集行采用与首页一致的 `ScrollView` + `ItemsRepeater`，保留虚拟化、目标集定位、换季复位与末尾加载；通过按住拖动或左右箭头横向移动，不再接收滚轮横移，纵向滚轮留给整页。
- 该轮 `dotnet build -p:Platform=x64 --no-restore` 通过，0 警告、0 错误；实际假数据窗口的最大化与最小尺寸设置页均保持 24 DIP 右内边距，侧栏未显示滚动条。


## 图标与许可

当前实现使用 **Feather Icons 4.29.2 / MIT**，不是计划初稿所列的 Lucide / ISC。线性路径在 `Themes/Icons.xaml` 中适配为 WinUI Geometry；来源为 [Feather 上游](https://github.com/feathericons/feather/tree/v4.29.2)。完整版权和 MIT 文本已加入 `LICENSES/Feather-4.29.2-LICENSE.txt`，并登记在 `THIRD_PARTY_NOTICES.md`、`LICENSES/sources.json` 中。现有项目规则自动将许可文件复制到构建和发布目录。

本次补齐图标归属不解除 `THIRD_PARTY_NOTICES.md` 已记录的 libmpv 对应源码分发限制。

## 历史验证记录（2026-10-03，统一动效切换前）

以下为本机实际执行结果；`artifacts/`、`publish/` 是本地验收产物，不入库。

这些结果对应旧 560 ms 翻折与黑色关闭面，保留用于追溯；不是本轮统一动效的验证。尤其下表的系统高对比/动画开关操作不能沿用为本轮人工确认。

| 检查 | 结果 |
|---|---|
| `dotnet build -p:Platform=x64` | 0 警告、0 错误。 |
| `dotnet test --no-build -p:Platform=x64` | 360/360 通过，未跳过；包含三种旧选集设置的迁移/持久化用例。 |
| `dotnet publish src/Mambo.App -p:Platform=x64 -p:PublishProfile=Aot -o publish/ui-parity-aot` | Native AOT 发布成功。 |
| `scripts/test-ui-lab.ps1`，Debug / AOT 各一轮 | `p9-debug-final.json`、`p9-aot-final.json` 均通过，各有九张离屏截图。两轮均为 144 项播放交互检查通过，50 次实际开关帧、50 次焦点恢复，播放器与视频表面留存均为 0；三种深滚动恢复偏差均为 0 DIP，五组 UIA 检查全部通过，报告写入错误为空。 |
| `scripts/test-native-overlay.ps1`，Debug / AOT 各一轮 | 本地真实 libmpv / composition 播放通过；窗口交换链为 2228×1200，全屏实际变为 2560×1599，两者均匹配视频视口。WASAPI、48 kHz 双声道、音量/静音、暂停/跳转/恢复、关闭解绑与停止上报均通过。 |
| 真实假数据窗口 | 浅色、深色的首页、最近播放、资料库、详情、搜索、设置及播放页已查看；实际鼠标悬停出现 27:38 气泡，气泡跟随指针且播放位置保持不变。列表/集号、收起面板、全屏、独立按键提示和翻折均已操作。 |
| 系统高对比度 / 关闭动画 | 分别实际启用高对比度、关闭客户端区域动画后运行窗口检查；高对比度各页使用系统颜色，关闭动画时直接到达播放终态。结束后重新读取系统选项，确认恢复原值。 |
| 图标许可发布内容 | AOT 输出包含 Feather 完整 MIT 文本；SHA-256 与 `LICENSES/sources.json` 一致。 |

最终证据：

- 界面报告：`artifacts/ui-parity/p9-debug-final.json`、`artifacts/ui-parity/p9-aot-final.json`。
- 原生 Debug：`artifacts/native-overlay-validation/2305047d3b1b439fb2cfd3a91981a6a3/`。
- 原生 AOT：`artifacts/native-overlay-validation/85c7eb17c40043d0ae359a83bbfa307b/`。
- 实拍：`artifacts/ui-parity/window-light-verified/`、`window-dark-verified/`、`window-contrast/`、`window-reduced-motion/`。

50 次生命周期样本使用假播放会话；真实 libmpv 的两轮验收不冒称为 50 次原生播放压力测试。一次性桌面驱动、文件共享复现脚本及临时原生诊断代码均已删除。

## 本轮统一动效验证（2026-10-04）

实现、最终 Debug/Native AOT 自动化门禁和两种尺寸的实际桌面动态采集已完成。Native AOT 候选位于 `publish/motion-after-aot/`。Windows 动画设置切换及真实媒体观感仍需用户验收；以下通过结论不覆盖这些人工项。

本轮实现与上面的历史候选分开验收。最终完整报告优先于分阶段和失败报告：

| 检查 | 实测与证据 |
|---|---|
| Debug 构建、单元测试 | 最近构建为 0 警告、0 错误；360/360 项测试通过，未跳过。 |
| 最终 Debug 完整 UI | `artifacts/motion-resume-debug.json`：整轮 Passed，Motion 90 项、导航、播放控制、50 次实际开关与焦点恢复通过；播放器/Surface 留存 0/0，播放呈现资源已释放，162.444 秒。 |
| 最终 Native AOT 完整 UI | `artifacts/motion-final-aot.json`：整轮 Passed，同样保留 Motion 90 项、导航/播放控制、50 次开关与焦点恢复、留存 0/0 和呈现资源释放检查，161.593 秒；未放宽 180 秒、5000 项或 50 次门禁。 |
| 分阶段完整 UI 门禁 | `artifacts/motion-browse-debug.json`、`motion-player-debug.json`、`motion-popups-fixed-debug.json` 通过；均保留 50 次实际打开/关闭帧及零播放器/Surface 留存。这些报告早于最终新增的 Motion 场景，不能代替最终完整报告。 |
| Motion 场景单独运行 | `artifacts/motion-focused-debug.json` 的 Motion 为 Passed，90 项检查通过，36.625 秒；覆盖 Hero、弹层、媒体就绪/回收、三种卡片和三种轨道。该诊断轮的整个 UiLabReport 不算通过；仅运行 Motion 的临时入口已移除。 |
| 实际桌面 Motion 观察 | `artifacts/motion-scenarios-desktop/` 的独立假数据进程记录了筛选/排序、Dialog/Toast、Hero 暂停与换项、媒体等待和导航交接；报告 Motion 90 项通过，实际系统动画开启，144 DPI。约 6 次采集/秒的 WebP 只能辅助观察，不证明 GPU 帧率，也不能代替完整翻折的动态验收。 |
| 原生播放 | 标题栏切换后的 Debug `artifacts/native-overlay-validation/c0e241752028499cabf9800953dcd49c/` 与最终 AOT `e736521eaa844173ba62d98f53074548/` 均通过：实际 composition、全屏、WASAPI、音量/静音及正常退出成功，无强制清理。AOT `artifacts/p3-playback-release.json` 的实际播放、暂停、倍速、跳转、下一集、重试、解绑和上报顺序检查通过。 |
| 历史 HTML 原型 | 实际浏览器操作、审阅开关和运行时 `prefers-reduced-motion` 切换通过，控制台/页面错误为空；具体操作与边界见 `design/README.md`。这不是 Windows 系统设置验收。 |
| 原候选动态基线 | `artifacts/motion-before-large/` 与 `motion-before-small/`：1500×860、1100×720 DIP，144 DPI、浅色、实际系统动画开启；Hero、浏览、详情、筛选和播放开关共七段实际桌面记录。已查看接触表；原翻折播放面中段/关闭起始存在整面黑色。采集时间不是 GPU 帧率；旧候选未支持的反向操作不计为通过。 |
| 最终候选动态记录 | `artifacts/motion-final-large/`、`motion-final-small/`：与原候选相同的两种尺寸、144 DPI、浅色和系统动画开启条件；各记录 Hero、资料库、详情、返回、筛选、播放打开/关闭/早期反向八段。已检查实际桌面帧：浏览整体交接，Hero 换项；翻折播放面保留静态 XAML 控件，不再是整面空黑。 |
| 翻折中后段反向 | `artifacts/motion-caption-reversal-large/player-reverse-late.webp` 与 `motion-late-reversal-small/player-reverse-late.webp`：650 ms 后请求返回，两种尺寸均记录到播放面展开后反向回浏览；采集帧中播放控制条持续可见。完整 UI 门禁另覆盖 p≈.15/.49/.51/.85 的中断、冻结输入及资源释放。 |
| 物理窗口状态 | 两种尺寸均实际最小化、失活 7.3 秒并恢复前台，恢复前后 Hero 标识一致。大尺寸实际标题栏拖动、双击最大化/还原、最大化按钮往返和原边界恢复通过；稳定布局截图位于 `artifacts/motion-caption-reversal-large/`。命中测试返回 Caption=2、Maximize=9；未将这些结果当作 Snap 弹出层视觉验收。 |

本轮曾发生不同的原生释放错误；以下保留原因证据与切换过程，最终 Debug/AOT 完整门禁已通过：

- `artifacts/motion-failed-debug.json` 对应全屏 Presenter 切换中的 `CTitleBar → CDevice → CSurfaceFactory` 释放栈。早期仅移除全屏前的 `SetTitleBar(null)`，但保留高层标题栏注册，并未解决后续错误；`null` 本身请求默认标题栏，不代表完全退出该路径。
- `artifacts/motion-release-failed-debug.json` 对应进入 50 次播放释放检查前的 `MenuFlyout → CustomWriterRuntimeContext → CVisualStateGroupCollection` 原生释放栈，不是同一个全屏栈。静态所有权检查未定位到足以证明原因的共享释放；没有关闭 GC、缩减生命周期次数或放宽通过条件来绕过它。
- 用户解锁后，`artifacts/motion-unlocked-failed-debug.json` 仍在全屏切换的 `Microsoft.UI.Input → CTitleBar` 释放链发生 `FAST_FAIL_GUARD_ICALL_CHECK_FAILURE`；锁屏并非充分解释，保留自绘标题栏绑定也未解决该错误。
- `artifacts/motion-provider-last-release.txt` 捕获了 SDK 标题栏先释放 provider A，再析构 provider B，B 又释放已失效 A 的顺序。独立 WinUI 最小程序 `artifacts/caption-repro/hybrid-38bb1344fdfe4525845b3f8be9bbe74d/` 在第一次全屏切换中复现同一释放栈；该程序不含 Mambo 的动效、服务、原生窗口子类化或 COM 辅助代码，UIA 遍历由独立系统框架客户端完成，运行库 SHA-256 与应用相同。这定位了高层标题栏路径中的生命周期错误，但没有证明更早的引用失衡点。
- 微软对该高层标题栏路径明确说明：[隐藏系统窗口按钮不受支持，应使用 InputNonClientPointerSource 实现完整自绘标题栏](https://github.com/microsoft/microsoft-ui-xaml/issues/8705#issuecomment-1960689442)。本轮保留既有外观、边框、可访问 XAML 按钮和最大化 hook，移除 `ExtendsContentIntoTitleBar` / `SetTitleBar` 注册；Caption、Passthrough、Maximize 三类区域统一由 WindowChrome 计算，全屏清除、返回重建、释放时清理。不关闭 UIA、GC 或系统堆保护。
- 首次切换后 `artifacts/motion-after-debug.json` 完整运行至结束：导航、播放控制、p≈.15/.49/.51/.85 反向、静态可读开关面、空闲关闭及 50 次实际开关/焦点恢复通过，播放器与 Surface 留存均为 0，未再出现原生异常；但 Motion 在 Hero 覆盖后恢复自动轮播阶段超时，因此该轮仍记为失败。随后只增加失败状态诊断，没有延长等待或改动生产轮播逻辑；最终 Debug/AOT 两轮完整通过。这不证明已定位该次超时根因。
- 最近 Windows 会话查询为已解锁；实际桌面采集仍会在本轮窗口失去前台或被遮挡时拒绝继续，不向其他程序发送输入。不修改全局锁屏、前台锁或动画设置。

性能对比见 `artifacts/motion-performance-comparison.json`。三轮均为 5000 项、144 DPI，最大实现卡片数均为 57/58/58；标题栏切换使视口高度相差一个物理像素（664.666687 → 665.333313 DIP，宽度均为 1276 DIP）。实测如下：

| 候选 | Peak Private Bytes（MiB） | 图片服务调用 / 解码缓存命中 | Top / Middle / Bottom P95 回调间隔（ms） |
|---|---:|---:|---|
| 改前 Debug | 252.00 | 327 / 168 | 11.1084 / 10.3128 / 10.0600 |
| 最终 Debug | 254.62 | 332 / 177 | 11.4964 / 10.6912 / 10.0696 |
| 最终 AOT | 256.81 | 330 / 179 | 11.3324 / 10.6468 / 10.3840 |

这些是单次 XAML Rendering 回调与进程 Private Bytes 采样，不是 GPU 呈现帧率，也不是统计基准；Debug 与 AOT 不是同一构建配置。不声称更快或更省内存。桌面 WebP/接触表记录真实合成像素，但采样有间隔，不能排除短于采样间隔的视觉问题。

本轮一次性桌面采集驱动、聚焦诊断入口脚本、调试器命令文件及独立标题栏复现项目/生成目录已清除；保留验收 JSON、动态记录和原生错误证据。

自动化与桌面动态对比已完成；剩余用户参与项如下。不沿用旧候选的人工通过结论，不据此勾选 P9 或此前尚未完成的关卡。

## 验收边界

离屏截图只用于检查布局、字体和间距，不能证明亚克力、原生视频或实际桌面合成效果。真实窗口检查使用独立假数据进程，原生播放使用本地样片和隔离诊断服务；都不需要记录真实服务器、令牌或密码。

【需用户】在 Windows 设置中首次关闭、运行中关闭及重新开启动画，观察实际窗口后恢复原设置；再使用自己的 Emby 账号确认真实海报、封面与视频内容的最终观感。布局可与原版并排参考，退休的残影、错峰和缩放不再是目标。假数据、本地样片和自动化均不能代替这些人工验收，不据此勾选 `docs/PLAN.md` 尚未完成的人工或硬件关卡。

标题栏切换后的 Windows Snap 布局弹出层也保留人工视觉确认；自动化仅证明命中区域、物理拖动及最大化/还原行为。没有修改全局动画、锁屏或前台策略。
