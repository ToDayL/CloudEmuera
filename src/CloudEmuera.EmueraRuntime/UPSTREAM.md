# Emuera.EM+EE source provenance

This directory contains a directly vendored source snapshot. It is tracked as
ordinary CloudEmuera repository files, not as a Git submodule.

| Field | Value |
| --- | --- |
| Upstream repository | `https://gitlab.com/EvilMask/emuera.em.git` |
| Upstream commit | `2175f8a629257efb08214e093704b3a3d3d06d05` |
| Original Git tree | `a3c96867e3a5b5d5f90877a4e7c6f8056d5f5b9b` |
| Commit date | `2026-07-25` |
| Imported | `2026-08-04` |
| Source directory | `Upstream/` |
| Primary license | zlib/libpng-style Emuera license |
| License file | `Upstream/Readme/License/Emuera.LICENSE.txt` |
| License SHA-256 | `8770a79e679a354cffc4005cee99403d609c31ce88dd2b79e4a50325317beb77` |

The upstream source and bundled notices retain their original licenses. The
root CloudEmuera Apache-2.0 license does not relicense files below `Upstream/`.

## Modification rules

- Import a new reviewed upstream revision in a dedicated commit before making
  CloudEmuera-specific changes.
- Never update from a moving branch without recording the exact commit/tree.
- Preserve upstream copyright and license notices.
- Mark modified upstream files prominently and add an entry to
  `../MODIFICATIONS.md` describing purpose, scope and verification.
- Update `RuntimeBaseline`, `THIRD_PARTY_NOTICES.md`, this file and the runtime
  compatibility report together when the upstream baseline changes.
- Run `./scripts/verify-third-party.sh` and the complete runtime compatibility
  suite before accepting an upstream update.
