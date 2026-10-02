# AGENTS.md

这个仓库正在把 Mambo（Windows 上的 Emby 媒体客户端）重写为 **WinUI 3 + C#/.NET 10 + libmpv**，画面通过 mpv 的 d3d11 composition 交换链挂到 SwapChainPanel 上。

## 分工（两个 agent 协作）

- **Codex（GPT）负责后端**：
  - 构建基础设施、`scripts/`、`third_party/`、`installer/`；
  - `src/Mambo.Core`（含 `Contracts/`、`Fakes/`）、`src/Mambo.Player`、`tests/`；
  - `src/Mambo.App` 中的 `Video/`、`Platform/`、`Debug/`，以及 `Composition/` 下的 `BackendServices.cs`、`UiScheduler.cs`、`AppShutdownCoordinator.cs`。
  - 阶段：P0、P1（最先做 P1a 契约与假实现）、P3、P7 的引擎部分、P8 的脚本部分。
- **Claude 负责前端**：`design/` 和 `src/Mambo.App` 的其余部分。
  - 阶段：P2、P4、P5，以及 P7、P8 的界面部分。
- **规则**：
  - 只修改自己拥有的路径；需要对方改动时，写进 `docs/handoff/requests.md`。
  - 完整的所有权表、契约和协作流程见 `docs/PLAN.md` §14。

## 必读

- `docs/PLAN.md` 是完整的实施计划。按阶段执行，每个阶段跑完验收后，在文末"进度"打勾。
- **每次开工**：先合并对方的分支（见 PLAN §14.4），再读 `docs/handoff/` 下的文件。
- **关卡**：P0 结束（视频技术验证）和 P2 结束（设计稿）时，必须停下来等用户确认。
- **【需用户】**：计划里标了这个记号的步骤要请用户参与，不要擅自代办，例如安装软件、提供 Emby 服务器、人工检查显示效果。

## 硬性约束

- **旧项目**：不复用 `D:\MAKISEV\emby-mpv-player`（旧 Tauri 项目）的任何文件或资产，唯一例外是字体（用本仓库的 MiSans）。旧项目只能只读参考行为，`docs/PLAN.md` 附录 A 已经整理好了。
- **前端依赖边界**：前端代码只能依赖 `Mambo.Core.Contracts` 和 `VideoSurface` 的公开 API，不得引用 Core 的实现类、DTO 或 Player 的内部类型。
- **敏感信息**：服务器地址、令牌、密码不得写入仓库、日志、测试数据或提交信息。
- **AOT**：遵守 `docs/PLAN.md` §4.3 的兼容规则，即使用 `LibraryImport`、JSON 源生成、`{x:Bind}`、partial 类型。
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
  - 提交信息用中文 conventional commits，scope 要能看出是前端还是后端，例如 `feat(core): …`、`feat(player): …`、`feat(ui): …`；
  - 不推送远端。
- **决策记录**：重要的技术决定记在 `docs/decisions/` 下。
