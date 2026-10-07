# Mambo 行为规格

> 功能与信息架构的依据，测试也按这里的规则编写。视觉取值以 `src/Mambo.App/Themes/` 和 `docs/decisions/P9-visual-parity.md` 为准；模块设计见 [ARCHITECTURE.md](ARCHITECTURE.md)。
>
> 小节沿用原计划「附录 A」的编号（A.1–A.11），其他文档里的「A.9」等引用指的就是这里。

## A.1 Emby API

**地址规范化**
- 规则：去掉首尾空白，长度 ≤ 2048 字节；没写协议时补 `https://`，只接受 http/https；必须有主机名；拒绝 userinfo、查询参数和片段；去掉末尾的 `/`；允许带路径（如 `https://host/emby`）。
- 错误文案：「请输入服务器地址」「服务器地址过长」「服务器地址格式无效」「服务器地址仅支持 HTTP 或 HTTPS」「服务器地址缺少主机名」「服务器地址不能包含用户名或密码」「服务器地址不能包含查询参数或片段」。
- 其它输入上限：用户名 ≤ 256 字符，密码 ≤ 4096 字节，id ≤ 256 字节，搜索词 ≤ 256 字符，分页 limit 在 1–500 之间。

**端点**

| 方法与路径 | 参数 / 说明 |
|---|---|
| POST /Users/AuthenticateByName | body `{"Username","Pw"}`，返回 `{AccessToken, User{Id,Name}, ServerId}`；401/403 →「用户名或密码错误」 |
| GET /Users/{uid} | 恢复会话时校验，只看状态码 |
| POST /Sessions/Logout | 2xx、401、403 都视为成功 |
| GET /Users/{uid}/Views | 媒体库列表 |
| GET /Users/{uid}/Items（浏览） | Fields=ITEM_FIELDS；ParentId、IncludeItemTypes、SortBy、SortOrder、StartIndex、Limit、Recursive、Filters、Genres（`\|` 分隔）、Years（`,` 分隔）、OfficialRatings（`\|` 分隔） |
| GET /Users/{uid}/Items（继续观看） | Recursive=true、SortBy=DatePlayed、SortOrder=Descending、Filters=IsResumable、IncludeItemTypes=Movie,Episode,Video、Fields=RESUME_FIELDS、Limit、StartIndex |
| GET /Users/{uid}/Items/Latest | ParentId、Limit、Fields=LATEST_FIELDS；返回数组 |
| GET /Users/{uid}/Items（搜索） | SearchTerm（规范化后加双引号，按完整短语查询，避免中文逐字宽泛匹配）、Recursive=true、IncludeItemTypes 默认 Movie,Series,Video、Limit、StartIndex、ParentId、Fields=ITEM_FIELDS |
| GET /Users/{uid}/Items/{id} | Fields=DETAIL_FIELDS |
| GET /Shows/NextUp | UserId、SeriesId、Limit=1，用精简字段 |
| GET /Users/{uid}/Items（季） | ParentId=剧集 id、IncludeItemTypes=Season、SortBy=IndexNumber、SortOrder=Ascending、Limit=100 |
| GET /Users/{uid}/Items（集） | ParentId=季 id、IncludeItemTypes=Episode、Recursive=true、SortBy=ParentIndexNumber,IndexNumber,SortName、SortOrder=Ascending、StartIndex、Limit，用精简字段 |
| GET /Items/Filters | UserId、ParentId、IncludeItemTypes；超时 3 秒；值可能是字符串或 `{Name}`。失败时退回为完整分页扫描（Fields=Genres,ProductionYear,OfficialRating&EnableImages=false&EnableUserData=false），或只用已加载的卡片推导（原版只读前 500 条，结果不完整） |
| POST /Items/{id}/PlaybackInfo?UserId= | body `{UserId, StartTimeTicks, IsPlayback:true, AutoOpenLiveStream:true, MaxStreamingBitrate:140000000, DeviceProfile}` |
| POST /Sessions/Playing、/Sessions/Playing/Progress、/Sessions/Playing/Stopped | 都加 `?reqformat=json`；载荷见 A.9 |
| DELETE /Videos/ActiveEncodings?DeviceId&PlaySessionId | 转码会话停止后调用 |
| GET /Items/{id}/Images/{type}?Tag&MaxWidth&MaxHeight&Index&Quality | 类型：Primary/Backdrop/Thumb/Logo；参数按这个顺序拼接 |
| 字幕 | 优先用 `MediaStream.DeliveryUrl`，否则用 `/Videos/{id}/{mediaSourceId}/Subtitles/{index}/Stream.{format}` |

