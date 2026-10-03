# P7 外部 MPV 后端

日期：2026-10-02（初版），2026-10-03（集成更新）。后端、设置页及外部播放面板已实现；Debug / Native AOT 的真实外部进程终止、停止补报与指纹重批自动验收通过，详情见 `P7-external-process-smoke.md`。下文保留初版实施与人工验收记录；界面接入已完成，真实 Emby 后台观察未冒充人工通过。

## 批准与复验

用户通过文件选择器明确选择 `mpv.exe` 后，`ISettingsService.ValidateExternalPlayerAsync` 才执行版本探测。生产实现使用 WinAppSDK `FileOpenPicker(AppWindow.Id)`；目录文本输入也可由验证器解析为其中的 `mpv.exe`，不搜索 PATH。只接受本地普通文件，不接受 UNC、网络盘、设备路径、备用数据流或经过 reparse point 的目录/文件。

版本探测固定使用 `--no-config --load-scripts=no --version`，3 秒超时；标准输出与错误输出各限制为 8192 字符，不把原始输出、路径或异常细节交给日志。只接受可解析的 MPV 0.38.0 或更新发行基线，可带 `v` 前缀及 nightly 后缀。无法证明兼容性的裸 git hash 不批准。[mpv 手册](https://mpv.io/manual/master/#command-interface) 说明 0.38.0 在 `loadfile` 第三参数插入 index，文件选项 map 移到第四参数；本实现固定使用这一协议。

批准记录是 `AppSettings.ExternalMpvApproval`：规范绝对路径、SHA-256、文件大小、UTC 修改时间和提取的版本。设置通过现有 JSON 源生成持久化；前端不能用普通 Update 伪造批准。版本验证与独立设置更新并发时合并最新音量/主题，后到的路径验证取消旧验证，过期结果不覆盖新设置。

每次创建外部引擎前只读复验指纹，不再次执行 `--version`。不匹配时清除批准、持久化 `PlaybackMode.Embedded`、发布 `ExternalPlayerStatus.Invalid`，此次播放使用内置引擎；再次启用外部模式须用户重新选择并验证。切换播放方式只影响下一次引擎创建，不替换正在播放的引擎。

验证/启动期间以 `FileShare.Read` 持有文件句柄，并用 `GetFinalPathNameByHandleW` 复核打开目标；启动连接后用 `GetNamedPipeServerProcessId` 确认管道服务器就是本次启动的 PID。批准覆盖所选 exe 的身份，不验证邻接 DLL，也不声称对本机敌对进程改变父目录的竞态提供完整隔离。Win32 调用均使用 `LibraryImport`。依据：[FileShare](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare?view=net-10.0)、[最终句柄路径](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew)、[管道服务端 PID](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeserverprocessid)。

## IPC 与进程

`ExternalMpvEngine` 实现后端 `IPlayerEngine`，复用 P3 的准备、候选回退、连播、轨道、上报、转码清理和发件箱逻辑。命令行只传固定启动参数和随机管道名；URL、认证头、恢复位置和每文件选项全部走 IPC。禁用配置、脚本、ytdl、默认输入绑定、媒体控制、OSC 和播放位置保存；丢弃进程输出，不保存可能含认证 URL 的日志。

`MpvIpcClient` 使用 UTF-8 + 换行帧，数字 `request_id` 对应等待者；启动连接 5 秒，`loadfile` 5 秒，其余请求默认 3 秒。等待写锁计入超时。迟到回复丢弃，写出一部分后取消则关闭连接，避免后续帧接在残缺 JSON 上。消息上限 1 MiB、深度 32；无效 JSON/UTF-8、截断、超限及断线转换为固定中文错误和单次 Shutdown。

直接使用 `Utf8JsonWriter`/`JsonDocument` 与类型化 `MpvValue`，无需反射序列化。补充有效的原始 UTF-8 非 BMP 字符，避免 mpv 不支持 JSON surrogate escape 的问题。初始化观察与内置引擎相同的 21 个属性；queue-overflow 重新读取这些属性。`loadfile` 形态为 `[loadfile, url, replace|append, -1, optionsMap]`，从成功回复的 `data.playlist_entry_id` 获取真实条目标识，而非合成编号。协议依据：[mpv JSON IPC](https://mpv.io/manual/master/#json-ipc)、[0.38.0 command.c](https://raw.githubusercontent.com/mpv-player/mpv/v0.38.0/player/command.c)。

只管理本次启动的进程。关闭先发 quit（500 ms），再等待退出（500 ms），必要时结束该进程树并有限等待（2 秒）；输出清理和退出观察同样有界。EOF/进程退出进入既有会话失败路径，对已确认开播的条目仅产生一次 Stopped，未播放的追加条目不会上报。

同批修复既有 P3 会话的手动选集问题：Replace 已清空 mpv 播放列表，不能依据历史 `loaded` 记录执行 `playlist-remove 0`。现在只在追加条目接入时移除上一项，并且每次接入最多一次；内置与外部播放都受益。

## 设置增量与界面接入

R-017：新增 `AppSettings.ThemeMode`，类型为 `SettingsThemeMode`（System / Light / Dark），旧 v1 文件默认 System。与 `Shell.ThemeMode` 分开命名，避免前端同时导入两个命名空间时歧义。前端映射值后用 Func 原子 Update 持久化。`GetCacheSizeAsync` 统计查询缓存与 mpv shader-cache 的文件字节数，跳过 reparse point；`LogDirectory` 返回本机日志目录。假实现返回 312 MiB 和空日志路径，不做磁盘 I/O。应用版本按 R-017 原请求放在 P8 版本方案处理。

R-018：`AddBackendServices(fake: true)` 未显式传 options 时读取 `MAMBO_FAKE_DELAY_MS` / `MAMBO_FAKE_FAILURE_RATE`，与 FakeLab 共用解析函数。延迟限制 0–10000 ms，失败率限制 0–1；非法输入沿用默认值，显式 options 优先。

前端只调用 Contracts：先 Validate，再将 PlaybackMode 更新为 External；监听 Changed 展示 Validating/Approved/Invalid，文件变化提示重新批准。`SessionSnapshot.EngineKind.External` 时显示“正在外部播放”面板；控制仍走会话公开命令。假模式始终使用 Demo，不执行所选 exe。详细接入请求见 `docs/handoff/requests.md` R-019。

## 自动验收

- `dotnet build -p:Platform=x64 --no-restore`：零警告、零错误。
- `dotnet test --no-restore`：326/326 通过，无跳过。新增覆盖批准边界/最低版本/指纹变化/取消/超时、设置验证并发、假参数，以及假 IPC 协议与真实会话。
- 假 IPC 的真实会话回归覆盖候选失败后回退、控制、追加/下一集、季末、严格 Playing/Stopped 顺序、真实倍速、断线停止和手动选择非追加条目；未访问真实 Emby 或执行未知 exe。
- Native AOT 发布零警告、零错误；`pwsh scripts/test-external-ipc-lab.ps1` 及 `-Aot` 均通过，实际执行命名管道 PID 的 LibraryImport、21 属性初始化、文件选项、真实条目标识、Unicode/嵌套节点和 quit。
- `pwsh scripts/test-playback-lab.ps1 -NoBuild` 及 `-Aot` 均通过，复测本地真实 libmpv composition、连播、重试、停止上报和解绑。P4 默认外壳上线后，脚本显式使用 `--video-lab --p3-smoke` 进入诊断窗口。
- `pwsh scripts/test-fake-lab.ps1 -Aot` 通过，分页、图片、播放和关闭保持正常，未加载 libmpv。

## 待用户人工验收

PLAN P7 明确要求：【需用户】播放中杀掉 mpv.exe，仍然会上报 Stopped；替换 mpv.exe 后会要求重新批准。本次没有代办或标记这些步骤已通过。

可在当前发布包验收后端：运行 `publish/aot/Mambo.exe --video-lab`，点击“选择并批准外部 MPV”选择自己的本地 mpv.exe（0.38.0+），恢复 Emby 会话后按 itemId 播放。确认诊断显示外部 MPV，手动结束该外部进程，检查 Emby 活跃播放结束。关闭播放后替换该 exe，再次发起播放应提示重新批准并切到内置；重新选择验证后才恢复外部播放。服务器信息只在本机工具输入。最终设置页与“正在外部播放”面板的接入已完成；真实 Emby 后台观察仍待人工验收。

P0 的 GPU composition Section/Mutant 增长及 DPI/多显示器/按钮/光标/抓包遗留项保持原结论；本次 P7 自动验收不覆盖它们。
