# 许可与归属原文

此目录保存第三方组件的官方许可原文和原生组件的来源记录，随应用一起分发。总览见仓库根目录的 `THIRD_PARTY_NOTICES.md`。

- **根目录下的许可文本**：GNU、Apache、Microsoft / .NET / Toolkit、MiSans、Feather、Inno Setup、画质着色器，以及 mpv、FFmpeg、Opus 的上游许可说明。来源和 SHA-256 记录在 `sources.json`；Windows App SDK、.NET、Toolkit 的 NOTICE 覆盖各自发布包附带的组件。
- **`libmpv-components.json`**：内置播放器实际分发的 MSYS2 包清单，含版本、对应源码归档、许可标签和所属 DLL。MSYS2 的汇总许可标签只作索引。
- **`msys2-native/`、`msys2-rust/`**：各原生包和 Rust 依赖的原始许可、版权与作者信息；来源及哈希索引为 `msys2-license-sources.json`。
- **`msys2-build/`**：各包的 PKGBUILD 与构建环境记录，`rust/` 下是 Rust 库的 Cargo.lock。重建原生库时使用。
- **`native-sources.lock.json`**：对应源码归档的文件名、字节数和 SHA-256，发布脚本按它获取并打包源码。
- **`release-readiness.json`**：发布脚本读取的发布条件，绑定运行时 lock 与源码 lock 的哈希。原生依赖变更后要重新核验并更新。
- **`msys2-rust-audit.json`、`msys2-rust-build-evidence.json`**：Rust 依赖实际编译版本的核对记录，以及对应的上游构建日志摘录。

多许可项目保留各自的原文，不改写成单一许可。许可文件不替代对应源码：对应源码随每个 GitHub Release 提供，范围与重建方法见 `docs/decisions/native-distribution.md`。

这些文件带 SHA-256 记录，按原始字节保存（见 `.gitattributes`），不要调整换行或空白。
