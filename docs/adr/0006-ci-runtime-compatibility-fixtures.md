# ADR-006：CI 运行时兼容测试资产

- 状态：Accepted
- 日期：2026-08-04
- 范围：P0-01 及其依赖的 P0-04/P0-05

## 背景

真实 Era 游戏通常包含作者、字体、图片、音频和其他资源。即使游戏代码公开，也不能从仓库提交记录推导出这些资源拥有可再分发到 CI 的授权。把真实游戏、运行时下载或开发者本地文件作为 CI 阻断条件，会使构建不可审计、不可离线复现，并可能违反版权约束。

P0-01 还需要同时表达 `1824+v18` 兼容面、当前固定 EM+EE 特性、两种原生存档布局、输入路径和媒体引用。这里的 transcript 是后续运行时 harness 的预期基线，不是已经在无 UI 环境执行成功的证明。

## 决定

CI 只使用仓库内由 CloudEmuera contributors 创作的最小合成资产。资产清单中的 `source` 固定为 `authored-for-cloudemuera`，SPDX 许可证固定为 `Apache-2.0`。`tests/fixtures/runtime/` 中的两套 profile 为：

- `v18-core`：受控的 v18-compatible 语法画像，覆盖基础启动、PRINT、变量、函数、分支、INPUT、HTML、图片/Sprite 引用和根目录存档语义；
- `em-ee-core`：绑定当前 `RuntimeBaseline` 的 `em-ee-current` 画像，使用固定上游 README 明确记录的 `EXISTFUNCTION` 扩展，并覆盖 `sav/` 存档语义。

两套资产都必须通过 `manifest.json` 登记逐文件 SHA-256、媒体类型、编码、来源和许可证。验证只读取仓库本地文件，默认模式不得改写 manifest，也不得访问网络。`tests/fixtures/runtime-local/` 供开发者放置获授权的真实游戏，目录被 gitignore，不能使 CI 通过或失败。

允许的 payload 是 ERB、CSV、`emuera.config`、JSON、预期 transcript 和项目原创媒体。禁止 DLL、可执行文件、外部 URL、网络依赖、插件和来源不清的二进制。P0-01 只做静态契约验证；真正的 ERB 执行、无 UI 输入和原生存档读写由 P0-04/P0-05 验证。

## 上游依据

fixture 语法和布局依据当前固定提交 `2175f8a629257efb08214e093704b3a3d3d06d05`：

- `Emuera/Runtime/Config/ConfigData.cs` 和 `Config.cs` 定义 `UseSaveFolder`、`SavDir` 以及根目录/`sav/` 切换；
- `Emuera/UI/Game/Image/AppContents.cs` 扫描 `resources/**/*.csv`，并按 `name,image,x,y,width,height,...` 创建 Sprite；
- `Readme/EmueraEE_readme (English).txt` 记录 `EXISTFUNCTION`；
- `Readme/Emuera.EM_readme.txt` 记录 EM 扩展函数和明确的 UTF-8/文本处理背景。

## 备选方案

1. **直接提交真实游戏**：拒绝。授权、字体和资源来源无法作为仓库契约证明，且会把不必要的用户内容纳入 CI。
2. **运行时下载测试资产**：拒绝。网络不可用时构建不可复现，也会绕过代码审查和哈希锁定。
3. **只保留一个 profile**：拒绝。单一 profile 无法同时约束 v18 兼容面和当前 EM+EE 扩展回归。
4. **只校验文件存在，不校验字节和编码**：拒绝。它不能发现静默编码转换、资源篡改或未登记 payload。

## 后果

合成资产可以稳定证明清单、来源、编码、路径、输入语义和后续运行时回归的最小边界，但不能代表完整 Era 游戏生态，也不能在 P0-01 阶段宣称动态兼容已经完成。发布前仍需要获得授权的人工或本地代表游戏测试，并在 P0-04/P0-05 记录实际执行结果。
