# P8 本地打包交付

日期：2026-10-03。状态：134251 交付候选已通过 P8 归档、安装目录完整 UI、首次安装、同版本升级、卸载及双基线用户文件保留。统一显式异步关闭修复已通过 Debug / AOT 各 10 次 IPC 正常退出、P3 与 Fake 两入口回归，实际自绘关闭按钮的取消、重入、确认及清理检查均通过；PlaybackRefresh 也全部 Passed。当前主机本地交付验收完成。最终已提交源码按固定流程归档，路径、大小和哈希以交付元数据为准。含当前 libmpv 的二进制公开分发仍被完整对应源码缺口阻挡。

## 发布输出

`pwsh scripts/publish.ps1 -Version 0.1.0` 先 locked-mode restore，再发布 Windows x64、Release、Native AOT、.NET 与 Windows App SDK 自包含产物。版本参数同时传入产品版本、数字文件版本、程序集说明版本。版本不依赖前端硬编码。

每次使用新的 `publish/releases/<version>/<timestamp>/app` 目录，拒绝已有目录，不递归删除旧发布物或任何用户目录。核验 EXE、PRI、XBF、WinUI / Windows App Runtime DLL、四个字体的哈希与 lock 中 libmpv SHA-256；libmpv 固定在 `mpv/`。附根 LICENSE、THIRD_PARTY_NOTICES 与 LICENSES，生成逐文件字节数/SHA-256 的 release-manifest，然后用 ZipFile 创建包含隐藏文件的便携 ZIP。另调用 `make-source-archive.ps1` 提供当前 Mambo 工作区源码 ZIP（逐文件哈希、HEAD/未提交状态），附准确修订并以 SHA-256 锁定的 mpv、FFmpeg、shinchiro 构建脚本源码 tar.gz。脚本返回路径、ZIP 体积及校验值；未自动签名或发布。

## 安装器

`-Installer` 使用已安装的 ISCC.exe，可显式给出 `-IsccPath`；找不到编译器时保留已生成便携包并给出中文失败原因。用户随后明确授权所有后续步骤，包括安装打包工具；root 负责工具安装与集中编译，脚本本身不暗中安装软件。

`installer/Mambo.iss` 使用 `PrivilegesRequired=lowest`，安装到当前用户 `%LOCALAPPDATA%/Programs/Mambo`，最低 Windows 10 22H2、Windows x64，中文界面。升级通过 Inno Setup Restart Manager 请求关闭被安装目录下的 Mambo.exe，不使用 `force`，不扫描/终止其他进程或外部 mpv。应用的有序退出负责停止播放和保存进度。默认提供开始菜单快捷方式，桌面快捷方式由用户勾选。

