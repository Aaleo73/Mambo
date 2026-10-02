# 前后端请求

需要对方修改它负责的路径时，在下表新增一条请求。处理方完成后更新"状态"。分歧无法解决时，交给用户决定。所有权见 `docs/PLAN.md` §14.1。

- **编号**：按顺序递增，格式为 R-001、R-002……
- **内容**：写清背景、期望的接口或行为、涉及的契约类型。
- **阻塞**：填"是"表示提出方在这件事完成前无法继续。
- **状态**：待处理 / 已完成 / 已拒绝（附理由）。

| 编号 | 提出方 | 日期 | 内容 | 阻塞 | 状态 |
|---|---|---|---|---|---|
| R-001 | Codex | 2026-10-02 | 用户已批准 P0 提交并进入 P1。Program / App / MainWindow 现移交 Claude；请按 PLAN §14.4 合并 backend 并验证构建。原生 DXGI 句柄增长及尚未完成的人工验收保留在 `docs/decisions/P0-video-spike.md`，不视为已通过。 | 否 | 待处理 |
| R-002 | Codex | 2026-10-02 | 请 Claude 合并 backend 后评审 `src/Mambo.Core/Contracts/` 初始草案与全套假实现。重点确认页面所需模型字段、Observe 查询与取消语义、PlayRequest 的替换确认、播放关闭及消息；说明与验证见 `docs/decisions/P1a-contracts.md`。61 项测试及 Debug / AOT 双入口冒烟通过。按 PLAN §14.3，评审通过后双方记录 v1 冻结并勾选 P1a；此请求阻塞契约冻结和 P1 阶段验收。 | 是 | 待处理 |
| R-003 | Codex | 2026-10-02 | 请 Claude 在最终 App / Program / MainWindow 外壳启动中接入 `BackendServices.IsFakeMode`、`AddBackendServices(fake, IUiScheduler)` 与 UI 服务注册；目前仅 Debug/VideoLab 提供假模式入口，真实服务注册仍待 P1。XAML 使用 getter-only partial ViewModel / 投影和 x:Bind，不直接以带 init 属性的 Contracts record 为 x:DataType，否则 WinUI 生成 setter 会报 CS8852；参考 `Debug/DemoRows.cs`。播放界面只使用 `VideoSurface.Attach(IPlaybackSession)` / `Detach()`。 | 否 | 待处理 |
