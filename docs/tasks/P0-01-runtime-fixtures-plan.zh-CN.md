# P0-01 兼容测试资产与运行时基线实现计划

状态：Ready for implementation

计划日期：2026-08-04

对应开发步骤：`P0-01 — 兼容测试资产与运行时基线`

需求映射：`COMP-001`～`COMP-003`、`COMP-005`、`AC-008`、`AC-009`、`NFR-014`、`ADR-006`

## 1. 为什么下一步是 P0-01

`docs/development-plan.zh-CN.md` 已将 P0-00 标为 `DONE`，并将 P0-01 标为唯一的 `NEXT`。仓库现状也与该状态一致：

- `third_party/emuera-em` 已固定到提交 `2175f8a629257efb08214e093704b3a3d3d06d05`；
- `CloudEmuera.RuntimeAdapter` 目前只有上游仓库和提交号基线，没有兼容测试资产；
- 尚不存在 `tests/CloudEmuera.RuntimeAdapter.Tests`、Runtime fixture 清单或 `scripts/verify-runtime-fixtures.sh`；
- P0-02 的路径端口、P0-03 的 Console 契约、P0-04 的无 UI harness 和 P0-05 的双存档布局都需要一套稳定、可合法分发、可校验的输入与预期结果。

因此本 task 的目标不是让解释器真正运行，而是先固定后续 Runtime 工作共同使用的测试资产契约、来源/授权、字节内容、编码、场景输入和预期 transcript。

## 2. 目标与完成定义

完成后，仓库应具备两套完全由本项目创作、可随 Apache-2.0 仓库进入 CI 的最小合成游戏：

1. `v18-core`：只使用 `1824+v18` 兼容面，覆盖启动、PRINT、变量、函数调用、分支、INPUT、基础 HTML、图片/Sprite 引用和根目录存档场景；
2. `em-ee-core`：面向当前固定 EM+EE 提交，至少包含一个有明确依据的 EM/EE 扩展用法，并覆盖 `sav/` 存档场景。

每套资产必须具有机器可读清单、逐文件 SHA-256、精确编码声明、来源与 SPDX 许可证声明、确定的输入步骤和预期 transcript。验证必须只读取仓库本地文件；文件缺失、内容被修改、未登记额外文件、许可证字段缺失或清单不一致时返回非零退出码。

本步骤完成时必须可以执行：

```bash
./scripts/verify-runtime-fixtures.sh
dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --filter 'Category=FixtureContract'
./scripts/check.sh
./scripts/verify-third-party.sh
git diff --check
```

## 3. 范围边界

### 3.1 本步骤包含

- 编写并接受 ADR-006；
- 建立 fixture 目录规范与版本化 manifest schema；
- 手工编写两套最小 ERB/CSV/config/媒体资产及场景文件；
- 固定当前 Runtime commit 和“尚无 CloudEmuera Runtime 补丁”的版本表示；
- 添加 fixture 契约验证器、失败分支测试和 shell 验证入口；
- 把 fixture 验证接入仓库全局检查；
- 将开发计划中 P0-01 更新为 `DONE`，并把 P0-02 更新为 `NEXT`（仅在全部验收命令通过后）。

### 3.2 本步骤不包含

- 不提取 `IGameConsole`、`RuntimePaths` 或其他平台端口（P0-02）；
- 不定义最终结构化 Console event schema（P0-03）；
- 不让 Emuera 在 Linux/headless 环境实际运行，也不生成真实运行 transcript（P0-04）；
- 不提交预制的原生 `.sav` 二进制，不实现保存、加载、重定向或原子提交（P0-05）；
- 不复制、裁剪或再分发任何第三方游戏、字体、图片或音频；
- 不把“v18-core”描述成在旧版二进制上验证过；它表示受控的 v18 兼容语法画像，真正的执行兼容性由 P0-04/P0-05 证明。

## 4. 设计决定

### 4.1 资产授权策略

ADR-006 应选择“仓库内原创合成资产”作为 CI 的发布阻断基线。所有文本、像素图和 Sprite 定义都由本项目新建，统一以 `Apache-2.0` 授权，不依赖下载或用户拥有商业游戏的证明。真实游戏只能作为开发者本地、不入库的补充验证集，不能成为 CI 成功的条件。

ADR 至少记录：

