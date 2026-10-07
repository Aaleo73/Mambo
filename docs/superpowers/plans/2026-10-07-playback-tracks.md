# 字幕、音轨与持久字幕 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在现有字幕控制组件中提供语言偏好、自动选轨恢复、字幕校时和样式，并静默保存、匹配和自动采用拖入字幕。

**Architecture:** App 只消费类型化 Contracts，Core 会话串行处理引擎状态；文件匹配与复制在后台执行，结果按条目与用户操作代际接回。持久字幕库独立于缓存，轨道偏好使用现有原子设置存储；外置 mpv 不参与这些功能。

**Tech Stack:** C#/.NET 10、WinUI 3、libmpv、JSON 源生成、xUnit v3。

**Spec:** `docs/superpowers/specs/2026-10-07-playback-tracks-design.md`（用户已批准）。

## Global Constraints

- 界面代码只能依赖 `Mambo.Core.Contracts` 和 `VideoSurface` 的公开 API。
- XAML 只放 `src/Mambo.App`；使用 `LibraryImport`、JSON 源生成、`{x:Bind}`、partial 类型。
- 不保存服务器地址、令牌、密码或源文件完整路径；日志只记录稳定错误码。
- 字幕目录为 `Mambo.exe` 同级的 `Subtitles/`，安装版和便携版采用同一规则。
- 字幕导入全程静默；无法唯一确定归属时跳过，不提供分配面板或结果提示。
- 样式直接位于现有字幕控制组件内；选轨自动保存且没有记忆 UI。
- 延迟为 -60.0 到 +60.0 秒、步进 0.1；每种菜单最多 32 项；每个项目最多 32 条本地字幕；选轨偏好最多 500 个内容项目。
- 字体 MiSans、字号 38（18–72）、白色、黑色描边 1.65（0–6）、底距 34（0–180）；ASS 覆盖默认关闭。
- 中文 conventional commits；不推送；【需用户】的真实文件和显示验收只登记，不冒充自动通过。

## 文件职责与执行顺序

1. Contracts 与公共类型：根工作区建立统一边界，使并行模块无需互相修改。
2. 设置、轨道偏好：`Persistence` 和纯匹配逻辑；不编辑播放会话或 App。
3. 持久字幕：`Core/Subtitles` 内实现解析、服务器目标解析、文件和索引事务；不编辑播放会话。
4. 会话集成：根负责偏好顺序、引擎控制、竞态、上报和组合根。
5. App 与 Fake：独立 UI 工作区直接依赖步骤 1 的类型。
6. 打包边界、集成测试、AOT、文档与审查。

步骤 2、3、5 使用仓库外独立工作区并行；根负责合并、逐块审查与最终集成。工作区位于 `../Mambo-worktrees/`。

### Task 1: 固定类型与会话命令边界

**Files:**
- Create: `src/Mambo.Core/Contracts/SubtitleContracts.cs`
- Modify: `src/Mambo.Core/Contracts/PlaybackContracts.cs`, `PlaybackModels.cs`, `SettingsContracts.cs`
- Test: `tests/Mambo.Core.Tests/SubtitleContractTests.cs`

**Interfaces:**
- `SubtitleStyleSettings`: `FontFamily`, `FontSize`, `TextColor`, `OutlineSize`, `BottomMargin`, `OverrideAssStyle`。
- `SubtitleStyleKind`: `None`, `Text`, `Ass`, `Bitmap`, `Unknown`。
- `TrackSource`: `Embedded`, `External`, `Local`；`TrackInfo` 增加安全 `Title`, `Codec`, `AudioChannels`, `IsForced`, `Source`, `SubtitleStyleKind`。
- `AppSettings`: 字符串 `PreferredAudioLanguage="auto"`, `PreferredSubtitleLanguage="zh"`, `SubtitleStyle`。
- `SessionSnapshot`: `EntryGeneration`、`SubtitleDelaySeconds`、`SubtitleStyle`、`SubtitleStyleKind`、`CanAdjustSubtitleDelay`、`CanImportSubtitles`。
- 会话增加带 `expectedEntryGeneration` 的选轨重载；`SetSubtitleDelayAsync(double seconds, long expectedEntryGeneration, string? expectedSubtitleTrackId, CancellationToken)`；`SetSubtitleStyleAsync(SubtitleStyleSettings style, long expectedEntryGeneration, CancellationToken)`。
- `SubtitleImportContext? BeginSubtitleImport()` 在读取拖放数据前捕获上下文；`ImportSubtitlesAsync(SubtitleImportContext context, IReadOnlyList<LocalSubtitleFile> files, CancellationToken)` 静默执行；两个路径/上下文类型均重写 `ToString()`。

