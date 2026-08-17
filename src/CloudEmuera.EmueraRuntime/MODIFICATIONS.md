# CloudEmuera modifications to Emuera.EM+EE

This ledger records modifications made after importing upstream commit
`2175f8a629257efb08214e093704b3a3d3d06d05`. It complements prominent notices
inside modified upstream files and does not replace Git history or review.

## 2026-08-04 — Direct-source import

- Scope: imported the complete upstream source tree without content changes.
- Purpose: replace the former submodule/patch workflow with ordinary source
  files that can be built, refactored and tested directly in CloudEmuera.
- Content verification: upstream license SHA-256 is
  `8770a79e679a354cffc4005cee99403d609c31ce88dd2b79e4a50325317beb77`;
  original Git tree is `a3c96867e3a5b5d5f90877a4e7c6f8056d5f5b9b`.
- Source changes: none.

Future entries must list modified files or bounded areas, behavior changes,
requirements/ADR references, and verification commands.

## 2026-08-12 — P1-07 structured Console/Input/Media boundary

- Headless glue changes (the pinned `Upstream/` source tree remains unchanged):
  `UpstreamHeadless/HeadlessEmueraConsole.cs` now translates line layout,
  temporary/replaced lines, manifest-backed Sprite/background/Shape/HTML Island
  state, logical viewport metadata, all supported input kinds and monotonic
  timeout results into RuntimeAdapter transactions. It also translates bounded
  CBG Graphics/Sprite/button-map output into platform-neutral raster or Sprite
  drawables. `HeadlessPlatformStubs.cs` keeps desktop-only declarations
  isolated, enables the reviewed Unix System.Drawing switch and routes audio
  through the structured media port.
  `UpstreamHeadless/UpstreamRuntimeSession.cs` unloads the upstream static
  Sprite/Graphics registry before releasing the process-wide runtime gate, so
  reopened sessions cannot inherit image IDs or allocation reservations.
- CloudEmuera contracts add bounded ConsoleSnapshot state, v3 protobuf
  transactions/snapshots, executable-free HTML AST nodes, prompt timing/source
  payloads, animation frames, bounded PNG rasters and media channel revisions.
  Desktop/external capabilities fail closed with matrix reason codes.
- Modified upstream files under ADR-0019:
  - `Upstream/Emuera/Runtime/Script/Process.cs` loads the resource registry in
    headless mode after the private SessionRoot has been validated/materialized,
    preserving native `GDRAWSPRITE`/`CBGSETSPRITE` lookup behavior.
  - `Upstream/Emuera/UI/Game/Image/GraphicsImage.cs` accounts mutable headless
    surfaces against a hard per-Worker 256 MiB aggregate allocation budget and
    releases reservations on unload/dispose and failed allocation.
  - `Upstream/Emuera/Runtime/Script/Statements/Function/Creator.Method.cs`
    corrects the duplicated X lower-bound check in `GGETCOLOR/GSETCOLOR`, so a
    negative Y returns native function failure instead of escaping as a GDI+
    exception. Its headless `GCREATEFROMFILE` branch also resolves only relative
    SessionRoot/resources paths and rejects rooted or traversing filenames;
    native numbered `GLOAD/GSAVE` remain inside `Config.SavDir`.
- Scope: P1-07, ADR-0018, ADR-0019,
  PLAY-001/002/003/004/007/008/009 and COMP-002～009.
- Verification: `scripts/verify-emuera-capabilities.sh`, structured
  RuntimeAdapter/IPC/Worker mapper contract tests, real ERB
  `GCREATE → GCLEAR → CBGSETG`, animated Sprite/CBG fixtures and the dev-Docker
  solution build. Full repository verification is recorded in the P1-07 handoff.

## 2026-08-05 — P0-04 headless integration

- Modified upstream files:
  - `Upstream/Emuera/GlobalStatic.cs`: the headless build does not instantiate
    the desktop GDI font collection.
  - `Upstream/Emuera/Runtime/Config/ConfigData.cs`: config paths follow each
    controlled session root and the singleton can be reset between fixtures.
  - `Upstream/Emuera/Runtime/Script/Process.cs`: headless initialization skips
    the Bitmap-based resource loader; image metadata is handled by the image
    port instead. It also accepts the runtime cancellation token and propagates
    cancellation instead of converting it into an ERB execution error.
  - `Upstream/Emuera/Runtime/Script/Process.ScriptProc.cs`: the headless
    instruction loop probes cancellation every 1,024 logical lines so a
    CPU-bound ERB loop cannot bypass the run deadline.
  - `Upstream/Emuera/Runtime/Script/Loader/ErbLoader.cs` and `ErhLoader.cs`:
    headless initialization probes cancellation between files and at bounded
    line/syntax-preparation intervals.
  - `Upstream/Emuera/Runtime/Utils/Preload.cs`: recursive preload accepts the
    initialization token and propagates it through parallel enumeration.
  - `Upstream/Emuera/Runtime/Script/Statements/Instraction.Child.cs`: headless
    audio commands delegate availability to `IRuntimeAudioPort` instead of
    probing a host path or opening an audio device.
