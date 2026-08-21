# Architecture Decision Records

ADR 使用 `NNNN-short-title.md` 命名，至少包含状态、背景、决定、备选方案和后果。详细设计第 19 节列出的待决事项应在进入对应实现前形成 ADR。

当前索引：

- [ADR-0001：本地身份与 OIDC 重新评审触发条件](0001-local-identity-and-oidc-trigger.md)
- [ADR-0003：ConsoleSnapshot 有界内存与恢复语义](0003-console-snapshot-bounds.md)
- [ADR-0004：Runtime 富内容安全结构化语义](0004-runtime-rich-content-allowlist.md)
- [ADR-0005：Emuera 固定源码直接集成](0005-vendored-emuera-source.md)
- [ADR-0006：CI runtime compatibility fixtures](0006-ci-runtime-compatibility-fixtures.md)
- [ADR-0007：SessionRoot 直接持有 Emuera 原生存档](0007-session-root-native-save-ownership.md)
- [ADR-0008：安全 ZIP 摄取边界与配额口径](0008-secure-zip-ingestion-policy.md)
- [ADR-0009：草稿发布事务与不可变版本身份（已被 ADR-0010 取代）](0009-draft-publication-and-immutable-version-identity.md)
- [ADR-0010：单一 Game 内容模型，不建立 GameVersion 实体](0010-single-game-content-without-version-entities.md)
- [ADR-0011：游戏包单根目录自动展平与固定文件名大小写不敏感查找](0011-single-root-flatten-and-fixed-case-lookup.md)
- [ADR-0012：游戏包暂存配额按实际大小预留](0012-actual-size-staging-reservation.md)
- [ADR-0013：逻辑删除的 Game 释放名称，活动名称保持唯一](0013-reusable-game-name-after-delete.md)
- [ADR-0014：摄取时自动把 UTF-16/UTF-32 文本文件转换为 UTF-8](0014-auto-convert-utf16-to-utf8-on-ingestion.md)
- [ADR-0015：API 直接管理 Session Worker 生命周期](0015-api-owned-worker-lifecycle.md)
- [ADR-0016：Session 是可反复开启的持久 SessionRoot](0016-reopenable-session-root-lifecycle.md)
- [ADR-0017：可信参与者自托管 MVP 的简化边界](0017-trusted-self-hosted-mvp-simplification.md)
- [ADR-0018：Emuera 完整结构化交互状态、能力矩阵、计时语义和 IPC major 升级](0018-emuera-structured-interaction-model.md)
- [ADR-0019：MVP 使用受限 libgdiplus 兼容 Emuera 动态 Graphics](0019-libgdiplus-mvp-graphics-compatibility.md)
- [ADR-0020：API 快照镜像与有界实时输出](0020-api-snapshot-mirror-and-bounded-realtime-output.md)
- [ADR-0021：冻结 Realtime WebSocket v1 与输入回执边界（输入部分已被 ADR-0025 取代）](0021-freeze-realtime-websocket-v1.md)
- [ADR-0022：Session 原生存档单文件大小上限](0022-session-save-file-cap.md)
- [ADR-0023：Session Presentation Asset 身份、清单与浏览器安全策略](0023-session-presentation-assets-and-csp.md)
- [ADR-0024：HTML_PRINT 使用固定上游解析器并在 headless 边界安全翻译](0024-html-print-upstream-parser-authority.md)
- [ADR-0025：浏览器输入投递到当前 Runtime 输入槽](0025-current-input-slot-realtime-v2.md)
- [ADR-0026：显示提交边界与原子 Realtime v3 帧](0026-display-commit-boundary-realtime-v3.md)