**字段集**
- `ITEM_FIELDS` = Genres,CommunityRating,OfficialRating,RunTimeTicks,PremiereDate,DateCreated,ProductionYear,BackdropImageTags,PrimaryImageItemId,PrimaryImageTag,ParentBackdropItemId,ParentBackdropImageTags,SortName
- `DETAIL_FIELDS` = ITEM_FIELDS + Overview,MediaSources,People,ParentLogoItemId,ParentLogoImageTag
- `RESUME_FIELDS` = Overview,Genres,MediaSources,BackdropImageTags,PrimaryImageItemId,PrimaryImageTag,ParentLogoItemId,ParentLogoImageTag,ParentBackdropItemId,ParentBackdropImageTags
- `LATEST_FIELDS` = RESUME_FIELDS 去掉 MediaSources
- 剧集列表和 NextUp 用精简字段，不要 MediaSources；播放前由 PlaybackInfo 获取媒体源（原版有过度获取的问题）。

**DeviceProfile**
- **DirectPlayProfiles**：一个 Video 配置，容器为 `mp4,mkv,avi,mov,wmv,m4v,ts,m2ts,webm,flv,ogm,ogv,mpg,mpeg,3gp`，不限制编码。
- **TranscodingProfiles**：两个，HLS 与 HTTP，容器都是 ts。共同参数：
  - VideoCodec=h264
  - AudioCodec=aac,mp3,ac3,eac3,flac,opus,vorbis
  - MaxAudioChannels="6"、MinSegments="1"
  - BreakOnNonKeyFrames=true、Context=Streaming
- **SubtitleProfiles**：srt/ass/ssa/sub/vtt 为 External；新增所有格式的 Embed。

**媒体库分类**
- **可播放库**：`CollectionFolder`，且 CollectionType 不是 books/boxsets/channels/folders/livetv/music/photos/playlists。
  - 首页"最新"各行、搜索分组、侧栏都只用可播放库（原版侧栏把所有库都列出来了，是 bug）。
- **资料库请求的 IncludeItemTypes**：movies → Movie,Video；tvshows → Series；其它 → Movie,Series,Video。客户端丢弃 Movie/Video/Series 以外的项。

## A.2 登录、恢复、注销

**登录表单**
- 字段：
  - 「服务器地址」：placeholder `https://your-emby-server`，提示「未填写协议时将使用 HTTPS。仅在可信网络中明确填写 http://。」
  - 「用户名」
  - 「密码」：可以为空
- 按钮「连接」，进行中显示「连接中...」；地址和用户名都不为空时才可用。
- 用上次的服务器地址和用户名预填。

**登录流程**
1. 规范化地址；
2. AuthenticateByName；
3. 保存凭据；
4. 进入已登录状态；
5. 在后台 flush 发件箱；
6. 跳转到首页。

- 已登录时拒绝再次登录。
- 认证错误显示「用户名或密码错误」，其它错误显示中文模板文案。

**恢复会话**
1. 读取凭据；
2. 先用缓存快照渲染界面；
3. 在后台 `GET /Users/{uid}`：
   - 401/403：删除凭据，转为未登录；
   - 网络错误或 5xx：保留凭据，分别隔 1 秒、2 秒、4 秒重试，全部失败后首页显示「无法连接服务器」和「重试」。
4. 成功后 flush 发件箱。

**已登录状态**：显示用户名，以及一个按钮：平时显示「已连接」，悬停时显示「断开连接」；操作进行中显示「断开中...」或「恢复中...」。

**注销**
1. 如果正在播放，先确认：标题「注销并结束播放？」，正文「正在播放。注销会结束当前播放并保存进度，是否继续？」，按钮「注销」。
2. 停止播放，并 flush 发件箱（此时仍用旧令牌）。
3. 删除凭据，清理该账号的查询缓存和图片缓存。
4. `POST /Sessions/Logout`；失败时只提示「本地已断开，但服务器会话可能仍有效」。

**账号**：同一时间只有一个活动会话；换账号就是注销后重新登录；缓存按账号隔离。

## A.3 首页

