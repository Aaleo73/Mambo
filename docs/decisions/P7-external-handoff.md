# 外置 MPV 接管

日期：2026-10-05。

用户指出外置模式仍打开 Mambo 播放页控制，要求与旧版行为一致，使用用户自备的 mpv。旧仓库仅只读核对行为，没有复制文件、代码或资产。

## 行为

- 创建会话时即公布所选引擎类型，避免启动瞬间打开内置播放层。Shell 观察实际类型：外置模式保留当前页面、滚动、导航和焦点，不创建 PlayerOverlay；指纹复验回退为内置时才显示播放层。播放成功只显示短通知。
- mpv 正常加载配置与脚本，启用默认输入和窗口键盘输入。沿用 mpv 自己的 OSC/uosc 选择，不强制覆盖。Mambo 不设置外置 mpv 的硬件解码、HDR、音量、静音或倍速；初始化与切集时只观察实际状态，上报原生暂停和进度。
- 外置队列依次加入当前集之后的剧集，保留已播放项供原生列表返回。每次原生 START_FILE 指向曾结束的条目时建立新的上报生命周期，未实际播放的队列项不会产生 Playing/Stopped。内置仍使用当前集和下一集的原策略。
- 关闭 mpv 自动结束会话、保存进度并释放拥有的进程；启动或片源失败自动清理，由浏览页通知提供重试。停止上报和发件箱仍走共享实现。
- 外置 mpv 地址仍通过设置服务显式验证并记录指纹，不硬编码个人路径。正常浏览可以继续；播放另一个项目仍经现有切换确认。

## 启动与配置边界

窗口位置、全屏、画质和脚本均交给用户的 mpv 配置。命令行只传固定选项与随机管道名；URL、认证头、标题及起始位置继续经 IPC。Mambo 不保存 mpv 输出，禁用 mpv 主日志文件、自动恢复和自动退出时保存位置，恢复进度由 Emby 管理。用户脚本按用户自己的配置运行。

播放列表由 Mambo 提供；支持 `autocreate-playlist` 的 mpv 在本次会话中将其关闭，避免目录扫描把首项重定向为未登记的条目。真实便携配置复现过 `Start:1 → Redirect → Start:2`，导致返回首项不能正确跟踪；此项只经 IPC 设置，不修改用户配置文件。通过运行时探测兼容没有该选项的旧版本。

依据：[mpv 配置文件](https://mpv.io/manual/stable/#files)、[mpv 输入与命令](https://mpv.io/manual/stable/#command-interface)。版本探测仍使用隔离参数，不加载配置和脚本；只在实际播放时加载。

## 未播放剧集的列表标题

同日用户反馈 uosc 列表只有当前集显示名称，后续集显示地址。`loadfile` 的 `force-media-title` 是打开该文件时才生效的选项，不能提前填充未播放项的 `playlist/N/title`。该属性来自列表元数据或已打开文件的媒体标题，见 [mpv 播放列表属性](https://mpv.io/manual/stable/#property-list)。

有标题的条目改为通过内存 M3U 的 `EXTINF` 加入原生列表。自行编写的 `Mambo.Player/External/mambo-playlist.lua` 随程序发布，经 IPC 加载到本次 mpv 进程，不修改用户配置。脚本先用 `loadlist` 获取原生编号并登记每项选项，再由 `on_load` 按编号设置 `file-local-options`；即使重排、跳集或重播，标题、续播位置、认证头也对应同一条目。认证头不提升为全局设置，空认证头以空数组明确清除。依据：[mpv hooks](https://mpv.io/manual/stable/#hooks)、[mpv 文件局部选项](https://mpv.io/manual/stable/#property-list)。

列表和选项仅通过私有 IPC 在内存传递，不写 M3U 文件。标题中的 CR/LF/NUL 转为空格，地址含行分隔符时拒绝加载。请求串行化并校验响应序号和原生编号，等待有 5 秒上限；启动结果不确定时不重复注册脚本，避免重复追加。无标题的调用仍使用原生 `loadfile`。

## 验证

新增 `ExternalPlaybackSessionTests` 覆盖外置意图在异步初始化前可见、启动失败清理、配置状态不被覆盖、原生暂停上报、整季后续队列、原生跳集/重播及关闭时一次停止。

`scripts/test-external-handoff.ps1 -MpvPath <用户选择的 mpv.exe 或目录> -SamplePath <本地片源>` 使用隔离的账号与设置、拦截的报告传输以及正式 Shell/播放入口，检查真实配置、uosc 输入绑定、空格键、mpv 原生列表、浏览导航、退出及回退到内置播放层。不会读取真实账号或连接 Emby。诊断输出只保留布尔检查、阶段和安全错误码。

- `dotnet build -p:Platform=x64 --no-restore`：零警告、零错误。
- `dotnet test --no-build --no-restore`：377/377 通过，无失败或跳过。
- Native AOT 发布成功，输出为 `publish/external-handoff/`。
- 用户自备 mpv 的 Debug 与 Native AOT 接管专项均通过：配置、脚本、uosc、原生空格键、后续列表、跳集/重播、原页面保留、可继续浏览、正常退出、三次播放各一次 Stopped、没有 Mambo 播放层、实际回退内置及统一释放，共 18 项。报告分别在 `artifacts/external-handoff/f745cd44a7af4fea91d28acbef40f6fd/result.json` 和 `artifacts/external-handoff/1f8087314ae444cb9aa937a40bc0c38c/result.json`。
- 附加完整 UI 回归未通过：首次被别的窗口遮挡（`UiInputTargetOccluded`）；重跑的播放控制通过，50 次开关完成且 Player/Surface 留存均为 0，但动效的物理输入步骤 `FilterReversal` 失去前台（`UiInputForegroundUnavailable`）。保留 `artifacts/external-handoff/ui-debug.json` 与 `ui-debug-retry.json`，不将该结果记为完整界面通过。
- 经设置服务显式验证并启用用户给出的播放器目录，保存到本机用户设置；个人路径未写入代码或仓库配置。

标题修正补充验证：构建零警告零错误，384/384 单元测试通过；新增 Unicode/逗号标题、换行注入、逐项选项、过期响应及无效编号覆盖。用户自备 mpv 的 Debug 专项 24 项通过，报告 `artifacts/external-handoff/eae9b66c04ae431186068cd0a7a14346/result.json`。新增项目检查三个标题在后续集尚未播放时已存在，重排后标题保持、跳集及返回重播时续播位置与请求头正确；测试中第三集空请求头不会继承前一集。Debug 假 IPC 冒烟通过。

标题修正版 Native AOT 发布到 `publish/external-playlist-titles/`，同样 24 项真实 mpv 专项通过，报告 `artifacts/external-handoff/d44732d082b04561b69ee91edeaaa0f7/result.json`。AOT 假 IPC 冒烟通过（`artifacts/p7-external-ipc-release.json`）。没有关闭用户正在运行的旧版本，也没有覆盖其程序目录；使用新版需退出旧进程后启动新目录的 `Mambo.exe`。

真实 Emby 后台与人工画面观感不由本地合成片源验证代替；本次没有重新标记 P0/P5 的人工验收项。
