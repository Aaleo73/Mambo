# 原生组件对应源码与公开分发

2026-10-05，按用户要求解决 GitHub 二进制发布阻碍。替换此前无法追溯全部静态输入的 shinchiro 20260610 DLL，采用 MSYS2 官方 UCRT64 精确版本包。此前 P8 文档与许可快照保留为历史记录，不能用来描述当前二进制。

## 输入与核验

- mpv 为 0.41.0-8，源码标签对应完整修订 `41f6a645068483470267271e1d09966ca3b9f413`，libmpv API 2.5。FFmpeg 为 9.0.2-1，libplacebo 为 7.360.1-2。
- `third_party/libmpv/libmpv.lock.json` 固定 105 个实际使用的二进制包的下载地址、大小及官方包数据库 SHA-256，以及 131 个 DLL、四个头文件的逐文件哈希。每个 DLL 记录包归属与 PE 普通/延迟导入；递归闭包没有缺失的非系统 DLL。
- [MSYS2 官方镜像说明](https://www.msys2.org/dev/mirrors/)明确提供匹配源码包。实际 DLL 所属包的 `.BUILDINFO` 中 `pkgbuild_sha256sum` 已与对应 `.src.tar.zst` 内 PKGBUILD 原始字节逐个匹配。源码包保留上游源码、.SRCINFO、PKGBUILD 与补丁，未执行第三方配方。
- 另外纳入实际构建记录中的静态、头文件和生成代码输入：glslang、SPIR-V、Vulkan/AMF/NV codec 头文件、fast_float、xxHash、GLAD、MinGW CRT 等。旧头文件包的二进制已轮换删除时，用父组件 `.BUILDINFO` 的精确安装版本与该版本源码包 `.SRCINFO` 核对，未以最新头文件替代。
- Rust 对应源码包含 741 份 Cargo.lock 锁定的原始 `.crate`，逐个与注册表 checksum 匹配。libdovi 和 librsvg 原构建日志实际编译的 226 个不同依赖版本均与源码锁匹配。librsvg 配方对 windows-sys 的调整已按同一命令重建 Cargo.lock，其所有新增版本与原日志一致。原始版本证据及锁文件保存在 `LICENSES/msys2-rust-build-evidence.json`、`msys2-rust-audit.json` 和 `msys2-build/rust/`。
- 合并得到 156 份 MSYS2 源码包和 741 份 Rust 源码，共 897 份，原始字节合计 2,500,253,358。`LICENSES/native-sources.lock.json` 固定所有文件；分包 ZIP 不改变归档内容。二进制包、源码、许可和构建元数据均有可校验的来源，未复用旧 Mambo 项目资产。

## 明确的构建范围

不把未分发的示例程序、测试、独立插件与通用构建工具说成当前 DLL 的组成部分。额外依赖审计中的八个旧源码包已经从镜像删除；它们不构成当前分发源码缺口：SDL 1.2 只用于 Theora 的 `examples/player_example.c`；Boost 1.82 只用于 Rubber Band 的 `src/test/`；LADSPA 与 Vamp SDK 只用于独立插件目录，均不进入 `librubberband-2.dll`；ncurses 6.4 只用于 LAME 的 `frontend/console.c`。三个旧 gcc-libs 版本是历史构建环境中使用的通用编译器运行库；分发的实际 GCC 16.2 运行库及其对应源码已固定，GNU Runtime Library Exception 原文随包提供。其余通用编译器、Meson、CMake、Python、Cargo 等的精确环境版本保留在 `.BUILDINFO`，不把工具本身纳入播放器发布目录。

GPL/宽松许可并存时按文件保留原有许可，不从 MSYS2 的汇总 SPDX 推断整个源码包只有一种许可。例如 JBIG 2.1 的 `libjbig/jbig.c` 明确允许 GPL 2 或更高版本；原包 `GPL-2.0` 标签不代表禁止 GPL 3。当前 Opus 使用官方 1.6.1 发行包和其补丁，没有使用旧配方中无独立许可的 DNN 模型下载。`LICENSES/msys2-native/`、`msys2-rust/` 保存实际包及锁定源码中的许可、版权和作者信息，来源/哈希索引为 `msys2-license-sources.json`。通用 GPL、LGPL、Apache 等全文继续随应用提供。

## 获取和重建

1. 获取同一 Release 的 `Mambo-<version>-sources.zip` 及全部 `Mambo-<version>-native-sources-*.zip`，按 SHA256SUMS.txt 校验。主源码包中的 source-manifest.json 列出全部原生分包及其大小和 SHA-256；解压全部分包得到 `native/`。
2. Windows 上构建 Mambo：使用 global.json 锁定的 .NET SDK、Native AOT C++ Build Tools 与 Windows SDK，运行 `pwsh scripts/fetch-libmpv.ps1`、`dotnet restore --locked-mode -p:Platform=x64`、`dotnet build -p:Platform=x64`、`dotnet test`。下载脚本只提取白名单文件并检查哈希，未安装 MSYS2，也不执行 PKGBUILD。
3. 重建原生库时在 MSYS2 UCRT64 环境解压所需 `.src.tar.zst`，按 `LICENSES/msys2-build/<package>/.BUILDINFO` 对应的 BUILDINFO.txt 还原工具与依赖版本，并用归档内 PKGBUILD 执行 `makepkg-mingw`。库对应包、源码名、安装依赖和编译配方均保留。仅重建库时可选择配方的库目标，不需要构建未分发的示例/插件。
4. Rust 库使用 `msys2-build/rust/` 的对应 Cargo.lock；`.crate` 可作为 Cargo 离线 registry/vendor 输入，保持锁定 checksum。librsvg 应先按 PKGBUILD 的补丁及 windows-sys 降级步骤准备源码，再使用保存的结果锁；libdovi 同时保存子目录锁与工作区锁，rav1e 保存上游锁。重建应保留原配方的 MSYS2 目标、features 和编译选项。

不宣称不同宿主和工具链可以得到逐字节相同 DLL。提供的是与已分发组件匹配的完整源码、修改和构建输入；实际分发 DLL 的哈希单独固定。

## 验收和发布绑定

候选 DLL 已通过 32 项真实 libmpv/GPU 测试（0 跳过），包括 D3D11 composition、HDR 参数、三种画质与暂停切换。独立 Native AOT 应用真实 4K HEVC HDR10 样片使用 d3d11va；SwapChainPanel 绑定、尺寸变化、全屏与三种画质确认通过，正常退出码 0。真实连播/进度上报/画面解绑验收通过。

20 轮资源测量仍报告 Section/Mutant 句柄增长；旧运行时历史报告也有同样增长，未将该项写为通过，未借本次源码发布工作扩大 P0 性能结论。完整应用构建、测试与公开发布验证另在 GitHub 更新决策中记录。

发布脚本自动校验所有运行时 DLL 及 897 份源码，任何哈希、大小或成员缺失即失败。`release-readiness.json` 还绑定运行时 lock 与源码 lock 的哈希，变更原生输入后必须更新核验证据。每份原生源码 ZIP 不超过 1500 MiB，和应用源码、便携包、安装器一同进入同一 GitHub Release。