**状态顺序**（依次判断，命中即停）
1. 恢复会话中：空白；
2. 恢复失败：「无法连接服务器」+「重试」；
3. 未登录：引导页；
4. 全部加载中：骨架屏；
5. 媒体库列表加载失败：「首页加载失败」+「重试」；
6. 没有任何内容：空白；
7. 以上都不是：显示内容。

**Hero 轮播**
- **数据**：GET Items（Recursive=true、IncludeItemTypes=Movie,Series,Video、SortBy=Random、Limit=32）→ 只保留有图的项 → 打乱 → 按"有 backdrop > 有 thumb > 只有 primary"排序 → 取前 8 个。
- **显示**：
  - logo（放在 360×130 的区域内），没有 logo 或加载失败时显示标题；
  - 元数据行：★ 评分、年份、最多 3 个类型（用 / 连接）、分级徽标；
  - 简介，最多 2 行；
  - 整块可点击，进入详情页。
- **切换**：每 7 秒自动切换。以下情况暂停：鼠标悬停或获得焦点、hero 在视口内不足 15%、窗口隐藏或失焦、鼠标悬停在标题栏分页点上、系统关闭了动画。
- **分页点**：幻灯片 ≥ 2 张时，标题栏中间显示分页点。
- **预取**：预取后续幻灯片的背景和 logo。

**「最近播放」行**
- **数据**：继续观看，Limit=16，按剧集去重（每部剧只留一条），只保留进度在 0–100% 之间且未看完的。
- **卡片**：横版卡片；单集用剧名作标题，副标题为「S01E02 · 集名」；带进度条和"从这里播放"按钮。
- **交互**：点击卡片进入详情；点击行标题旁的箭头进入最近播放页。
- **刷新**：播放结束后刷新这一行。

**每个可播放库一行「最新」**
- **数据**：每个库请求 Latest，Limit=16，最多 4 个库并发。
- **卡片**：海报卡片，副标题为年份或集号，带评分徽标。
- **出现顺序**：按侧栏顺序逐行出现。
- **失败**：某一行失败时，只在这一行显示错误和「重试」，重试也只针对这一行。
- **交互**：点击行标题进入该库。

**卡片行通用交互**
- 左右箭头每次滚动行宽的 82%；支持鼠标滚轮横向滚动。
- 前 4 张图片优先加载。
- 卡片悬停 300ms 后预取详情。

**未登录引导**：两行大号可点击文字「前往连接你的 / Emby 服务器」，点击后进入设置页。

## A.4 最近播放页

- **页头**：标题「最近播放」，旁边显示「N 项」（已加载数）。
- **网格**：虚拟化的横版卡片网格，列数随窗口宽度变化；每张卡都有播放按钮。
- **分页**：接近末尾时自动加载下一页（继续观看接口分页，Limit=120），按 id 去重。
- **错误**：首屏失败显示「最近播放加载失败」+「重试」；后续页失败显示「重试加载」。

## A.5 资料库页

**页头**
- 标题为库名，右侧是筛选栏。无筛选时显示服务器返回的 TotalRecordCount（「N 项」）；有筛选时显示已验证的卡片数量，未读完显示「已加载 N 项」，读完显示「N 项」，不把服务器可能忽略筛选的总数当成匹配数。
- 切换库时，旧内容一直保留到新库的首屏数据到达。

**筛选面板**
- 三组：类型（genres）、年份（按年代显示，如「2020年代」，请求时展开成具体年份）、分级。
- 组内是"或"，组间是"与"。
- 每组都有「全部」，选中即清空该组；只要有任何筛选，就出现「重置」。
- 列表接口可能忽略筛选参数：请求仍携带条件，客户端按返回元数据再次校验；类型与分级忽略大小写，年份精确匹配。按服务器原始条数推进分页，连续无匹配页继续读取，直到找到匹配项或真正读完；刷新从原始首条重新开始。

**排序**
- 添加日期：DateCreated 降序，默认
- 名称：SortName 升序
- 评分：CommunityRating 降序
- 年份：ProductionYear 降序
- 时长：Runtime 降序，只在电影库出现

