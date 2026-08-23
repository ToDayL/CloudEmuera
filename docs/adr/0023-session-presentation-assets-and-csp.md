# ADR-0023：Session Presentation Asset 身份、清单与浏览器安全策略

- 状态：已接受
- 日期：2026-08-15
- 关联任务：P1-11

字体范围修订（2026-08-23）：本文关于 SessionRoot 游戏字体的 manifest/HTTP 映射已被
[`ADR-0029`](0029-bundled-font-authoritative-headless-layout.md) 取代。图片和音频的 Session-scoped
asset 规则继续有效；MVP 字体改由产品内置目录按 digest 提供，游戏字体不加载、不发布。

## 背景

P1-11 的浏览器需要读取 SessionRoot 中冻结的图片、Sprite、背景和音频，但不能接触逻辑
路径、Game workspace 或其他用户的文件。旧 Session 也必须可以只读适配，且媒体需要正确 MIME、
Range 和缓存语义。浏览器的动态结构化样式还需要 CSP 明确允许的最小范围。

## 选项

1. 直接暴露 SessionRoot 相对路径。实现简单，但会把路径、链接逃逸和 TOCTOU 风险带入浏览器。
2. API 为每个请求生成临时 opaque ID。隔离较好，但刷新、缓存和旧 Session 重开不稳定。
3. 以冻结 Runtime manifest 的 SHA-256 内容摘要生成 Session-scoped asset ID，并提供 presentation
   manifest 和按 ID 读取端点。资源打开仍通过 protected root/dirfd、no-follow、普通文件、摘要和
   MIME signature 校验。

## 决策

采用选项 3。

- 资源身份为 `sha256-<64 位小写十六进制>`；清单同时返回 `mediaType`、`byteLength`、
  `contentDigest` 和 ETag。只投影 allowlist 中的图片和音频；不公开路径或原始 manifest。
- `GET /api/v1/sessions/{id}/presentation-manifest` 和
  `GET /api/v1/sessions/{id}/assets/{assetId}` 先按 owner 过滤，再执行 `SessionRead` 授权；不存在、
  越权和未知 ID 都使用 not-found 语义。活动 Worker 仍允许只读读取。
- 文件扩展名、allowlist MIME 和 magic signature 必须取交集；打开后的 fd 以冻结长度和摘要复核。
  Linux 使用 `openat(O_NOFOLLOW)` 逐段遍历并拒绝符号链接、硬链接、非普通文件和不安全权限。
- 资源支持完整响应和单一 byte range；多 range 或无效 range 返回 `416`，成功 partial response
  返回 `206`、`Content-Range`、`Accept-Ranges: bytes`。响应使用 `private` cache、内容摘要 ETag、
  `immutable`（asset）或短缓存（manifest）和 `nosniff`。
- 字体不再属于本清单。ADR-0029 的产品内置字体目录、不可变 face ID 和内容寻址 WOFF2 端点是唯一
  Runtime/Web 字体来源；SessionRoot 字体条目即使存在也不投影。
- 生产 CSP 固定为同源 `script-src`、`style-src`、`connect-src`、`img-src 'self' blob:`、
  `media-src 'self'`、`font-src 'self'`，并禁止 `base`、`object`、`frame`、外部 form 和 frame ancestor。
  结构化游戏样式只使用 React 的受控 style 属性，因此通过 `style-src-attr 'unsafe-inline'` 单独放行；
  不允许 `unsafe-eval`、raw HTML、外部 URL 或任意 `data:`。动态 Raster 仅由已校验 PNG bytes 创建
  renderer 管理并及时 revoke 的 Blob URL。
- 缺少或损坏的旧 frozen manifest 不重建 SessionRoot；图片/音频 manifest/asset 服务 fail closed，
  并由页面显示兼容阻断信息。

## 后果

- 需要在 SessionRoot 替换或权限异常时以安全错误停止资源响应，而不是返回 Worker 刚写入的任意字节。
- 同摘要内容可以受浏览器缓存复用，但认证响应永不进入共享公共缓存；manifest 体积和资源并发上限由
  实例配置/后续 P1-13 继续收紧。
- Asset ID 只表达字节身份，不建立 GameVersion、SaveArtifact 或历史资源回滚模型。

## 验证

- OpenAPI 暴露两个端点；`scripts/generate-api-types.mjs --check` 和
  `scripts/verify-generated-contracts.mjs` 检查 DTO、realtime schema、能力摘要与 live OpenAPI 无漂移；
  manifest 与 stream 的正常、跨用户、未知 ID、digest mismatch、MIME signature、symlink/hardlink、ETag
  和 range 测试通过。
- 生产/开发 CSP 均无 `unsafe-eval`，浏览器组件测试证明安全 HTML AST 不创建 raw markup，Raster URL
  在卸载/替换时 revoke。
