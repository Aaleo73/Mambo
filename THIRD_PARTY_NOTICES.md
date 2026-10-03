# Mambo 第三方声明

Mambo 使用下列第三方组件。本应用使用 **MiSans 字体**。各组件的版权仍属于其作者；Mambo 的 GPL-3.0-or-later 许可不取代字体、Microsoft 可分发运行时或其他组件的原有许可。

此文件对应仓库当前锁定依赖。许可原文在 `LICENSES/`。**当前二进制输出仅供本地验收，暂不能公开分发便携包或安装器。** 完整对应源码归档与原生组件修订核对尚未完成，下列源码入口与开发源码 ZIP 尚不构成齐备的 GPL 对应源码包。

## 应用与 .NET 组件

| 组件 | 当前锁定版本 / 来源 | 许可与归属记录 |
|---|---|---|
| Mambo | 同一仓库的源代码、构建脚本与版本参数 | GPL-3.0-or-later；根目录 `LICENSE` |
| .NET 自包含运行时、Native AOT、Microsoft.Extensions.DependencyInjection | 10.0.12；[dotnet/runtime](https://github.com/dotnet/runtime/tree/v10.0.12) | MIT，© .NET Foundation and Contributors；`dotnet-10.0.12-license.txt` 与完整 `dotnet-10.0.12-THIRD-PARTY-NOTICES.txt` |
| Windows App SDK | NuGet Microsoft.WindowsAppSDK 2.5.1 及 `src/Mambo.App/packages.lock.json` 锁定的子包；[官方项目](https://github.com/microsoft/WindowsAppSDK) | 实际 NuGet 发布包适用 **Microsoft Software License Terms**，包括可分发代码条款；`Microsoft-WindowsAppSDK-2.5.1-license.txt`、完整 NOTICE 与 WinUI NOTICE。开放源代码仓库的许可不能代替分发包条款 |
| Windows App SDK Machine Learning | 2.1.94，由 Windows App SDK 引入 | 单独的 `Microsoft-WindowsAppSDK-ML-2.1.94-license.txt`；发布目录中的 AI/ML、WebView2、图形与其他附带运行时归属见 Windows App SDK 完整 NOTICE |
| CommunityToolkit.Mvvm | 8.4.2；[官方项目](https://github.com/CommunityToolkit/dotnet/tree/v8.4.2) | MIT，© .NET Foundation and Contributors；`CommunityToolkit-8.4.2-license.md`、`CommunityToolkit-8.4.2-ThirdPartyNotices.txt` |
| Serilog | 4.3.0；[官方项目](https://github.com/serilog/serilog)；NuGet 源修订 `726e29c5b172aa8813285be1ad8cc728fd531eab` | Apache-2.0，© Serilog Contributors；`Apache-2.0.txt` |
| Serilog.Sinks.File | 7.0.0；[官方项目](https://github.com/serilog/serilog-sinks-file)；NuGet 源修订 `23c732a8658a0df2a5434fe69b0011800b14f0da` | Apache-2.0，© Serilog Contributors；`Apache-2.0.txt` |
| MiSans Regular / Medium / Semibold / Bold | 本仓库 `src/Mambo.App/Assets/Fonts/`；[小米官方许可](https://hyperos.mi.com/font-download/MiSans%E5%AD%97%E4%BD%93%E7%9F%A5%E8%AF%86%E4%BA%A7%E6%9D%83%E8%AE%B8%E5%8F%AF%E5%8D%8F%E8%AE%AE.pdf) | 小米科技有限责任公司 MiSans 字体知识产权许可协议；完整官方 PDF 为 `MiSans-license.pdf`。字体随应用分发，未修改或独立销售 |
| Feather Icons | 4.29.2；[上游图标库](https://github.com/feathericons/feather/tree/v4.29.2)；`Themes/Icons.xaml` 中的线性路径适配为 WinUI Geometry | MIT，© 2013–2023 Cole Bemis；完整许可 `Feather-4.29.2-LICENSE.txt` |

Microsoft.Windows.CsWin32、xUnit、测试 SDK 等开发/测试工具不作为独立程序集随 Native AOT 应用分发。最终文件清单及哈希见发布目录 `release-manifest.json`。

安装器由 Inno Setup 6.7.3 生成，© Jordan Russell / Martijn Laan，许可原文附 `Inno-Setup-license.txt`。简体中文语言资源原样取自 [官方仓库该版本的贡献翻译](https://github.com/jrsoftware/issrc/blob/is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl)，保留 Zhenghan Yang 的来源声明；文件 SHA-256 为 `7d544b9bb1d142cfa11f2e5d3cc8abe2e55f8e066c5124e3772675aa236e1278`。编译器安装不自带此贡献翻译，仓库单独固定该文件，避免依赖本机额外语言配置。

## libmpv、FFmpeg 与原生构建

`mpv/libmpv-2.dll` 来自 [shinchiro 官方构建发布 20260610](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20260610)，没有从旧 Mambo 项目复制。

- 构建包：`mpv-dev-x86_64-20260610-git-304426c.7z`。
- 构建包 SHA-256：`8cbb25ea784f01afbb3f904217cab1317430a8bcfd5680fd827a866367f71cc9`。
- DLL SHA-256：`5c876d79e070529128331591b48f87846fb30557f19c11280df9c6ee9b6dbafa`。
- mpv 修订：[304426c390901436fb1d4a63efbd582ae80c88f4](https://github.com/mpv-player/mpv/tree/304426c390901436fb1d4a63efbd582ae80c88f4)；DLL 内标识 `mpv v0.41.0-744-g304426c39`。mpv 默认 GPL-2.0-or-later，亦含逐文件宽松许可；此构建没有声明 LGPL-only。完整修订版权说明为 `mpv-Copyright.txt`。
- FFmpeg 修订：[2576e09434d8026aab1769481b7b2fb43aa567c3](https://github.com/FFmpeg/FFmpeg/tree/2576e09434d8026aab1769481b7b2fb43aa567c3)；DLL 内标识 `N-124930-g2576e0943`。所锁定构建配方启用 `--enable-gpl --enable-version3`，按该配置适用 GPL-3.0-or-later；对应修订许可说明为 `FFmpeg-LICENSE.md`。
- FFmpeg 带有 Independent JPEG Group 的代码。本软件部分使用 Independent JPEG Group 的工作；Mambo 没有修改 FFmpeg 的 `jfdctfst.c`、`jfdctint_template.c`、`jrevdct.c`。第三方预构建产物的补丁核对仍见下文缺口。
- 构建脚本：[shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/tree/5efd298cb51513c2410e4e9029b5e56b83c2aaac)，发布标签对应修订 `5efd298cb51513c2410e4e9029b5e56b83c2aaac`；[原始工作流](https://github.com/shinchiro/mpv-winbuild-cmake/actions/runs/27243718577)。关键配方原文附为 `winbuild-20260610-mpv.cmake` 与 `winbuild-20260610-ffmpeg.cmake`。

### 原生组件清单及证据边界

`LICENSES/libmpv-components.json` 列出从锁定 mpv / FFmpeg 配方递归获得的依赖，包括可选分支和构建头文件：视频编解码（aom、dav1d、davs2、uavs3d、SVT-AV1、x264、x265、Xvid、libvpx）、图像与颜色（libjpeg-turbo、libpng、libwebp、libjxl、lcms2、zimg / graphengine）、音频（LAME、Opus、opus-dnn、Ogg、Vorbis、Speex、rubberband、libsamplerate、libsoxr、libbs2b、libopenmpt、libmodplug、libmysofa、OpenAL Soft）、字幕与字体（libass、FreeType、Fontconfig、FriBidi、HarfBuzz、libaribcaption、libzvbi、libunibreak、uchardet、libsixel、subrandr）、DVD / Blu-ray（libdvdcss、libdvdnav、libdvdread、libbluray、libudfread）、脚本与帧处理（LuaJIT、MuJS、VapourSynth、AviSynth、SDL2）、GPU（ANGLE、Vulkan、shaderc、SPIR-V / glslang、libplacebo、AMF、NV codec、libva、libvpl）、网络/归档与其他依赖（OpenSSL、libssh、SRT、libarchive、libxml2、Expat、Brotli、bzip2、xz、lzo、zlib-ng、zstd、Highway、fast_float、xxHash、glad）。

上面的配方依赖集合包含构建期或条件依赖，并不代表每项都已经在 DLL 内独立识别。JSON 逐项记录来源配方、上游、已取得的许可快照和未确认事项；`libmpv-upstream/` 保存实际下载的官方上游许可原文。部分上游同时使用多种逐文件许可，GitHub 的 `NOASSERTION` 仅表示无法给出单一 SPDX 标识，应阅读原文，不能把它理解为无许可。

许可证族包括 GPL、LGPL、Apache-2.0、MPL-2.0、MIT、BSD、ISC、zlib、WTFPL 及项目专用条款。已确认的单一 SPDX 结果与多许可原文入口列在 JSON；没有原文或没有对应修订的项明确标记待核对。通用 GNU GPLv2、GPLv3、LGPLv2.1、Apache-2.0 全文亦附于 `LICENSES/`。

Fontconfig 原文取自[官方 Git 修订 d416ada7b20a1cdd5050b60a7a1bbdb3d9ddd2dc](https://gitlab.freedesktop.org/fontconfig/fontconfig/-/blob/d416ada7b20a1cdd5050b60a7a1bbdb3d9ddd2dc/COPYING)，包含主项目、Unicode 数据与逐文件条款；这只是许可快照，尚未确认与锁定 DLL 的 Fontconfig 修订相同。

Opus DNN 配方固定的模型归档已按 SHA-256 核验；归档没有独立 LICENSE/COPYING。另已核验[官方 Opus 1.6.1 发布源码](https://www.opus-codec.org/release/stable/2026/01/14/libopus-1_6_1.html)的归档哈希，附其 BSD `COPYING` 和 DNN README。模型归档的 21 份生成 C/头文件中有 7 份与该发布包逐字节相同；其余文件、训练权重 `.pth` 与实际 DLL 的 Opus 修订仍待确认。因此不把主项目的 BSD 许可或 README 的一般说明扩大为整个模型归档已获许可的结论。范围与逐文件哈希见 `LICENSES/opus-model-license-evidence.md` 和 `opus-model-generated-files.json`。

### 对应源码尚待补齐

Mambo 源代码是本仓库；此检出尚未配置公开 Git 远端，不编造下载地址。向第三方发布二进制前应同时提供对应 Mambo 源代码快照、完整原生依赖源码及补丁、实际构建工具/选项，并在发布页面提供可用的对应源码下载入口。

当前 libmpv dev 归档只含 DLL、导入库和头文件。发布脚本另生成 `Mambo-<version>-sources.zip`，含当前 Mambo 源码及准确修订的 mpv、FFmpeg、shinchiro 构建脚本原始源码 tar.gz；来源、字节数与 SHA-256 在 `LICENSES/native-sources.lock.json` 中固定。这个 ZIP 可以作为本地开发归档继续使用。构建脚本能说明配方，却没有钉住所有 Git 依赖修订；已保存的许可快照不能证明对应二进制的逐文件归属完全一致。

原始公开 CI 页面当前没有可下载 Artifacts；锁定工作流的依赖日志只保留 1 天，日志 Gist 以 amend/force 覆盖更新。发行资产列表只提供二进制和构建脚本仓库归档，没有全部静态依赖的对应源码包；锁定配方没有生成完整依赖修订清单。这些证据无法恢复全部构建时依赖修订。**在取得匹配的全部源码、补丁/生成文件、许可及构建配置之前，当前 libmpv 二进制及包含它的 Mambo 发布物不能公开分发。** 后续可取得原始构建者的完整对应源码，或改为自行构建并逐项固定所有原生输入；不能通过附上三份主工程源码或最新依赖源码消除缺口。

## 许可原文来源

GNU 文本从 [gnu.org](https://www.gnu.org/licenses/) 下载；Apache-2.0 从 [apache.org](https://www.apache.org/licenses/LICENSE-2.0.txt) 下载；Microsoft/.NET/Toolkit 文本原样取自本机已锁定并还原的官方 NuGet 包；MiSans PDF 从小米官方网站下载；Feather 文本取自 [v4.29.2 上游许可](https://raw.githubusercontent.com/feathericons/feather/v4.29.2/LICENSE)。文件 SHA-256 与来源在 `LICENSES/sources.json` 中记录。
