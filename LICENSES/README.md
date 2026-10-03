# 许可与归属原文

此目录保存官方许可原文，不从旧项目复用。`sources.json` 给出来源及 SHA-256；Windows App SDK、.NET、Toolkit 的 NOTICE 覆盖各自发布包附带的组件。

`libmpv-components.json` 是锁定构建配方的递归依赖清单，包含条件依赖/构建头文件，不能等同于最终 DLL 的完整组成证明。`libmpv-upstream/` 为官方上游默认分支的许可快照；版本对应尚待核对的项在 JSON 和根 `THIRD_PARTY_NOTICES.md` 中说明。

GitHub API 识别为 NOASSERTION 的文件已保留原文，不把多许可项目改写成单一许可。下载响应经文本检查，访问挑战/HTML错误页未作为许可证收入。尚无可用原文的组件仍在清单标记待核对。

Fontconfig 的官方 Git COPYING 已补齐，固定了许可快照修订；仍不能证明它对应 20260610 的 DLL。Opus DNN 的官方发行证据和生成文件对比见 `opus-model-license-evidence.md`，整个模型包的许可范围仍待确认。

**当前含 libmpv 的二进制输出仅供本地验收，暂不能公开分发。** 公开二进制分发前仍需补齐对应源码与所有原生组件的修订/逐文件归属。此目录的许可证文件不替代对应源码；开发源码 ZIP 也不是完整的原生对应源码包。
