# Mambo 状态

当前进度和尚未完成的验收。开工前先看这里；完成一项后在这里更新，验证细节记在 `docs/decisions/` 对应的记录里。

最新发布版本为 v0.1.7。模块设计见 [ARCHITECTURE.md](ARCHITECTURE.md)，行为规格见 [SPEC.md](SPEC.md)。

逐轮验收的原始记录（候选构建编号、截图、报告文件路径、各阶段任务清单）已从文档中移除。需要追溯时查看 `v0.1.7` 标签下的 `docs/PLAN.md`、`docs/decisions/` 和 `docs/handoff/`。

## 尚未完成的验收

标【需用户】的项需要用户参与（真实服务器、人工观感、物理键鼠），不要用自动化结果代替，也不要擅自代办。

2026-10-07，用户确认此前列出的【需用户】人工验收全部通过。用户给的是整体结论，没有逐项记录；这些项已移到「已完成」。之后新增的功能仍按同样的规则登记。

| 项 | 待办 | 记录 |
|---|---|---|
| 字幕与音轨体验【需用户】 | 真实 Emby 上拖入单字幕/多集字幕、重启后采用、晚到服务器外挂与手选保护；暂停时检查 SRT/ASS 字体、描边、位置和覆盖效果；实际键鼠输入及高 DPI 布局，包括音量向上展开、跨入滑块和拖出释放，以及控制条显示时的播放器四角 | [playback-tracks](decisions/playback-tracks.md) |
| 5000 项资料库滚动 60 fps | GPU 呈现帧率没有验收：本机没有 PresentMon，当前账户也没有启动实时 ETW 跟踪的权限。已测的是 UI 回调间隔，不等于呈现帧率 | [P4–P6 集成](decisions/P4-P6-integration.md) |
| 长篇剧集分页 | 真实片库复验。需要真实服务器，但原先没有标【需用户】，不确定是否包含在 2026-10-07 的确认里 | [episode-pagination](decisions/episode-pagination.md) |
| 筛选响应优化 | 真实服务器耗时复验。同上 | [library-filter-responsiveness](decisions/library-filter-responsiveness.md) |

## 已知限制

- **硬件渲染的句柄增长**：装有 NVIDIA App 的机器上，每次播放多一个 Section 和一个 Mutant 句柄。它们是 NVIDIA App 注入的 `nvspcap64.dll` 打开后没有关闭的，不是 Mambo、WinUI 或 libmpv 的泄漏；探针测到每次还多几十 KB 内存，重启应用清空，接受为已知限制。`test-video-lab.ps1 -RequireStableResources` 在这类机器上会继续失败。见 [P0 视频技术验证](decisions/P0-video-spike.md)。
- **启动耗时**：热文件缓存下达标（窗口 Loaded 501–567 ms，缓存首页 565–623 ms）。更新后第一次启动和冷机启动没有测。
- **画质模式**：只在本机 RTX 4060 上测过，AMD、Intel 显卡和专用显存没有测。见 [画质模式](decisions/video-quality-modes.md)。
- **安装包**：只在本机验证过安装、升级、卸载；干净系统（专用虚拟机）验收已按用户要求取消。见 [P8-packaging](decisions/P8-packaging.md)。

## 已完成