- 背景：真实 Era 游戏的版权、字体和资源授权通常不可证明；
- 决定：CI 只使用最小原创资产，两套 profile 分别约束 v18 兼容面与当前 EM+EE 面；
- 资产来源规则：`authored-for-cloudemuera`，作者为 CloudEmuera contributors，SPDX 为 `Apache-2.0`；
- 可接受文件类型和禁止内容：不得包含 DLL、可执行文件、外部 URL、网络依赖或来源不清的二进制；
- 哈希与编码规则；
- 真实游戏仅允许通过被 gitignore 的本地目录参与非阻断测试；
- 备选方案及拒绝原因：直接采用真实游戏、运行时下载资产、仅用单个 profile；
- 后果：合成资产能证明契约与回归，但不能代表完整生态兼容性；发布前仍需要获授权的人工/本地代表游戏测试。

建议文件：`docs/adr/0006-ci-runtime-compatibility-fixtures.md`。状态设为 `Accepted`，因为后续任务将直接依赖该决定。

### 4.2 Fixture 目录与文件布局

采用以下布局，避免把测试资产混入上游 submodule：

```text
tests/fixtures/runtime/
├── README.md
├── LICENSE
├── manifest.json
├── v18-core/
│   ├── CSV/
│   ├── ERB/
│   ├── resources/
│   ├── emuera.config
│   ├── scenario.json
│   └── expected-transcript.txt
└── em-ee-core/
    ├── CSV/
    ├── ERB/
    ├── resources/
    ├── emuera.config
    ├── scenario.json
    └── expected-transcript.txt
```

若 Emuera 实际要求图片/Sprite 清单位于不同的标准目录，应以固定上游代码的加载规则为准调整 `resources/`，并在 README 中解释；不要为了保持上述示意目录而创造运行时不认识的布局。

`README.md` 说明如何重新计算哈希、各 profile 的设计意图、哪些断言在 P0-01 只做静态验证，以及 P0-04/P0-05 如何消费 `scenario.json`。根 `LICENSE` 保存完整 Apache-2.0 文本；manifest 中仍需对每个资产声明 SPDX，不以“目录里有 LICENSE”代替逐项来源记录。

### 4.3 Manifest 契约

`manifest.json` 是唯一的资产索引，建议 schema 如下：

```json
{
  "schemaVersion": 1,
  "runtimeBaseline": {
    "upstreamRepository": "https://gitlab.com/EvilMask/emuera.em.git",
    "upstreamCommit": "2175f8a629257efb08214e093704b3a3d3d06d05",
    "cloudEmueraPatchVersion": "0"
  },
  "fixtures": [
    {
      "id": "v18-core",
      "compatibilityProfile": "v18-compatible",
      "source": "authored-for-cloudemuera",
      "license": "Apache-2.0",
      "gameRoot": "v18-core",
      "scenario": "v18-core/scenario.json",
      "expectedTranscript": "v18-core/expected-transcript.txt",
      "coverage": ["startup", "print", "variable", "function", "branch", "input", "html", "image", "sprite", "save-root"],
      "files": [
        {
          "path": "v18-core/ERB/START.ERB",
          "sha256": "<64 lowercase hex>",
          "mediaType": "text/plain",
          "encoding": "shift_jis",
          "source": "authored-for-cloudemuera",
          "license": "Apache-2.0"
        }
      ]
    }
  ]
}
```

实现时可调整字段名，但必须保持以下不变量：

- 顶层和 scenario 都有整数 `schemaVersion`；未知可选 JSON 字段被忽略，缺少必填字段则失败；
- 所有路径使用 `/`、相对 `tests/fixtures/runtime`、不得为空，不得含 `.`、`..`、绝对路径、反斜杠或符号链接；
- SHA-256 使用 64 位小写十六进制；如沿用设计文档的摘要表示，也可统一使用 `sha256:<hex>`，但测试和文档只能选一种；
- manifest 枚举 fixture 下除说明性 `README`/`LICENSE` 外的所有文件；磁盘上的未登记 payload 也视为错误；
- 文本文件显式声明 `utf-8`、`utf-8-bom` 或 `shift_jis`，二进制文件不允许伪装成文本；
- `source` 和 SPDX `license` 在 fixture 与逐文件级均非空；本 task 只允许 `authored-for-cloudemuera` + `Apache-2.0`；
- 每个 fixture 引用且只引用自己的 scenario 和 transcript；fixture ID、profile 和文件路径全局唯一；
- manifest 的 Runtime 仓库/提交/补丁版本必须与 `RuntimeBaseline` 常量一致。

