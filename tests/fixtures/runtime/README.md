# Runtime compatibility fixtures

This directory contains the two original, synthetic game profiles used by the
P0-01 fixture contract. All payloads were authored for CloudEmuera contributors
and are distributed under Apache-2.0. They are intentionally small and avoid
third-party games, fonts, audio, plugins, DLLs, executables, external URLs and
network dependencies.

manifest.json is the only payload index. It records the runtime baseline,
profile metadata, expected semantic scenario, transcript, media type, declared
encoding, source, SPDX license and SHA-256 for every payload file. The manifest
does not hash itself.

The validation phase is local-only. On a clean checkout, the script first
performs the repository's locked dependency restore; subsequent validation
does not download or query runtime assets:

    ./scripts/verify-runtime-fixtures.sh

When a payload is intentionally edited, hashes may be regenerated only with
the explicit maintenance command:

    ./scripts/verify-runtime-fixtures.sh --update

The default command never changes files. scenario.json describes semantic
steps for the future P0-04/P0-05 harness; expected-transcript.txt is an
expected stable visible-text baseline, not a claim that the upstream
interpreter has already run headlessly.

## Profiles

v18-core is a controlled 1824+v18 compatibility image. Its ERB and CSV
payloads are declared as Shift-JIS, and it exercises startup, PRINT, a
variable, a user function, a branch selected by integer input, EM HTML output,
an image/Sprite resource and root-level save00.sav/global.sav semantics. Its
entry point is @SYSTEM_TITLE, matching the fixed upstream SystemProc startup
callback; the scenario input is the custom title flow.

em-ee-core is bound to the current EM+EE commit in RuntimeBaseline. Its
UTF-8 payloads include one BOM-bearing CSV and BOM-free text files. It uses
the EXISTFUNCTION extension documented by the pinned upstream
Readme/EmueraEE_readme (English).txt, and declares UseSaveFolder:YES, which
is the upstream configuration key that maps native saves into sav/.

Both fixture Sprite CSVs point to an original 2x2 PNG. The fixed upstream
ImgUtils loader sends non-WebP files to System.Drawing.Bitmap, so PNG is used
instead of SVG. The contract checks the PNG signature and dimensions; actual
headless image display remains P0-04 work.

The resource CSV layout follows the pinned upstream
Emuera/UI/Game/Image/AppContents.cs: resource CSV files are scanned below
resources/, and each Sprite row names a parent image followed by an optional
rectangle. The PNG files are original static media sources for this contract;
actual headless display support remains P0-04 work.

Real, locally authorized games may be placed under the gitignored
tests/fixtures/runtime-local/ directory for non-blocking experiments. They
must not be added to this manifest or required by CI.
