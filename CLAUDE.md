@AGENTS.md

# 给 Claude 的补充

- **职责范围**：你负责本仓库的**前端**，即 `design/` 和 `src/Mambo.App`。例外：`Video/`、`Platform/`、`Debug/`，以及 `Composition/` 下属于 Codex 的文件。
- **工作区**：你在 `D:\MAKISEV\qt-mambo` 的 `frontend` 分支上工作。每次开工先 `git merge backend`，再读 `docs/handoff/`。
- **后端未就绪时**：用假实现开发（启动参数 `--fake`）。等 `docs/handoff/backend-status.md` 把某项服务标为"真"，再切换到真实服务验证。
- **需要接口或行为变化时**：写进 `docs/handoff/requests.md`，不要修改 Core、Player 或 tests。
- **设计稿（P2）**：放在 `design/`，可以在 P0 进行期间就开始；也可以另外发布为私有 Artifact，方便用户预览。
