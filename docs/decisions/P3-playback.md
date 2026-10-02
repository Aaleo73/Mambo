# P3 播放引擎与会话

日期：2026-10-02。状态：实现、自动验证与真实服务器人工验收通过；用户回复“验收通过，继续”，PLAN 的 P3 已勾选。

## 实现与边界

`BackendServices.AddBackendServices(fake: false, scheduler: …)` 已注册真实 `PlaybackCoordinator`、准备层和 `LibMpvEngine`。Contracts v1 的公开签名保持不变；前端只使用 `IPlaybackService` / `IPlaybackSession` 和 `VideoSurface.Attach(IPlaybackSession)`。Core 的引擎接口、DTO、候选 URL、HTTP 头和原生类型不进入前端。

播放目标每次从服务器解析。剧集优先 NextUp，失败后按季/集顺序寻找未看条目；连播计划读取失败时退化为单集。每个条目重新请求 PlaybackInfo，使用服务器提供的 PlaySessionId。候选按媒体源与直连/转码规则排序；直连能力明确关闭时优先转码。候选解析或未确认开播的 END_FILE(error) 会尝试下一候选；预取下一集失败时跳过，连续失败最多三集。

`PlaybackSession` 用单读者 inbox 串行更新状态，网络准备工作在 actor 外进行并校验代际。真正收到 FILE_LOADED 后的首次 PLAYBACK_RESTART 才确认开播。只保留当前与下一集的原生播放列表；START_FILE 的真实 entryId 是切集依据。属性事件生成不可变快照，位置通知最多每 100ms 一次；20 秒打开提示与 10 秒上报计时各自管理。暂停和 seek 立即上报，倍速、音量、轨道和缓冲区间来自实际引擎状态。保存的音量及本会话设置的倍速/静音在新引擎 load 前应用，切集与重试不被初始默认属性覆盖。

`LibMpvEngine` 复制原生事件和节点，命令等待原生确认，loadfile 使用 mpv 返回的 entryId。类型化值、LibraryImport 和 JSON 源生成可用于 Native AOT。mpv 日志默认关闭；错误只保留安全文案、类型和 HRESULT。

URL 预解析限时、限次、限跳转，按协议、主机、端口及服务器路径边界决定是否携带认证；跨边界跳转永久清除认证与 RequiredHttpHeaders。每个 loadfile 都显式指定 HTTP 头，避免上一片源的状态继承。文本外挂字幕下载为有大小限制的临时本地文件，单独检查重定向并在结束时删除。

## 上报、关闭与画面生命周期

Playing / Progress / Stopped 共用有序上报队列。Stopped 进入网络队列时立即开始原子落盘，并在持久化完成后才发送；旧集停止不会被新集开播超越。停止记录还保留真实倍速、播放方式、暂停/音量/轨道与连播位置，离线重建后补发保持最终状态；旧 v1 文件使用兼容默认值。退出 HTTP 共用 1.5 秒预算，取消任何条目正在等待的请求；失败的停止记录保留在账号隔离的 StopOutbox，现有五分钟重试会继续补发。转码候选回退、未确认播放、未开始的预取条目、重试与正常停止均尽力清理服务器转码会话。成功停止及离线补发后，通过账号代际保护的消息使相关查询失效。

关闭先等待 END_FILE，最多两秒，再持久化停止记录、释放字幕，并等待 UI 解绑交换链后销毁引擎。`PlaybackVideoBridge` 使用引擎持有的交换链引用；面板绑定持有自己的 COM 引用。重试仅解绑旧引擎，保留同一会话的桥接订阅，以便新引擎重新绑定；控件 Detach / Unloaded 或会话 Closed 才释放整个桥接。HDR、像素尺寸和硬件解码设置在后端桥接中处理。

Video Lab 的关闭入口先 `Task.Yield()`。实际冒烟曾在关闭回调同步再次调用 Window.Close 时以 C000027B 退出，延后关闭后 Debug / AOT 均正常退出。P4 外壳应在 Window.Closing 返回后排队执行最终 Window.Close，即使 CloseAsync 已同步完成；请求见 R-016。

## 自动验证