**网格**
- 海报卡片：标题、「年份 · 进度%」、评分徽标、进度条。
- 网格没有逐卡入场；冷图仅 160ms 就绪淡入，父级交接、缓存和缺图直接终态。
- 接近末尾自动加载下一页，Limit=60。
- 空状态：有筛选时显示「当前筛选没有内容」+「重置筛选」，否则显示「暂无内容」。
- 排序或筛选改变时，旧结果保留到新结果到达，然后滚回顶部。

**筛选选项与偏好**
- 筛选选项：`/Items/Filters`（5 分钟内视为新鲜）与已加载卡片推导出的选项合并，让筛选立即可用。
- 偏好：排序和筛选按 `server|user|library` 记忆。

## A.6 详情页

**背景图**：优先级为 自身 backdrop → 父级 backdrop → （单集）所属剧集的 backdrop（需要额外获取剧集）→ 剧集 primary → 自身 primary。

**文字区**
- logo 或标题，加元数据行；
- 单集额外显示一行「第 N 季 · 第 M 集 · 集名」；
- 简介最多 2 行，选中某一集时显示该集的简介。

**播放按钮**：72px 圆形；有续播位置时显示白色进度环；启动中呈脉动状态。

**剧集区**（剧集或单集详情才有）
- **季胶囊**：「第 N 季」或季名，配左右箭头。
- **剧集横向列表**：
  - 卡片内容：「N. 集名」、「yyyy-MM-dd · 45 分钟」、进度条；
  - 点击卡片选中这一集：hero 简介和播放目标随之改变，卡片显示描边；
  - 每张卡片自带播放按钮；
  - 接近末尾加载下一页（Limit=30）；会自动翻页直到目标集加载出来，再把它滚动到可见位置。
- **季的选择**：NextUp 所在的季优先，否则第一季；用户手动选的季在数据刷新后保留；换季时清除选中的集。

**演职人员**
- 标题「演职人员」；横向列表，卡片宽 132px，头像比例 3:4，有圆角，无头像时显示剪影。
- 卡片显示名字，加角色名；没有角色名时按类型显示 导演/编剧/制片/演员/演职员。

**错误文案**：「详情加载失败」「剧集加载失败」「无法确定接下来播放的剧集：…」「无法加载剧集季：…」都带「重试」；后续页失败显示「重试加载」。

**原版没有的功能**：收藏、标记已看、相似推荐、媒体信息。尚未实现。

## A.7 搜索

- **输入**：
  - 搜索框在侧栏：按 Enter 或点搜索图标触发，有清除按钮；
  - 未登录时禁用，悬停提示「连接服务器后可用」；
  - 页面标题为「搜索」。
- **规范化**：做 NFKC 规范化，去掉标点和符号后至少剩 1 个字符才搜索；以带双引号的完整短语提交，不要求用户手动加引号。
- **分组**：每个可播放库一组。按已加载标题与搜索词的相关性排列：完整片名、片名前缀、片名包含、其它；同级保持媒体库原顺序。迟到结果及加载更多可以提升分组，移动原有分组而不丢弃其分页状态；刷新库列表不重建未变更的分组。
  - 最多 4 组并发，前 2 个库的请求是前台优先级；
  - 每组请求 Limit=24，「加载更多」按钮翻页，不自动加载。
- **结果处理**：只搜 Movie,Series,Video；单集折叠成所属剧集（用剧集的 id 和剧名）；空组隐藏；某组失败时只在该组显示错误和「重试」。
- **返回**：返回搜索页时恢复滚动位置；侧栏搜索框与当前搜索词保持同步。

## A.8 设置

- **「服务器配置」**：见 A.2。
- **「播放器设置」**：
  - 「播放方式」二选一：「内置播放器」（默认）/「外置 MPV」（只有在自定义 mpv.exe 已通过校验时可用）。外置模式使用 mpv 自身的配置、脚本、快捷键和控制界面，不打开 Mambo 播放层；HDR、硬件解码、音量和倍速由 mpv 自行管理。
  - 「MPV 路径」：输入框 +「选择文件」（只显示 .exe）。
  - 「预览播放页」：用假数据打开播放层。
  - 状态行，四种之一：校验中 / 已批准 / 当前使用内置播放器 / 路径无效、已改用内置。
  - 新增：「HDR」（自动/始终/关闭）、「硬件解码」（自动/关闭）。
  - 「首选音轨语言」（默认自动）、「首选字幕语言」（默认中文，中文无匹配时尝试英文）；字幕可选关闭。设置只用于下一次内置播放，修改对应语言会使该类旧选轨偏好失效。
