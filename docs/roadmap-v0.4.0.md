# Roadmap to v0.4.0 — "What you see is what the game shows"

v0.3.0 was the editing release (the inspector redesign). v0.4.0 closes the gap between what the
editor renders and what the game renders. Every item below comes from the recorded backlog —
measured gaps and explicit deferrals, not speculation.

## Core (defines the release)

| # | Item | Size | Notes |
|---|------|------|-------|
| 1 | Map brightness root cause | M | The editor renders maps darker than in-game (measured DX11 map mean 34.6 against a visibly brighter in-game reference). Two attempts were tried and reverted (M284 tint default, M285 input-sRGB); the next step is evidence from the compiled map pixel shader and the render-target pipeline, not another guess. |
| 2 | DX11: live geometry edits | M | The largest recorded GL-vs-DX11 gap: edits reach the GL viewport immediately but DX11 only on scene rebuild. |
| 3 | DX11: skybox + clear colour | S | Parity survey item. |
| 4 | DX11: per-material back-face culling | S | Parity survey item. |
| 5 | DX11: grass tint | S | Parity survey item. |
| 6 | zstd-subchunked textures | S–M | Map12/Bloom `.tex` fail to load (`WadFile.Subchunks` NRE); whole texture families invisible today. |
| 7 | DX11: soft particles + beam/trail emitters | M | Remaining particle-pipeline parity. |

## Stretch (ship if ready, hold if not)

| # | Item | Size | Notes |
|---|------|------|-------|
| 8 | Inspector unification | M | `SceneObjectInspectorView` and the Particle Editor inspector still speak the pre-0.3.0 design language. |
| 9 | Top VFX emitter fields | M | The census reads ~40 of 134 emitter fields; cherry-pick the fields live maps actually use from `docs/research/vfx-support-report.md`, not the whole list. |
| 10 | Materials Filters dropdown | S | The one piece of the v0.3.0 inspector mockup that was skipped. |

## Explicitly deferred to v0.5.0

- **Lightmap bake Phase 2 (UV2 generation)** — blocked on instanced vertex buffers + the
  LeagueToolkit writer; too large to co-headline this release.
- **HUD editing** (viewer → editor) — deserves its own release theme.
- **Stencil particle modes 2/3/4** — research-first, low demand so far.

## Release criteria

- A side-by-side of the DX11 preview against an in-game screenshot reads as the same image
  (brightness, sky, culling), not "the dark editor version".
- Editing geometry with both renderers open shows the change in both, live.
- Map12/Bloom load with no missing textures.
- 0 test regressions; the wiki's screenshots-relevant pages updated if the look changes.
