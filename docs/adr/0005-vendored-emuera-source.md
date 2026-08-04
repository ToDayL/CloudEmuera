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

## 验证

```bash
./scripts/verify-third-party.sh
./scripts/verify-runtime-fixtures.sh
./scripts/check.sh
git diff --check
```

验证必须证明内置源码存在、来源/许可证一致、没有 `.gitmodules` 或嵌套 `.git`，RuntimeBaseline 与 fixture manifest 使用同一 upstream commit 和 integration version，且现有项目全部编译和测试通过。