不要手写哈希。实现一个明确的维护命令（例如验证脚本的 `--update` 模式，或一个只在显式参数下运行的测试工具）按路径序排序后重算哈希。默认模式只能验证，绝不能悄悄改写 manifest。

### 4.4 Scenario 与 transcript

`scenario.json` 只描述与 UI/协议无关的语义步骤，不提前绑定 P0-03 的 Console event 类型。建议步骤类型包括：

- `expectOutput`：按顺序出现的规范化纯文本；
- `submitInput`：固定整数或字符串输入；
- `expectVariable`：输入后由游戏 PRINT 出来的可观察变量值；
- `expectSavePath`：期望使用 `save00.sav`/`global.sav` 或 `sav/...`，这里只固定路径语义；
- `expectDiagnostic`：EM+EE profile 对尚未支持能力的预期分类（如本场景确有该需要）。

`expected-transcript.txt` 使用 UTF-8、LF 和末尾换行，只保存规范化可见文本；不包含时间、随机数、绝对路径、机器区域设置、字体度量或原生存档字节。输入值与预期输出要能证明变量赋值、函数调用和至少两个分支中的选定分支，而不是只证明脚本打印了一段常量。

场景设计建议：

- `v18-core` 使用 Shift-JIS ERB/CSV，选择输入后调用用户函数、修改变量、进入确定分支并输出结果；引用一个项目原创的微型图片及 Sprite 定义；配置/命令表达根目录 `save*.sav` 与 `global.sav` 语义；
- `em-ee-core` 使用 UTF-8（至少一个文件含 BOM、一个不含 BOM），执行一个在固定上游 README 或源码中能定位的 EM/EE 扩展，并在资产 README 注明依据；配置为 `sav/` 布局；
- 所有输出只使用稳定 ASCII 或明确编码的短文本，避免测试同时受字体、翻译和本地化影响；编码测试本身可放入不参与 transcript 比较的短注释/CSV 值中。

如果当前固定上游没有可通过静态检查可靠确认的 `sav/` 配置键或某个命令语法，应先从上游加载器/配置代码确定真实语法；不得在 fixture 中发明伪配置。无法在 P0-01 静态证明的动态行为应记录为 P0-04/P0-05 的待验证项，而不是写成已通过结论。

## 5. 代码和项目改动

### 5.1 Runtime baseline

修改 `src/CloudEmuera.RuntimeAdapter/RuntimeBaseline.cs`：

- 保留现有 `UpstreamRepository` 和 `UpstreamCommit`；
- 添加显式的 CloudEmuera patch-set 版本常量，初值为 `"0"`，含义是“尚无运行时补丁”；
- 如测试需要，可添加稳定的 profile 名常量，但不要把 fixture 路径放入生产代码。

该类仍然不读取文件、不依赖测试项目，也不引入新的 NuGet 包。

### 5.2 新建测试项目

新增 `tests/CloudEmuera.RuntimeAdapter.Tests/CloudEmuera.RuntimeAdapter.Tests.csproj`：

- 目标与仓库一致，为 `net10.0`、xUnit、不可打包测试项目；
- 引用 `src/CloudEmuera.RuntimeAdapter`；
- 使用中央包版本，不在项目中写版本号；
- 将 fixture 复制到测试输出不是必需条件。优先通过一个可靠的仓库根定位器读取源树，以保证能发现磁盘上的未登记文件；定位失败必须给出明确错误，不能跳过测试；
- 加入 `CloudEmuera.slnx`，确保 `dotnet test CloudEmuera.slnx` 和 `scripts/check.sh` 覆盖它；
- 提交对应的 NuGet `packages.lock.json`，并保证 locked restore 成功。

### 5.3 Fixture 验证器

在测试项目中建立小型、可单测的验证器，例如：

```text
tests/CloudEmuera.RuntimeAdapter.Tests/Fixtures/
├── RuntimeFixtureManifest.cs
├── RuntimeFixtureValidator.cs
├── RuntimeFixtureContractTests.cs
└── RuntimeFixtureValidatorTests.cs
```

职责分开：

- manifest DTO 只负责 JSON 反序列化和必填字段表达；
- validator 接收 fixture 根路径，返回结构化错误集合，不能直接 `Assert` 或 `Environment.Exit`；
- contract tests 对仓库中的真实 fixture 调用 validator；
- validator tests 在临时目录构造最小副本，覆盖每个失败分支。