- [ ] 写契约测试，保证默认值与路径脱敏；例如：
  ```csharp
  Assert.Equal(38, new SubtitleStyleSettings().FontSize);
  Assert.False(new SubtitleStyleSettings().OverrideAssStyle);
  Assert.DoesNotContain("secret.srt", new LocalSubtitleFile("secret.srt").ToString());
  ```
- [ ] 实现类型和兼容默认方法；旧 Fake/probe 会话仍能编译，未实现导入时默认不受理。
- [ ] 运行 `dotnet test --project tests/Mambo.Core.Tests -- --filter-class '*SubtitleContractTests'`，验证后提交 `feat(core): 定义字幕控制与导入契约`。

### Task 2: 首选语言、自动选轨偏好与可靠匹配

**Files:**
- Create: `src/Mambo.Core/Persistence/TrackPreferences.cs`, `src/Mambo.Core/Playback/TrackSelection.cs`, `src/Mambo.Core/Playback/SubtitleStyle.cs`
- Modify: `src/Mambo.Core/Persistence/SettingsStore.cs`, `StorageJsonContext.cs`
- Test: `tests/Mambo.Core.Tests/TrackPreferenceTests.cs`, `SubtitleStyleTests.cs`

**Interfaces:**
- `TrackPreferenceEpoch(string AudioLanguage, string SubtitleLanguage, long AudioRevision, long SubtitleRevision)`。
- `TrackChoice(bool Disabled, TrackFingerprint? Fingerprint = null, string? LocalSubtitleId = null)`。
- `TrackFingerprint` 由安全标题、规范语言、forced、codec、channels 构造；不存原生 ID。
- `TrackPreferences(SettingsStore settings, AccountContext accounts)` 提供 `Capture()`，`Get(account, entry, kind, epoch, bool exactItem=false)`，`SaveAsync(account, entry, kind, TrackChoice choice, epoch, bool exactItem, token)`，`ClearExactSubtitleAsync(account, entry, epoch, token)`。
- `TrackSelection.Fingerprint(TrackInfo)`，`Match(TrackFingerprint, IEnumerable<TrackInfo>)` 返回唯一 ID 或 null；`LanguageCodes(string preference, bool subtitle)` 返回 libmpv 偏好字符串。
- `SubtitleStyle.Normalize(SubtitleStyleSettings)` 修复加载值；`Validate` 拒绝命令中的非法值；`Properties(style)` 返回类型化 mpv 属性映射。

- [ ] 先测试重启/跨季/跨账号恢复，稳定特征匹配而非轨道 ID：
  ```csharp
  var before = new TrackInfo("1", TrackKind.Audio, "日语") { Language = "jpn" };
  var after = before with { Id = "7", Language = "ja" };
  Assert.Equal("7", TrackSelection.Match(TrackSelection.Fingerprint(before), [after]));
  Assert.Null(TrackSelection.Match(TrackSelection.Fingerprint(before), [after, after with { Id = "8" }]));
  ```
- [ ] 在 `SettingsDocument` 加双语言修订号与有序最多 500 项偏好；更改语言只清空对应记录并增加修订号。写锁内检查旧 epoch，语言改回原值也不能让旧会话写回。
- [ ] 载入未知语言/损坏样式时保留其余设置并回退；采用原子写和 JSON 源生成。确切项目记录与整剧记录分开读取，本地 ID 只写确切项目。
- [ ] 实现安全标题/语言归一化、唯一匹配及样式属性映射；文本样式不使用全局 sub-scale/sub-pos。设置命令失败不改变持久值。
- [ ] 覆盖容量淘汰、独立音轨/字幕失效、并发设置写入及失效 epoch；运行对应测试并提交。

### Task 3: 持久字幕库与媒体自动匹配

**Files:**
- Create: `src/Mambo.Core/Subtitles/LocalSubtitleLibrary.cs`, `LocalSubtitleModels.cs`, `LocalSubtitleJsonContext.cs`, `SubtitleFilenameParser.cs`, `LocalSubtitleTargetResolver.cs`
- Test: `tests/Mambo.Core.Tests/LocalSubtitleLibraryTests.cs`, `SubtitleFilenameParserTests.cs`, `LocalSubtitleTargetResolverTests.cs`

