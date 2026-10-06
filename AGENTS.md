# Codex maintenance entry / Codex 维护入口

This file applies to the whole repository. / 本文件适用于整个仓库。

- Before editing, check the working tree and read [README](README.md#maintenance-entry--维护入口), the relevant [maintenance workflow](docs/MAINTENANCE_GUIDE.md), and the selected [task](docs/TODO.md). Preserve unrelated changes; task status lives only in TODO.
- 修改前核对工作区，按 README → 维护指南 → 对应任务读取；保留无关改动，任务状态只在 TODO 维护。
- Before investigating a failure, search [engineering lessons](docs/troubleshooting/engineering-lessons.md), affected task evidence and `PROJECT_MEMORY.md` by the error text and module. Verify that an old method still applies to the current source/environment.
- 遇到问题先按错误文本和模块检索排障经验、任务证据与 PROJECT_MEMORY；复用前核对当前代码、环境和适用边界。
- After resolving a nontrivial failure, update the matching lesson before handoff: symptom, trigger, confirmed cause or uncertainty, effective steps, verification, applicability and evidence. Extend an existing entry instead of duplicating it; a later successful retry does not prove the cause. Follow [the recording workflow](docs/MAINTENANCE_GUIDE.md#problem-resolution-records--问题解决记录).
- 解决非平凡问题后，交付前必须更新对应经验条目；记录现象、触发条件、已确认原因或不确定性、有效步骤、验证、适用边界和证据。优先补充原条目，不能把重试成功当作根因已确认。没有新增经验时，在交付中说明复用了哪条经验或没有可复用的新结论。
- Record each scoped change in `docs/FEATURE_CHANGELOG.md` and run its required checks. Keep unresolved diagnostics in the task index. Never put credentials/private keys, signed download query strings or raw sensitive logs in documentation.
- 每次变更同步变更日志及必要验证；未解决的诊断按任务索引保留。文档不得包含凭据、私钥、带签名的下载查询参数或敏感原始日志。
- Use `build/resolve-dotnet.ps1` for the pinned SDK. Do not weaken TLS/certificate/CRL checks or dependency-audit gates. Commit, push, tag and release require explicit publication authorization; ordinary continuation is not publication authorization.
- 使用项目 SDK 解析入口，不放宽 TLS、证书、CRL 或依赖审计门禁；普通继续维护不等于提交、推送、打标签或发布授权。