读取 manifest 时使用 `System.Text.Json` 严格检查必填字段、重复键和枚举值，同时允许未知可选字段以满足 schema 前向兼容。校验文件时使用流式 SHA-256；先做词法路径检查，再解析规范绝对路径并确认仍位于 fixture 根内；拒绝文件和父目录中的符号链接。目录枚举和诊断输出按 ordinal 路径排序，保证不同机器结果一致。

### 5.4 Shell 验证入口

新增可执行文件 `scripts/verify-runtime-fixtures.sh`，遵循现有脚本风格：

```bash
#!/usr/bin/env bash
set -euo pipefail
```

脚本要求：

- 从脚本位置解析仓库根，不依赖调用者当前目录；
- 默认验证 `tests/fixtures/runtime`，可通过仅供测试的 `--root <path>` 验证临时副本；
- 不调用 `curl`、`wget`、包管理器或任何网络 API；
- 运行 fixture contract validator，并原样传播非零退出码；
- 成功时只输出简短摘要（schema 版本、fixture 数和验证文件数）；失败时输出稳定的相对路径与原因；
- 未知参数、根目录不存在或 manifest 不存在时返回非零；
- 默认运行绝不改写资产；如提供 `--update`，必须显式、与 `--root` 语义清楚，并在完成后再次验证。

为了让 shell 脚本和 xUnit 使用同一套规则，推荐把 validator 放在一个极小的测试工具控制台入口中，或让脚本以专用 filter 调用同一测试程序集。不要分别实现两套会漂移的 JSON/哈希规则。若采用 `dotnet test` 入口，脚本应使用 `--no-restore`，并在缺少构建产物/依赖时给出如何先 restore/build 的提示；仓库的正式验证流程应先完成 locked restore。

将脚本加入 `scripts/check.sh`。位置应在完整 build/test 之前，使资产契约错误尽早失败；但不能破坏干净 checkout 下现有容器化检查流程。

## 6. 测试清单

所有本 task 新增的 xUnit 测试加 trait：

```csharp
[Trait("Category", "FixtureContract")]
```

### 6.1 正常契约测试

至少添加以下测试（名称可按项目风格调整）：

1. `RepositoryFixturesSatisfyManifestContract`
   - 对真实 `tests/fixtures/runtime` 执行完整 validator；
   - 失败信息一次列出所有错误，便于修复。
2. `ManifestContainsV18AndCurrentEmEeProfiles`
   - 恰有 `v18-compatible` 和 `em-ee-current` 所需基线；
   - 当前 EM+EE fixture 的 commit 与 `RuntimeBaseline.UpstreamCommit` 相同。
3. `FixtureCoverageIncludesRequiredPhaseZeroScenarios`
   - 汇总 coverage 后包含 startup、print、variable、function、branch、input、save-root、save-directory；
   - 同时登记 HTML、image、sprite，作为 `COMP-002` 后续执行用资产。
4. `DeclaredTextEncodingsMatchFileBytes`
   - UTF-8 BOM、UTF-8 无 BOM 和 Shift-JIS 声明与实际字节一致；
   - 使用严格 decoder，非法字节失败，不使用系统默认编码。
5. `ScenariosAndTranscriptsAreDeterministic`
   - scenario schema 可读、步骤类型合法、至少有一次输入和输入后的可观察输出；
   - transcript 为 LF、末尾换行，不含绝对路径、CR、时间/随机占位符或 fixture 根之外引用。
6. `ManifestAllowsUnknownOptionalFields`
   - 给合法 manifest 增加未知顶层、fixture 和 file 字段后仍通过已知字段解析。

### 6.2 失败与篡改测试

每个测试使用临时目录副本，结束后清理，不修改仓库 fixture：

