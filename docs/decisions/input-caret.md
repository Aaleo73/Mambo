# 输入框插入光标使用柔和灰色

日期：2026-10-07。范围：TextBox / PasswordBox 中闪烁的竖线，不是选中文字的高亮或鼠标指针。

## 决定

- 浅色主题使用 `#60656F`，深色主题使用 `#A4A9B3`，由 `Themes/Tokens.xaml` 的 `InputCaretBrush` 管理；高对比度映射到 `SystemColorWindowTextColor`。
- `Themes/InputCaret.cs` 只替换原生光标形状的 `Fill` 和合成模式。光标位置、宽度、RTL 形状、闪烁、选区与输入法仍由 WinUI 管理，不增加自绘光标、计时器或键盘拦截。
- 普通与 Soft 样式的文本框、密码框统一覆盖；文字、占位文字、选中高亮和输入框边框保持原样。

## 原因与实现边界

WinUI 没有公开的 `CaretBrush`。当前 SDK 的原生 `TextBoxView` 创建白色光标形状，并设置内部的 `DestInvert` 合成模式（值为 3）。修改 `SelectionHighlightColor` 不能解决这个竖线问题。

依据：[WinUI TextBoxView](https://github.com/microsoft/microsoft-ui-xaml/blob/main/dxaml/xcp/core/native/text/Controls/TextBoxView.cpp) 的 `EnsureCaretElement` / `CreateCaretShape`，以及 [ElementCompositeMode 定义](https://github.com/microsoft/microsoft-ui-xaml/blob/main/dxaml/xcp/tools/XCPTypesAutoGen/XamlOM/Model/Microsoft.UI.Xaml.Media.cs)。运行验证基于仓库锁定的 Windows App SDK 2.5.1 / WinUI 2.3.9。

附加属性通过公开的 VisualTreeHelper 和 WinRT 类型查询找到模板 `ContentElement` 的内容视图；只在它具有唯一 Rectangle / Path 子元素、不可命中、左上对齐、使用原生反色合成模式时接管。接管后使用 `SourceOver`，避免灰色再次被反色。SDK 或模板结构不匹配时保留原生光标，不影响输入。

这是对 WinUI 内部视觉树结构的有限适配，不是稳定的公共光标主题 API。升级 SDK 后必须重新运行专项检查。AOT 下原生元素可能仅投影为 DependencyObject，需要显式 `As<T>()` 查询，不能依赖 CLR 的 `is Rectangle` 推断原生类型。

只在控件加载且有焦点时订阅 LayoutUpdated，以覆盖延迟创建和光标重建；失焦、卸载或移除附加属性时停止观察并恢复原生颜色与合成模式。焦点由 FocusState 属性同步通知驱动，避免延迟到达的 GotFocus / LostFocus 覆盖最新状态。不修改 Opacity，以保留系统闪烁动画。

## 验证与未验收

- `dotnet build -p:Platform=x64`：通过，0 警告、0 错误。
- `dotnet test`：586 成功、23 个按现有环境开关跳过、0 失败。
- Debug 与 Native AOT 的 `scripts/test-input-caret.ps1`：各 23 项通过，覆盖 Soft / 默认样式、文本框 / 密码框、聚焦时深浅主题切换、原生宽度保留、文字与选区画刷不变、失焦恢复、左右书写方向、密码显示、关闭定制、卸载与重新加载。
- 专项检查使用假数据和编程焦点，只验证实际控件树与属性，不发送物理键鼠，也不证明实际桌面合成观感。未重新执行完整界面与播放回归。
- 【需用户】实际灰度与闪烁观感、中文输入法候选与组合输入、DPI / 多屏、Windows 高对比度和文本光标辅助设置。RTL 文本方向已检查，但 RTL 小钩的实际创建还依赖系统键盘布局，没有以 FlowDirection 切换冒充该项验收。2026-10-07 用户确认人工验收全部通过（整体结论，没有逐项记录）。
