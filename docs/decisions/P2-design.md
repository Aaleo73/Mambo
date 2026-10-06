# P2 设计稿定稿

日期：2026-10-02。状态：**用户已确认定稿**（关卡 ②）。

- `design/`：可点击的 HTML 原型（`index.html`、`prototype.css`、`prototype.js`）与 `tokens.css`，是 P4、P5 的视觉依据。查看方式和实现提示见 `design/README.md`。
- `src/Mambo.App/Themes/Tokens.xaml`：由 `tokens.css` 转换，已合并进 `App.xaml`。之后改 token 时，两边同步修改。

## 结论

| 编号 | 事项 | 结论 |
|---|---|---|
| D1 | 标题栏 | Windows 11 窗口按钮 46×40；最大化按钮登记为非客户区，悬停出现贴靠布局 |
| D2 | 控件风格 | 不用 Fluent 默认样式，在原版基础上优化：中性柔和底、1px 细边框、12 圆角、13px 半粗体文字；高度 36 / 42 / 30；补齐悬停、按下、焦点、禁用状态 |
| D3 | 内容层 | 不加。内容区与外壳同一层亚克力，只有左、上 1px 描边和距标题栏 12px 的间距 |
| D4 | 首页 hero | 不加按钮，整块点击进入详情 |
| D5 | 播放页 | 全屏优先：隐藏侧栏、画面铺满，保留深色标题栏；选集抽屉；片尾"即将播放"卡；双击画面切换全屏 |
| D6 | 转场 | 浏览 ↔ 播放用缩放加淡入；卡片 → 详情用 ConnectedAnimation |
| D7 | 设置页 | 第 1 版的分组（服务器 / 播放 / 外观 / 关于），每行图标 + 标题、控件在右；样式按原项目 |
| D8 | 详情页 | 72px 圆形播放按钮旁加"继续播放 · 剩余时间"；海报圆角 12 |
| D9 | 深色模式 | v1 提供；设置 → 外观可选跟随系统 / 浅色 / 深色 |
| D10 | 应用图标 | 暂缓，P2 不交付图标（2026-10-06 已另行设计，见 `app-icon.md`） |
| Q1 | 第 2 版控件 | 认可 |
| Q2 | 选集抽屉 | "列表"和"集号"两种都留，在抽屉右上角切换；默认列表，记住上次选择 |
| Q3 | 主题默认值 | 跟随系统 |
| R1 | 横版网格 | 列数 = floor((可用宽 + 16) / (240 + 16))，卡片拉伸填满整行，图片保持 16:9；首页卡片行仍是固定 300×169、横向滚动 |
| R2 | 界面文案 | 不加解释性说明文字，只留标题、数值、状态和必要的错误信息；对话框正文一句 |

## 与 PLAN 的差异

PLAN 由用户维护，这里只记录差异，不改 PLAN 正文。

- §9 拟调整的"控件统一为 Fluent 风格"不采用（D2）。
- §9 要求的新应用图标暂缓（D10）。P4 任务中的"应用图标"要等用户另行确认。
- §9 的"评估深色模式"改为 v1 实现（D9）。
- 附录 A 的提示类文案不显示（R2），包括：登录表单的 HTTPS 提示、筛选规则说明、资料库加载计数与"已经到底了"、MPV 路径的长句状态。地址校验、播放失败等错误文案照常保留。
- A.8：播放方式的选项叫"内置播放器 / 外部窗口"；MPV 路径状态只在校验时和校验后显示，文案为"正在验证… / 已验证 / 未找到 mpv.exe"。
- A.10：选集由 208px 的 4 列方块改为抽屉（Q2）；20 秒加载提示缩短为"片源响应较慢"。
- 附录 B：弹出面圆角由 18 改为 16。

## Tokens.xaml

**命名与分组**

| 内容 | XAML 形式 |
|---|---|
| 浅色 / 深色颜色 | `ThemeDictionaries` 的 `Light` 与 `Default` 中的 `SolidColorBrush`，键名 `…Brush`；用 `{ThemeResource}` 引用 |
| 弹出面底色 | 应用内 `AcrylicBrush`（`PopupBackgroundBrush`），回退为不透明底色；投影颜色 `PopupShadowColor` |
| 播放层颜色 | 与主题无关的 `Player…Brush`，含顶部、底部渐变 |
| 字体 | `MiSans{Regular,Medium,Semibold,Bold}FontFamily` 与同名 `…FontWeight` 成对使用；只设其中一个会让 DirectWrite 模拟粗细 |
| 字号 | `{Caption,BodySmall,Body,BodyLarge,Subtitle,Title,TitleLarge}FontSize` / `…LineHeight`；眉标字距 `EyebrowCharacterSpacing` |
| 圆角与尺寸 | `CornerRadius{XS,S,M,L,XL,Pill}`、控件 / 外壳 / 卡片尺寸、`ContentCornerRadius`、`PagePadding` 等 |
| 动效 | `x:String`，格式与 WinUI 的 `ControlFastAnimationDuration`、`ControlFastOutSlowInKeySpline` 相同：XAML 里作 `KeyTime` / `KeySpline` 的 `StaticResource`，Composition 动画在代码里解析 |

- 深色字典的键用 `Default`，与 WinUI 的 generic.xaml 一致。高对比度模式会回退到它，不会因缺键崩溃；系统高对比度颜色的映射放到 P6。
- 键名已与 WinUI generic.xaml（Windows App SDK 2.5.1 使用的 WinUI 2.3.9）逐一比对，没有冲突。按钮、输入框的 12 圆角叫 `SoftControlCornerRadius`，因为 `ControlCornerRadius` 是 WinUI 自带的键（值为 4），不去覆盖它。
- 没有转换的：窗口亚克力的 tint、窗口描边与阴影（由系统的 `DesktopAcrylicController` 和窗口边框负责，原型里只是示意）；间距刻度（按 4px 栅格直接写数值）。
- Typography、Controls、Player 三份样式字典在 P4、P5 编写，都只引用这里的 token。

**验证**
- `dotnet build -p:Platform=x64`：0 警告、0 错误，生成 `Themes/Tokens.xbf`。
- 用临时探针（已删除）以 `--fake` 启动应用：180 项资源全部能在运行时解析（浅色 48、深色 48、共用 84）。MiSans 通过 `ms-appx` 在免打包应用中正常加载，四个字重测得的宽度各不相同，并且与回退字体不同。
