# ADR-0019：MVP 使用受限 libgdiplus 兼容 Emuera 动态 Graphics

状态：已接受

日期：2026-08-12

## 背景

固定上游 Emuera.EM+EE 的 `GraphicsImage`、`ConstImage`、`SpriteF/G/Anime` 直接依赖
`System.Drawing.Bitmap`、`Graphics`、`Font`、颜色矩阵和逐像素访问。P1-07 要求 Linux Worker 支持
`GCREATE/GDRAW*/GGETCOLOR`、动态 Sprite 和 CBG，而不是把计时输入或动态绘图列为 MVP blocker。

设计原先选择 SkiaSharp/HarfBuzzSharp 作为长期图像与字体后端。验证表明，完整迁移必须同时重写上述
上游对象及文字、mask、旋转、颜色矩阵和像素数组语义，工作量会显著扩大 P1-07。另一方面，固定
`System.Drawing.Common 6.0.0`、启用其 Unix compatibility switch 并安装 `libgdiplus`，已在 .NET 10
Linux 开发容器中通过真实 ERB 的 `GCREATE → GCLEAR → CBGSETG` 场景。

## 决定

1. P1 MVP 的 Worker 固定使用 `System.Drawing.Common 6.0.0` 与镜像提供的 `libgdiplus`，复用固定
   上游的像素实现。项目目标框架仍为 `net10.0`；此组合是 CloudEmuera 自行验证和维护的兼容层，
   不宣称获得现代 .NET 的非 Windows 官方支持。
2. `System.Drawing` 类型不得进入 Domain、Application、RuntimeAdapter、IPC 或浏览器契约；它只存在
   于 `CloudEmuera.EmueraRuntime.UpstreamHeadless` 内部。Worker 将 CBG surface 冻结为有界 PNG
   `RasterDrawable`，或将 manifest Sprite 表达为结构化 `SpriteDrawable`。
3. 每个 Graphics 的尺寸、总像素内存、动态 surface 数量、PNG normal/hover 合计字节、scene 项数和
   IPC envelope 都必须有硬上限。超限确定性失败，不允许无限帧推流。
4. `GLOAD/GSAVE` 保留上游的 SessionRoot 原生 `imgNNNN.png` 语义；`GCREATEFROMFILE` 仅接受相对路径，
   并按原模式规范化到 SessionRoot 或其 `resources/` 后验证边界。绝对路径与目录穿越返回失败；
   `CALLSHARP`、进程、网络和桌面输入继续 fail closed。
5. Linux x64 是 MVP 发布基线。ARM64 只有在相同真实 Graphics fixture 通过后才能声明支持。
6. SkiaSharp/HarfBuzzSharp 保留为长期替换方向，不阻塞 P1 MVP。替换时以相同 ERB fixture 和像素
   golden 为兼容门，协议层无需暴露或感知具体绘图库。

## 备选方案

1. 在 P1-07 内完整迁移 SkiaSharp：长期维护性更好，但需要重写上游整套图像对象，推迟 MVP，拒绝。
2. 保持动态 Graphics Blocked：违背 MVP 完整支持 Emuera 的当前要求，拒绝。
3. 把原始 GDI+/Bitmap 对象跨 IPC 传递：不可序列化且破坏 Worker 边界，拒绝。

## 后果

- 能以最小上游改动保留 Emuera 的逐像素、mask、文字和动态 Sprite 语义。
- 生产与开发镜像增加 `libgdiplus` 及字体/图像原生依赖；基础镜像升级必须重新验证。
- `System.Drawing.Common 6.0.0` 是有意固定的兼容依赖，NuGet 漏洞审计和锁文件漂移检查属于发布门。
- libgdiplus 与 Windows GDI+ 的字体、抗锯齿和少数混合细节可能不同，必须以明确的兼容测试和诊断
  管理差异，不能宣称像素级完全相同而没有证据。

## 验证

- 真实解释器 ERB 覆盖创建、清除、像素读写、图元、合成、动态 Sprite、CBG 图层与按钮；
- RuntimeAdapter/IPC 覆盖 Raster normal/hover、动画帧、大小上限和 round-trip；
- 开发与生产镜像 smoke test 证明 `libgdiplus` 可加载；
- `dotnet list package --vulnerable --include-transitive`、locked restore、完整 `scripts/check.sh` 通过。
