# 修复内置播放器无声

日期：2026-10-03。

用户在视频跳转认证修复后反馈画面可播放但没有声音。根因是 `MpvCore` 正常播放分支设置 `ao=auto`：ao 是音频输出驱动列表，auto 不是有效驱动名称。此设置不阻止视频进入 Playing，但音频输出没有初始化。前端没有初始化写入 aid=no；音量为 100，播放会话默认 mute=false。

## 修复

正常播放不覆盖 ao，使用 libmpv 内建的音频驱动选择。仅在 headless 或显式 enableAudio=false 时设置 ao=null，保留无声自动化测试的行为。不修改用户 Windows 主音量、默认音频设备或系统混音器设置。

## 复现与验证

使用本地生成的低幅度 PCM16、48 kHz 双声道 WAV，测试进程音量 15、静音关闭，同样创建 1×1 composition 引擎：

| 配置 | current-ao | 音轨 | audio-out-params |
|---|---|---|---|
| 旧生产配置 ao=auto | 不可用 | auto，未选中 | 不可用 |
| 仅改 ao=wasapi | wasapi | 1，pcm_s16le | 有效 |
| 修复后的生产默认配置 | wasapi | 1，pcm_s16le | 有效 |

同一已保存账号、同一剧集实际复测：HTTP 206、FILE_LOADED、PLAYBACK_RESTART，current-ao=wasapi，音轨已选中，mute=false，audio-out-params 为 48000 Hz / 2 channels。认证信息只在内存中使用，未输出服务器地址、令牌、媒体 ID 或响应正文，也没有上报诊断播放进度。结束时服务器清理转码返回404，该候选为直连，不影响音频验证。

上一轮 NativeOverlaySmoke 只记录 AudioOutputAvailable，并未作为通过条件，无法检出这次无声问题。本次把音频输出、选中音轨、采样率/声道、真实音量/静音控件回写纳入正式界面验证门禁，并给原本无音轨的视频样片附加本地合成的低幅音轨。自动验证证明音频已送入 Windows 音频驱动，不宣称替代人工听感检查。

新增四组真实 libmpv 配置测试读取原生 options/ao，确认正常模式为空驱动列表、禁用音频/headless 为 null，且 audio-device 保持 auto；不加载媒体、不打开声卡，不依赖 CI 音频硬件。

Debug 与 Native AOT 的正式界面音频回归都通过：WASAPI、选中外部 PCM 音轨、48000 Hz / 2 channels、音量回写、静音 / 取消静音回写、恢复播放后时间推进、交换链匹配、正常关闭及停止上报，进程正常退出、没有强制清理。