1. 删除一个已声明文件，validator 报 `missing file`；
2. 改动一个 ERB 字节但不更新 manifest，报 SHA-256 mismatch；
3. 添加一个未声明 payload，报 unlisted file；
4. 删除 fixture 级 `source` 或 `license`，报必填元数据缺失；
5. 删除 file 级许可证，仍必须失败；
6. 将 SPDX 值改为未知/本 task 不允许的许可证，失败；
7. 使用重复 fixture ID、重复文件路径或同一路径不同大小写，失败；
8. 使用绝对路径、`../`、反斜杠或符号链接逃逸，失败；
9. 使用大写/错误长度 SHA-256，失败；
10. manifest 声明编码与 BOM/字节不一致，失败；
11. scenario 引用另一个 fixture 的文件或包含未知必需步骤，失败；
12. Runtime commit 或 patch version 与 `RuntimeBaseline` 不一致，失败；
13. `scripts/verify-runtime-fixtures.sh --root <tampered-copy>` 对缺失、篡改和许可证不完整三种副本分别返回非零。

符号链接测试在当前平台无法创建链接时不能静默通过；应明确标记平台原因，Linux CI 必须执行该用例。

### 6.3 脚本行为测试

验证以下 CLI 行为：

- 从仓库根和其他工作目录调用都成功；
- `--root` 缺参数、未知参数和不存在路径均失败；
- 默认验证后 `git status --short` 不产生新改动；
- 断网/无网络 namespace 中验证仍成功（CI 环境支持时运行），证明资产验证无下载依赖。

## 7. 实现顺序

建议其他 session 严格按以下顺序实现：

1. 阅读固定上游的 ERB loader、配置项和图片/Sprite 加载规则，记录 v18 安全语法、当前 EM/EE 扩展依据及真实的 `sav/` 配置键；
2. 编写 ADR-006，先固定授权、来源和 CI 边界；
3. 定义 manifest/scenario schema，并先写 validator 的路径、元数据、哈希和编码失败测试；
4. 新建测试项目并加入 solution，生成 locked NuGet 文件；
5. 手工创建两套原创 fixture 及媒体资产，使用显式工具编码 Shift-JIS 文件；不要通过编辑器“看起来像 Shift-JIS”来判断；
6. 生成哈希，完成真实仓库 fixture contract tests；
7. 添加 shell 入口和临时副本失败测试；
8. 接入 `scripts/check.sh`，运行全部质量门；
9. 全部通过后，更新 `docs/development-plan.zh-CN.md`：P0-01 `NEXT → DONE`，P0-02 `TODO → NEXT`，并在 P0-01 下记录实际验证命令或报告位置。

## 8. 验收标准

本 task 只有同时满足以下条件才算完成：

- ADR-006 为 `Accepted`，明确 CI 仅使用原创 Apache-2.0 合成资产及真实游戏的非阻断边界；
- manifest 与 scenario 均有 schema 版本；
- 至少发现一个 v18 profile 和一个绑定当前 commit 的 EM+EE profile；
- 必需 Phase 0 coverage 标签齐全，两种存档布局均有场景但不伪造已完成运行验证；
- 所有 payload 都有来源、SPDX、编码/媒体类型和匹配的 SHA-256，不存在未登记文件；
- fixture contract tests 完全本地执行，不下载内容；
- 缺失、篡改、未登记、许可证不完整、路径逃逸和编码错误均有自动化失败测试；
- `scripts/verify-runtime-fixtures.sh` 对正常资产返回 0，对三类规定坏副本返回非零；
- 测试项目已加入 solution 和 locked restore；
- 全局质量门全部成功；
- 开发计划状态只在上述条件满足后推进到 P0-02。

## 9. 实现时需要特别核实的风险

- **ERB 语法真实性**：不要凭记忆编写。每个用于区分 v18/EM+EE 的命令都应在固定上游源码或随附 README 中留下可追踪依据。
- **存档场景可执行性**：`SAVEGAME`/`LOADGAME` 可能受调用上下文限制。P0-01 应把真实调用前置条件写入 scenario/README；P0-04/P0-05 执行时若需专用入口，再以最小改动修正 fixture 并更新哈希。
- **编码再保存**：普通文本编辑可能把 Shift-JIS 自动转成 UTF-8。编码契约测试必须检查原始字节，而不只检查声明。
- **哈希自引用**：manifest 不应把自己的 SHA-256 放进自己。它校验 payload；manifest 的结构和内容由代码审查、版本控制和契约测试保护。
- **测试工具漂移**：shell 和 xUnit 不应各自维护一套 manifest 规则，必须共享 validator 或让一个调用另一个。
- **范围膨胀**：本步骤的 transcript 是预期基线，不是运行结果证明。只有 P0-04/P0-05 的真实 harness 通过后，才能宣称 AC-008/009/013 的动态路径已满足。
