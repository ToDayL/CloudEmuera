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
  values are now runtime-validated as children of one private view; dynamic
  Graphics/CBG calls fail closed before GDI objects can be created.
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
