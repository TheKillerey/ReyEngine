# Roadmap to v0.4.0 — "What you see is what the game shows"

v0.3.0 was the editing release (the inspector redesign). v0.4.0 closes the gap between what the
editor renders and what the game renders.

> **Revised 2026-08-06 after investigating items 1 and 2.** Both turned out to be substantially
> already fixed. The backlog they came from was written around v0.2.2 and never re-checked against
> M261 / M268 / M328–M350, so it described defects that later work had quietly closed. **Verify an
> item against the current build before scheduling it** — two of the first two were stale, and one of
> them (the "map is too dark, find Riot's gamma step") had already cost two reverted attempts
> (M284, M285/M286) at a problem that did not exist.

## Done

| # | Item | Outcome |
|---|------|---------|
| 1 | ~~Map brightness root cause~~ | **Not a brightness problem.** Disassembling the map pixel shaders out of `ShaderCache.dx11` shows every one ending in plain linear `mad` into `o0` — no sRGB encode, no `pow`-shaped math. **League is gamma-space end to end and the "find where Riot applies gamma" theory is closed with evidence; do not add sRGB SRVs/RTVs.** The real defect was black map *ground*: `4TextureBlend_UVBased_baseMat` carries no RDEF default on any used constant, and nothing supplied `R_/G_/B_mask_multiplier` (declared `1` in shaders.bin), so they uploaded as `0` and collapsed the four-way terrain blend. The fallback already existed in `Dx11SceneBuilder` since M257; the Shader Preview's material path never got it. Fixed as parity in **M352** (`30e524a`). Riot declares `Tint = 0`, which also retroactively settles M284/M286. |
| 6 | ~~zstd-subchunked textures~~ | **Already fixed by M135, verified by measurement.** Extracted and decoded every texture in all 18 shipping map WADs: **108,731 textures, 84,858 of them subchunked, 0 extract failures.** The `WadFile.Subchunks` NRE in the backlog note predates M135's TOC-less fallback. The only decode failures anywhere are 192 encrypted esports banners — see Known non-defects — and **M353** now names them honestly instead of reporting "unknown texture file format". |
| 2 | ~~DX11: live geometry edits~~ | **Already implemented, discovered while scoping.** `MainWindow.axaml.cs` watches `MeshVerticesRevision` and calls `UpdateDx11EditedMeshVertices`, a targeted vertex-range upload rather than a rebuild. All ten `MeshVerticesRevision++` sites are transform edits — translate, rotate, scale, gizmo drag, numeric apply, normal flip, tab restore — so moving/rotating/scaling meshes already updates DX11 live. See item 2b for the part that is genuinely missing. |

## Core (defines the release)

| # | Item | Size | Notes |
|---|------|------|-------|
| 2b | DX11 live edits: paint and topology | S–M | The remaining half of item 2, and **unverified** — scope it before building. Painting (`MapPaintSession`) changes vertex colours / baked-paint textures, not positions, so `UpdateMeshVertices` cannot carry it. Add Mesh and delete change topology and need a full rebuild (`MapGeneration++`) rather than a range upload. Test first: with both viewports open, move a mesh (expected: works), then paint, then Add Mesh. |
| 3 | DX11: skybox + clear colour | S | Parity survey item. |
| 4 | DX11: per-material back-face culling | S | Parity survey item. |
| 5 | DX11: grass tint | S | Parity survey item. |
| 7 | DX11: soft particles + beam/trail emitters | M | Remaining particle-pipeline parity. |

## Stretch (ship if ready, hold if not)

| # | Item | Size | Notes |
|---|------|------|-------|
| 8 | Inspector unification | M | `SceneObjectInspectorView` and the Particle Editor inspector still speak the pre-0.3.0 design language. |
| 9 | Top VFX emitter fields | M | The census reads ~40 of 134 emitter fields; cherry-pick what live maps actually use from `docs/research/vfx-support-report.md`, not the whole list. |
| 10 | Materials Filters dropdown | S | The one piece of the v0.3.0 inspector mockup that was skipped. |

## Explicitly deferred to v0.5.0

- **Lightmap bake Phase 2 (UV2 generation)** — blocked on instanced vertex buffers + the
  LeagueToolkit writer; too large to co-headline this release.
- **HUD editing** (viewer → editor) — deserves its own release theme.
- **Stencil particle modes 2/3/4** — research-first, low demand so far.

## Known non-defects (do not "fix")

- **The Shader Preview's material ball has a black lower hemisphere.** `DefaultEnv_Flat` computes
  `saturate(N·L) * SUN_COLOR + baked * LIGHT_MAP_COLOR_SCALE`; the preview sphere has no lightmap, so
  the away-facing half is genuinely zero and correct. It disappears with Map sun off only because
  that path substitutes scale 1.0. Real map geometry never shows it. Adding ambient to make the ball
  look nicer would misrepresent the game — the M284 mistake.

- **192 esports banner textures will never decode.** Everything under
  `assets/esports/sponsoredbanners/secret/` is shipped encrypted by Riot: all 192 files share one
  16-byte header and one byte length (1 MiB + 16) despite being different banners. M353 reports them
  as encrypted; there is nothing to fix and no decoder to write.

## Release criteria

- A side-by-side of the DX11 preview against an in-game screenshot reads as the same image
  (brightness, sky, culling), not "the dark editor version".
- Editing geometry with both renderers open shows the change in both, live — including paint.
- Map12/Bloom load with no missing textures.
- 0 test regressions; wiki pages updated if the look changes.