全套测试覆盖 FakeEngine 候选回退、手动/自动连播、季末、打开中关闭、准备与关闭竞态、旧事件代际、失败重试、转码清理，以及停止持久化、上报顺序、退出预算和账号隔离。Player 项目另外有六项真实无头 libmpv 测试。

最终 `dotnet test --no-restore`：237/237 通过，0 失败、0 跳过（Core 231 项，Player 6 项）。完整 Debug 构建与详细裁剪诊断的 App Native AOT 发布均为 0 警告、0 错误。Debug / Release 的 locked-mode 还原通过，锁文件未漂移。

Debug / Native AOT 的 composition 冒烟使用本地 4K HEVC HDR10 样片与内存 HttpMessageHandler，经过真正的 mpv 引擎和 SwapChainPanel。检查暂停、1.5 倍速、seek、切集、引擎退出后的同会话重试、画面重新绑定、停止上报顺序、发件箱清空和解绑后退出。它不读写 Windows 凭据、不访问用户服务器，也不代替人工观感验收。报告在忽略的 `artifacts/p3-playback-debug.json`、`artifacts/p3-playback-aot.json`。

```powershell
dotnet restore --locked-mode -p:NuGetAudit=false
dotnet build -p:Platform=x64 --no-restore
dotnet test --no-restore
pwsh scripts/test-playback-lab.ps1 -NoBuild
dotnet publish src/Mambo.App -p:Platform=x64 -p:PublishProfile=Aot -p:TrimmerSingleWarn=false --no-restore -o publish/aot
pwsh scripts/test-playback-lab.ps1 -Aot
```

Claude 的 R-015 同批修复：中央启用 `UseMicrosoftTestingPlatformRunner`，使 xUnit v3 生成的入口与 global.json 的 MTP 运行器一致；干净原 P1 基线的 174 项测试及加上持久化取消测试后的 175 项均被真正发现并执行。`dotnet test -p:Platform=x64 --no-build` 也已验证可用。入口约定见 [xUnit v3 的 Microsoft.Testing.Platform 文档](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform)。

同批调查捕获 Windows File.Replace 的 80070497 / ERROR_UNABLE_TO_REMOVE_REPLACED（1175）；此错误保持原文件与替换文件的名称，可做 25/50/100ms 有界、可取消重试。1176 / 1177 不按此处理。AtomicFile / Outbox 边界测试额外重复 20 轮、共 380 次通过；取消测试确认原件保留、临时文件删除。原 P1 未记录 HRESULT 的写入失败仍不能追溯断言为同一原因。错误语义见 [Microsoft ReplaceFileW 文档](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)。

## 真实服务器验收【需用户】

2026-10-02：用户确认以下 P3 验收通过，包括位置/倍速上报、强制转码及停止清理、整季连播、断网后停止补发。P0 原有未通过与待测遗留项不因本次确认改变。以下步骤保留供复验。

1. 运行 `publish/aot/Mambo.exe`，在 Video Lab 点“恢复 Emby 会话”，输入本机 Emby 条目的 itemId，再点“按 itemId 播放”。沿用 P1 保存的 Windows 凭据；恢复失败时用 P1 的本地登录工具重新登录。
2. 选择电影或单集，暂停、拖动进度、切换 1.5 倍速，再停止；在 Emby 后台核对 Playing / Progress / Stopped 的位置及倍速。
3. 验证强制转码片源可播，停止后服务器转码会话已清理。按一个季或其中一集的 itemId 启动，让同季连续播放至末集结束。
4. 断网后停止播放，再恢复网络，等待发件箱的五分钟重试，核对停止进度补发成功。需要时可重启并恢复账号触发重新处理；无须删除数据或凭据。

PLAN §12 的真实服务器回归还需要直链、302 与强制转码场景；若服务器暂不具备某项条件，记录未测项即可，不将合成测试写成人工通过。只回复通过项或安全错误文案，勿把地址、令牌、密码或抓包原文发进聊天或仓库。P0 仍保留原有资源增长与待验收项，P3 自动回归不改变那些结论。最终播放页面、导航和控制层视觉由 Claude 在 P4 / P5 接入；外部播放器属 P7。
