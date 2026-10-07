# P1 Core 平台层

日期：2026-10-02。真实平台实现位于 `Mambo.Core`，Windows 凭据桥接位于 App 的 `Platform/`。界面只解析 `Contracts`；`BackendRuntime` 和 DTO 不进入 ViewModel。

## 组合与生命周期

`BackendServices.AddBackendServices(fake: false, scheduler: …)` 以显式工厂注册会话、资料库、图片、设置和账号偏好。P1 交付时的真实播放为 Preparing → Failed 的准备会话；P3 已接入真实 libmpv 播放，见 [P3 播放引擎与会话](P3-playback.md)。离线 Preview 保持相同替换确认契约。

外壳在启动时解析 `AppShutdownCoordinator`（同时挂接 WinUI 异常日志），再调用一次 `ISessionService.RestoreAsync`。退出时先 await coordinator.CloseAsync，再销毁 DI 容器；关闭包含播放、停止发件箱预算、查询快照和音量写入，以及图片/发件箱 I/O 的异步释放。

认证与图片使用独立 HttpClient/handler，禁止自动重定向和 Cookie，HTTP/2 可降级。API 不跟随重定向；图片手动限制同主机、同端口及允许的协议升级，认证只放请求头。JSON 使用 `EmbyJsonContext` 源生成，读取限制 16MB，Filters 同时兼容字符串和 Name 对象。继续观看与 Latest 分别按 `docs/SPEC.md` A.1 的字段集请求。

`RequestScheduler` 保留 metadata 的前台槽位及独立 reliability 通道，截止时间从排队开始计算；只对幂等请求重试。账号取消终止旧观察和请求。登录先保存凭据再公开账号；恢复在网络校验前提供账号作用域供磁盘缓存读取，短暂离线保留凭据；401/403 只使对应当前账号失效。注销先关闭播放和刷新旧账号停止记录，再清本地状态，远端失败由 LogoutResult 告知界面。

## 本地数据

- `WindowsCredentialStore` 用 `LibraryImport` 调用系统凭据 API，目标 `Mambo:emby-session:v1`。仅保存服务器和账号身份、令牌，不保存密码；写入缓冲结束后清零。目标内容上限及本机持久化方式按 [Microsoft CREDENTIALW 文档](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw)。
- 设置采用 schema v1、原子临时文件和备份；已有文件一律 File.Replace，首次创建用 File.Move，读者允许 Delete 共享。共享冲突只做 25/50/100ms 有界重试，可取消，持续锁定仍返回安全 PersistenceFailed 和 HRESULT 诊断值。损坏主文件可从 `.bak` 恢复，修复时保留有效备份。首次 DeviceId 持久化且不可修改，Func 更新避免音量和设置页互相覆盖。资料库偏好按账号及库隔离。
- QueryCache 合并同键读取、SWR 保留旧值、通过异步 UI 通知共享更新。白名单首页/库首屏快照按作用域哈希命名，静止 2 秒后落盘，保留 7 天、总量 20MB；损坏或不符合领域结构的数据不进入 UI。
- 图片缓存采用 SHA-256 键：64MB 压缩字节 LRU、单条 16MB；只有带 Tag 的图片落盘。磁盘 512MB、每写 32MB 检查并裁剪到 90%；魔数和版本校验，原子替换。6 槽 Hero/Visible/Prefetch 排队，重复读取合并，取消仅撤回对应等待者。
- StopOutbox 在发送前原子落盘，按服务器和用户隔离、GUID 去重，至少一次投递；401/403 等待重新认证，可重试状态保留，其他 4xx 死信删除并写安全诊断。每 5 分钟及退出时重试，预算包含排队和网络等待。文件不含令牌、密码或媒体标题。
- 日志使用 Serilog 4.3.0 / File sink 7.0.0，按天滚动、7 份、单份 10MB。构造字面量 LogEvent 绕过任意对象反射解构；异常只记录类型和 HResult，不记录异常文本。最终输出再次经 UrlRedactor。默认不启用 mpv 自身日志。

没有复制旧项目文件或资产；测试服务器和认证值全部运行时生成，域名使用 `.invalid`，不访问真实服务器。

## 验证与用户验收

自动验证使用合成 `HttpMessageHandler`、内存凭据和隔离数据目录，不调用实际的 Windows 凭据读写，也不访问真实服务器。Debug 与 Native AOT 都验证过登录、库列表、缓存重载、设置、发件箱、注销、日志落盘和令牌不落盘。

2026-10-02 用户确认真实 Emby 登录、列出视频库、重启恢复和凭据管理器目标 `Mambo:emby-session:v1` 检查通过。当时用的一次性命令行验收工具 `Mambo.CoreSmoke` 已从仓库移除；需要复验时直接用应用登录。

## 实现中确认的坑

验证中曾出现一次停止记录本地写入失败，原始异常没有 HRESULT，未确认该次的直接成因。新增边界测试在本机明确复现：File.Move(overwrite) 在旧读者允许 Delete 共享时仍返回 80070005，而 File.Replace 成功；行为与 [.NET runtime issue 114230](https://github.com/dotnet/runtime/issues/114230) 一致。现已用原子替换修复，并验证旧读者快照、短暂锁重试、长期锁失败保留原数据。Win32 错误分类参考 [Microsoft 系统错误码](https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--0-499-)。

测试项目必须在中央 `Directory.Build.props` 显式启用 xUnit v3 的 MTP 入口，否则 `dotnet test` 会「运行零个测试」并以退出码 5 结束。中央 `PublishAot` 属性保证 Debug / Release 的锁文件图一致；`.gitattributes` 固定 libmpv 头文件为 LF，避免重新获取后出现只有行尾差异的改动。

后续回归又捕获 File.Replace 的 80070497 / Win32 1175，按 ReplaceFileW 保持文件名称的错误语义补充有界重试，并验证取消后原件仍在、临时文件删除；原 P1 没有 HRESULT 的失败不追溯断言为此原因。详情见 P3 记录。