- CloudEmuera scope: `UpstreamHeadless/` compiles the pinned upstream
  loader/parser/`Process`/variable/instruction sources with an adapter-backed
  Console/Input/Clock/Image/Audio boundary. `Headless/EmueraRuntimeHost.cs`
  owns lifecycle, deadlines and diagnostics. The former fixture-only
  `VendoredErbParser`/AST executor was removed.
- Behavior verified so far: both controlled profiles execute through the real
  upstream `Process.Initialize` and `Process.DoScript`, block at INPUT, assign
  RESULT through upstream code, then emit HTML/Sprite nodes and reach QUIT.
  INPUTS, cancellation, AWAIT/clock and unsupported audio have bridge tests.
  CPU-bound infinite loops return `DeadlineExceeded` within an outer hard test
  timeout; the host also bounds its post-cancellation cleanup wait.
- Initialization ownership: the session and its private file view are returned
  as one disposable result. Deadline/caller-cancellation paths dispose results
  that finish during the grace window and attach cleanup to later successful
  completion. Initialization cancellation releases the static runtime gate;
  a regression verifies that the private view disappears and a second host
  initializes successfully.
- Structured output: all integer/string `PrintButton` and `PrintButtonC`
  overloads emit `ButtonNode` values (the upstream methods expose no tooltip
  argument). The previous output-text `NAME=value` heuristic was removed;
  fixture score evidence is now explicitly reported as
  `verifiedByVisibleOutput` and runtime `Variables` are not fabricated.
- File boundary: `UpstreamRuntimeFileView` recursively obtains declared content
  through `IRuntimeFileSystem`, materializes a disposable session-private view,
  and gives the fixed loader only paths inside that view. A port-only test runs
  successfully while the physical GameRoot remains empty.
- The direct `System.IO` call-point audit and P0-05 deferral boundary are
  recorded in `docs/runtime-system-io-audit.zh-CN.md`. All `Program.*Dir`
  values are now runtime-validated as children of one private view. The former
  dynamic Graphics fail-closed boundary was superseded by the bounded
  libgdiplus decision in ADR-0019; Graphics file paths are now constrained to
  the private SessionRoot while numbered load/save retains native behavior.
- Requirements/decisions: P0-04, ADR-0004, ADR-0005 and ADR-0006.
- Verification: `./scripts/test-runtime-compat.sh --scenario input-roundtrip`
  passes 18 assertions per fixture; RuntimeBridge 16, RuntimeCompatibility 19,
  RuntimeAdapter 116, Domain 4 and Web 1 tests pass. `./scripts/check.sh`,
  source/fixture verification and `git diff --check` pass.

## 2026-08-05 — P0-05 persistent SessionRoot native saves

- Modified upstream file:
  - `Upstream/Emuera/Runtime/Config/Config.cs`: under
    `CLOUDEMUERA_HEADLESS`, creation of a missing `sav/` directory no longer
    invokes the desktop migration dialog or moves root-level native saves.
    The SessionRoot builder owns creation of the private directory and the
    runtime rejects a conflicting root-level save layout before loading ERB.
- Headless glue change: `UpstreamRuntimeSession` now receives the actual
  `RuntimePaths`, points `Program.ExeDir` at the persistent SessionRoot, and
  verifies `Config.UseSaveFolder` after the pinned upstream config loader has
  populated its static state.
- Scope: ADR-0007 and P0-05 SAVE-011/012/013/015. The upstream save/load
  writer and reader methods remain unchanged.
- Verification: the NativeSave compatibility scenario uses two fully
  released hosts and checks root and `sav/` layouts, while SessionRoot tests
  cover copy isolation and headless migration fail-closed behavior.

## 2026-08-09 — P1-04 Validator parity across Debug/Release

- Headless glue change: `UpstreamHeadless/HeadlessEmueraConsole.cs` no longer
  records `PrintSystemLine` status output into `RuntimeMessages`. The pinned
  upstream DEBUG build emits elapsed-time status lines through this channel,
  and the headless session treats any recorded message as a fatal script
  diagnostic. System/progress lines are informational; only `PrintError` and
  `PrintWarning` now gate activation. No `Upstream/` source files changed.
- Scope: P1-04 GAME-007 and the one-shot parser Validator; this keeps the
  Debug build used by the development API consistent with the Release build
  used by CI.
- Verification: `GameValidatorProcessIntegrationTests` and the new
  `GameLibraryApiContractTests.CreateIngestBindValidateActivateFlowWorksOverHttp`
  pass; `HeadlessRuntimeFixtureTests.HeadlessSystemLinesAreNotRecordedAsScriptDiagnostics`
  covers the recording rule; the Validator Debug and Release DLLs both return
  `canActivate: true` for the minimal controlled package.

