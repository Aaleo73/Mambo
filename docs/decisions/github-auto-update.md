# GitHub 托管与自动更新

日期：2026-10-05。

用户要求上传 GitHub 并增加自动更新，随后指定新建公开仓库。目标为 `Aaleo73/Mambo`。本次上传授权覆盖 AGENTS.md 与 PLAN 原有“不推送远端”的日常约定；后续任务仍按其约定执行。P0/P2 的确认已有记录，本次不重新开启这些关卡。

## 更新方式

沿用已验收的 Native AOT、自包含应用与 Inno Setup，避免更换安装目录、卸载机制和用户数据位置。设置页增加自动检查开关、手动检查、下载进度、取消下载、下载并安装及发行页面。启动后延迟五秒检查公开稳定版；检查失败只更新状态，发现版本时用通知引导进入设置页。演示模式没有更新来源，不访问网络。

UI 只依赖 `IAppUpdateService` 与 Contracts 中的读取模型。GitHub 客户端在 Core 实现，在 Composition 注册。版本和仓库通过程序集元数据提供，发布脚本接受 `-UpdateRepository`；不在客户端放入 GitHub 令牌。

检查 `GET /repos/{owner}/{repo}/releases/latest`，只接受 `vX.Y.Z` / `X.Y.Z` 稳定版和准确命名的 `Mambo-X.Y.Z-win-x64-setup.exe`。版本按数字比较，相同版本不重复安装，不降级，当前预发布版本可以升级到同号稳定版。跳过草稿/预发布不是把不完整发行版当作最新版，而是显示发行信息错误。缺少安装包或 SHA-256 时显示明确状态。

HTTP 与下载客户端独立于 Emby；不发送登录凭据，不使用 Cookie。只允许 HTTPS、指定仓库下载路径以及 GitHub 官方资产域名的有限跳转；元数据最大 2 MiB，安装包最大 512 MiB，检查限时 20 秒，下载限时 10 分钟。下载到本次独立临时目录，完整字节数与 GitHub `digest` 中的 SHA-256 都一致后才改名为 `.exe`。启动前再次校验；错误文案不保留传输异常、签名 URL 或内层异常。取消/失败清理本次临时文件。

升级由用户点击后打开安装向导，使用既有 Restart Manager 与应用有序退出，不在播放中静默替换文件。保留用户设置和凭据。便携版通过安装包更新时转为安装版，在确认文案中说明；本次不实现便携目录覆盖或差分更新。

API 依据：[GitHub Releases REST 文档](https://docs.github.com/en/rest/releases/releases)。SHA-256 来源为 GitHub 上传时生成的资产摘要，依据：[GitHub 发行资产摘要说明](https://github.blog/changelog/2025-06-03-releases-now-expose-digests-for-release-assets/)。这提供下载完整性与仓库来源验证，不冒充独立代码签名。

## 发布流程和现有缺口

GitHub Actions 的 push / PR 工作流使用锁定 SDK、锁定还原、着色器校验、libmpv 校验、构建与测试。`vX.Y.Z` 标签工作流先执行同样检查，再生成源码发行版与 SHA256SUMS。Action 版本固定为核实过的提交。

`LICENSES/release-readiness.json` 当前 `binaryDistributionApproved=false`，对应 P8 已记录的完整原生对应源码缺口。当前工作流不会公开上传含 libmpv 的安装器、便携包或构建产物；源码发布无需阻塞。发布脚本同时检查该记录，不能单靠工作流参数绕过。补齐准确依赖修订、补丁、源码及逐文件归属证据后，才能将审查结论落成已批准状态并开放二进制发布。

因此，更新功能代码完成不代表已有可安装的线上更新。首次公开二进制与从旧版升级到新版的真实安装链仍依赖上述缺口的处理和两个不同版本的 Releases。

## 验证

- 覆盖数字版本比较、同版/旧版、草稿/预发布、资产缺失/重复、摘要缺失/错误、空字段、非法来源、请求限流/失败、元数据大小、下载跳转、完整性、损坏/截断/超长、取消清理和无凭据请求。
- 既有播放上报测试在最终关闭前等待该用例要验证的三次异步进度到达，避免将关闭时允许取消旧进度的行为误判为偶发失败；不改播放实现。
- 默认 Debug 命令及独立 Release 输出目录构建均为 0 警告 / 0 错误；Native AOT 发布完成。555 项回归：532 通过、23 按配置跳过（22 项需显式启用 GPU，1 项需测量输出目录）；更新专项 37/37 通过。最初 Debug 输出被运行中的应用锁定，先用独立目录验证，后续默认构建也已完成。
- 从 Git 索引全量检出到新目录，着色器生成与 SHA-256 验证通过，许可证来源清单全部匹配。修复 PowerShell CRLF here-string 改变生成字节的问题，并保留两份锁定 CMake 配方与一份 Markdown 许可的原始字节。
- AOT 实际设置页深色/浅色渲染完成，无页面构造错误。完整 UI 首轮在 EpisodeRail 拖动检查超时，二轮被 UiInputTargetOccluded 拦截；其他页面、50 次开关和对象释放通过的证据仅来自首轮，不将两轮记作完整 UI 通过。本次没有改动 EpisodeRail 或输入探测逻辑。
- 历史提交与待提交内容经过 Gitleaks 8.30.1 脱敏扫描，无发现；扫描工具按 GitHub 官方资产摘要校验，报告与工具保留在忽略的 artifacts/。
- [线上构建与测试](https://github.com/Aaleo73/Mambo/actions/runs/37305037345)通过，提交 `eb4770c` 已推送到公开仓库 main，保留当前分支的完整提交历史。
- `v0.1.0` 标签触发的[发布工作流](https://github.com/Aaleo73/Mambo/actions/runs/37305527521)通过。[首个源码 Release](https://github.com/Aaleo73/Mambo/releases/tag/v0.1.0)公开包含 `Mambo-0.1.0-sources.zip`（24,342,608 字节）与 `SHA256SUMS.txt`，没有安装器、便携包或原生 DLL。源码归档共有 581 个文件/目录项，没有 artifacts、publish、bin、obj、Git 或测试输出。
- 重新下载线上源码资产，GitHub 摘要校验通过；581 个归档项的名称、字节数和内容 SHA-256 均与本地同提交的归档匹配。两个 ZIP 容器的哈希不同，但逐项内容一致；不将本地 ZIP 的容器哈希代替线上资产摘要。
- 含 libmpv 的两个不同版本安装升级链仍未公开验收；原生对应源码缺口未解决。
