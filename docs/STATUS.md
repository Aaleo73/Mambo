# Mambo 状态

当前进度和尚未完成的验收。开工前先看这里；完成一项后在这里更新，验证细节记在 `docs/decisions/` 对应的记录里。

最新发布版本为 v0.1.7。模块设计见 [ARCHITECTURE.md](ARCHITECTURE.md)，行为规格见 [SPEC.md](SPEC.md)。

逐轮验收的原始记录（候选构建编号、截图、报告文件路径、各阶段任务清单）已从文档中移除。需要追溯时查看 `v0.1.7` 标签下的 `docs/PLAN.md`、`docs/decisions/` 和 `docs/handoff/`。

## 尚未完成的验收

标【需用户】的项需要用户参与（真实服务器、人工观感、物理键鼠），不要用自动化结果代替，也不要擅自代办。

### P4 外壳与浏览页面

实现和自动回归已完成。未达标或未验收：

- 冷启动显示缓存首页的目标是 800 ms，最近一次测量为 972 / 919 / 827 ms。
- 5000 项资料库滚动保持 60 fps：GPU 呈现帧率尚未验收。

记录：[P4–P6 集成](decisions/P4-P6-integration.md)

### P5 播放页 UI

实现和自动回归已完成，包括 SPEC A.10 的按键与真实按钮、停止播放后详情 / 最近播放 / 资料库的进度刷新。

- 【需用户】真实键鼠、光标与系统效果的人工确认。

### P6 打磨与加固

- 窗口 Loaded 的目标是 600 ms，最近一次测量为 881 / 865 / 774 ms。
- 【需用户】讲述人实际朗读主要控件。
- P0 遗留：硬件渲染每次创建 / 销毁约增加一个 Section 和一个 Mutant 句柄。不加载 WinUI 和 libmpv 的独立工具 `scripts/diagnostics/Mambo.CompositionProbe` 也能复现。
- P0 遗留【需用户】：DPI 与多显示器、按钮 / 光标 / 闪烁、真实 Emby 抓包确认令牌不发往跨域地址。

记录：[P0 视频技术验证](decisions/P0-video-spike.md)、[P4–P6 集成](decisions/P4-P6-integration.md)

### P9 统一动效语言

实现和 Debug / AOT 完整门禁已通过。

- 【需用户】Windows 动画设置切换、真实媒体观感、Snap 弹出层。

记录：[P9 视觉基准与统一动效](decisions/P9-visual-parity.md)

### P10 弹幕

实现、单元测试和线上匹配实测已通过。

- 【需用户】真实 Emby 上的自动匹配、高刷新率与 HDR 观感。
- 完整界面回归未执行。

记录：[弹幕](decisions/bullet-chat.md)

### 画质模式

功能实现和自动验收已通过。

- 【需用户】画面验收：SDR / PQ / HLG、Windows HDR 切换和跨屏，新旧动画、颗粒电影、真人的观感。不同显卡的冷编译、热运行、掉帧和专用显存分别记录，不用本机结果推断全部设备。
- Video Lab 的独立资源稳定性检查仍未通过，没有算作性能验收。
- 最后一次修订后，没有重跑会激活窗口的界面与真实播放诊断。

记录：[画质模式](decisions/video-quality-modes.md)

### 其他待复验

| 项 | 待办 | 记录 |
|---|---|---|
| 输入框竖线光标 | 已改为随主题适配的柔和灰色，Debug / AOT 专项检查通过；【需用户】实际观感、输入法、DPI 与高对比度 | [input-caret](decisions/input-caret.md) |
| 长篇剧集分页 | 真实片库复验 | [episode-pagination](decisions/episode-pagination.md) |
| 筛选响应优化 | 真实服务器耗时复验 | [library-filter-responsiveness](decisions/library-filter-responsiveness.md) |
| 海报清晰度 | 【需用户】跨显示器观感 | [P6-image-resolution](decisions/P6-image-resolution.md) |
| 应用图标 | 【需用户】任务栏实际观感 | [app-icon](decisions/app-icon.md) |
| 外部播放器 | 【需用户】Emby 后台人工观察 | [P7-external-handoff](decisions/P7-external-handoff.md) |
| 完整界面输入回归 | 多次受窗口遮挡或超时中断，未完整跑通 | [github-auto-update](decisions/github-auto-update.md)、[P7-external-handoff](decisions/P7-external-handoff.md) |
| 安装包 | 只在本机验证过安装、升级、卸载；干净系统（专用虚拟机）验收已按用户要求取消 | [P8-packaging](decisions/P8-packaging.md) |

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
| 三种画质模式（实现与自动验收） | 2026-10-05 | [video-quality-modes](decisions/video-quality-modes.md) |
| 应用内组件更新 | 2026-10-06 | [component-updates](decisions/component-updates.md) |
| 长篇剧集分页修复 | 2026-10-06，v0.1.6 | [episode-pagination](decisions/episode-pagination.md) |
| 应用图标 | 2026-10-06，v0.1.7 | [app-icon](decisions/app-icon.md) |

## 验证方式

- **每次改动后**：`dotnet build -p:Platform=x64` 成功，`dotnet test` 全部通过。
- **界面与播放**：`scripts/test-*.ps1` 是本机验收脚本，配合 `src/Mambo.App/Debug/` 下的入口运行，会激活窗口，不在 CI 里。
- **真实服务器**【需用户】：直链播放、302 到 CDN、强制转码、带外挂字幕的片源、一整季连播、断网后恢复；在 Emby 后台核对会话、进度和已播放标记。
- **显示**【需用户】：HDR 开 / 关；100 / 150 / 200% 缩放；在两台显示器之间拖动；最大化 ↔ 全屏。
- **发布前**：AOT 发布冒烟；在本机完成安装、升级与卸载验证。