- **外部 mpv 路径规则**：
  - 去掉首尾空白，不能为空，长度 ≤ 32767 个 UTF-16 单元；
  - 必须是名为 mpv.exe 的文件，或包含 mpv.exe 的目录；要规范化成绝对路径；
  - 编辑后防抖 400ms 再校验；
  - 无效时提示「未找到该路径下的 MPV 可执行文件，将使用内置播放器」，并切回内置播放。
- **「关于」**：Mambo、版本号（从程序集读取）、一句简介、许可与第三方声明入口、日志目录入口、「清除缓存」。

## A.9 播放

**启动**
- **入口**：首页「最近播放」行的播放按钮、最近播放页卡片的按钮、详情页 hero 的播放按钮、剧集卡片的播放按钮。
- **防重复**：启动中或替换确认中，再次点击无效；同一个项目正在播放时，点击也无效。
- **替换确认**：已有内容在播放时弹出确认：标题「切换播放？」，正文「正在播放其他项目。切换播放会结束当前播放并保存进度，是否继续？」，危险按钮「切换」。取消则保持原状态。
- **失败**：Toast「播放失败：{原因}」，带「重试」；播放层显示失败态。
- **关闭窗口时正在播放**：确认「退出应用？」/「正在播放。关闭应用会结束播放并保存进度，是否退出？」/「退出」，然后停止播放再退出。

**目标解析**
- Movie / Episode / Video：用显式起点，否则用 `UserData.PlaybackPositionTicks`，没有阈值。
- Series：先取 NextUp；没有时分页读完全部剧集（recursive，每页最多 500，不限制总集数），依次找：续播位置 ≥ 30 秒的第一集 → 第一个未看的集 → 第一集。详情页的 NextUp 回退使用相同分页规则。
- Season：分页读完本季剧集后按同样规则找。
- 其它类型：返回契约错误。

**标题**
- 单集的媒体标题：`{SeriesName} S{季:02}E{集:02} - {Name}`。
- 播放层顶栏标题：「剧名 · 第3集 标题」。

**连播计划**
- 只针对有 SeasonId 的 Episode；分页取完该季全部剧集，每页最多 500，不限制总集数，按 ParentIndexNumber、IndexNumber、SortName 升序。
- 按服务器实际返回的原始条数推进 StartIndex；总数已知时继续到总数，未提供总数时继续探测到空页，不能将服务器限制的短页视作末页。重复页终止请求；服务器仍声明有未读项时按失败处理。见 `docs/decisions/episode-pagination.md`。
- 只保留 Type=Episode 且 SeasonId 匹配的项（没有 SeasonId 的行也接受）；选中的集必须在其中。
- 任何失败都降级为单集播放。

**媒体源资格**
- "显式 URL"指 DirectStreamUrl、TranscodingUrl 或 http(s) 的 Path。
- 以下情况拒绝该媒体源：
  - 没有显式 URL，且 MediaStreams 为空或没有视频流；
  - 没有显式 URL，且容器是 iso/dvd/bluray/bdmv；
  - 显式 URL、SupportsDirectStream、SupportsDirectPlay 全都没有。
- 带 `RequiredHttpHeaders` 的源**允许使用**。

**媒体源优先级**
- 顺序：0 DirectStreamUrl；1 http Path；2 SupportsDirectStream/DirectPlay；3 TranscodingUrl。
- 同级时依次比较：最大视频高度（降序）、码率（降序）、服务器返回的顺序。

**候选 URL**（按排名）

| 排名 | 来源 | 播放方式 | 说明 |
|---|---|---|---|
| 1 | direct_stream_url | DirectStream | 转成绝对地址，缺 PlaySessionId 时补上 |
| 2 | http_path | DirectPlay | |
| 3 | `/Videos/{id}/stream.{container}?DeviceId&MediaSourceId&Static=true[&PlaySessionId]` | DirectPlay | 仅当 SupportsDirectPlay/DirectStream 为真 |
| 4 | 同上，不带扩展名 | DirectPlay | 同上 |
| 5 | transcoding_url | Transcode | |

- 有多个媒体源时，先按类别排，再按源的排名排，并按 URL 去重。
- 拼进路径的 id 要做路径编码；空字符串、`.`、`..` 一律拒绝。

