# Roadmap to v0.4.0 — "What you see is what the game shows"

v0.3.0 was the editing release (the inspector redesign). v0.4.0 closes the gap between what the
editor renders and what the game renders.

> **Revised 2026-08-06 after investigating items 1 and 2.** Both turned out to be substantially
> already fixed. The backlog they came from was written around v0.2.2 and never re-checked against
> M261 / M268 / M328–M350, so it described defects that later work had quietly closed. **Verify an
> item against the current build before scheduling it** — two of the first two were stale, and one of
> them (the "map is too dark, find Riot's gamma step") had already cost two reverted attempts
> (M284, M285/M286) at a problem that did not exist.
>
> **And verify a render change ON A REAL MAP, not just "it builds and starts".** M354 passed both and
> still deleted the terrain, because it enabled a code path that had never run before.

## Done

| # | Item | Outcome |
|---|------|---------|
| 1 | ~~Map brightness root cause~~ | **Not a brightness problem.** Disassembling the map pixel shaders out of `ShaderCache.dx11` shows every one ending in plain linear `mad` into `o0` — no sRGB encode, no `pow`-shaped math. **League is gamma-space end to end and the "find where Riot applies gamma" theory is closed with evidence; do not add sRGB SRVs/RTVs.** The real defect was black map *ground*: `4TextureBlend_UVBased_baseMat` carries no RDEF default on any used constant, and nothing supplied `R_/G_/B_mask_multiplier` (declared `1` in shaders.bin), so they uploaded as `0` and collapsed the four-way terrain blend. The fallback already existed in `Dx11SceneBuilder` since M257; the Shader Preview's material path never got it. Fixed as parity in **M352** (`30e524a`). Riot declares `Tint = 0`, which also retroactively settles M284/M286. |
| 6 | ~~zstd-subchunked textures~~ | **Already fixed by M135, verified by measurement.** Extracted and decoded every texture in all 18 shipping map WADs: **108,731 textures, 84,858 of them subchunked, 0 extract failures.** The `WadFile.Subchunks` NRE in the backlog note predates M135's TOC-less fallback. The only decode failures anywhere are 192 encrypted esports banners — see Known non-defects — and **M353** now names them honestly instead of reporting "unknown texture file format". |
| 4 | ~~DX11: per-material back-face culling~~ | **Done, second attempt.** M354 shipped it and deleted the map terrain; M356 reverted. The cause was a winding convention that had never executed - the map host pins the global cull off, and `CullMode.None` ignores winding - so M354 ran it for the first time. **M357** corrected it from the user's measurement (with `MirrorX` on, `FrontCounterClockwise=true` removes the surfaces that should remain, so League is CCW-front and mirroring flips it), and **M358** (`3e3f22e`) re-enabled the per-material flag on top. Verified on the reporting user's real map. |
| 3a | ~~DX11: match GL's clear colour~~ | **Done** (M359). The DX11 host set no clear colour at all and fell back to the renderer default `(0.08, 0.09, 0.11)`, a lighter greyer field than GL's `(0.039, 0.051, 0.075)`, so switching renderers shifted the whole image before any geometry drew. **Correction to this file's earlier note:** GL does NOT clear to the map's sky colour - it is a hardcoded editor background in `ViewportControl.OnOpenGlRender`. Deriving one from `MapSunProperties` would have made the two viewports disagree. |
| 2b | ~~DX11 live edits: paint and topology~~ | **Done.** Paint needed building: it is a TEXTURE path, raising neither `MeshVerticesRevision` nor `MapGeneration`, so no signal carried it — **M360** (`497f18b`) routes strokes by overwriting the pooled texture in place (materials hold the SRV, not the key, so swapping the pool entry would never reach them) and **M361** (`134f6b7`) draws the brush ring from GL's tessellation, extracted rather than duplicated. Topology needed nothing: Add Mesh never touches the mapgeo — it stages onto the prop-instance channel, which D3D11 has rendered since M295 — so added meshes already appeared. All three verified by the user on a real map. Delete/removal was not separately traced. |
| 3b | ~~DX11: port the skybox~~ | **Done** (M362). All three sources ported - cubemap, equirect and authored dome - as two HLSL entry pairs, since the dome carries UVs and the cube does not. The depth state was the one thing this file said to settle first, and `SkyboxRenderer.Render` answered it: test off, WRITES off, blend off, cull off, drawn first, `xyww` pinning the far plane - all five mirrored. Two non-transcription decisions: the equirect uses `SampleLevel(...,0)` because `atan2` makes u jump a full turn at the seam and the derivative selects the smallest mip (a blurred stripe GL never had, having no mips), and `DepthClipEnable` is off because `xyww` sits exactly on the clip boundary. Face order needed no remap: GL and D3D11 both walk +X -X +Y -Y +Z -Z. |
| 7a | ~~DX11: soft particles~~ | **Done** (M363), and the roadmap had the framing wrong. DX11 already RENDERED these permutations - M234b worked out constants that neutralise the fade to "fully visible" - so they drew as hard sprites, not broken. Needed: R32_TYPELESS depth (a typed depth format can never carry an SRV), a depth COPY captured lazily on the first soft material inside the draw loop, and the SRV resolved at bind time rather than stored (materials hold the SRV, and this one dies on resize). **The reusable lesson:** `cDepthConversionParams` is deliberately NOT GL's constant. System.Numerics emits a D3D-convention projection, D3D passes it through so window depth fills [0,1] and the textbook pair is right; GL applies its own `(z+1)/2` on top and must DOUBLE the slope, a factor of two its own probe caught. Copying GL here would reintroduce that ~1.9x error mirrored, invisibly. |
| 2 | ~~DX11: live geometry edits~~ | **Already implemented, discovered while scoping.** `MainWindow.axaml.cs` watches `MeshVerticesRevision` and calls `UpdateDx11EditedMeshVertices`, a targeted vertex-range upload rather than a rebuild. All ten `MeshVerticesRevision++` sites are transform edits — translate, rotate, scale, gizmo drag, numeric apply, normal flip, tab restore — so moving/rotating/scaling meshes already updates DX11 live. See item 2b for the part that is genuinely missing. |

