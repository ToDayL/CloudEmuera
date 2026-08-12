# Third-party notices

CloudEmuera's original code is licensed under Apache-2.0. That license does
not replace or relicense the third-party works listed below.

## Emuera.EM+EE

- Upstream: <https://gitlab.com/EvilMask/emuera.em>
- Local path: `src/CloudEmuera.EmueraRuntime/Upstream`
- Pinned commit: `2175f8a629257efb08214e093704b3a3d3d06d05`
- Commit date: 2026-07-25
- Primary license: zlib/libpng license
- Original tree: `a3c96867e3a5b5d5f90877a4e7c6f8056d5f5b9b`
- License file: `src/CloudEmuera.EmueraRuntime/Upstream/Readme/License/Emuera.LICENSE.txt`

The upstream repository contains additional bundled components and license notices. Their original notices must remain intact. This file is an inventory, not a replacement for those license texts.

CloudEmuera vendors and modifies this source directly. Modified files must be
identified as changed and must retain the applicable upstream notices, as
required by the zlib/libpng license. Import and modification records live in
`src/CloudEmuera.EmueraRuntime/UPSTREAM.md` and `MODIFICATIONS.md`.

## System.Drawing.Common

- Package: `System.Drawing.Common` 6.0.0
- Source: <https://github.com/dotnet/runtime>
- License: MIT
- License: <https://github.com/dotnet/runtime/blob/v6.0.0/LICENSE.TXT>

This package is intentionally pinned for the Linux MVP compatibility layer described by ADR-0019. CloudEmuera does not claim that this configuration has modern .NET non-Windows product support.

## libgdiplus

- Source: <https://github.com/mono/libgdiplus>
- Distribution: installed from the Debian base-image package repository
- License: MIT/X11
- License: <https://github.com/mono/libgdiplus/blob/main/LICENSE>

`libgdiplus` is present only in the Worker/runtime container image and implements the native GDI+ compatibility calls used by the pinned Emuera source.
