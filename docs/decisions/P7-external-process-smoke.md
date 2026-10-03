# P7 真实外部进程隔离验收

日期：2026-10-03。状态：独立诊断 Debug 和 Native AOT 构建及真实进程验收均已通过。

## 官方输入

诊断使用与仓库 libmpv 同日期的 [shinchiro 20260610 官方 MPV 发布](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20260610)，不读取旧项目。资产摘要取自[官方资产列表](https://github.com/shinchiro/mpv-winbuild-cmake/releases/expanded_assets/20260610)，固定于 `scripts/diagnostics/Mambo.ExternalSmoke/mpv-smoke.lock.json`：

- `mpv-x86_64-20260610-git-304426c.7z` SHA-256：`facac536baa73c7b925771af5e39a3c9cb16b8d75b59a6e9800de89799dffca7`。
- 从该核验归档提取的 `mpv.exe` SHA-256：`b0bb2dc1928e6d86cc26d950815c80c977440081e814c6a46e93f6e9e99c276d`，117,537,280 字节。
- `d3dcompiler_43.dll` SHA-256：`4b074a3976399dc735484f5d43d04b519b7bdee8ac719d9ab8ed6bd4e6be0345`。

缓存仅在仓库 `artifacts/tools/external-mpv/20260610/`。脚本只提取上述运行文件，不调用发布包 updater / installer。每次运行再次核验压缩包和两文件；诊断程序复制到唯一运行目录后还会复核固定哈希，再调用真实版本批准 API。

## 验证链与隔离

`scripts/test-external-process.ps1 -PrepareOnly` 只准备核验缓存。正常调用先 locked restore、构建独立诊断，`-NoBuild` 供 root 已集中构建后运行。`-Aot -NoBuild` 直接执行默认 `publish/external-smoke-aot/Mambo.ExternalSmoke.exe`，也可用 `-AppDirectory` 指向已集中发布的诊断目录；`-Aot` 未指定 `-NoBuild` 时先发布此独立工具的 Release 自包含 Native AOT，再运行。

每轮使用全新的 `artifacts/p7-external-smoke/<guid>/runtime` 和 `state`。设置只使用此目录的真实 SettingsStore/AppPaths；不加载默认 `%LOCALAPPDATA%/Mambo`、Windows Credential Manager 或用户 Emby 会话。内存账户全部随机产生，地址为 `.invalid`；EmbyApi 注入不带网络回退的 HttpMessageHandler，任何非诊断 HTTP 请求立即失败。真实样片为现有本地 `jellyfin-4k-hevc-hdr10.mp4`，也可显式指定路径。

1. `MpvExecutableApproval` 真实执行官方 `--version`，SettingsStore 显式批准并启用 External。真实 ExternalMpvEngine 使用命名管道 IPC；LocalPreparer 仅代替 Emby 选片/网络解析，PlaybackCoordinator、PlaybackSession、PlaybackReporter、StopOutbox 都使用生产实现。
2. 在开播前通过 IPC 设置 `vo=null`、`ao=null`、`force-window=no`。生产引擎使用 CreateNoWindow，但最初的 `force-window=immediate` 可能短暂创建窗口；这项诊断不检查 HDR 或显示效果。
3. 等到 IPC 驱动的 Playing、非零位置和隔离 HTTP Playing 后暂停。进程定位只保留与唯一隔离 mpv.exe 绝对路径一致、启动时间属于本轮且已打开 handle 的单个 Process。检测到多个匹配或身份不一致就失败；Kill(false) 仅针对该 handle/PID，不结束其他路径的 MPV 或其进程树。
4. Kill 后由事件桥观察真实 EngineEvent.Shutdown 或 Failure（仍按原样转交会话），验证 Phase=Failed。生产进程退出检测发 Shutdown；管道异常还可能先发 Failure，所以结果分别记录次数，不强求特定竞态顺序。故意让隔离 Stopped HTTP 返回503，检查 StopOutbox 内存与实际磁盘各只有本次停止记录，包含最终位置；再允许请求成功 Flush，确认一次成功送达、Outbox 清空、关闭不重复上报。
5. 完成关闭后向隔离 EXE 追加一个字节，只调用只读 Verify/GetApproved；修改的文件绝不执行。确认旧指纹无效、批准被移除、模式切回 Embedded、状态 Invalid。恢复官方原始字节也不得自动恢复批准或启用 External。只有再次显式 Validate 后才能新建真实外部会话并 Playing，正常关闭仍上报 Stopped。

结果 `result.json` 只保存哈希、时刻、本次 PID、阶段布尔值、播放位置和成功上报计数；不保存版本输出、HTTP 头、账户或令牌。失败或成功均保留隔离目录供审阅，不扫描/删除用户目录。

## 验收边界

这是本机真实进程、真实 IPC、真实会话生命周期和离线停止补报的自动验收。HTTP 服务器端接受/活跃会话显示、设置页面提示、实际 HDR 显示和干净 Windows 系统仍须另外验证。官方 EXE 的下载仅供本地诊断；不会因这项测试解除 P8 对应源码缺口造成的公开二进制分发限制。

## 已完成的真实进程结果

root 于 UTC 2026-10-02 17:48:47 至 17:48:54（本地 2026-10-03 00:48）集中构建并运行 Debug 诊断，退出成功。完整结果保存在 `artifacts/p7-external-smoke/256877b362a54c38880b339eaaf3912f/result.json`：

- 成功 Playing / Stopped 各 2 次，对应两轮真实外部播放；首次关闭没有重复停止。
- 本轮隔离 PID 58036 被结束后观察到 1 次 Shutdown、0 次 Failure，会话进入 Failed，符合生产进程退出检测路径。
- 位置 1,000,000 ticks（0.1秒）通过停止记录保持；Stopped HTTP 503 后记录实际落盘，恢复后一次成功投递、Outbox 清空。
- EXE 改字节后旧批准撤销、Embedded / Invalid；恢复官方原字节仍须再次明确批准；重新 Validate 后第二轮真实 IPC 播放恢复。

上述阶段布尔值全部为 true。先前用户延后的人工 P7 检查不改写为人工观察通过；此真实进程自动验收按用户最新“全部交付”授权替代进程崩溃与指纹重批的自动验证条件。没有宣称真实 Emby 活跃会话页面或人工显示检查已完成。

Native AOT 于 UTC 2026-10-03 01:13:27 至 01:13:34 实际运行通过，结果位于 `artifacts/p7-external-smoke/b232ca521185467c89f3a2ae3cda05e7/result.json`。成功 Playing / Stopped 各 2 次；隔离 PID 47444 退出后 1 次 Shutdown、0 次 Failure，停止位置 500,000 ticks 得到保持；九项阶段布尔值全部通过。结束后确认引擎已 Dispose、本轮进程已退出、诊断 Process 句柄已释放；替换 EXE 时只对 Win32 共享/锁定错误有限重试，本次重试一次成功。失败报告只记录固定阶段、异常类型与 HRESULT，成功报告在所有异步释放完成后写出。修改后的 EXE 从未执行。
