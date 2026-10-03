# P6 单实例与有序退出

日期：2026-10-03。范围：本地实现与自动验收；未推送远端。

## 实现

`Program.Main` 在 `Application.Start`、服务构建和窗口创建前调用 `SingleInstanceLifetime.Register`。普通模式注册 `Mambo`，`--fake` / `MAMBO_FAKE=1` 注册 `Mambo:fake`；`--video-lab`、`--smoke`、`--fake-smoke`、`--ui-smoke`、`--startup-smoke` 使用带本轮 PID 的诊断键。因此普通与假模式分别保留一个实例，诊断进程不会激活正在使用的普通窗口。

第二进程在 MTA 线程池执行 `RedirectActivationToAsync`，STA 主线程通过 `CoWaitForMultipleHandles` 等待完成事件，最多 10 秒；STA 等待保留 COM 消息处理，避免同步等待重定向回调时死锁。超时或异常返回退出码 1，成功返回 0；两条路径都在创建 `App` / `MainWindow` 前退出。[微软的单实例流程](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-single-instance)与 [CoWait 的 STA 行为](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cowaitformultiplehandles)支持此做法。

首进程的 `Activated` 回调排入窗口 Dispatcher；`MainWindow` 在未关闭时恢复最小化窗口并激活自身。正常关闭先结束后端播放、保存进度并释放外壳和服务，再排队最终 `Close`，避免重入原生 Closing 回调。`Program` 的生命周期结束后解除 Activated 订阅并注销单实例键。

## 自动验收与结果

`scripts/test-single-instance.ps1 -AppDirectory <应用目录>` 将应用完整复制到新建的仓库内 GUID 目录，拒绝重解析点，并核验复制后 EXE 的 SHA-256。两次启动均明确使用假服务；只记录本轮进程归属、可见窗口数量和退出结果，不查询或复制真实 Emby 凭据、不读取窗口标题或内容。每个进程保留原始启动句柄，核对 PID、启动时间和可执行路径；映像路径现在通过该句柄调用 `QueryFullProcessImageNameW`，避免进程刚启动时 `MainModule` 尚不可枚举的竞态。finally 只处理确认归属的本轮进程，需要强制终止就判失败。

实际报告为 [本轮 result.json](../../artifacts/single-instance-validation/577d8e1be5b946e29bfb7dc5307e72d9/result.json)，UTC 02:17:57–02:18:10，状态 `Passed / TwoLaunchesOneWindow`：

| 检查 | 实际结果 |
| --- | --- |
| 首进程 | 归属确认，峰值 1 个可见窗口，重定向后仍运行 |
| 第二进程 | 归属确认，5 次可见窗口采样均为 0；141ms 退出，退出码 0 |
| 首进程关闭 | `CloseMainWindow` 被接受；463ms 内正常退出，退出码 0 |
| 清理 | 未强制终止，未出现归属无法确认 |

本轮证明同假模式两次启动的重定向及有序退出。普通 Emby 模式复用同一注册机制，但本报告未运行真实账号或播放会话，也不作为普通模式前台焦点、真实播放退出上报或启动性能的验收结果。

## AOT 复测与历史失败边界

上述 Debug 通过记录保留。旧 AOT 的 [908b9c2c 报告](../../artifacts/single-instance-validation/908b9c2cec5a429a995837318bef1a0b/result.json) 中，第二进程退出码 0、无可见窗口；首进程接受关闭后 6450ms 退出，退出码 `-1073741189 / 0xC000027B`，因此 `FirstExitNonzero / Failed`，未强制清理。当时原生退出排查记录涉及 `CoreMessagingXP.dll`，但没有证实异常根因或证明单实例注销时机造成该异常。

[5f0e98ed](../../artifacts/single-instance-validation/5f0e98ed6dc74f458a234c6ed1b0bfac/result.json) 与 [de17112d](../../artifacts/single-instance-validation/de17112d4b104f4ead3fbf3dd5890393/result.json) 均在 `FirstWindow` 阶段因脚本 `MainModule` 启动身份查询竞态发生 `PropertyNotFoundException`，最终记录 `ForcedOwnedProcessCleanup / Failed`。两份报告的首进程正常关闭退出码均为空，不能当成应用的原生关闭异常，也不能计为通过。脚本随后改用保留句柄的内核映像查询，继续保留 PID、创建时间和完整路径核验。

`validation-lifetime` AOT 产物连续四轮均为 `Passed / TwoLaunchesOneWindow`：

| 报告 | 第二进程退出耗时 | 首进程关闭耗时 |
| --- | --- | --- |
| [10621d7e](../../artifacts/single-instance-validation/10621d7e58c7466cb9b6e044d46ebf6e/result.json) | 63ms | 465ms |
| [c9ada57c](../../artifacts/single-instance-validation/c9ada57c26cb4b7da4838b4e5910d512/result.json) | 65ms | 385ms |
| [ef6ebcc6](../../artifacts/single-instance-validation/ef6ebcc64bc941b2a1e35bed6d930231/result.json) | 77ms | 459ms |
| [c11cade9](../../artifacts/single-instance-validation/c11cade92c4f4af2b0994a0cd0fd68b0/result.json) | 58ms | 416ms |

四轮均确认首、次进程归属，首进程峰值 1 个可见窗口、第二进程 0 个；重定向后首进程仍运行，两个退出码均为 0，无强制清理、无归属无法确认。各轮仅假模式启用的固定阶段报告完整经过 `WindowClosing`、`BackendClosed`、`ShellDisposed`、`ServicesDisposed`、`WindowClosed`、`ApplicationStartReturned`、`InstanceEventRemoved`、`InstanceKeyRemoved`，未记录异常类型或 HResult。另一次使用 `-SkipLifetimeTrace` 停用阶段报告写入的 [0d65b2b8 复测](../../artifacts/single-instance-validation/0d65b2b8068848f4ab5322fffdbb6d8b/result.json) 也通过：第二进程 64ms、首进程关闭 464ms，两个退出码 0、窗口数量 1 / 0，无强制清理，目录中未生成进程阶段报告。

运行时没有提前解除 `AppInstance`：仍在 `Application.Start` 返回后由幂等 `Dispose` 解除事件并注销键。微软官方 `main` 分支的 [AppInstance.cpp](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/AppLifecycle/AppInstance.cpp#L433-L442) 中，`UnregisterKey` 只在互斥锁内清理键与键互斥锁；[事件解除实现](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/AppLifecycle/AppInstance.cpp#L539-L543) 调用事件集合的 `remove`。此源码不是本机精确二进制的根因证明。最新五轮说明该 AOT 产物在本机假模式下完成重定向及正常关闭，不宣称历史原生退出异常的根因已经定位或证明消除。