已安装的 Inno Setup 6.7.3 位于当前用户 `Programs/Inno Setup 6`。root 从[官方 issrc 标签 is-6_7_3 的语言文件](https://github.com/jrsoftware/issrc/blob/is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl)取得 `installer/ChineseSimplified.isl`；安装器用此仓库相对路径，避免依赖当前编译器是否附带简体中文翻译。原文件保留维护者归属；Inno Setup 官方许可已由 root 收录到 `LICENSES/Inno-Setup-license.txt`，不覆盖这两份原文。

卸载仅由 Inno 的安装文件记录清理程序文件；没有 UninstallDelete 用户数据规则，保留 `%LOCALAPPDATA%/Mambo`。不加入应用图标设计，沿用用户已确定的暂缓决定。

依据：[PrivilegesRequired](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)、[CloseApplications](https://jrsoftware.org/ishelp/topic_setup_closeapplications.htm)、[CloseApplicationsFilter](https://jrsoftware.org/ishelp/topic_setup_closeapplicationsfilter.htm)。

## 许可证据与源码

记录来自本仓库 lock、官方原生构建修订、DLL 内版本标识和官方 NuGet 包，详见 THIRD_PARTY_NOTICES。当前 Windows App SDK 2.5.1 NuGet 许可是 Microsoft Software License Terms，已纠正设置页的 MIT 占位断言；ML 子包另附条款。原文包含 Microsoft、.NET、Toolkit 完整 NOTICE、小米 MiSans 官方 PDF、GNU 与 Apache 文本。

mpv 锁定修订 304426c390901436fb1d4a63efbd582ae80c88f4；DLL 内 FFmpeg 标识经官方 API 对应到 2576e09434d8026aab1769481b7b2fb43aa567c3；构建标签20260610对应5efd298cb51513c2410e4e9029b5e56b83c2aaac。原生配方包含 GPL/version3 配置与大量静态依赖，递归清单包括条件分支与头文件，附已获取的官方上游许可快照。未伪造每个组件的精确二进制修订。

仓库尚无公开远端；完整原生对应源码、构建时每个依赖修订/补丁与逐文件许可未齐。当前开发源码 ZIP 可作为私人开发归档；当前便携包/安装器仅供本地验收，暂不能公开分发。正式公开发布前需补齐匹配源码归档与下载入口，或改为自行构建并锁定全部原生输入。

对公开 CI 的核对：[原运行页面](https://github.com/shinchiro/mpv-winbuild-cmake/actions/runs/27243718577)确认5efd298、成功、无当前可下载 Artifacts。锁定工作流在构建前执行 `ninja update`，日志产物保留1天；日志 Gist 通过 amend/force 覆盖最新版本，不是历史修订清单。[20260610 发行资产列表](https://github.com/shinchiro/mpv-winbuild-cmake/releases/expanded_assets/20260610)没有全部静态依赖的对应源码；锁定 mpv 包装配方只复制程序、手册、DLL/导入库与头文件，不生成完整依赖修订清单。当前证据无法恢复全部历史原生依赖修订，此缺口无法仅由三份主工程源码压缩包消除。

清单含83项配方依赖，取得82项可读许可快照（部分仅头文件或声明入口，状态逐项说明），`sources.json` 索引102份官方原文（含安装器许可）。Fontconfig 网页/发行下载仍返回访问挑战，但官方 Git 协议可读，已取得修订 d416ada7b20a1cdd5050b60a7a1bbdb3d9ddd2dc 的完整 COPYING 并保留逐文件/Unicode 归属；它是许可快照，没有冒充 20260610 二进制匹配修订。

Opus DNN 官方165,994,834字节模型归档通过配方 SHA-256，但归档内没有 LICENSE/COPYING。另下载官方 Opus 1.6.1 发行源码并按发行页 SHA-256 核验，附 BSD COPYING / DNN README；21份模型生成 C/头文件中7份逐字节相同，其余14份及 `.pth` 权重的许可范围仍未确认，详见 `LICENSES/opus-model-license-evidence.md` 和逐文件对比 JSON。因此未给整个模型包套用 MIT 或 BSD，也没有将 Opus 1.6.1 视为 DLL 的已确认修订。

锁定 URL/哈希的 libiconv、libopenmpt、Xvid、LZO 归档许可已核验；其他无法锁定二进制 Git 修订的许可明确注明发行/默认分支快照。许可快照数量不等于构建组成或完整对应源码已验证。

## 已生成的本地候选与归档校验

最新交付元数据为 `artifacts/release-candidate-delivery.json`，原始本轮元数据另保留为 `artifacts/release-candidate-close-entry.json`，二进制发布目录为 `publish/releases/0.1.0/20261003-134251-380/app`。该候选包含 PlaybackRefresh 与统一关闭入口修复，已通过下述关闭回归、完整 UI、归档和安装验收。本地通过不表示公开分发原生对应源码要求已满足。

| 产物 | 字节数 | SHA-256 |
| --- | ---: | --- |
| Mambo.exe | 22,148,096 | `d846bda50d22b206de12507f1e85959655bfaa953b58b5e4fc4c4fa68c7ffaef` |
| 便携 ZIP | 147,222,998 | `5849e5e562daf3a8dc2529a127e94a37008e0c4478211fb1ba1d2a0aa45ba359` |
| Inno 安装器 | 102,978,152 | `94448c608046533a30988e98be4ba5eec7c945711bf3abf096e0a687f8ba2b53` |

`artifacts/release-archive-validation-close-entry.json` 的状态为 Passed，实际核验了便携 ZIP 的 437 个文件项：436 个发布清单文件逐项长度、SHA-256 匹配，另一个为与发布目录同哈希的 `release-manifest.json`；缺失、额外、重复、不安全及禁止目录项均为 0。清单自身 SHA-256 为 `817797f7b242cb1735ab95f3b0cc07a7062e3a999952d35c161c48fcd5931b79`，已与新安装报告匹配。ZIP 整体大小和哈希与候选元数据匹配；安装器长度及 `InstallerSha256` 与元数据和实际安装报告均匹配。

134251 打包时生成的源码 ZIP 是生成时工作区快照，实际核验了 413 个文件项：408 个 Mambo 文件、3 个原生主工程归档、2 份说明/来源清单。每个 Mambo 项的长度、SHA-256 与随包 `source-manifest.json` 匹配，3 个原生归档与随包 lock 的哈希、长度和修订匹配。说明与清单另计算哈希，整个 ZIP 与生成时元数据 SHA-256 匹配；这两份文档没有伪称在清单中自列哈希。不存在已取消的 clean-windows、artifacts、publish、bin、obj、Git 元数据或 TestResults 项。

该快照的来源清单记录未提交工作区。核验当时 408 项全部与当前工作区匹配，`sourcesMatchCurrentWorkspace=true`，没有不同、缺失、当前 git 文件未收录或审计期间修改；但工作区仍为 dirty，本文随后也有更新，不将此快照称为最终已提交源码。

最终源码交付流程固定为：本地提交完成后，只重新归档已提交源码至同一 release 根的 `sources-final/`，更新忽略的 `artifacts/release-candidate-delivery.json` 中 `SourceZip`、`SourceBytes`、`SourceSha256`，再核验随包 `source-manifest.json` 的逐项长度、SHA-256、原生 lock 与提交标识。最终路径、大小、哈希及对应归档校验报告以该元数据为准；本文不嵌入最终源码 ZIP 自身哈希，避免文档进入 ZIP 后产生递归依赖。生成时源码 ZIP 保留为历史快照，已验收二进制不因源码归档改变。最终 Mambo 源码和三份原生归档仍不等于完整 libmpv 对应源码。

## 当前主机已实测安装结果

最新安装轮次 `artifacts/installer-validation/b86c1004e97e4039a3b5cf3ba337e479/result.json` 的 `status=Passed`、`LifecyclePassed=true`。首次安装 436 个清单文件及清单本身匹配；安装目录 `ui-smoke.json` 也为 Passed，RunId 为 `eae600a5ff95416194a2913132a707a4`。50 次会话关闭、焦点恢复及实际开关帧循环均为 50，UIA 5 项通过，`RetainedPlayers=0`、`RetainedSurfaces=0`，没有加载 libmpv；425ms 对象收集样本经过 4 个清理帧，player/surface 均为 0。`CaptionCloseCancelled`、`CaptionCloseReentryIgnored`、`CaptionCloseConfirmed`、`CaptionCloseCleanupCompleted` 全部 true，实际自绘关闭按钮经取消、重入、确认后必须完成统一清理才能报告。`PlaybackRefresh.Passed=true`、17 项全 true，未访问真实服务器或账户。本轮升级实例 PID 55276 在 703ms 检测到窗口，升级请求使该实例正常退出，退出码为 0，升级后文件再次全部匹配；没有强制清理。

卸载程序正常结束，发布文件、清单、卸载器 exe/dat 和注册项全部清除。安装前 356 个原用户文件在卸载前和结束时均无删改；固定的卸载前 357 文件快照包含本轮 marker，卸载后全部长度、SHA-256 与文件集合相同。`originalUserFilesPreserved`、`preUninstallUserFilesPreserved`、`uninstallUserDataPreserved` 均为 true。本轮 marker 在卸载后保留相同字节，最后只删除该 marker；没有失败后的强制清理或半成品注册残留。文件身份与哈希仅保存在 RAM，没有写出私人文件内容或名称。

旧 125913 元数据已另存 `artifacts/release-candidate-125913.json`，其 `ee9d0e81ce2b40368f3e0782012e12fe` 安装轮次仍保留 Passed、570ms 正常升级及 356 / 357 文件保留事实；独立窗口退出回归的失败也仍保留，不作为最新交付证据。较早的 123901 候选见 `release-candidate-verified.json` 和 `34b4b28678d94440b24a52898c867665` 安装轮次，窗口就绪为 546ms，不含后加入的 PlaybackRefresh。更早的 `211fcecd26bf41a9b98f6355fcdb8a6c` 轮次仍为 Failed：player/surface 各留存 2、安装前基线数量 21 且 `originalUserFilesPreserved=false`，没有卸载前快照，不能将变化归因于卸载或说 21 个文件全部改变。新通过轮次不追认或改写历史失败。

## 原生退出故障与关闭入口修复回归

125913 与永久 Dispose 改动后的 131910 候选均未通过后续原生退出回归；131910 元数据为 `artifacts/release-candidate-lab-dispose.json`，发布目录为 `publish/releases/0.1.0/20261003-131910-229/`。永久 Dispose 单独没有解决故障，不将 131910 标为新通过交付候选，也不把已通过的 IPC 协议报告等同进程正常退出。

WER 小 dump 缺少 stowed payload，只能显示 CoreMessaging failfast 栈，既不能读出 `0x8000FFFF`，也不能定位真正调用点。`0x8000FFFF` 来自后来约 562MB 的完整 dump 中的 STOWED0。再后续的 CDB live first-chance CLR 栈记录在 `artifacts/native-exit-helper/ipc-refresh-after-close-proof.txt` 及同名 JSON，结合相同二进制的反汇编，才精确定位 UI 线程 `VideoLab.RefreshDiagnostics+0x226` 对应 `Slider.Maximum`，调用者为仍活跃的 `DispatcherQueueTimer`。窗口已由 `Window.Close()` 销毁 XAML，计时器继续刷新控件，最终触发 `0xC000027B`；该轮新报告仍显示 UI Passed，但进程退出码失败、二进制哈希未变。因此故障属于窗口销毁与清理顺序，不是 IPC 协议错误或未执行的新 fixture。

官方 [Window.Close](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.window.close?view=windows-app-sdk-2.0) 定义为销毁窗口；[AppWindow.Closing](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindow.closing?view=windows-app-sdk-2.0) 针对系统关闭入口，且 AppWindow.Destroy 不触发该事件，不能假定直接 Window.Close 总会先进入可等待的 Closing 清理。134251 已统一显式异步关闭入口，先停止计时器和回调、完成资源清理，再销毁窗口；MainWindow 自绘关闭按钮也走同一清理路径。

`artifacts/close-regression-debug/summary.json` 与 `artifacts/close-regression-aot/summary.json` 均为 Passed：Debug / AOT 各 10 次 IPC 正常退出码 0，P3、Fake 命令行和环境变量入口均通过。完整 UI 报告 `ui-debug-close-entry.json`（RunId `0d176b3e9b36419f8a278e929169bf46`）与 `ui-aot-close-entry.json`（RunId `878fb055ece44f8abde542060c4bf6b5`）也均 Passed；对象收集样本分别为 439ms / 419ms、4 个清理帧后留存均 0 / 0，四个 CaptionClose 布尔值全部 true，UIA 5 项、50 次关闭及 PlaybackRefresh 17 检查均通过。该证据与新安装目录完整 UI 一起覆盖正常窗口销毁顺序，不以旧候选协议报告替代实际正常退出。

2026-10-03 用户明确取消专用 Windows 虚拟机验收并要求删除。已停止该工作，确认没有本次 QEMU 进程运行；后续仅验证当前 Windows 主机的安装、升级与卸载。本机已有 SDK/运行时，因此报告不宣称经过干净 Windows 10/11 验收。

清理尚未执行成功：执行工具自动审批以 `blocked by policy` 拒绝了递归目录删除及具体单个虚拟磁盘文件删除，未提供更详细理由。尚存的本次专用目录为 `artifacts/clean-windows`（约 24.65 GB，含虚拟磁盘、安装镜像、QEMU/MSYS2 专用下载与工具）和 `scripts/diagnostics/clean-windows`（12 个部署/验收脚本文件）。两目录均已核对位于本仓库内且没有重解析点；不再运行或继续开发其中内容，也不把它们纳入最终交付。

## 本地交付与公开分发边界

完整 AOT / ZIP / Inno 编译、归档校验、当前主机双基线安装验收、PlaybackRefresh 及统一关闭入口的 Debug / AOT / 安装目录回归均已通过，P8 当前主机本地交付验收完成。125913 / 131910 的原生失败及根因证据保留为历史；134251 的正常退出与实际关闭按钮清理证据作为最新结论。最终已提交源码使用上述固定归档流程及忽略的交付元数据。

P8 的通过范围是上述打包、归档、安装和关闭流程，不扩展为性能或全部阶段通过。新候选单实例轮次 `b19a513633c247c3947ff282097a1694` 为 Passed；真实启动轮次 `e24cf88c865e4cafa666c4b14cc16cb0` 仍为 ResultsFailed，三轮窗口为 881 / 865 / 774ms、缓存首页为 972 / 919 / 827ms，均未达到 600 / 800ms 目标。性能与阶段状态仍由 PLAN 和相关决策记录单独维护。

- 当前主机首次安装、安装目录完整 UI、升级正常退出、卸载及用户文件保留均已通过；最新证据对应 134251 交付候选。
- 生成时源码 ZIP 保留为历史快照；最终提交源码包的路径、大小、哈希只引用忽略的交付元数据。
- 专用干净 Windows 虚拟机验收已按用户要求取消；不扩展本机结果为干净系统验收。
- 可选签名：未请求或执行。
