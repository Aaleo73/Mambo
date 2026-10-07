# Mambo 第三方声明

Mambo 使用下列第三方组件。本应用使用 **MiSans 字体**。各组件的版权仍属于其作者；Mambo 的 GPL-3.0-or-later 许可不取代字体、Microsoft 可分发运行时或其他组件的原有许可。

此文件对应仓库当前锁定依赖。许可原文在 `LICENSES/`。同一 GitHub Release 同时提供 Mambo 源码及全部原生对应源码分包；原生包、补丁、Rust 依赖与逐文件归属已固定并经哈希核验，详见 `docs/decisions/native-distribution.md`。

## 应用与 .NET 组件

| 组件 | 当前锁定版本 / 来源 | 许可与归属记录 |
|---|---|---|
| Mambo | 同一仓库的源代码、构建脚本与版本参数 | GPL-3.0-or-later；根目录 `LICENSE` |
| .NET 自包含运行时、Native AOT、Microsoft.Extensions.DependencyInjection | 10.0.12；[dotnet/runtime](https://github.com/dotnet/runtime/tree/v10.0.12) | MIT，© .NET Foundation and Contributors；`dotnet-10.0.12-license.txt` 与完整 `dotnet-10.0.12-THIRD-PARTY-NOTICES.txt` |
| Windows App SDK | NuGet Microsoft.WindowsAppSDK 2.5.1 及 `src/Mambo.App/packages.lock.json` 锁定的子包；[官方项目](https://github.com/microsoft/WindowsAppSDK) | 实际 NuGet 发布包适用 **Microsoft Software License Terms**，包括可分发代码条款；`Microsoft-WindowsAppSDK-2.5.1-license.txt`、完整 NOTICE 与 WinUI NOTICE。开放源代码仓库的许可不能代替分发包条款 |
| Windows App SDK Machine Learning | 2.1.94，由 Windows App SDK 引入 | 单独的 `Microsoft-WindowsAppSDK-ML-2.1.94-license.txt`；发布目录中的 AI/ML、WebView2、图形与其他附带运行时归属见 Windows App SDK 完整 NOTICE |
| Win2D | NuGet Microsoft.Graphics.Win2D 1.4.0；[官方项目](https://github.com/microsoft/Win2D)；发布目录中的 `Microsoft.Graphics.Canvas.dll` | MIT，© Microsoft Corporation；`Win2D-LICENSE.txt` 取自官方仓库 `winappsdk/main` 分支（修订 `25680382dd2136779e10ea6084f0c5ba437ae288`）。按官方 Win2D README 对该项目公布的 MIT 许可附原文。NuGet 过时的许可链接已重定向到无关文档；包内源修订 `57e06e2c24703526f500035129251d063881a44d` 未公开，因此不宣称该 NuGet 二进制可逐字节重建 |
| CommunityToolkit.Mvvm | 8.4.2；[官方项目](https://github.com/CommunityToolkit/dotnet/tree/v8.4.2) | MIT，© .NET Foundation and Contributors；`CommunityToolkit-8.4.2-license.md`、`CommunityToolkit-8.4.2-ThirdPartyNotices.txt` |
| Serilog | 4.3.0；[官方项目](https://github.com/serilog/serilog)；NuGet 源修订 `726e29c5b172aa8813285be1ad8cc728fd531eab` | Apache-2.0，© Serilog Contributors；`Apache-2.0.txt` |
| Serilog.Sinks.File | 7.0.0；[官方项目](https://github.com/serilog/serilog-sinks-file)；NuGet 源修订 `23c732a8658a0df2a5434fe69b0011800b14f0da` | Apache-2.0，© Serilog Contributors；`Apache-2.0.txt` |
| MiSans Regular / Medium / Semibold / Bold | 本仓库 `src/Mambo.App/Assets/Fonts/`；[小米官方许可](https://hyperos.mi.com/font-download/MiSans%E5%AD%97%E4%BD%93%E7%9F%A5%E8%AF%86%E4%BA%A7%E6%9D%83%E8%AE%B8%E5%8F%AF%E5%8D%8F%E8%AE%AE.pdf) | 小米科技有限责任公司 MiSans 字体知识产权许可协议；完整官方 PDF 为 `MiSans-license.pdf`。字体随应用分发，未修改或独立销售 |
| Feather Icons | 4.29.2；[上游图标库](https://github.com/feathericons/feather/tree/v4.29.2)；`Themes/Icons.xaml` 中的线性路径适配为 WinUI Geometry | MIT，© 2013–2023 Cole Bemis；完整许可 `Feather-4.29.2-LICENSE.txt` |

Microsoft.Windows.CsWin32、xUnit、测试 SDK 等开发/测试工具不作为独立程序集随 Native AOT 应用分发。最终文件清单及哈希见发布目录 `release-manifest.json`。

安装器由 Inno Setup 6.7.3 生成，© Jordan Russell / Martijn Laan，许可原文附 `Inno-Setup-license.txt`。简体中文语言资源原样取自 [官方仓库该版本的贡献翻译](https://github.com/jrsoftware/issrc/blob/is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl)，保留 Zhenghan Yang 的来源声明；文件 SHA-256 为 `7d544b9bb1d142cfa11f2e5d3cc8abe2e55f8e066c5124e3772675aa236e1278`。编译器安装不自带此贡献翻译，仓库单独固定该文件，避免依赖本机额外语言配置。

## 内置画质着色器

| 组件 | 固定来源 | 许可与接入修改 |
|---|---|---|
| Anime4K | v4.0.1，修订 `4029bf701ecaa15f163cdc49cffe5501c1acf410`；[官方源码](https://github.com/bloc97/Anime4K/tree/4029bf701ecaa15f163cdc49cffe5501c1acf410) | Clamp、Restore Soft M、Upscale M/S 为 MIT，© 2019–2021 bloc97；`Anime4K-4.0.1-MIT.txt`。两个 AutoDownscale 文件为 Unlicense，完整逐文件许可保留于 GLSL，另附 `Anime4K-AutoDownscale-Unlicense.txt` |
| AMD FidelityFX CAS | 修订 `9fabcc9a2c45f958aff55ddfda337e74ef894b7f` 的 `ffx-cas/ffx_cas.h`；[官方源码](https://github.com/GPUOpen-Effects/FidelityFX-CAS/tree/9fabcc9a2c45f958aff55ddfda337e74ef894b7f) | MIT，© 2017–2019 Advanced Micro Devices, Inc.；`FidelityFX-CAS-MIT.txt`。移植 FP32 sharpen-only 核心，增加源线性空间处理、固定混合强度与边界保护 |

`third_party/shaders/upstream/` 保留完整使用文件的上游原文，`shaders.lock.json` 记录逐文件来源和 SHA-256。Mambo 的接入修改位于 `adapters/`，生成脚本为 `scripts/build-video-shaders.ps1`，运行时文件与哈希位于 `runtime/`。Anime4K 的网络权重未改动；接入增加 HDR 灰度代理、受限细节增益、匹配尺寸变化的基线、alpha 恢复及 HDR Clamp 位置调整。生成的 GLSL 保留相应完整版权和许可。此处列出的 shader 不需要外部模型下载。

## libmpv、FFmpeg 与原生构建

当前内置播放器采用 [MSYS2 UCRT64 官方包](https://packages.msys2.org/packages/mingw-w64-ucrt-x86_64-mpv)：mpv 0.41.0-8、FFmpeg 9.0.2-1、libplacebo 7.360.1-2，以及实际 DLL 导入闭包的全部原生依赖。mpv 源码标签对应 `41f6a645068483470267271e1d09966ca3b9f413`，支持 D3D11 composition、gpu-next、HDR 与 d3d11va。

`third_party/libmpv/libmpv.lock.json` 固定 105 个包的来源、版本与官方数据库 SHA-256，131 个 DLL 的大小、哈希及包归属。Windows 系统库与显卡驱动由操作系统提供。`LICENSES/libmpv-components.json` 为实际分发组件清单；MSYS2 汇总许可标签仅作索引，各组件和逐文件原始许可保留在 `msys2-native/` 与 `msys2-rust/`，来源及哈希见 `msys2-license-sources.json`。

Mambo 采用 GPL-3.0-or-later。mpv 为 GPL-2.0-or-later；FFmpeg 该包为 GPL-3.0-or-later，另含多种逐文件宽松许可。其他依赖保留 GPL、LGPL、MIT、BSD、ISC、Apache、MPL、zlib 等原有条款及版权信息，不被应用许可覆盖。本软件部分使用 Independent JPEG Group 的工作。当前 JBIG 源代码明确允许 GPL 2 或更高版本；Opus 为官方 1.6.1 发行源码，没有使用历史配方的独立 DNN 模型归档。

### 对应源码与构建输入

同一 [Mambo GitHub Release](https://github.com/Aaleo73/Mambo/releases) 提供应用源码 `Mambo-<version>-sources.zip` 和全部 `Mambo-<version>-native-sources-*.zip`。原生分包包含 156 份 MSYS2 精确版本 `.src.tar.zst`（上游源码、PKGBUILD、.SRCINFO、补丁）及 741 份校验过 Cargo.lock checksum 的原始 `.crate`。构建配方、原 `.BUILDINFO` 环境版本、Rust 构建日志的版本证据和重建锁文件同时提供。

实际 DLL 所属源码包的 PKGBUILD 已与其二进制 `.BUILDINFO` 的配方哈希匹配。静态/头文件输入按原构建的安装版本核验，Rust 原日志的 226 个编译依赖版本均与保存的锁匹配。源码文件名、字节数与 SHA-256 固定在 `LICENSES/native-sources.lock.json`；源码 ZIP 的分包哈希在主包 source-manifest.json 与 Release SHA256SUMS.txt 中提供。获取、重建步骤及未分发的工具/示例范围详见 `docs/decisions/native-distribution.md`。

旧 shinchiro 20260610 构建及其不完整的依赖快照不进入发布包；当时为调查它而保存的上游许可快照和构建配方已从 `LICENSES/` 移除。
## 许可原文来源

GNU 文本从 [gnu.org](https://www.gnu.org/licenses/) 下载；Apache-2.0 从 [apache.org](https://www.apache.org/licenses/LICENSE-2.0.txt) 下载；Microsoft/.NET/Toolkit 文本原样取自本机已锁定并还原的官方 NuGet 包；MiSans PDF 从小米官方网站下载；Feather 文本取自 [v4.29.2 上游许可](https://raw.githubusercontent.com/feathericons/feather/v4.29.2/LICENSE)。文件 SHA-256 与来源在 `LICENSES/sources.json` 中记录。
