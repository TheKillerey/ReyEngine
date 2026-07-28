# Phase 6: moving the editor viewport to DX11

Deliberately NOT started in the M247 session — sized first, because the answer changes the approach.

## What is actually there

~7,500 lines, and only a minority of it is League shading:

| file | lines | does it need DX11? |
|---|---|---|
| `ViewportMeshRenderer.cs` | 2,317 | partly — mesh draw yes, selection/outline no |
| `Vfx/VfxParticleRenderer.cs` | ~1,400 | yes — this is the one that "looks wrong ingame" |
| `ViewportControl.cs` | 1,650 | no — input, camera, orchestration |
| `MapGeoRenderer.cs` | — | yes |
| `SkyboxRenderer.cs` | — | yes |
| `GridRenderer.cs`, `ViewportPicking.cs`, `MeshRayIndex.cs` | — | **no** — editor chrome and CPU picking |

The rewrite-in-D3D11 surface is roughly *half* the code. The rest is editor tooling that gains nothing from
the move and would only acquire risk.

## The decision this forces

Two viable shapes, and they are not the same project:

**A. Composite.** DX11 renders League content to a texture; GL keeps drawing chrome over it. Preserves all
picking, gizmos and grid code untouched. Cost: two GPU contexts alive at once, and a per-frame readback the
preview already measures at 1.05 ms / 1667×1147 — acceptable, but it is a real cost paid every frame
forever.

**B. Full port.** Everything in D3D11, GL removed. No readback, one context. Cost: gizmos, grid, picking
and outline rendering all have to be reimplemented — ~3,000 lines of working, debugged code rewritten for
no visual gain.

**Recommendation: A**, and specifically as a *toggle* rather than a replacement, for as long as it takes to
trust it. The reason is not caution for its own sake — it is that the GL path is the only reference we have
for "what the editor used to look like", and deleting it removes the ability to A/B a regression.

## Sequence, each step independently shippable and reversible

1. **Side-by-side, off by default.** A `Renderer: OpenGL | DX11 (experimental)` setting on the map
   viewport. DX11 renders into a `WriteableBitmap` behind the existing GL surface; chrome still GL.
   Nothing is deleted. This is the step that makes every later one measurable.
2. **Camera + scene sync.** One camera drives both, and loading a map populates both renderers. Needed
   before any comparison means anything.
3. **A/B capture.** Same camera, same frame, both renderers, image diff. This is what turns "looks wrong
   ingame" into a list. Expect the diff to be dominated by the things M237 already named: emitter
   orientation modes, and the four particle permutations that still render blank.
4. **Move map geometry over.** Highest accuracy gain, and the path already works in the preview.
5. **Move particles over.** Second highest, and the reason phase 6 was wanted.
6. **Skybox, backdrops.**
7. **Decide on chrome.** Only once 4–6 are trusted. Possibly never — GL chrome over a DX11 scene is a
   perfectly reasonable end state.

## What is already built and waiting

Everything from M241–M247 was written to be consumed by this and is not preview-specific:

- `ShaderDescription` / `StateDescription` / `PipelineKey` / `ShaderResolver` — backend-neutral
- pipeline cache (564 map materials → 32 pipelines)
- per-slice frustum culling, verified 0 false rejects over 200,000 boxes
- pipeline-sorted draw order (227 → 32 state changes)
- quality scaling via Riot's own permutations (36.2% fewer instructions)
- async CPU decode off the UI thread

None of it has touched the editor viewport yet. `D3D11ParticlePlayback` is the closest thing to a template
for step 5 — it already drives the shared, GL-free `VfxParticleSimulator` into the D3D11 renderer.

## The one thing worth doing before any of it

A RenderDoc capture of the live client, as argued in `renderer-architecture-plan.md`. Step 3 produces a
diff list; without the capture, several entries on that list are unresolvable by inspection — the
`blendMode` table, the V-axis convention, `miscRenderFlags`, `stencilMode`. Doing the capture first means
step 3's output is actionable rather than a list of open questions.
