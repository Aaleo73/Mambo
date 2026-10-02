# 后端状态（Codex 维护）

前端根据这张表决定何时从假实现切换到真实服务。契约定义见 `docs/PLAN.md` §14.3。

| 契约服务 | 状态（未开始 / 假 / 真 / 已测试） | 已知限制 | 更新日期 |
|---|---|---|---|
| `ISessionService` | 未开始 | | |
| `ILibraryService` | 未开始 | | |
| `IImageService` | 未开始 | | |
| `ISettingsService` | 未开始 | | |
| `ILibraryPreferences` | 未开始 | | |
| `IPlaybackService` / `IPlaybackSession` | 未开始 | | |
| `VideoSurface`（`Attach` / `Detach`） | 未开始 | P0 内部交换链探针已验证；公开 `Attach(IPlaybackSession)` 待 P1a，不可据此接入前端 | 2026-10-02 |

## P0 骨架与视频验证

2026-10-02：WinUI 3 / .NET 10 四项目骨架、libmpv 下载锁、Video Lab、HDR / DPI / 交换链互操作、认证 URL 预解析及 AOT 配置已实现。`dotnet build -p:Platform=x64` 零警告零错误，`dotnet test` 13 项通过；Debug 与 AOT 的 4K HEVC `d3d11va` 播放、缓冲区尺寸同步、最大化 / 全屏及 AOT DLL 缺失中文错误已验证。

**用户批准携遗留项进入 P1**（2026-10-02：“提交，进入P1”）：硬件渲染每次创建 / 销毁约增加一个 Section 和一个 Mutant；不加载 WinUI / libmpv、直接调用 DXGI composition 的独立工具也复现，详见 `scripts/diagnostics/Mambo.CompositionProbe`。用户已确认 HDR 高光及播放中关闭 Windows HDR 切回 SDR 通过。DPI / 多显示器、按钮 / 光标 / 闪烁和真实 Emby 抓包仍需人工验收。详见 [P0 视频技术验证](../decisions/P0-video-spike.md)。这些验收结果不变，P1a 契约未冻结；Program / App / MainWindow 的后续修改现移交 Claude。

复测：`pwsh scripts/fetch-libmpv.ps1`、`pwsh scripts/fetch-video-sample.ps1`，构建后执行 `pwsh scripts/test-video-lab.ps1`。AOT 先用 `dotnet publish src/Mambo.App -p:Platform=x64 -p:PublishProfile=Aot -o publish/aot`，再执行 `pwsh scripts/test-video-lab.ps1 -Aot`；加 `-RequireStableResources` 才将资源检查作为脚本失败条件，当前会失败。

## 契约变更记录

（暂无）
