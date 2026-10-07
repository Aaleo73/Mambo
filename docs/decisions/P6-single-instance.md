# P6 单实例与有序退出

日期：2026-10-03。范围：实现与自动验收。

## 实现

`Program.Main` 在 `Application.Start`、服务构建和窗口创建前调用 `SingleInstanceLifetime.Register`。普通模式注册 `Mambo`，`--fake` / `MAMBO_FAKE=1` 注册 `Mambo:fake`；`--video-lab`、`--smoke`、`--fake-smoke`、`--ui-smoke`、`--startup-smoke` 使用带本轮 PID 的诊断键。因此普通与假模式分别保留一个实例，诊断进程不会激活正在使用的普通窗口。

第二进程在 MTA 线程池执行 `RedirectActivationToAsync`，STA 主线程通过 `CoWaitForMultipleHandles` 等待完成事件，最多 10 秒；STA 等待保留 COM 消息处理，避免同步等待重定向回调时死锁。超时或异常返回退出码 1，成功返回 0；两条路径都在创建 `App` / `MainWindow` 前退出。[微软的单实例流程](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-single-instance)与 [CoWait 的 STA 行为](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-cowaitformultiplehandles)支持此做法。

首进程的 `Activated` 回调排入窗口 Dispatcher；`MainWindow` 在未关闭时恢复最小化窗口并激活自身。正常关闭先结束后端播放、保存进度并释放外壳和服务，再排队最终 `Close`，避免重入原生 Closing 回调。`Program` 的生命周期结束后解除 Activated 订阅并注销单实例键。

## 验收

`scripts/test-single-instance.ps1 -AppDirectory <应用目录>` 把应用完整复制到新建的隔离目录，两次启动都明确使用假服务；只记录本轮进程归属、可见窗口数量和退出结果，不查询真实 Emby 凭据，不读取窗口标题或内容。进程映像路径通过保留的启动句柄调用 `QueryFullProcessImageNameW` 获取，避免进程刚启动时 `MainModule` 尚不可枚举的竞态。finally 只处理确认归属的本轮进程，需要强制终止就判失败。

Debug 和 Native AOT 都通过：首进程保持 1 个可见窗口并在重定向后继续运行；第二进程没有可见窗口，约 60–140 ms 内以退出码 0 退出；首进程接受关闭后约 0.4–0.5 秒正常退出。AOT 连续五轮结果一致。

范围限制：只验证了假模式两次启动的重定向与有序退出。普通 Emby 模式复用同一注册机制，但没有用真实账号或播放会话跑过，不能作为普通模式前台焦点、真实播放退出上报或启动性能的验收结果。

## 历史失败与未定位的问题

一个早期 AOT 构建在首进程接受关闭后 6450 ms 才退出，退出码 `0xC000027B`。当时的排查记录涉及 `CoreMessagingXP.dll`，但没有证实根因，也没有证明是单实例注销时机造成的。之后的构建连续五轮未再出现，不宣称该异常的根因已经定位或证明消除。

运行时没有提前解除 `AppInstance`：仍在 `Application.Start` 返回后由幂等的 `Dispose` 解除事件并注销键。微软官方 [AppInstance.cpp](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/AppLifecycle/AppInstance.cpp#L433-L442) 中，`UnregisterKey` 只在互斥锁内清理键与键互斥锁；该源码不是本机精确二进制的根因证明。