**Interfaces:**
- `LocalSubtitleLibrary(string subtitleRoot, AccountContext accounts, ILocalSubtitleTargetResolver targets, Action<AppError>? log=null)`。
- `ImportAsync(AccountSession, PlaybackEntry, ImmutableArray<string> sourcePaths, CancellationToken)` 返回 `LocalSubtitleImportResult`，其中 `Items` 为本批成功项，保留输入顺序；每项有 `ItemId` 和字幕描述。
- `GetForItemAsync(AccountSession, string itemId, CancellationToken)` 返回 `LocalSubtitleItem`：`Files` 和 `AdoptedSubtitleId`。
- `SetAdoptedAsync(AccountSession, string itemId, string subtitleId, CancellationToken)`；导入事务不自行改写采用项，根会话按操作代际决定。
- `LocalSubtitleFileInfo`：`Id`, `DisplayName`, `Format`, `ManagedPath`；`ToString()` 不暴露路径。
- `ILocalSubtitleTargetResolver.ResolveAsync(account, context, ImmutableArray<SubtitleImportCandidate>, token)` 返回输入索引到 ItemId 的映射；实际 resolver 接 `EmbyApi`，复用 `EmbyEpisodeReader` 完整分页。

- [ ] 用真实临时目录测试导入后删源文件并重开库仍可读取相同内容：
  ```csharp
  var imported = await library.ImportAsync(account, episode, [source], token);
  File.Delete(source);
  var saved = await reopened.GetForItemAsync(account, episode.ItemId, token);
  Assert.Single(saved.Files);
  Assert.Equal(expectedText, await File.ReadAllTextAsync(saved.Files[0].ManagedPath, token));
  ```
- [ ] 文件名解析支持 SxxEyy、NxM、中文季集和明确纯集号；年份/分辨率/校验/多集/冲突模式跳过；单个无编号回退基于原始批次数量。
- [ ] 服务器解析只接受唯一真实 ItemId，跨剧按有限规范化精确名称；读取完整季/整剧，不能用当前队列作为全集；失败只跳过。
- [ ] 流式复制并 SHA-256 命名；同目标内容去重、同名异内容保留；写锁内原子索引提交及备份。索引双损坏不覆写旧资料；无关联文件不复制；拒绝非法摘要路径和目录穿越。
- [ ] 并发导入不丢记录、每项目 32 条、取消/账户更换不跨写、部分成功保留、源路径不泄漏；运行三组测试并提交。

### Task 4: 引擎和会话完整接入

**Files:**
- Modify: `src/Mambo.Core/Playback/PlaybackSession.cs`, `PlaybackCoordinator.cs`, `EngineContracts.cs`, `src/Mambo.Core/BackendRuntime.cs`
- Create: `src/Mambo.Core/Playback/PlaybackSession.Subtitles.cs`, `PlaybackSession.Tracks.cs`
- Modify: `src/Mambo.Player/LibMpv/MpvCore.cs`, `LibMpvEngine.cs`, `src/Mambo.Core/Networking/EmbyDtos.cs`
- Test: `tests/Mambo.Core.Tests/RealPlaybackSessionTests.cs`, `PlaybackPreparationTests.cs`, `tests/Mambo.Player.Tests/LibMpvEngineTests.cs`

**Interfaces:** 消费任务 1–3 的契约与服务；`PlaybackSession` 改为 partial，字幕逻辑放独立文件；组合根传 `Path.Combine(AppContext.BaseDirectory, "Subtitles")`。

- [ ] 扩展现有 fake engine 测试：
  ```csharp
  await session.SelectSubtitleTrackAsync("2", session.Snapshot.EntryGeneration, token);
  await session.SetSubtitleDelayAsync(0.4, session.Snapshot.EntryGeneration, "2", token);
  Assert.Equal(0.4, session.Snapshot.SubtitleDelaySeconds);
  // 新条目开始后，旧 generation 的命令必须拒绝；同轨重选保持延迟。
  ```
- [ ] 会话创建捕获语言 epoch，设置 alang/slang、字幕关闭初值与全局样式；每次条目起播递增 EntryGeneration 并归零延迟、清除手选保护；外置播放器跳过全部新逻辑。
- [ ] 解析轨道安全元数据和真实外部路径（仅内部识别）；公开列表优先已选与本地，最多 32。服务器索引只关联内封轨或已经识别的服务器外挂，绝不将本地 ff-index 上报。
- [ ] 自动选择顺序按设计；手选成功再保存。服务器/本地 sub-add 用不会自动选择的模式，接收新轨列表后由统一规则选择，保护手选与关闭。
- [ ] 延迟验证当前条目与字幕，修改 sub-delay，观察实际值；切字幕/切条目/换源/显式重试归零，暂停/seek 保持。样式命令合并迟到值、整套失败回滚，并自动保存成功结果。
- [ ] Drop 起点捕获条目和用户操作代际；复制/服务器匹配脱离 actor；结果投回后逐项目保存采用项，当前加载还需同一条目代际且无更新手选。成功项不受后续取消影响，账号 token 取消未提交工作。
- [ ] 持久文件只加载对应 ItemId，正常切轨/关闭压住导入默认；新拖入覆盖旧明确项目选择；文件缺失/加载失败静默回退。关闭等待文件事务收尾但不删除受管库。
- [ ] 省略可选 SubtitleOffset，更新上报断言；真实 headless libmpv 加载合成 SRT/ASS 验证 sub-delay、样式属性、sub-add 与轨道事件。运行 Core/Player 测试并提交。