**上报载荷**
- **Playing / Progress**：
  - 条目：ItemId、MediaSourceId?、PositionTicks、PlaySessionId?、LiveStreamId?、PlayMethod、PlaybackStartTimeTicks（开始时刻的 Unix 纪元 100ns ticks）
  - 状态：EventName（Progress 用 TimeUpdate / Pause / Unpause，Playing 不带）、IsPaused、IsMuted、VolumeLevel（默认 100）、PlaybackRate（真实倍速）
  - 播放列表：PlaylistIndex / PlaylistLength（在连播计划中的位置）、NowPlayingQueue=[]
  - 固定值：MaxStreamingBitrate=2147483647、RepeatMode="RepeatNone"、CanSeek=true、Shuffle=false；不发送 SubtitleOffset。
  - 可选：AudioStreamIndex / SubtitleStreamIndex，仅内封轨和已识别的服务器外挂映射到 Emby MediaStream.Index；本地导入轨不发送服务器索引。
- **Stopped**：ItemId、MediaSourceId?、PositionTicks、PlaySessionId?、LiveStreamId?、PlaybackStartTimeTicks、Failed=false。

**上报节奏**
- 首次确认开播时发 Playing；之后至少间隔 10 秒发一次 Progress；暂停、继续、拖动时立即发 Progress；结束时发 Stopped。
- 从未确认开播的条目：不上报，也不进发件箱。
- 发 Stopped 前，如果 Playing 还没成功，先尽力补发一次 Playing。

**换算**：1 秒 = 10^7 ticks；秒数保留 3 位小数；续播位置大于 0 才设置 `start`。

**mpv 控制映射**

| 操作 | mpv 命令或属性 | 取值 |
|---|---|---|
| 暂停 | pause | — |
| 跳转 | `seek <秒> absolute` | 秒数 ≥ 0 |
| 音量 | volume | 0–100 |
| 静音 | mute | — |
| 倍速 | speed | 0.25–4 |
| 音轨 / 字幕 | aid / sid | 轨道 id，或 `no` 表示关闭 |
| 逐帧 | `frame-step` / `frame-back-step` | 以该修订的文档为准 |
| 下一集 | `playlist-next` | — |

**轨道菜单**
- 字幕、音轨各最多 32 条，优先保留当前选中及本地轨；文本截断到 96 个字符，不暴露 external-filename。
- 标签显示可用的标题、语言、编码、音轨声道及内封/外挂/本地来源；同名轨补序号。
- 成功的手动选轨按账号及整剧/单片静默保存，下次自动匹配稳定元数据；本地字幕采用及其后的明确选择绑定具体 ItemId。没有记忆开关、保存/清除记忆按钮或状态提示。
- 优先级：当前手选 → 当前项目明确选择 → 已采用本地字幕 → 整剧/单片选择 → 首选语言 → 原生默认。指纹不唯一或轨道缺失时回退；新导入可替换该项目之前的采用项，但不能覆盖之后的手选。

**字幕时间与样式**
- 所有编辑直接放在现有字幕/音轨控制组件中，统一滚动，无额外样式子面板。修改后保持组件打开，输入控件拥有 C/V、空格和方向键，不触发播放快捷键。
- 字幕时间范围 -60.0 至 60.0 秒，精度 0.1 秒；负值提前，正值延后。只对当前已选字幕生效，暂停和跳转保留；切字幕、关闭字幕、换集、换源及重试归零，不持久化。
- 文本样式全局自动保存并即时应用，包括暂停时：字体默认 MiSans（支持系统字体）、字号 38（18–72）、颜色 #FFFFFF、黑色描边 1.65（0–6）、底部距离 34（0–180 整数，按 720 高度缩放）。恢复默认只重置样式。
- ASS/SSA 默认保留自带排版，打开「覆盖 ASS 样式」后才能改文字样式；图片/未知字幕禁用文字样式，仍可改时间。无选中字幕时可预设后续文本样式。

