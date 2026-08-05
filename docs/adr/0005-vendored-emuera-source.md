# ADR-0005：Emuera 固定源码直接集成

状态：Accepted
日期：2026-08-04

## 背景

P0-00 最初使用 Git submodule 固定 Emuera.EM+EE，计划在仓库外维护上游原始树，并用 patch queue 表达 CloudEmuera 修改。P0-02/P0-03 证明后续接线需要系统性拆分 WinForms/GDI+、静态路径、输入、Console、图片和音频，而不是少量外围补丁。继续使用 submodule 加临时 staging 会增加 IDE 导航、重构、构建、测试和未来 Worker 引用的复杂度。

上游 zlib/libpng 风格许可证允许修改和再分发，但要求保留来源与许可证声明、不得冒充原作，并显著说明源码已被修改。

## 决定

将固定 commit `2175f8a629257efb08214e093704b3a3d3d06d05`、原始 tree `a3c96867e3a5b5d5f90877a4e7c6f8056d5f5b9b` 的 Emuera.EM+EE 源码作为普通 Git 文件导入：

```text
src/CloudEmuera.EmueraRuntime/
├── Upstream/
├── UPSTREAM.md
└── MODIFICATIONS.md
```

不再使用 submodule、gitlink、patch queue 或构建时临时应用补丁。P0-04 在同一 `CloudEmuera.EmueraRuntime` 边界内新增 headless project/integration，并可直接修改 `Upstream/`。

以下规则是强制的：

1. 首次导入与每次上游升级分别使用独立提交；升级不得跟踪浮动分支。
2. `UPSTREAM.md` 固定来源仓库、commit、tree ID、导入日期、许可证路径和校验值。
3. 修改内置上游源码时保留原版权/许可证，并在修改文件显著标记 CloudEmuera 修改；同时维护 `MODIFICATIONS.md`。
4. 运行时清单记录 upstream commit 与 `CloudEmueraIntegrationVersion`；integration version 表示仓库内源码集成修订，而不是不存在的 patch 文件版本。
5. `scripts/verify-third-party.sh` 验证来源元数据、许可证、禁止的嵌套 Git 元数据和基线常量；源码修改本身由普通 Git diff、review 和兼容测试审查。
6. `CloudEmuera.RuntimeAdapter` 保持平台契约项目，不反向引用真实解释器；`CloudEmuera.EmueraRuntime` 消费这些端口，未来 Worker 再消费 runtime host。

## 备选方案

### 保留 submodule 与 patch queue

来源边界清晰，但每次构建要 staging/apply，跨文件重构与调试困难，且未来 Worker 无法自然引用一个稳定的解决方案项目，因此拒绝。

### 维护独立 CloudEmuera fork

可保留完整上游历史，但需要额外仓库、权限和同步流程，并让产品构建依赖外部 checkout。当前团队规模与 Phase 0 目标不需要该复杂度，因此暂不采用。

### 仅复制解释器的部分文件

仓库更小，但难以证明遗漏依赖、许可证和上游行为，升级也无法可靠对比，因此拒绝。

## 后果

- 日常修改、IDE 导航、重构、测试和未来 Worker 引用更直接；干净 checkout 无需初始化 submodule。
- 主仓库包含约 5.2 MB 上游源码，并保留其原始许可证；根 Apache-2.0 不重新授权这些文件。
- 上游升级会成为显式的三方源码合并工作，可能产生冲突；升级必须通过双 fixture 兼容测试和安全审查。
- 不再通过 patch 文件摘要证明修改集合，改用 Git 历史、integration version、`MODIFICATIONS.md` 和 runtime compatibility tests。

## P0-04 集成边界与能力表

依赖方向固定为 `Worker/compatibility harness → CloudEmuera.EmueraRuntime →
CloudEmuera.RuntimeAdapter`。RuntimeAdapter 不反向引用解释器，headless 程序集
目标为 Linux `net10.0`，不引用 WinForms、WPF、WMP/COM 或 NAudio。

平台调用替换目标如下：Console/Input 已走 `IGameConsole`，deadline 与 AWAIT 已走
`IRuntimeClock`，静态 PNG 与 Sprite 已走 `IRuntimeImagePort` 的 metadata 节点，
音频只走 `IRuntimeAudioPort` 并观察 `Unsupported`。服务端执行路径不创建
Bitmap、Graphics、字体测量对象或音频设备。文件输入先由
`IRuntimeFileSystem` 复制为 session 私有兼容视图，固定上游 loader 只获得该视图
内的路径；物理 GameRoot 为空的 port-only 测试证明内容不能绕过文件端口。私有视图
内部的直接 `System.IO` 调用点已经分类审计，并明确记录 P0-05 延后边界。
P0-05 将依据 ADR-0007 用持久 SessionRoot 替换正式执行路径中的可销毁视图：内容由
管理方按发布 manifest 完整复制，原生存档由 Emuera 直接留在 SessionRoot，不增加
artifact 提交层。

| 能力 | P0-04 状态 |
| --- | --- |
| 真实 CSV/GAMEBASE、配置和 ERB loader/interpreter | Supported（受控双 fixture 的 Phase-0 slice） |
| PRINT/PRINTFORM、变量、CALL/RETURN、IF、EXISTFUNCTION | Supported（Phase-0 slice） |
| INPUT/INPUTS、promptId、取消和 deadline | Supported |
| 允许列表 HTML、静态 PNG/Sprite ImageNode | Supported |
| 原生 save/global 双布局 | Deferred to P0-05 |
| CBG、动态 Graphics、字体测量、动画 | Unsupported / fail closed |
| 桌面输入、剪贴板、插件、CALLSHARP、网络 | Unsupported / fail closed |

CloudEmuera integration version 首次可运行修订为 `headless-p0.4.1`。升级固定
上游时必须保留原始 commit/tree，更新 integration version，并重新通过双 fixture
正序、反序与专项回归；回退时可整体回退 runtime integration 提交而不改写上游来源。

## 验证

上游 `System.IO` 的 P0-04 调用点、私有视图约束和 P0-05 延后边界记录在
[`runtime-system-io-audit.zh-CN.md`](../runtime-system-io-audit.zh-CN.md)。

```bash
./scripts/verify-third-party.sh
./scripts/verify-runtime-fixtures.sh
./scripts/check.sh
git diff --check
```

验证必须证明内置源码存在、来源/许可证一致、没有 `.gitmodules` 或嵌套 `.git`，RuntimeBaseline 与 fixture manifest 使用同一 upstream commit 和 integration version，且现有项目全部编译和测试通过。