### Task 5: 现有控制组件、设置与 Fake

**Files:**
- Modify: `src/Mambo.App/Views/PlayerChoicePanel.xaml`, `.xaml.cs`, `PlayerOverlay.xaml`, `.xaml.cs`, `SettingsPage.xaml`, `.xaml.cs`
- Modify: `src/Mambo.App/ViewModels/SettingsViewModel.cs`；必要时新增 `SubtitleControlsViewModel.cs`
- Modify: `src/Mambo.Core/Fakes/FakePlaybackSession.cs`, `FakeSettingsService.cs`
- Test: `src/Mambo.App/Debug/PlayerControlsSmoke.cs`, `tests/Mambo.Core.Tests/FakePlaybackTests.cs`

**Interfaces:** 仅消费任务 1 Contracts；字体枚举复用现有 Win2D，不添加依赖。

- [ ] 扩展现有 TracksPanel：字幕轨道、时间、全部样式控件、音轨，统一滚动；其它使用 PlayerChoicePanel 的菜单保持原行为。
- [ ] 控件绑定实际快照，改动立即命令；数字非法输入原位提示；ASS/图片/未知按设计禁用，未选字幕仍可保存全局样式。更新保持焦点/滚动，选轨后可继续调整。
- [ ] 设置页增加两种首选语言（自动、中文、日语、英语、韩语、法语、德语、西班牙语、俄语；字幕另加关闭）及下次播放说明，变更自动保存。
- [ ] Drop 前调用 BeginSubtitleImport，再读取 StorageItems，完成后传 LocalSubtitleFile；只接受系统真实文件，不弹结果。导入不用通用 Toast wrapper；演示会话不访问文件。
- [ ] 文本框/数字框/字体选择等输入不触发播放器 C/V、空格、seek；Esc 先关组件。Fake 同步提供延迟/样式实际快照与条目代际。
- [ ] 增补针对控件的自动冒烟断言：
  ```csharp
  // 打开字幕组件后直接找到字号、颜色、描边、底距；调整后快照改变，组件仍打开。
  // 在数值输入框编辑时，PositionTicks 和选轨不改变。
  ```
- [ ] 构建 App 与对应 fake 测试，提交 UI 变更；真实观感保留【需用户】。

### Task 6: 打包、回归、文档与审查

**Files:**
- Modify: `scripts/publish.ps1`, `installer/Mambo.iss`、更新清单路径验证对应文件（按实际生成点）
- Modify: `docs/SPEC.md`, `docs/STATUS.md`
- Create: `docs/decisions/playback-tracks.md`
- Test: `tests/Mambo.Core.Tests/AppUpdateTests.cs`

- [ ] 发布源枚举明确排除 Subtitles，安装器排除该目录；更新清单拒绝包含该保留目录，卸载不增加删除用户字幕的规则。
- [ ] 用临时发布树验证运行时字幕不进入 manifest/安装复制，并验证更新及缓存清理保留受管资料；示意断言：
  ```csharp
  Assert.DoesNotContain(manifest.Files, file => file.Path.StartsWith("Subtitles/", StringComparison.OrdinalIgnoreCase));
  Assert.True(File.Exists(savedSubtitle));
  ```
- [ ] 运行 `dotnet build -p:Platform=x64` 和 `dotnet test`；修复新失败，不重复旧的人工验收。
- [ ] 运行 AOT publish 与新增控件定向检查；记录命令与真实结果，无法通过自动化确定的显示观感只列【需用户】。
- [ ] 对每块 diff 做规格与质量审查，再做整分支审查；修复竞态、路径、账号隔离及 UI 输入问题后补跑相关检查。
- [ ] 更新 SPEC、STATUS 与决定记录，计划勾选实际完成项；提交所有已验证代码，合并回本地工作分支，不推送或发布。

## 自检记录

全部设计章节对应任务：语言与记忆→2/4/5；轨道与延迟→1/4/5；样式→1/2/4/5；导入匹配/事务→3/4/5；架构→所有任务；上报→4；验收与发布边界→6。任务 2 和 3 不编辑会话或 App；任务 5 不编辑持久化或会话实现。语言 epoch 与条目/操作代际分别处理设置失效和异步播放失效。