**本地字幕拖放**
- 内置播放器接受 SRT、ASS、SSA、VTT，拖入后复制到 Mambo.exe 同级的 `Subtitles/`；按账号和具体媒体 ItemId 保存，下次播放自动加载，源文件不修改。
- 单个无明确编号冲突的字幕默认用于当前条目。批量必须通过真实服务器元数据唯一确定归属；支持同季、跨季及有唯一精确剧名的其他剧集，无法确定、多集组合或元数据失败的成员跳过，不显示分配面板。
- 保留原始拖入批次数量和顺序；不能在过滤不支持文件后将批量误判为单文件。其他集只保存，播放对应集时加载；异步结果不改变拖入后已切换的条目，也不覆盖更晚手选/关闭。
- 成功、跳过、写入失败和加载失败均静默，无 Toast、说明或导入状态；仅写固定脱敏错误码。退出、注销、清除缓存、升级和卸载保留受管字幕；发布包排除运行时 `Subtitles/`。
- 外置 mpv 继续自行管理；假数据预览支持控制区设置，但不读取真实拖入文件。

**Toast 文案**：跳过某集时「有一集无法加入连播，已跳过」；意外中断时「播放意外中断，已保存最新进度」（显示 8 秒）。

## A.10 播放页交互

**状态**
- 打开中：加载指示 +「正在打开」；20 秒后追加「片源响应较慢，仍在等待画面」+「关闭播放页」。
- 失败：错误信息（缺省为「这部片子暂时打不开」）+「重试」/「关闭」。
- 缓冲：paused-for-cache 为真时显示「缓冲中…」胶囊。
- 暂停：控制层可见时，中央显示大播放按钮。

**底栏**
- **进度条**：拖动时显示时间提示，松手后提交绝对 seek；显示已缓冲区间。
- **左侧**：
  - 「上一集」（不是第一集时显示）
  - 播放/暂停
  - 「下一集」（不是最后一集时显示）
  - 时间：「m:ss / m:ss」，超过 1 小时用「h:mm:ss」
- **右侧**：
  - 倍速：按钮显示「倍速」或当前值如「1.25×」；选项 0.5/0.75/1/1.25/1.5/2，允许范围 0.25–4
  - 轨道菜单：「字幕」（含「关闭字幕」）、「音轨」；没有轨道时显示「这个文件没有可选轨道」
  - 音量：静音按钮，悬停时展开滑块
  - 全屏
  - 最大化/还原

**自动隐藏**
- 控制层可见的条件：鼠标按住中，或焦点在控制层内，或（指针在画面区域内且未到空闲时间）。
- 空闲时间 3000ms；画面区域内的指针移动、按下以及任何按键都会重新计时。
- 鼠标按住或焦点在控制层内时，不会因空闲而隐藏。
- 打开时默认隐藏，直到指针移动。
- 显示和隐藏的淡入淡出为 240ms；系统关闭动画时瞬间完成。
- 光标只在"播放中且控制层隐藏"时隐藏。
- **只有画面区域内的指针移动才算数**：在选集面板上移动不会唤出控制层。

**鼠标**
- 单击：250ms 后切换播放/暂停（给双击留出判断时间）；点在按钮或滑块上的单击忽略。
- 双击：切换最大化。
- 两者都只在播放或暂停状态下生效。

**按键**（除 F11、Esc 外，只在播放或暂停状态下生效）

| 按键 | 动作 |
|---|---|
| Space | 播放/暂停 |
| M | 静音 |
| `[` / `]` | 倍速降一档 / 升一档 |
| C | 循环切换字幕（循环中包含"关闭"） |
| V | 循环切换音轨 |
| D | 开关弹幕 |
| `,` / `.` | 逐帧后退 / 前进 |
| ← / → | 后退 / 快进 5 秒（按住可连发） |
| ↑ / ↓ | 音量 ±5 |
| F11 | 全屏 |
| Esc | 全屏时退出全屏；否则关闭播放层并停止播放 |
| Alt+← | 关闭播放层 |
| 鼠标后退键 | 关闭播放层 |
| 鼠标前进键 | 忽略 |

**选集**
- 标题「选集」；每集一个方形按钮，显示集号，原版为 4 列。
- 集号解析：先取 episodeLabel 末尾的数字；没有则从标题的 SxxEyy 里取集号；再没有就用序号 + 1。
- 当前集高亮；只有在播放或暂停状态下才能点击。
- 连播条目多于 1 个，或第一个条目有集号时，才显示选集。

**结束与全屏**
- 季末自动关闭播放层。
- 全屏时隐藏标题栏和侧栏；关闭播放层时如果处于全屏，先退出全屏。

## A.11 外壳、导航、快捷键、通用组件

**窗口**：默认 1500×860 居中，最小 1100×720；无边框加亚克力，圆角 8。

