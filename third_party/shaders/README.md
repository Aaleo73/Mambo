# 内置画质着色器

`upstream/` 是按固定 Git 修订下载的逐字节原文，`shaders.lock.json` 记录下载地址、许可和 SHA-256。不得从用户的 mpv 配置或旧项目复制资源。

`adapters/` 是 Mambo 的接入代码；`runtime/` 是安装包实际使用的两份合并 GLSL 及哈希清单。生成和离线复核：

```powershell
pwsh scripts/build-video-shaders.ps1
pwsh scripts/build-video-shaders.ps1 -Verify
```

清晰使用 AMD CAS 的 FP32 sharpen-only 核心，逐 RGB 通道权重、真实 sqrt/div、sharpness=0、结果混合 0.35。核在 `adapters/cas.glsl`，由两个条件互补的 pass 共用，每帧只运行其一：放大和等大时在 MAIN 按源 TRC 线性化并除以 `max(linearize(1))`，最后还原；缩小时改在 SCALED 运行，libplacebo 在线性光下缩小，那里已经是同一种线性 RGB，不再施加传递函数。负值、非有限值或超范围邻域直通。两处都在色调映射之前，与 AMD 推荐的色调映射后集成不同，必须在所锁定 libmpv 上验收。

动画使用 Anime4K v4.0.1 的 B Fast 顺序：Clamp、Restore Soft M、Upscale M、AutoDownscale x2、AutoDownscale x4、Upscale S。保留原来的尺寸条件。源线性白值大于 1.01 时进入 HDR 包装：maxRGB 通过有界压缩和 2.2 次幂编码生成灰度代理，网络只处理代理；在原 HDR RGB 上施加 0.9–1.1 的统一增益，保留色度比与 alpha，平坦区及黑位不增强。未增强代理和原 HDR RGB 随每次尺寸变化使用相同的采样步骤，避免将级联插值差异当作网络残差。

SDR Clamp 保留 PREKERNEL 位置；HDR 在返回原色彩空间前于 MAIN 对代理进行 Clamp，后续 PREKERNEL Clamp 直通。HDR 原图在神经网络更改的中间尺寸上进行采样，因此动画并不承诺与标准模式的缩放基线像素相同。包装不改变 mpv 的 HDR 输出策略或显示器设置，也不会把 HDR 转为 SDR。

六份 Anime4K 上游文件中，两个 AutoDownscale 文件为 Unlicense，其余为 MIT；各文件头保留完整许可。CAS 完整 MIT 许可也进入生成文件。来源副本和接入改动同时保存在仓库，数值契约测试仅验证数学不变量，不能代替 D3D11 编译、图像和性能验收。