## Core (defines the release)

| # | Item | Size | Notes |
|---|------|------|-------|
| 5 | DX11: grass tint | S | Verified real: **zero** references to grass tint in any DX11 file. `CurrentGrassTint` / `CurrentGrassTintRect` exist (M78) and only GL consumes them. |
| 7b | DX11: beam/trail ribbon emitters | **S-M** — scoped, reuse path found | The last open item. `D3D11MapParticles` still skips these at one line, and the recorded reason (*"ribbon geometry, not billboards"*) is accurate. But scoping found the work is mostly **wiring, not invention**: GL already has the generator — `BuildRibbon(buf, k, points, halfWidth, ...)`, private static in `VfxParticleRenderer`, used by BOTH `RenderTrailEmitter` and `RenderBeamEmitter` — and M283 already gave D3D11 a per-material arbitrary-geometry channel whose vertex buffer is **Dynamic**, with `CreateMeshGeometry(positions, uvs, indices)` and a per-frame `UpdateMeshGeometryPositions(id, pos)` that Maps with WriteDiscard. A ribbon rebuilt every frame is exactly that shape. Plan: extract `BuildRibbon` to public static (the M361 precedent — extract, do not duplicate), allocate one geometry per emitter at MAX ribbon length, rewrite positions per frame, drop the skip. **Three things measured as genuinely open, do not discover them mid-build:** (1) ribbon vertex COUNT varies per frame as history grows, while the geometry is allocated once — needs a degenerate-tail convention, not a resize; (2) `UpdateMeshGeometryPositions` deliberately does not touch UVs, but a ribbon's UVs DO slide along its length, so either the API grows a UV twin or the ribbon takes a fixed parameterisation; (3) **beams need a target**, which is precisely why M177 deferred them and M183 solved it only by binding to the target dummy — the map viewport has no dummy, so map beams need their own target resolution before they can be drawn at all. |

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