**标题栏（40px）**
- **左侧**：后退、前进按钮（28px）；播放层打开时，后退改为关闭播放层。
- **中间**，按场景显示：
  - 详情页：剧名或片名；
  - 播放层：「剧名 · 第3集 标题」；
  - 首页且 hero 有 2 张以上：分页点（6px，当前项拉长到 22px）。
- **右侧**：窗口按钮，关闭按钮悬停时显示危险色。
- 空白处可以拖动窗口，双击最大化。

**侧栏（208px）**
- 从上到下：搜索框、「首页」、「最近播放」（未登录时禁用）、可播放库列表（图标按 movies/tvshows/其它区分）、底部「设置」。
- 列表可滚动；条目有激活、悬停、按下三种状态。

**导航**
- 路由：home | recent | library(libraryId) | detail(itemId) | search(query) | settings。
- 规则：
  - 重复导航到当前页（或当前库）不做任何事；
  - 已在搜索页时再次搜索，替换当前记录；
  - 从详情进入另一个详情，正常入栈；
  - 历史不持久化。
- 后退的输入：标题栏按钮、Alt+←、Esc（焦点不在输入框里，且事件没被处理过）、鼠标后退键。
- 前进的输入：标题栏按钮、Alt+→、鼠标前进键。
- 滚动位置在首页、资料库（按库分别记）、最近播放、搜索恢复；详情页换了条目就回到顶部。

**全局快捷键**
- Ctrl+F，或 `/`（焦点不在输入框里时）：聚焦侧栏搜索框
- Ctrl+,：打开设置
- Ctrl+1…9：打开第 n 个库
- F5 或 Ctrl+R：刷新当前页数据

**对话框**
- 模态；包含标题、正文、「取消」和「确认」（文字可自定义，危险操作时确认按钮为红色）。
- Esc 或点击背景等同于取消；打开时焦点限制在对话框内，关闭后恢复到原处。
- 多个请求排队依次显示。

**Toast**
- 显示在右下角，最多 3 条；已满时丢弃最旧的非错误 Toast。
- 错误 Toast 需要手动关闭；其余默认 3 秒消失（部分为 6 秒或 8 秒）；鼠标悬停时暂停计时。
- 可以带「重试」按钮。

**错误兜底页**：「出了点问题」+ 错误信息 +「返回首页」/「重试」。

## 附录　原版缺陷（实现时已规避，改动相关逻辑时不要回归）

**播放与后端**
1. 登录凭据从未持久化：keyring 缺少 Windows 后端，落到了内存 mock。
2. IPC 控制没有超时，可能永久挂起。
3. 片源打不开时卡在"正在打开"：原版忽略了 END_FILE(error)。
4. 转码实际不可达。
5. 进度每 3 秒才更新、操作后回弹，靠 400ms 轮询维持。
6. 外挂字幕从未加载；带 RequiredHttpHeaders 的源被直接拒绝。
7. 令牌可能随 302 泄露（`curl-max-redirects` 并不是 mpv 选项）。
8. 连播计划失败会让整次播放失败。
9. 倍速固定上报 1.0；暂停和拖动不上报；转码会话不清理。
10. 退出时的停止上报依赖析构函数；发件箱没有定时重试。
11. Release 版日志丢失。
12. 错误文案中英混杂；筛选项回退只读前 500 条。
13. 锁内做阻塞 I/O。新实现改为 actor 加异步 I/O。
14. 用本地生成的 PlaySessionId 走捷径，服务器从未登记这个会话。新实现每次都调用 PlaybackInfo。

**界面**
- B1：状态回弹，时钟每 3 秒才跳一次。
- B2：倍速和"缓冲中"不显示。
- B3：全屏时仍显示外壳；Esc 直接停止播放；关闭播放层不退出全屏。
- B4：播放结束后，详情、最近播放、资料库的进度不刷新。
- B5：搜索页滚动位置不恢复。
- B6：关于页的版本号和链接是占位。
- B7：在选集面板上移动鼠标也会唤出控制层。
- B8：播放时前进按钮仍可用，会去导航被遮住的页面。
- B9：侧栏列出了音乐、照片等非视频库。
- B10：返回后侧栏搜索框的内容与当前搜索不同步。
- B11：高频轮询（每秒约 5 次 IPC 调用）。
