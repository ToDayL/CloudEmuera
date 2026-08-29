# CloudEmuera modifications to Emuera.EM+EE

- Added a Session-bound width policy at the headless configuration seam: Original uses the game's `WindowX` exactly,
  Max uses the lesser of startup browser CSS width and 2000px, Adaptive uses the lesser of startup browser CSS width
  and `WindowX`, and Custom uses the persisted user width exactly. Only Max and Adaptive are browser-bounded; no mode
  reflows during a Worker run (SESS-014/PLAY-015, ADR-0030/0037).
- Added a Session-bound display projection that maps U+005C to U+00A5 before headless font measurement when enabled.
  The default-on mapping affects visible text only; runtime strings, button values, input and paths remain unchanged
  (SESS-015/PLAY-016, ADR-0031).
- Added a headless display projection that expands upstream U+0009 tabs to eight U+0020 spaces before structured
  text validation and authoritative measurement. The original game/script data and input values are not rewritten;
  the projection keeps the RuntimeAdapter control-character boundary closed (PLAY-001/PLAY-014, issue #18).

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

## 2026-08-28 — Normalize upstream Windows separators in controlled SessionRoot paths (issue #15)

- `UpstreamHeadless/HeadlessFileSystem.cs` now translates the pinned upstream
  runtime's Windows backslash separator to the host separator before containment and
  case-insensitive component lookup. This is required because
  `Utils.GetValidPath` rewrites logical `/` paths before the aliased headless
  `File`/`Directory` APIs receive them.
- The normalization remains limited to the already validated private
  SessionRoot; paths outside that root retain normal host filesystem behavior.
- Scope: `ENUMFILES` → `LOADTEXT` → `XML_GET` in EraFL 0.48, where the old
  Linux lookup returned an empty string and raised `Root element is missing`.
  References: COMP-002/006, GAME-008, SAVE-011, ADR-0011.
- Verification: `Issue15EnumeratedWindowsPathLoadsXmlOnLinux` and its targeted
  RuntimeCompatibility run in the development Docker environment.

## 2026-08-28 — Expand literal PRINT tabs at the structured display boundary (issue #18)

- `UpstreamHeadless/HeadlessEmueraConsole.cs` and
  `UpstreamHeadless/UpstreamHtmlTranslator.cs` now apply the same eight-space
  tab display width already used by the pinned graphics measurement path before
  creating browser-facing text, button labels and hit-region tooltips.
- `UpstreamHeadless/HeadlessFontMetrics.cs` also expands tabs before reading
  the authoritative TTF advances, so measurement cannot diverge if a legacy
  upstream display path still supplies a raw tab.
- Scope: the Docker-volume `eraInSchoolML` Session trace at
  `SHOP/SHOP.ERB:270`, where `[100] - 調教開始\t\t` reached `TextNode` and
  aborted the Worker with `text contains a control character`. Script data,
  parser input and button submission values remain unchanged.
- Verification: `Issue18LiteralPrintTabsBecomeDisplaySpacesBeforeStructuredValidation`,
  `Issue18LiteralPrintTabsDoNotAbortPinnedInterpreter`, and the targeted
  RuntimeCompatibility tests in the development Docker environment.

## 2026-08-28 — Preserve configured long button values for ONEINPUT prompts

- `RuntimeAdapter/Input/ConsolePrompt.cs` carries the headless equivalent of
  the pinned upstream `AllowLongInputByMouse` setting, and
  `UpstreamHeadless/HeadlessEmueraConsole.cs` binds it from the loaded game
  configuration when opening a prompt.
- `RuntimeAdapter/Input/InputCoordinator.cs` now applies the one-character
  `ONEINPUT` normalization to keyboard input while preserving a complete
  semantic button value when the game has enabled the upstream exception.
  This keeps EraFL's `4000`-series `ONEINPUTS` menu IDs intact without
  changing the browser or IPC/Realtime protocols.
- Scope: EraFL `QUEST/QUEST_MENU.ERB`, where `ONEINPUTS` dispatches numeric
  HTML button IDs through `%RESULTS%`; references PLAY-002/007/008/011 and
  COMP-007.
- Verification: `OneInputButtonValueHonorsConfiguredLongInputException`,
  `OneinputsPreservesLongHtmlButtonValueWhenConfigAllowsMouseInput`, and the
  targeted RuntimeAdapter/RuntimeCompatibility tests in the development
  Docker environment.

## 2026-08-28 — Preserve HTML overlay layout and nested button hit targets

- `UpstreamHeadless/HeadlessEmueraConsole.cs` now keeps `DivNode` width out of
  the physical inline cursor, matching upstream `ConsoleDivPart` behavior.
  Sibling `rect` divs consequently remain positioned overlay layers, so
  multi-panel HTML layouts do not accumulate each preceding panel's width.
- `Web/src/console/ScrollbackRenderer.tsx` explicitly restores pointer hit
  testing on each semantic button. Pointer-transparent non-button wrappers
  can still pass clicks through, while buttons nested inside an Emuera div
  remain keyboard/focus/click targets. Input availability remains owned by
  the server-side legacy display-line projection; the browser only renders
  structured button elements and reports their clicks.
- Scope: issue #14 / COMP-007, including the `eraBlue` management panel.
  Verification: `HtmlPrintDivsDoNotConsumeInlineFlowWidth`, the nested-div
  ScrollbackRenderer regression, and the existing BINPUT-family tests.

## 2026-08-27 — Layered HTML budget and saved dynamic sprites

- `UpstreamHeadless/HeadlessEmueraConsole.cs` now checks the pinned upstream
  `AppContents` registry for runtime-created `SpriteG` values before consulting
  the static resource resolver. `Upstream/Emuera/UI/Game/Image/GraphicsImage.cs`
  records the logical SessionRoot asset after a successful `GSAVE` or `GLOAD`,
  invalidates it after pixel mutation, and materializes a content-addressed
  PNG for an unsaved `GCREATE`/`OVERLAY_GCREATE` composite on first use.
  `Upstream/Emuera/Runtime/Script/Statements/Function/Creator.Method.cs`
  bridges saved paths into the structured `path-*` asset id.
- `RuntimeAdapter/Console/ConsoleContractLimits.cs` raises the finite HTML
  input/tag/segment/part/text budgets and the batch/physical-line node budgets
  so a normal multi-character portrait fragment is not rejected at the old
  32 KiB/256-tag/512-node defaults. `StructuredIpcProtocol` accepts the same
  larger physical-line segment budget; output node and asset validation remain
  active.
- `HTML_POPPRINTINGSTR()` now consumes the structured pending print line, not
  the already committed scrollback list. Its upstream `ConsoleDisplayLine`
  bridge preserves text, styles, images, shapes and button metadata, so the
  `PRINTSTR`/`HTML_PRINT` daily interaction panel is not discarded after the
  preceding dialogue line is emitted.
- Scope: the `eraAM2` development Session (Session name, not a Game name),
  COMP-007/SAVE-011, PLAY-002 and ADR-0019/ADR-0024. This keeps generated
  `sav/imgNNNN.png` files and unsaved runtime composites browser-addressable
  without embedding raw HTML or an inline duplicate raster.
- Verification: `HtmlPrintAllowsLargeLayeredPortraitFragmentWithinParserBudget`,
  `HtmlPopPrintingStringConsumesPendingStructuredOutputOnly`,
  `HtmlPopPrintingStringKeepsTheInteractivePanelInTheSameHtmlPrint`,
  `SavedDynamicSpriteResolvesThroughHtmlPrintInSavLayout`,
  `UnsavedDynamicSpriteMaterializesCurrentGraphicsSurface`, and the existing
  Graphics/HTML RuntimeBridge tests in the development Docker environment.

## 2026-08-26 — Browser right-click message skip (issue #2)

- `UpstreamHeadless/HeadlessEmueraConsole.cs` preserves the desktop
  right-click message-skip mode while the headless interpreter advances
  through consecutive `EnterKey`/`AnyKey` waits, and clears it at the first
  value or forced wait boundary.
- `RuntimeAdapter` carries the existing structured `POINTER` button `2`
  gesture as `GameConsoleInput.SkipMessage`; the Realtime and IPC contracts
  remain unchanged. The console surface accepts the same gesture from a
  browser context menu or a two-finger touch start. Multi-touch is captured
  at the console-page boundary before any descendant control can receive a
  synthesized click; single-finger controls and game hit regions keep their
  existing behavior.
- Scope: issue #2, PLAY-009 and the cross-platform input behavior in
  ADR-0018/ADR-0025. Verification: `PromptController`/console surface tests,
  `StructuredGameConsoleInputTests`, and
  `RightPointerSkipsConsecutivePressAnyKeyWaitsUntilValueInput` in the
  development Docker environment.

## 2026-08-26 — Unified runtime diagnostic severity

- `UpstreamHeadless/UpstreamRuntimeSession.cs` no longer treats a non-empty
  `RuntimeMessages` collection as initialization failure. The pinned upstream
  `Process.Initialize` result plus its headless ERB-loader error state are the
  initialization error signals; the headless `HasFatalError` transition remains
  the execution error signal. The loader state is exposed only at the headless
  seam because desktop Emuera reports it later from the title state.
- `Headless/EmueraRuntimeHost.cs` preserves initialization and execution
  messages as bounded non-fatal `runtime_message` diagnostics. Warnings remain
  `runtime_warning`; only fatal runtime transitions produce blocking errors.
- `Infrastructure/Games/GameValidatorProcessClient.cs` normalizes persisted
  severity from `activationBlocking`, preventing an informational message from
  being stored as `ERROR` and preventing a blocking error from being labelled a
  warning. Scope: ADR-0011, P1-04 GAME-007.
- Verification: the loading-report Validator regression accepts the package
  with a non-blocking diagnostic, while malformed ERB remains a blocking
  `RUNTIME_INITIALIZATION_FAILED`; direct runtime and protocol normalization
  regressions cover both sides of the boundary.

## 2026-08-26 — Session-local upstream JSON settings

- `UpstreamHeadless/UpstreamRuntimeSession.cs` now calls the pinned upstream
  `JSONConfig.Load()` after binding `Program.ExeDir` to the current SessionRoot.
  A missing `setting.json` is therefore created with upstream defaults inside
  that root; Validator uses a temporary root and Worker uses the persistent
  SessionRoot, so Game source content is never modified by this lifecycle.
- `Upstream/Emuera/Runtime/Config/JSON/JSONConfig.cs` resolves the file path on
  every load/save instead of caching the first `Program.ExeDir`. This preserves
  isolation when the process-global pinned runtime is reused for another
  Session. The runtime gate still serializes upstream sessions, and JSON data is
  reloaded for each initialization.
- `Upstream/Emuera/Runtime/Script/Statements/FunctionIdentifier.cs` refreshes
  the optional `VARI`/`VARS` registrations after each JSON load, so a reused
  headless process cannot retain the previous Session's extension set.
- Scope: SESS-011/SAVE-011, COMP-002 and the SessionRoot rules in ADR-0007 and
  ADR-0035. A missing file keeps the upstream default
  `UseScopedVariableInstruction:false`; packages that use `VARI`/`VARS` must
  provide the setting explicitly.
- Verification: headless creation, per-SessionRoot reload, Validator setting
  loading, real Worker creation and input-root immutability regressions in the
  RuntimeCompatibility, Worker Integration and Infrastructure suites.

## 2026-08-25 — WebP asset and upstream Sprite compatibility

- `Headless/WebpMetadataReader.cs` validates bounded RIFF/WEBP containers and
  extracts dimensions from VP8, VP8L, VP8X and animated WebP payloads without
  decoding untrusted pixels in the metadata path.
- `Headless/RuntimeImageMetadataPort.cs` now accepts validated WebP resources
  as `image/webp`; `UpstreamHeadless/HeadlessWebpDecoder.cs` bridges the
  pinned upstream Bitmap-based Sprite path through the container's `libwebp7`.
- Scope: P1-07/COMP-007 manifest sprites, including `PRINT_IMG`,
  `SPRITECREATED`, `GDRAWSPRITE` and `CBGSETSPRITE`. Dynamic browser raster
  surfaces remain bounded PNG payloads.
- Verification: WebP metadata rejection, `PRINT_IMG` WebP resolution and
  upstream AppContents Sprite loading in the RuntimeCompatibility suite.

## 2026-08-25 — Preserve headless PRINTBUTTON state for BINPUT

- UpstreamHeadless/HeadlessEmueraConsole.cs now mirrors structured
  PRINTBUTTON and implicit numeric-button nodes into the pinned upstream
  ConsoleDisplayLine.Buttons inventory. Integer and string submission
  values retain their upstream distinction; BINPUT/BINPUTS therefore
  validate the same button set that the browser receives.
- Upstream/Emuera/UI/Game/ConsoleButtonString.cs uses a headless-only
  legacy-generation seam so this compatibility mirror does not alter the
  structured RuntimeAdapter generation exposed to HTML/buttons.
- The headless compatibility PrintStringBuffer is initialized even when
  no runtime font is bound, and a pending button-bearing line is committed at
  the upstream RefreshStrings boundary. CLEAR removes the compatibility
  inventory together with the structured console.
- Scope: COMP-002/007, PLAY-003 and the real erablue startup menu. Verification:
  BinputSeesHeadlessPrintButtonBeforeLineBreak,
  BinputDoesNotReuseConsumedButtonGeneration and all 114 tests in the
  RuntimeCompatibility suite pass in the development Docker environment.

## 2026-08-27 — Preserve HTML_PRINT integer buttons for BINPUT (issue #14)

- `UpstreamHeadless/UpstreamHtmlTranslator.cs` now carries the pinned
  `HtmlManager` button value kind to the headless legacy button inventory;
  numeric `<button value='...'>` elements are therefore represented as
  integer buttons while string values remain string buttons.
- `UpstreamHeadless/HeadlessEmueraConsole.cs` collects the numeric HTML
  buttons only after parsing and translation succeeds, then mirrors them into
  `ConsoleDisplayLine.Buttons` before the `BINPUT` boundary. The legacy
  projection recursively walks structured containers such as `DivNode`, and
  `RefreshStrings` uses the same traversal for buffered HTML output. This
  keeps the structured browser node and the upstream input validator in
  agreement without changing the IPC or Realtime contracts. The projection
  also keeps source-node snapshots for the full logical-line lifecycle, so
  same-line appends, `CLEARLINE` replacements, and deletions update the
  server-side button inventory atomically with the structured output; browser
  rendering is never consulted to decide whether a button exists.
- Scope: issue #14, the `eraBlue` Session's
  `起床前メニュー関連処理/起床前メニュー.ERB:325` path and the analogous
  `キャラクター招待改良版.ERB:162` path, PLAY-002, COMP-002/007 and
  ADR-0024. Both ERB files emit numeric menu buttons; the failure was the
  headless type/tree projection, not missing game content.
- Verification: `BinputSeesIntegerButtonFromHtmlPrint`, the nested-div and
  buffered-HTML BINPUT regressions, the font-bound `CLEARLINE` replacement
  regression, all four `BINPUT`/`BINPUTS`/`ONEBINPUT`/`ONEBINPUTS` variants,
  the existing PRINTBUTTON/BINPUT generation regressions, and the
  RuntimeCompatibility suite in the development Docker environment.

## 2026-08-24 — Use the bundled face for GRAPHICS-mode layout measurement

- `Upstream/Emuera/UI/Game/StringMeasure.cs` now consults the bound TTF's
  `hmtx` metrics before the upstream `Graphics.MeasureCharacterRanges` path.
  This keeps games such as eraSQC, whose `emuera.config` selects
  `描画インターフェース:GRAPHICS`, on the same authoritative measurement
  path as TEXTRENDERER and the browser's matching WOFF2 face.
- The upstream graphics measurement remains the fallback for unbound fonts and
  non-headless callers. Scope: P1-S04, PLAY-014, COMP-007 and ADR-0029.
- Verification: the GRAPHICS/TEXTRENDERER bundled-font parity regression and
  the RuntimeCompatibility font-layout suite in the dev Docker environment.

## 2026-08-24 — Era East Asian ambiguous map-cell compatibility

- `UpstreamHeadless/HeadlessFontMetrics.cs` gives the box-drawing, common
  geometric and horizontal-bar glyphs used by Era text maps the selected
  face's U+3000 CJK-cell advance when their native `hmtx` advance is only a
  half-cell. Ordinary ASCII and glyphs already occupying a wide cell are
  unchanged.
- The Web renderer applies the matching bounded visual projection inside the
  Worker-published positioned segment, so browser ink and authoritative
  geometry remain consistent for both bundled font families.
- Scope: eraTW map rendering, PLAY-002/014, COMP-007/010 and ADR-0029.
  Verification: `EraTwMapButtonsKeepOneFullwidthDigitEqualToTwoAsciiDigits`,
  `EraTwMapWideSymbolsKeepTheCjkCellAdvance`, the Web renderer projection test
  and the complete dev-Docker check.

## 2026-08-24 — Era halfwidth yen display compatibility

- `UpstreamHeadless/HeadlessEmueraConsole.cs` and `UpstreamHtmlTranslator.cs` map visible U+005C text to U+00A5
  before authoritative measurement and physical layout when the persistent Session option is enabled.
- The mapping deliberately excludes button submission values, prompt defaults, user input, parser data and paths.
- Scope: SESS-015, PLAY-016 and ADR-0031. Verification covers enabled/disabled display and unchanged button values,
  API/persistence propagation, migration defaults and the complete dev-Docker check.

## 2026-08-23 — Bundled session font measurement and authoritative physical layout

- Modified `Upstream/Emuera/UI/FontFactory.cs`,
  `Upstream/Emuera/Runtime/Config/ConfigData.cs`,
  `Upstream/Emuera/Runtime/Script/Statements/Function/Creator.Method.cs`,
  `Upstream/Emuera/UI/Game/EmueraConsole.Print.cs` and
  `Upstream/Emuera/UI/Game/Image/GraphicsImage.cs` so headless measurement and
  graphics text use the selected image-owned `PrivateFontCollection`. Game
  font names are retained only as bounded diagnostics; they never trigger
  host font discovery or a package-font load.
- `UpstreamHeadless/HeadlessEmueraConsole.cs` now uses the real
  `StringMeasure`/`PrintStringBuffer` metrics for physical lines and applies
  the pinned Shift-JIS `PRINTC`/`PRINTLC` padding correction. It emits
  positioned segments and button actions while keeping `PrintCPerLine` as the
  upstream flush trigger.
- Scope: P1-S04, ADR-0029, SESS-013, PLAY-013/014 and COMP-007/010. The
  six-face catalog and web conversions are verified by
  `scripts/verify-runtime-fonts.sh`; no SessionRoot font is loaded.
- Verification: `./scripts/verify-runtime-fonts.sh`, RuntimeCompatibility
  font/layout and PRINTC tests, structured IPC v6/realtime v4 contract tests,
  and the complete dev-Docker check.

- `UpstreamHeadless/HeadlessFontMetrics.cs` reads the selected catalogued TTF's
  Unicode `cmap`, `head` and horizontal `hmtx` advance tables. This replaces
  the headless placeholder/libgdiplus CJK width path at the `TextRenderer`
  compatibility seam, so physical segment widths, alignment and PRINTC padding
  use the same face metrics as the browser's verified WOFF2. The browser keeps
  each segment/action clipped to that authoritative box; it does not use ink
  overflow to compensate for a measurement mismatch.
- Verification: `FontLayout|PrintCCompatibility` RuntimeCompatibility tests,
  mobile Chromium positioned-button geometry/pixel regression, and the full
  dev-Docker check.

## 2026-08-23 — Preserve single-line PRINTSINGLE output

- `UpstreamHeadless/HeadlessEmueraConsole.cs` keeps `PRINTSINGLE*` output on
  one physical line and truncates the measured prefix at the drawable width;
  ordinary wrapping and no-wrap bar output remain separate paths.
- Scope: PLAY-002/014 and COMP-007/010 structured-console compatibility.
- Verification: `PrintSingleFormsTruncatesInsteadOfWrapping` and the full
  dev-Docker check.

## 2026-08-23 — Delete complete physical groups for wrapped CLEARLINE output

- `UpstreamHeadless/HeadlessEmueraConsole.cs` now treats `CLEARLINE` as a
  logical-line operation. A measured logical line may have several physical
  `ConsoleLine` rows after wrapping, so deferred replacement and visibility
  cleanup replace or delete the complete logical group instead of retaining
  whitespace-only prefix rows. Immediate reprints continue to reuse the
  logical line identity; obsolete physical suffixes are deleted in the same
  transaction.
- Scope: P1-S04 authoritative layout, GAME-007/COMP-002 structured-console
  compatibility. This covers eraTW's fullwidth-space movement status line
  followed by `CLEARLINE 1`.
- Verification: `ClearLineRemovesAllPhysicalRowsOfWrappedLogicalLine` and the
  RuntimeCompatibility suite in the dev Docker environment.

## 2026-08-22 — Literal fast path for direct ESCAPE in FINDELEMENT

- Modified the upstream expression/function path and `VariableEvaluator` so a
  direct `ESCAPE(...)` argument to `FINDELEMENT` or `FINDLASTELEMENT` retains
  its literal provenance. Exact searches use ordinal equality and partial
  searches use ordinal substring lookup without constructing or executing a
  Regex instance.
- Arbitrary EM+EE regular-expression arguments continue through
  `RegexFactory`, including patterns composed from an escaped value and other
  regex syntax. Constant-folded `ESCAPE` terms retain the same marker and
  continue to expose the escaped string to other functions.
- Scope: runtime implementation only; no game ERB files are changed.
- Verification: `docker compose -f docker/compose.dev.yml run --rm api dotnet
  test tests/CloudEmuera.RuntimeCompatibility.Tests --no-restore
  --configuration Release --filter
  'FullyQualifiedName~FindElementEscapedLiteralUsesLiteralPathAndKeepsRegexFallback'`.

## 2026-08-22 — Recognize plain literal patterns in FINDELEMENT

- Modified `RegexFactory` and the `FINDELEMENT`/`FINDLASTELEMENT` function
  path so an evaluated pattern containing no regex metacharacters uses the
  existing ordinal string-search implementation. The classifier is
  conservative and leaves patterns containing escapes, anchors, wildcards,
  groups, character classes or alternation on the regular-expression path.
- Scope: runtime implementation only; no game ERB files are changed. This
  complements the explicit `ESCAPE(...)` provenance fast path and avoids
  requiring scripts to add `ESCAPE` around already-literal patterns.
- Verification: the
  `FindElementEscapedLiteralUsesLiteralPathAndKeepsRegexFallback` test also
  covers plain literal exact/partial searches and regex fallback patterns.

## 2026-08-20 — Reuse structured line identity for immediate CLEARLINE reprints

- `UpstreamHeadless/HeadlessEmueraConsole.cs` defers a single-line delete until
  the next output is known. An immediate reprint uses `ReplaceLine` with the
  original line ID; a delete with no replacement is still emitted before the
  next input wait or full console clear.
- Purpose: the game's 30 ms portrait loop uses `CLEARLINE 1` followed by
  `REPRINT_SHOP_ANIME`. Keeping the line identity lets the browser update the
  existing Sprite/Canvas nodes instead of unmounting and remounting the whole
  animated line each frame.
- Scope: GAME-007/COMP-002 structured-console rendering compatibility.
- Verification: `HeadlessConsoleReusesLineIdentityForClearAndImmediateReprint`
  plus the RuntimeCompatibility suite in the dev Docker environment.

## 2026-08-20 — Preserve layered HTML image positioning and timed-input defaults

- `UpstreamHeadless/HeadlessEmueraConsole.cs` keeps `TINPUT`/`TINPUTS` default
  values as timeout results instead of exposing them as prefilled browser text;
  timeout dispatch still returns the original numeric or string default.
- The Web console projects HTML `pos` nodes to the same absolute x coordinate
  used by desktop Emuera, allowing layered portrait parts to composite instead
  of flowing side by side. Non-interactive portrait layers are pointer-transparent;
  interactive `pos` buttons remain in document flow. Sprite animation redraws
  only when the frame changes, uses a decoded-image cache across reprinted lines,
  and keeps the last frame visible while a replacement asset loads.
- Scope: GAME-007/COMP-002 visual and timed-input compatibility.
- Verification: RuntimeCompatibility timed-input regression and Web console
  rendering/typecheck/build checks pass in dev Docker.

## 2026-08-26 — Preserve explicit overlay origins during physical layout

- `UpstreamHeadless/HeadlessEmueraConsole.cs` now treats a locked `button pos`
  coordinate as an absolute overlay origin even when an earlier layer has
  already advanced the flow cursor. This keeps repeated `pos='0'` portrait
  layers at one x coordinate instead of placing them side by side.
- Incremental reflow also carries the positioned action's x coordinate back
  into the reconstructed logical line. Unpositioned buttons retain ordinary
  measured flow placement.
- The Web projection keeps the disabled empty-value action used for
  `<nonbutton>` portrait layers as a pointer-transparent span, not a disabled
  HTML button, so the global disabled-button opacity cannot alter the
  composite; source order remains the paint order and later layers stay on
  top.
- Verification: `HtmlPrintKeepsExplicitPositionsForLayeredPortraits` in the
  RuntimeCompatibility suite; no protocol or upstream source version change.

## 2026-08-20 — Preserve upstream optional Sprite CSV fallbacks

- Headless glue: `Headless/EmueraRuntimeHost.cs` now matches upstream
  `AppContents.CreateFromCsv` for optional Sprite rectangle, offset, delay and
  destination fields. Non-numeric optional values produce bounded non-fatal
  `runtime_warning` diagnostics and retain upstream defaults; invalid numeric
  rectangles skip only the affected Sprite instead of failing runtime
  initialization.
- Scope: GAME-007/COMP-002 resource compatibility. The parser remains the
  pinned upstream implementation; this change corrects the headless structured
  Sprite adapter's fallback behavior.
- Verification: `HeadlessRuntimeFixtureTests.StaticSpriteCsvKeepsUpstreamFallbackForMalformedOptionalRectangles`
  and the existing Sprite clipping test pass in the dev Docker environment.

## 2026-08-19 — Preserve TWAIT's single effective request in headless mode

- Modified upstream file: `Upstream/Emuera/Runtime/Script/Statements/Instraction.Child.cs`.
  Desktop Emuera first stages `ReadAnyKey` and then immediately overwrites it
  with TWAIT's timed `InputRequest`. The synchronous headless `ReadAnyKey`
  would incorrectly block and publish that intermediate prompt. Under
  `CLOUDEMUERA_HEADLESS`, TWAIT now retains only the original
  `NeedWaitToEventComEnd = false` side effect before opening its final timed
  request; desktop compilation remains unchanged.
- Headless glue: `UpstreamHeadless/HeadlessEmueraConsole.cs` exposes the
  narrow side-effect method. `InputCoordinator` rejects client input for a
  `WaitOnly` prompt so force-wait cannot finish early through a forged request.
- Scope: official TWAIT semantics and ADR-0018/P1-07 timed input contract.
- Verification: RuntimeCompatibility TWAIT regression tests and RuntimeAdapter
  WaitOnly rejection test in the dev Docker environment.

## 2026-08-19 — Callable COM_ABLE callback parameters

- Modified upstream file: `Upstream/Emuera/Runtime/Script/Loader/ErbLoader.cs`.
  The parser identified `COM<n>`, `COM_ABLE<n>` and `ABLUP<n>` labels as system
  functions, then marked a non-event label with declared parameters as invalid
  even though its adjacent upstream comment requires the warning level to be
  reduced and the error cleared. A dynamic `TRYCCALLFORM COM_ABLE{n}(ARG)` then
  failed during argument conversion and terminated the Worker. Non-event labels
  now retain the compatibility warning while accepting and storing their declared
  arguments; event-system labels remain fatal.
- Scope: ADR-0011, GAME-007 and COMP-002 compatibility for real era game
  command callbacks. The warning remains a bounded `runtime_warning` diagnostic
  and is not written to the player console.
- Verification: `HeadlessRuntimeFixtureTests.DynamicComAbleCallbackWithArgumentRemainsCallableAndWarningStaysOutOfConsole`
  runs the real pinned interpreter through dynamic `TRYCCALLFORM`; dev-Docker
  RuntimeBridge and full RuntimeCompatibility test suites pass.

## 2026-08-18 — Opt-in ERB/structured-output trace

- Modified upstream file: `Upstream/Emuera/Runtime/Script/Statements/ExpressionMediator.cs`.
  Under the existing `CLOUDEMUERA_HEADLESS` build symbol it reports the current
  ERB source position, print instruction, rendered text and wait-for-input flag
  to the headless diagnostic bridge immediately before normal output processing.
  Desktop builds retain upstream behavior; headless Workers remain unchanged
  unless the explicit trace flag is enabled.
- Headless glue: `RuntimeDebugTrace` is disabled by default and is enabled only
  by `CLOUDEMUERA_RUNTIME_DEBUG_TRACE=1` or `true`, including in Production.
  It appends JSON Lines to the owning Session directory's
  `metadata/runtime-debug.jsonl` (outside game `root/`), recording `erb_output`,
  `erb_wait` (including standalone `WAIT`) and every resulting structured
  console operation with sequence, prompt and bounded Node summaries. It never
  records submitted input values.
- Scope: opt-in diagnosis for PLAY-001/PLAY-004 and the eraTW consecutive
  empty-input investigation.
- Behavioral parity: `HeadlessEmueraConsole.ReadAnyKey` now clears
  `Process.NeedWaitToEventComEnd`, matching upstream `EmueraConsole.ReadAnyKey`.
  Without this, eraTW's `EVENTCOMEND.ERB:487` `TWAIT 100,0` opened its own
  wait but the upstream process added a second fallback empty wait afterwards.
- Verification: RuntimeBridge tests exercise PRINT/PRINTW output and assert the
  trace file is created only when the explicit environment flag is enabled.

## 2026-08-18 — Linux resource path composition in AppContents

- Modified upstream file: `Upstream/Emuera/UI/Game/Image/AppContents.cs`.
  `LoadContents` composed the parent image path as
  `Path.GetDirectoryName(path) + "\\"` and `CreateFromCsv` concatenated
  `dir + arg2`, producing Windows-only separators. On Linux every declared
  sprite failed `File.Exists`/bitmap load, so `SPRITECREATED` was false for all
  resources and era-games such as eraTW silently skipped `SPRITECREATED`-gated
  portraits (`Look.ERB` → `PRINT_TARGET_IMAGE` → `画像セット`). The headless
  build now uses `Path.DirectorySeparatorChar` and `Path.Combine`, which is
  byte-identical on the desktop Windows build.
- Scope: COMP-007 headless Linux resource compatibility with real era-game
  distributions; no desktop behavior change, no resource-name semantics change.
- Verification:
  `ReproAppContentsSpriteRegistryLoadsResources` loads `立ち絵.csv`/`1.png`
  through the pinned `AppContents.LoadContents` and asserts
  `SPRITECREATED(立絵_服_通常_1)`; dev-Docker RuntimeBridge tests and the full
  `scripts/check.sh` pass.

## 2026-08-17 — P1-07 upstream HTML parser extraction

- Modified the pinned upstream HTML display area: `Upstream/Emuera/UI/Game/HtmlManager.cs`,
  `HtmlSemanticModel.cs`, `UpstreamHtmlFragmentMaterializer.cs`, `ConsoleStyledString.cs`,
  `ConsoleImagePart.cs`, `ConsoleShapePart.cs` and `Runtime/Utils/EvilMask/ConsoleDivPart.cs`.
  `HtmlManager.ParseFragment` now exposes the original state machine as a neutral internal
  fragment with bounded input/tag/depth/output accounting; no raw HTML, URL, GDI object or
  desktop UI object crosses the headless boundary. The part classes retain only direct
  semantic values needed by that materialization, and the headless build avoids font/GDI
  lookup while the desktop build remains on the original path.
- `UpstreamHeadless/UpstreamHtmlTranslator.cs` maps that fragment once into bounded
  RuntimeAdapter nodes. `HeadlessEmueraConsole.PrintHtml` and `PrintHTMLIsland` share the
  same parse/translation path, preserve display-line versus print-buffer boundaries, and
  fail atomically on CloudEmuera resource/capacity errors.
- Requirements/decisions: P1-07 HTML parser reuse plan, ADR-0024, ADR-0018 and ADR-0019.
- Verification: dev-Docker solution build, HTML_PRINT/HTML Island RuntimeBridge tests,
  structured IPC/realtime mapper tests and Web typecheck.

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
  recorded in `internal-docs/runtime-system-io-audit.zh-CN.md`. All `Program.*Dir`
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

## 2026-08-20 — Sprite resource warning compatibility

- `Headless/EmueraRuntimeHost.cs` now follows the pinned upstream
  `AppContents.CreateFromCsv` policy for malformed Sprite resources: missing or
  unreadable images, invalid optional fields, duplicate names, invalid
  animation declarations, empty animations, out-of-canvas frames, and excess
  frames produce bounded non-fatal `runtime_warning` diagnostics and are
  skipped or defaulted as appropriate. The structured console frame limit is
  retained as a safety boundary.
- Source rectangles are clipped to the image while preserving the requested
  destination size, including partially out-of-bounds legacy declarations.
- Verification: `HeadlessRuntimeFixtureTests.SpriteLoadWarningsDoNotRejectTheWholeGame`,
  `RuntimeCompatibility.Tests` (83 passed), and the existing malformed
  rectangle regression test.

## 2026-08-20 — Headless CHKFONT private-font fallback (superseded by S04)

- Modified upstream file:
  `Upstream/Emuera/Runtime/Script/Statements/Function/Creator.Method.cs`.
  The headless runtime deliberately leaves `GlobalStatic.Pfc` uninitialized to
  avoid loading desktop private font files. `CHKFONT` and the related graphics
  font lookup now treat that optional collection as empty instead of dereferencing
  it.
- Scope: historical COMP-002 behavior. The S04 binding above supersedes the
  system-font query for production Sessions: `CHKFONT` now recognizes only the
  selected bundled face and its controlled aliases.
- Verification:
  `HeadlessRuntimeFixtureTests.CheckfontTreatsUnavailablePrivateFontsAsNotInstalled`.
## 2026-08-27 — P1-S09 structured browser tooltip presentation

- Headless glue change: `UpstreamHeadless/HeadlessEmueraConsole.cs` routes the
  eight fixed-baseline `TOOLTIP_*` setters through the strongly typed
  `ITooltipStateSink` instead of throwing the desktop `HOST_SHIM` failure.
  Requested game fonts are mapped to the Session face and diagnosed once.
- Protocol change: structured IPC v7 and Realtime v5 carry the complete
  bounded tooltip presentation plus validated optional PNG resources.
- Browser change: game tooltips use one delegated portal layer for
  mouse/hover-pen, keyboard focus, touch corner inspection, long press and
  explicit inspect mode. Native `title` attributes were removed from game
  button, nonbutton and CBG hit targets.
- Graphics projection: `GraphicsImage` now publishes a monotonic headless
  revision for surface mutations. The headless Console incrementally tracks
  visible numeric tooltip references, encodes only dirty referenced surfaces
  as bounded PNG resources, reclaims stale resources, and flushes mutations at
  INPUT/WAIT/quit stable boundaries. Projection failures retain raw text and
  emit bounded diagnostics without rolling back primary display output.
- Verification: `TooltipSettersRunThroughPinnedInterpreterAndPublishPresentationState`,
  `TooltipGraphicsRewriteFlushesLatestPngAtInputBoundary`,
  `DestroyedTooltipGraphicsRemovesOldProjectionAndKeepsRawText`, the
  `v18-core`/`em-ee-core` fixture scenarios, five-project Playwright tooltip
  matrix, IPC v7 contracts, Realtime v5 contracts and the static capability
  verifier.

## 2026-08-29 — Normalize HTML `button pos` before physical layout

- `UpstreamHtmlFragmentMaterializer` preserves the pinned HtmlManager's raw
  `RelativePointX` value. `UpstreamHeadless/HeadlessEmueraConsole.cs` now
  converts that value from hundredths of the configured font size to physical
  pixels at the layout boundary, matching `ConsoleButtonString.LockPointX`.
- Already materialized `PositionedInlineSegmentNode` coordinates are treated
  as physical and are not scaled again. Incremental logical-line reflow marks
  reconstructed physical buttons separately, while pending HTML output keeps
  the raw coordinate available to `HTML_POPPRINTINGSTR`.
- Scope: PLAY-002/COMP-007 visual compatibility for WindowDrawer/eraFL
  positioned tables and layered HTML output. No protocol or upstream version
  change.
- Verification:
  `HeadlessRuntimeFixtureTests.HtmlPrintConvertsRelativeButtonPositionsToPhysicalPixels`,
  the complete RuntimeCompatibility suite (146 passed), and the existing
  layered portrait/reflow coverage.

## 2026-08-29 — Keep HTML flow cursor local to explicit positions

- `UpstreamHeadless/HeadlessEmueraConsole.cs` now tracks the inline flow
  cursor separately from the physical line's maximum painted extent. An
  explicit HTML `pos` therefore establishes the starting point for following
  unpositioned content even when it moves backwards over an earlier window.
  This preserves the concatenated WindowDrawer `nobr` layout used by eraFL:
  module borders retain their absolute coordinates while path buttons follow
  their own window instead of inheriting the module's far-right edge.
- Alignment still uses the maximum painted extent, so layered portraits and
  other overlapping absolute segments retain their existing composition.
  No protocol or upstream version change is required.
- Scope: PLAY-002/COMP-007 visual compatibility for positioned tables and
  mixed absolute/flow HTML output.
- Verification:
  `HeadlessRuntimeFixtureTests.HtmlPrintResetsFlowCursorAfterExplicitPositionMovesBack`,
  the complete RuntimeCompatibility suite, and the existing layered
  portrait/reflow coverage.
