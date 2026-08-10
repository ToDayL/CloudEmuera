# ADR-0014：摄取时自动把 UTF-16/UTF-32 文本文件转换为 UTF-8

状态：Accepted

日期：2026-08-10

关联：GAME-003、ADR-0008、P1-03/P1-04

## 背景

部分 era 游戏包内文本文件使用 UTF-16/UTF-32（带 BOM）。当前处理：

- 摄取 `AnalyzeText` 检测到 UTF-16/UTF-32 会记录阻断诊断 `TEXT_UTF16_OR_UTF32_UNSUPPORTED`；
- 校验 `InspectWorkspace`/`TryReadGameText` 只接受 UTF-8（含/不含 BOM）与 CP932，UTF-16 文件
  产生阻断诊断 `TEXT_ENCODING_UNSUPPORTED`，无法启用；
- 上游 Emuera `EncodingHandler.DetectEncoding` 已能按 BOM 自动识别 UTF-16/UTF-32、UTF-8 与
  Shift-JIS（JIS 回退），即运行时本身可读 UTF-16 BOM 文件。

问题在于“静态校验层”比“运行时”更严格：UTF-16 游戏被校验拒绝，而运行时本可读取。

## 选项

- A1（采用）：摄取展开后、分析前，把带 BOM 的 UTF-16/UTF-32 文本文件解码并重写为 UTF-8
  （无 BOM）。工作区/current 内容、manifest、文件索引、摘要与运行时全部基于转换后的规范
  UTF-8，校验/启用/编辑/运行一致；同时记录非阻断诊断 `TEXT_ENCODING_CONVERTED`。
- A2：校验快照内临时转换。激活暂存若不转换会再次失败；转换则与工作区原文摘要/文件索引
  不一致（ETag/哈希漂移）。
- A3：放宽校验，接受 UTF-16 原文。编辑器与摘要仍需按 UTF-16 处理，且与
  `TEXT_UTF16_OR_UTF32_UNSUPPORTED` 的既有语义冲突。

## 决定

- 摄取期把带 BOM 的 UTF-16 LE/BE、UTF-32 LE/BE 文本文件转换为 UTF-8（无 BOM），原子写回
  （同目录临时文件 + renameat + fsync），并更新 `ExtractionResult` 的字节数与摘要；
- 转换失败（严格解码/编码抛错）或转换后超过单文件上限时保留原文，沿用既有阻断诊断；
- Shift-JIS 与 UTF-8 文件不转换（运行时已自动识别；转换 Shift-JIS 有字节级脚本语义风险）；
- 转换不改变行尾符；新增非阻断 `TEXT_ENCODING_CONVERTED` 诊断。

## 后果

- UTF-16 游戏可直接导入、验证、启用并运行，无需手工改包；
- 工作区文本变为 UTF-8（编辑器展示与编辑回写一致）；内容摘要基于转换后字节，跨阶段一致；
- 摘要变化属于一次性规范化；重复上传同一包会得到确定性的同一摘要。

## 验证

- GamePackages：UTF-16LE ERB/CSV 包摄取后磁盘文件为合法 UTF-8、manifest 摘要匹配转换后字节、
  无 `TEXT_UTF16_OR_UTF32_UNSUPPORTED`、含 `TEXT_ENCODING_CONVERTED`；UTF-8/Shift-JIS 包无转换
  诊断；转换失败保留原文并保持阻断诊断；
- `./scripts/check.sh` 全绿。
