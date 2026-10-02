# 后端状态（Codex 维护）

前端根据这张表决定何时从假实现切换到真实服务。契约定义见 `docs/PLAN.md` §14.3。

| 契约服务 | 状态（未开始 / 假 / 真 / 已测试） | 已知限制 | 更新日期 |
|---|---|---|---|
| `ISessionService` | 假 | 演示账号；登录 / 恢复 / 退出生命周期已测试；无真实认证或凭据持久化 | 2026-10-02 |
| `ILibraryService` | 假 | 三库含 5000 项、分页、筛选、搜索及详情；取消 / 刷新 / 错误保留已测试；无真实 API 或共享磁盘缓存 | 2026-10-02 |
| `IImageService` | 假 | 原创 BMP 渐变与点阵标题；确定性及 WinUI 解码已测试；无真实图片请求或缓存 | 2026-10-02 |
| `ISettingsService` | 假 | 只保存在内存；事件 / 取消 / 退订已测试；外部播放器验证暂返回无效 | 2026-10-02 |
| `ILibraryPreferences` | 假 | 演示账号内存偏好；校验 / 通知已测试；未接入真实账号作用域切换 | 2026-10-02 |
| `IPlaybackService` / `IPlaybackSession` | 假 | 12 集、时钟、缓冲、错误、轨道、关闭已测试；不创建 mpv 或打开媒体 URL | 2026-10-02 |
| `VideoSurface`（`Attach` / `Detach`） | 假 | 公开 `Attach(IPlaybackSession)` 已支持 Demo Composition 纯色并通过 Debug / AOT；真实会话桥接待 P3，P0 内部交换链入口仅供探针 | 2026-10-02 |

## P1a 契约与假服务

2026-10-02：契约草案、全部假服务与 `AddBackendServices(fake: true)` 已实现。`--fake` / `MAMBO_FAKE=1` 当前在 Debug/VideoLab 启动假数据演示。构建零警告零错误、61 项测试通过；Debug / AOT 的两种入口均通过分页、生成图片解码、12 集切集、画面挂接与关闭冒烟，且未加载 libmpv。

**尚未冻结 v1**：按 PLAN §14.3 等待 Claude 评审 R-002，因此 PLAN 的 P1a 仍未勾选。API、取消与生命周期约定、运行命令及 XAML record 绑定限制见 [P1a 契约草案](../decisions/P1a-contracts.md)。真实 P1 平台层尚未接入；前端通过 Contracts 接口开发，不依赖 `Fakes/` 类型。

## P0 骨架与视频验证

2026-10-02：WinUI 3 / .NET 10 四项目骨架、libmpv 下载锁、Video Lab、HDR / DPI / 交换链互操作、认证 URL 预解析及 AOT 配置已实现。`dotnet build -p:Platform=x64` 零警告零错误，`dotnet test` 13 项通过；Debug 与 AOT 的 4K HEVC `d3d11va` 播放、缓冲区尺寸同步、最大化 / 全屏及 AOT DLL 缺失中文错误已验证。

**用户批准携遗留项进入 P1**（2026-10-02：“提交，进入P1”）：硬件渲染每次创建 / 销毁约增加一个 Section 和一个 Mutant；不加载 WinUI / libmpv、直接调用 DXGI composition 的独立工具也复现，详见 `scripts/diagnostics/Mambo.CompositionProbe`。用户已确认 HDR 高光及播放中关闭 Windows HDR 切回 SDR 通过。DPI / 多显示器、按钮 / 光标 / 闪烁和真实 Emby 抓包仍需人工验收。详见 [P0 视频技术验证](../decisions/P0-video-spike.md)。这些验收结果不变，P1a 契约未冻结；Program / App / MainWindow 的后续修改现移交 Claude。

复测：`pwsh scripts/fetch-libmpv.ps1`、`pwsh scripts/fetch-video-sample.ps1`，构建后执行 `pwsh scripts/test-video-lab.ps1`。AOT 先用 `dotnet publish src/Mambo.App -p:Platform=x64 -p:PublishProfile=Aot -o publish/aot`，再执行 `pwsh scripts/test-video-lab.ps1 -Aot`；加 `-RequireStableResources` 才将资源检查作为脚本失败条件，当前会失败。

## 契约变更记录

2026-10-02：新增 P1a 初始草案（读取观察、领域模型、全部服务、消息与 VideoSurface 公开 Demo 入口），待 Claude 评审后记录 v1 冻结。
