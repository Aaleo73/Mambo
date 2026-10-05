# 窗口重新激活后内容区无法点击

2026-10-05 用户反馈：内容区出现 `Ctrl+F` 提示，随后右侧页面无法点击。

## 原因与修复

`ShellView.OnWindowActiveChanged` 在失活时调用 `Motion.SetActive(false)`，触发 `PageHost.SettleTransition`，由 `PageInputScope` 关闭当前页面的命中测试、Tab 和自动化访问。重新激活时只恢复动效标记便直接返回，没有恢复页面的输入状态。侧栏不经过这个页面输入作用域，因此表现为只有右侧失效。

外壳现在在设置新的激活状态后，两条分支都同步 `PageHost` 的呈现和输入状态。播放层覆盖期间仍受 `BrowseFace` 的非活动标记约束，不会误开启底下的页面。

全局快捷键注册在 `ShellView` 上时，WinUI 会自动为整个外壳生成快捷键提示。根控件设置 `KeyboardAcceleratorPlacementMode="Hidden"`，保留全部快捷键；搜索提示改为只在侧栏搜索区域显示，同时为搜索框提供自动化快捷键说明。参考：[Microsoft 键盘加速键文档](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-accelerators)。

## 回归证据

`UiLabActivation.cs` 加入现有假服务 UI 回归，使用正式 `WindowContext` 激活通知和真实 XAML 页面，重复检查页面命中测试、Tab、自动化属性恢复，并检查播放覆盖隔离和关闭后的恢复。取得测试窗口输入后，还会悬停卡片检查提示、实际点击进入详情并验证搜索快捷键。

- 修复前：`artifacts/window-input-before.json`，初始可交互和失活隔离均通过，第一次重新激活检查 `ReactivatedContentRestoresInput0` 失败，复现用户现象。
- Debug 构建零警告、零错误；384/384 单元测试通过。
- 修复后 Debug：`artifacts/window-input-after-debug-visible.json`，13 项专项检查通过，包含三次重新激活、命中测试、播放覆盖隔离、关闭播放恢复、实际卡片点击进入详情、内容区悬停不显示全局提示及 `/` 搜索快捷键。
- Native AOT 发布成功，独立输出目录 `publish/window-input-fix/`；`artifacts/window-input-after-aot.json` 的同样 13 项专项全部通过。
- 附加完整 Debug/AOT UI 回归仍未通过：后续动效步骤 `FilterReversal` 失去前台，均记录 `UiInputForegroundUnavailable`；五项原生 UIA、50 次播放关闭、Player/Surface 零留存及窗口清理均通过。此项与上述 13 项专项分别记录，不将整套 UI 回归记为通过。

该回归不代替真实显示效果、HDR 或其他尚未完成的人工阶段验收。