## 2026-08-10 — P1-04 real-game compatibility (flattening, fixed-case, warnings)

- Headless glue changes (no `Upstream/` source files changed):
  - `UpstreamHeadless/HeadlessEmueraConsole.cs`: `PrintWarning` is recorded
    separately from `PrintError`; only errors are fatal runtime messages, so
    non-fatal parser warnings no longer reject a valid game.
  - `UpstreamHeadless/UpstreamRuntimeSession.cs`: `@SYSTEM_TITLE` is optional
    (matching upstream `callFunction` fallback), and `InitializationWarnings`
    exposes parser warnings.
  - `Headless/EmueraRuntimeHost.cs`: initialization warnings are surfaced as
    non-fatal `runtime_warning` diagnostics (bounded to 128).
- Scope: ADR-0011, P1-03/P1-04 GAME-001/006/007 compatibility with real
  era-game distributions.
- Verification: RuntimeCompatibility 27, RuntimeAdapter 140, GamePackages 47,
  API GameLibrary 4 and Infrastructure GameLibrary 28 tests pass; the user's
  eraJK package (single wrapper folder + `GameBase.csv` + COM-function warnings,
  no `@SYSTEM_TITLE`) validates with `canActivate: true` after flattening.

## 2026-08-16 — Linux fixed CSV case compatibility

- Upstream source changes:
  - `Upstream/Emuera/Runtime/Utils/Preload.cs` exposes a headless-only lookup
    against its existing ordinal-ignore-case preload cache.
  - `Upstream/Emuera/Runtime/Script/Data/ConstantData.cs` uses that cache for
    headless fixed-name CSV and `.als` reads instead of Linux case-sensitive
    `File.Exists`/`File.ReadAllLines` calls. Desktop builds retain their original
    path.
- Reason: real Windows-distributed games commonly contain `Talent.csv` while
  upstream requests `TALENT.CSV`. On Linux this silently skipped the talent-name
  table and later produced false unknown-identifier execution failures for names
  such as `性別` and `胸部尺寸`.
- Scope: COMP-006 and ADR-0011 Windows-to-Linux filename compatibility. The
  lookup is limited to the controlled, preloaded Emuera source tree and does not
  change resource-name or save-file semantics.
- Verification: `HeadlessRuntimeFixtureTests.MixedCaseTalentCsvUsesWindowsCompatibleLookupOnLinux`
  executes a named TALENT access with only `Talent.csv` present.

## 2026-08-16 — Simplified Chinese configuration aliases

- Modified upstream file: `Upstream/Emuera/Runtime/Config/ConfigData.cs` accepts
  the Simplified Chinese labels emitted by the `Nahlot/emuera-cn` Gitee
  project (`58ffbfdfabf20bd96b1eb5c6ee1689da5df2ecbb`) for the primary runtime
  configuration, debug configuration and `_Replace.csv` entries. Each label
  resolves to its existing `ConfigCode`; the current `UseSaveFolder` alias is
  retained in the same table.
- Scope: headless configuration compatibility for the pinned runtime. Legacy
  Chinese encoding switches that have no corresponding `ConfigCode` in the
  pinned Emuera.EM+EE source are deliberately left unmapped rather than being
  assigned another setting's semantics.
- Verification: `ChineseConfigMappingTests` covers all supported translated
  labels, while the existing localized save-layout/runtime round-trip tests
  verify that `UseSaveFolder` still controls the native `sav/` layout.

## 2026-08-16 — Headless implicit buttons and East-Asian string conversion

- Modified upstream files:
  - `Upstream/Emuera/Runtime/Script/Statements/Function/Creator.Method.cs`
    routes headless `TOHALF`/`TOFULL` through the portable Unicode converter.
  - `Upstream/Emuera/Runtime/Script/Statements/ExpressionMediator.cs` routes
    headless hiragana/katakana conversion through the same portable boundary.
  Desktop builds retain `Microsoft.VisualBasic.Strings.StrConv` unchanged.
- Headless glue changes:
  - `UpstreamHeadless/HeadlessEmueraConsole.cs` applies the pinned upstream
    `ButtonStringCreator` when ordinary printed lines are flushed, preserving
    implicit numeric choices such as `[1000]` as structured buttons.
  - `UpstreamHeadless/HeadlessStringConverter.cs` implements deterministic
    fullwidth/halfwidth ASCII, space, kana, and hiragana/katakana conversion
    because Windows NLS-backed `StrConv` throws on Linux.
- Verification:
  `OrdinaryPrintLinesPreserveUpstreamImplicitNumericButtons`,
  `PrintButtonAllowsEmptyStringSubmissionValue`, and
  `EastAsianWidthConversionWorksWithoutWindowsStrConv` exercise the real
  pinned interpreter through the headless host.