| 项 | 完成 | 记录 |
|---|---|---|
| P0 工具链、骨架与视频技术验证 | 2026-10-02，用户批准携遗留项进入 P1 | [P0-video-spike](decisions/P0-video-spike.md) |
| P1a 契约与假实现 | 2026-10-02，契约 v1 冻结 | [P1a-contracts](decisions/P1a-contracts.md) |
| P1 Core 平台层 | 2026-10-02，用户确认真实登录、列库、重启恢复 | [P1-core-platform](decisions/P1-core-platform.md) |
| P2 设计稿 | 2026-10-02，用户确认定稿 | [P2-design](decisions/P2-design.md) |
| P3 播放引擎与会话 | 2026-10-02，用户确认真实服务器验收 | [P3-playback](decisions/P3-playback.md) |
| P7 外部播放器 | 2026-10-05，改为外置 mpv 接管 | [P7-external-handoff](decisions/P7-external-handoff.md)、[P7-external-player](decisions/P7-external-player.md)、[P7-external-process-smoke](decisions/P7-external-process-smoke.md) |
| P8 本地打包交付 | 2026-10-03 | [P8-packaging](decisions/P8-packaging.md)、[P8-installer-validation](decisions/P8-installer-validation.md) |
| 筛选与搜索修复 | 2026-10-04 | [library-filter-responsiveness](decisions/library-filter-responsiveness.md) |
| 筛选响应优化 | 2026-10-05 | 同上 |
| 海报清晰度修复 | 2026-10-05 | [P6-image-resolution](decisions/P6-image-resolution.md) |
| 完整原生对应源码 | 2026-10-05 | [native-distribution](decisions/native-distribution.md) |
| GitHub 托管与自动更新 | 2026-10-05，v0.1.3 起公开发布二进制 | [github-auto-update](decisions/github-auto-update.md) |
| 三种画质模式（实现与自动验收） | 2026-10-05；修订后会激活窗口的三项诊断在 2026-10-07 补跑通过 | [video-quality-modes](decisions/video-quality-modes.md) |
| 应用内组件更新 | 2026-10-06 | [component-updates](decisions/component-updates.md) |
| 长篇剧集分页修复 | 2026-10-06，v0.1.6 | [episode-pagination](decisions/episode-pagination.md) |
| 应用图标 | 2026-10-06，v0.1.7 | [app-icon](decisions/app-icon.md) |
| 输入框竖线光标 | 2026-10-07，尚未发布 | [input-caret](decisions/input-caret.md) |
| 播放控制条精简 | 2026-10-07，移除最右侧最大化按钮，字幕/音轨拆为独立入口，模式按钮置首并显示标准/清晰/动画，其余按钮图标化（音轨为双音符），音量向上竖向展开，画面与控制层统一圆角裁切；尚未发布 | [播放页交互](SPEC.md#a10-播放页交互) |
| 字幕与音轨体验（实现与自动验证） | 2026-10-07，静默选轨偏好、现有组件内的时间/样式、按项目保存并自动加载拖入字幕；尚未发布，真实体验待上方人工验收 | [playback-tracks](decisions/playback-tracks.md) |
| 人工验收 | 2026-10-07，用户确认此前全部【需用户】项通过：P5 真实键鼠与光标、P6 讲述人、P0 遗留的 DPI / 多显示器 / 闪烁 / 跨域令牌抓包、P9 动画设置与 Snap、P10 弹幕、画质模式、输入光标、海报清晰度、应用图标、外部播放器 | 各项对应的记录 |
| 完整界面输入回归 | 2026-10-07，Debug 与 AOT 各跑通；脚本能识别桌面被打断并自动重跑 | [P4–P6 集成](decisions/P4-P6-integration.md) |
| 启动性能（600 / 800 ms） | 2026-10-07，AOT 两批共 10 轮全部达标 | [P4–P6 集成](decisions/P4-P6-integration.md) |
| 硬件渲染句柄增长的定性 | 2026-10-07，接受为已知限制 | [P0-video-spike](decisions/P0-video-spike.md) |

## 验证方式

- **每次改动后**：`dotnet build -p:Platform=x64` 成功，`dotnet test` 全部通过。
- **界面与播放**：`scripts/test-*.ps1` 是本机验收脚本，配合 `src/Mambo.App/Debug/` 下的入口运行，会激活窗口，不在 CI 里。
- **播放控件定向检查**：`pwsh scripts/test-player-controls.ps1`（加 `-Aot` 检查 AOT），只运行控制条布局、竖向音量、字幕/音轨组件及其全屏、输入隔离路径，不重复完整 UiLab。
- **完整界面回归**：`pwsh scripts/test-ui-lab.ps1`（加 `-Aot` 验 AOT 产物），一轮约 3 分钟，用真实键鼠输入，窗口必须全程在前台。运行期间不要操作这台电脑，包括点聊天或终端窗口；被打断时脚本会报出占用前台的进程并自动重跑，最多 3 次。
- **AOT 诊断目录**：带 `-Aot` 的验收脚本读取 `publish/aot`，需要先发布：`dotnet publish src/Mambo.App/Mambo.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishAot=true -p:WindowsAppSDKSelfContained=true -o publish/aot`。链接步骤报「文件名、目录名或卷标语法不正确」时，把 `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` 加进 PATH 再试；`scripts/publish.ps1` 已自动处理。
- **真实服务器**【需用户】：直链播放、302 到 CDN、强制转码、带外挂字幕的片源、一整季连播、断网后恢复；在 Emby 后台核对会话、进度和已播放标记。
- **显示**【需用户】：HDR 开 / 关；100 / 150 / 200% 缩放；在两台显示器之间拖动；最大化 ↔ 全屏。
- **发布前**：AOT 发布冒烟；在本机完成安装、升级与卸载验证。
