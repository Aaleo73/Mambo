# Opus DNN 模型归档的许可证据边界

核对日期：2026-10-03。此记录说明已经取得的证据，不是整个模型包或锁定 DLL 的许可放行声明。

## 锁定构建输入

`shinchiro/mpv-winbuild-cmake` 修订 `5efd298cb51513c2410e4e9029b5e56b83c2aaac` 的 [opus-dnn.cmake](https://github.com/shinchiro/mpv-winbuild-cmake/blob/5efd298cb51513c2410e4e9029b5e56b83c2aaac/packages/opus-dnn.cmake) 固定下载：

- [官方模型归档](https://media.xiph.org/opus/models/opus_data-8a07d57c4fce6fb30f23b3e0d264004e04f1d7b421f5392ef61543d021a439af.tar.gz)。
- 字节数：165,994,834。
- SHA-256：`8a07d57c4fce6fb30f23b3e0d264004e04f1d7b421f5392ef61543d021a439af`，已与配方核验一致。

归档包含 `dnn/models/*.pth` 训练权重和 21 份生成 C/头文件，未发现 LICENSE、COPYING、README 或 AUTHORS。仅列出归档并读取文本；没有反序列化或执行模型。

[opus.cmake](https://github.com/shinchiro/mpv-winbuild-cmake/blob/5efd298cb51513c2410e4e9029b5e56b83c2aaac/packages/opus.cmake) 把生成 C/头文件复制到 Opus `dnn/`，然后进行 Meson 构建；Opus 指向未固定的 `main`。配方本身不证明哪些 DNN 模块最终进入 DLL，也不证明其编译配置或确切 Opus 修订。

## 官方发布包证据

官方 [Opus 1.6.1 发行页](https://www.opus-codec.org/release/stable/2026/01/14/libopus-1_6_1.html)提供 [源码包](https://downloads.xiph.org/releases/opus/opus-1.6.1.tar.gz)并公布 SHA-256：

`6ffcb593207be92584df15b32466ed64bbec99109f007c82205f0194572411a1`。

已下载并核验。该包包含 BSD 三条款 `COPYING` 和生成的 `dnn/*_data.c/h`；原文附为 `opus-1.6.1-COPYING.txt`、`opus-1.6.1-dnn-README.md`。README 将 LPCNet 软件描述为 BSD 许可，并说明 Git 构建时另下载模型。官方[许可页](https://www.opus-codec.org/license/)也说明其参考实现和官方修订实现适用 BSD 三条款。这些一般声明没有单独列出锁定模型归档的 SHA 或全部权重。

将上述发行包与锁定模型归档逐文件、逐字节比较，21 份生成文件中 7 份相同：`lace_data.h`、`nolace_data.h`、`fargan_data.h`、`pitchdnn_data.h`、`plc_data.h`、`lossgen_data.c`、`lossgen_data.h`。所有文件的 SHA-256、字节数和对比结果在 `opus-model-generated-files.json`。其他文件可能包含不同模型、生成器结果或格式；没有据此宣称内容或许可相同。

## 未确认的范围

此证据支持上面的 7 份字节相同文件在官方 BSD 发布包中的归属。它不确认其余 14 份生成文件和 `.pth` 权重的整个归档许可，也不证明锁定 DLL 使用 Opus 1.6.1。`opus-dnn` 的单一 SPDX 与整个模型包许可仍保持未确认；没有按推测套用 MIT 或 BSD。

公开分发前需要取得该准确模型归档的明确许可归属，以及实际 Opus 修订、编译配置、补丁和对应源码。仅添加 BSD 主项目文本不能使整个预构建 libmpv 的对应源码要求齐备。
