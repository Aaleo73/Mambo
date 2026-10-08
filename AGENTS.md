# AGENTS.md

这个仓库是 Mambo（Windows 上的 Emby 媒体客户端）的 **WinUI 3 + C#/.NET 10 + libmpv** 实现，画面通过 mpv 的 d3d11 composition 交换链挂到 SwapChainPanel 上。

## 开工前

- `docs/STATUS.md`：进度和未完成的验收。先看这里，区分"实现完成"与"人工验收未完成"；已完成的阶段不要重新执行。
- `docs/ARCHITECTURE.md`：模块设计与约束。`docs/SPEC.md`：功能与交互规格。按需查阅。
- `docs/decisions/`：各项技术决定的原因与限制。
- **【需用户】**：`docs/STATUS.md` 里标了这个记号的验收要请用户参与，不要擅自代办，例如提供 Emby 服务器、安装软件、人工检查显示效果。

## 已有用户授权

- **实际 Emby 服务器访问**：用户于 2026-10-08 明确长期授权，可随时使用本机 Mambo 已保存的登录信息连接本次排查《喜鹊谋杀案》字幕所用的实际服务器，进行浏览、播放、故障排查与技术验证，无需逐次询问。需要访问该服务器的【需用户】项已具备连接授权；人工观感等仍不能用自动化结果代替。
- 凭据仅通过本机现有安全存储使用；服务器地址、令牌、密码不得写入本文件、仓库、日志、测试数据或提交信息。此授权不包括删除媒体、修改服务器权限或其他破坏性管理操作。

## 仓库与分支

- 界面、内部服务、播放器和更新器都在本仓库的 `src/` 下，主分支是 `main`，远端是 `Aaleo73/Mambo`。
- 如需并行开发，使用任务分支和仓库外的 `../Mambo-worktrees/<任务名>`；不要在主仓库内嵌套 worktree。

## 硬性约束

- **旧项目**：不复用重构前的 Tauri 项目 `emby-mpv-player` 的任何文件或资产，唯一例外是字体（用本仓库的 MiSans）。旧项目不在本仓库内，只能只读参考行为；所需规格已整理在 `docs/SPEC.md`。
- **依赖边界**：`Mambo.App` 的界面代码只能依赖 `Mambo.Core.Contracts` 和 `VideoSurface` 的公开 API，不得引用 Core 的实现类、DTO 或 Player 的内部类型。
- **敏感信息**：服务器地址、令牌、密码不得写入仓库、日志、测试数据或提交信息。
- **AOT**：遵守 `docs/ARCHITECTURE.md` §2.3 的兼容规则，即使用 `LibraryImport`、JSON 源生成、`{x:Bind}`、partial 类型。
- **XAML 位置**：所有 XAML 只放在 `src/Mambo.App`。
- **提交质量**：不要提交编译不过或测试失败的代码。

## 工具链与命令（只用命令行，不依赖 Visual Studio）

- 构建：`dotnet build -p:Platform=x64`
- 测试：`dotnet test`
- 运行：`dotnet run --project src/Mambo.App -p:Platform=x64`
- 用假数据运行：在上一条命令末尾加 `-- --fake`
- 获取 libmpv：`pwsh scripts/fetch-libmpv.ps1`（需联网，按 lock 文件校验 SHA-256）
- 排查 XAML 或构建问题：加 `-bl` 生成二进制日志

## 约定

- **语言**：界面和错误文案用简体中文，代码标识符用英文。
- **C#**：启用 Nullable，使用文件范围命名空间；ViewModel 用 CommunityToolkit.Mvvm 的 partial 属性。
- **提交**：
  - 提交信息用中文 conventional commits，scope 标明模块，例如 `feat(core): …`、`feat(player): …`、`feat(ui): …`；
  - 未经用户明确要求不推送远端。
- **决策记录**：重要的技术决定记在 `docs/decisions/` 下，写清决定、原因、限制和未验收项，不记逐轮验收流水。
