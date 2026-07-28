# Renderer architecture: running Riot's shaders for real

Plan for the next phase. Written after M210–M240, which built a working DX11 path that runs Riot's
unmodified bytecode for maps, champions and particles — so most claims here are measurements from that
work rather than estimates.

---

## Two corrections to the brief, before anything else

### 1. Render states are NOT in the shader

The brief asks the internal shader description to store *"blend states, depth and stencil states,
rasterizer and culling states"*. **Riot's shaders do not contain any of these**, so the description
cannot carry them.

Measured (M232): `data/shaders/shaders.bin` holds 347 `CustomShaderDef` objects and a full field-hash
census yields 17 distinct field names — **none blend-related**. `quad_ps.ps.dx11` likewise declares no
blend state. DXBC carries the shader program and its resource bindings; the pipeline state object lives
in the engine, i.e. in `League of Legends.exe`.

Render state therefore has a **separate source of truth** and must be modelled as such:

| state | where it actually comes from |
|---|---|
| blend mode | emitter `blendMode` (U8) for VFX; material/technique fields for map materials |
| depth test / write | content type — see the M240 preset table; particles off, geometry on |
| cull mode | off for essentially all League content (single-sided art) |
| alpha test | the `alphaRef` value, which also *selects* the ALPHA_TEST permutation |
| stencil | emitter `stencilMode` / `stencilRef` (26,393 emitters; semantics still unresolved) |

Consequence for the design: the pipeline is `(shader description) + (state description)`, two inputs,
resolved separately and combined at pipeline-build time. Folding state into the shader description would
model something that does not exist and would make the cache key wrong.

The integer→blend-state table is not in shipped data at all. ReyEngine's `IsAdditive(m) => m is 1 or 3 or
4 or 5` is a **guess** carried deliberately in both renderers so they cannot disagree. Deciding it
properly needs a frame capture from the live client — worth doing once, and it would settle modes 6/7/8
(258 emitters) which currently fall off the end of the list.

### 2. "Automatic compatibility with Riot updates" is already true for DX11, and is the hard part for anything else

The extraction half is built and version-independent by construction:

- `ShaderCacheReader` parses the TOC3.0 format: define pool, permutation keys (XXH64 of ordinal-sorted
  `NAME=VALUE`), blob indices, and the `_{N}` container split.
- Permutation resolution takes a material's switches/macros plus the shader's own feature defines and
  finds the cooked variant the client would pick. Verified over 10,659 real materials and, for particles,
  over 12,299 emitters resolving to 33 permutations with **0 unresolved**.
- `DxbcReflection` reads RDEF (constant buffers with byte offsets and used-flags), ISGN and OSGN.

None of that hardcodes shader names or versions. A Riot patch that adds a shader, adds a permutation axis
or moves a constant is picked up on next load with no ReyEngine change. **That goal is met today, for
DX11.** It is met because we never translate — we hand Riot's bytes to the same API Riot compiled them
for.

Every non-DX11 backend has to translate DXBC, and translation is exactly where both *accuracy* and
*automatic update compatibility* are lost: a new Riot shader using an instruction the translator does not
implement breaks silently or loudly, and the fix is a ReyEngine update. **The two headline goals are in
tension, and DX11 is the only point where they are not.**

---

## Recommendation: DX11 primary, and probably DX11 only

The brief says backend choice should follow compatibility and performance. Following that honestly:

- ReyEngine targets **Windows only** (net10.0-windows, Avalonia desktop, win32). DX11 is available on
  every machine that can run it — feature level 10_0 hardware from ~2007 onward. There is no
  compatibility gap for OpenGL to fill.
- Accuracy: DX11 runs Riot's shaders *unmodified*. No translation can beat that; it can only approach it.
- Performance: the offscreen-readback presentation path measures **1.05 ms/frame at 1667×1147 with 9 draw
  calls** in the shader preview window. That is the concern people raise about readback, and it is not a
  problem at editor resolutions.

**What OpenGL is still needed for is not shaders.** The existing GL viewport carries gizmos, the bucket
grid, NVR backdrops, picking, the outliner overlay — a lot of editor chrome unrelated to League shading.
Replacing that wholesale is a large project with no accuracy benefit.

So the realistic end state is not "pick a backend" but:

- **DX11** renders League content — map geometry, champions, particles — with Riot's own shaders.
- **OpenGL** keeps the editor chrome it already draws well.
- They composite, exactly as the preview window already composites into Avalonia.

DXBC→GLSL translation is then **deferred, not designed in**. If it is ever needed (a Linux port, a GPU
where D3D11 misbehaves), the shader-description layer below is the seam it would plug into.

---

## The shader description layer

Worth building regardless of backend count, because it is also the debugging surface and the cache key.

```
ShaderCache.dx11.wad.client
        ↓  ShaderCacheReader          (TOC, permutation resolution, blob trim)   [BUILT]
   DXBC bytecode
        ↓  DxbcReflection             (RDEF / ISGN / OSGN)                       [BUILT]
   ShaderDescription                  (backend-neutral)                          [NEW: extract]
        ↓  + StateDescription         (from material / emitter — see above)      [NEW]
   PipelineKey → PipelineCache                                                   [NEW]
        ↓
   D3D11 pipeline   (bytecode used directly)
   [GL pipeline]    (translated — deferred)
```

`ShaderDescription` holds: stage, permutation key and the define set that produced it, vertex inputs
(semantic + index + component type), constant buffers (name, bind point, size, and every variable with
byte offset, type and used-flag), texture and sampler bindings with register numbers. All of that is
already produced by the two built layers; this step is mostly *lifting existing types out of the DX11
assembly* so a second backend could consume them.

**Do not** put render states in it. See correction 1.

---

## Caching

The brief's key list is right with one subtraction and one addition.

```
PipelineKey = shaderName + stage + permutationKey (XXH64 of the define set)
            + blobIndex + bytecodeHash
            + gameVersion
            + backend
            + stateDescription        ← ADD: same shader, different blend = different pipeline
                                      ← DROP: GPU vendor, for the DX11 path
```

GPU vendor belongs in the key only for **translated** shaders, where the translator might emit different
code per vendor. For native DXBC the driver maintains its own compiled-shader cache keyed on the
bytecode; adding vendor to our key just fragments it.

`bytecodeHash` makes `gameVersion` redundant for correctness but keep both — version is what lets the
cache be *pruned* on patch day, and it is what a user-facing "shaders rebuilt for patch 15.3" message is
keyed on.

Three cache tiers, all already partly present in the DX11 path:

1. **In-memory pipeline cache**, keyed as above. M226 measured the cost of not having one: Map12 built
   921 pipeline objects for 120 material names before contiguous-slice merging.
2. **In-memory resource caches** — SRVs by texture path, constant buffers shared per frame. M216 collapsed
   1,600 draw calls × 2–4 cbuffer Map/fill/Unmap pairs down to two by recognising that `PerFrameVertexCB`
   and `PerFramePixelCB` are per *frame*, not per material. That was most of the frame time.
3. **On-disk cache** — only meaningful for translated shaders. Native DXBC needs no disk cache; it is
   already on disk, in Riot's WAD.

---

## Performance work, in the order I would do it

Ranked by measured or expected win, not by how interesting they are:

1. **Async shader load.** `LoadScene` is still synchronous — a 2–4 s UI freeze on a large map. This is the
   most user-visible item and is independent of everything else.
2. **Sort draws by pipeline.** Currently one pass per material in container order. Sorting by
   (shader, permutation) collapses state changes; for particles it also has to respect the emitter `pass`
   field (I16, present on 79.7% of emitters, currently unread) which is the authored draw order.
3. **Frustum + bucket-grid culling.** The bucket grid is already parsed and drawn as a debug overlay
   (M77); using it to cull is a small step from there and is the single biggest draw-call reduction
   available on maps.
4. **Instancing for particles.** Currently four CPU-built vertices per quad. Riot's own `quad_vs` reads
   per-vertex attributes rather than instance data, so true instancing would need a different VS — likely
   not worth it until profiling says the CPU quad build is the bottleneck.
5. **Quality scaling.** The permutation system gives this almost free: `LOW_QUALITY_MODE` and
   `ENV_QUALITY` are real axes in Riot's own TOCs. Selecting a cheaper cooked permutation is strictly
   better than writing a simplified shader, and it is what the client does.
6. **Mip-0-only texture decode.** ~25% faster loads, but it touches every texture consumer; do it after
   the above.

---

## Compatibility and honest failure

The brief's requirements here are right and match the discipline the last thirty milestones were built
on. Concretely:

- **Name the exact unsupported thing.** The unbound-constant report has already been the sharpest
  diagnostic tool in this work — it found `Alpha` on champions, `BAKED_LIGHT_SCALE_AND_BIAS` on maps, and
  the deleted `PARTICLE_DEPTH_PUSH_PULL` case in M235. Keep that pattern: report the binding, not "shader
  failed".
- **Fallback must be visibly marked.** A fallback that looks plausible is worse than a magenta one,
  because the whole point of this project is that the preview is trustworthy. Anything rendered through a
  fallback should be flagged in the UI, not just in a log.
- **Keep the original description available.** Reflection output, permutation key, define set and blob
  index should stay attached to whatever was built, for the Debug tab.

### Failure modes actually seen, worth guarding against by name

These are all real, from M210–M240, and each one compiled and ran while producing a wrong image:

| failure | symptom | guard |
|---|---|---|
| blob length prefix one byte long | bare `E_INVALIDARG` from shader creation only | trim to DXBC `totalSize` |
| unslotted vertex semantic | aliases the zero pad, draws plausible garbage | `PreviewVertexLayoutTests` asserts all 13 offsets |
| unbound cbuffer array | `rsq(0)` → NaN position → geometry silently vanishes | unbound-constant report |
| material built but not registered | correct pipelines, nothing drawn | `IsReady`/`AddMaterial` contract |
| two emitter lists of different lengths | index out of range on first frame | index the list you took the definition from |

---

## Sequencing

| phase | content | risk |
|---|---|---|
| 0 | Decide DX11-primary (this document's recommendation) | — |
| 1 | Lift `ShaderDescription` + `StateDescription` out of the DX11 assembly | low |
| 2 | `PipelineKey` + in-memory pipeline cache | low |
| 3 | Async load, then draw sorting by pipeline | low, high user value |
| 4 | Bucket-grid culling | medium |
| 5 | Quality scaling via `LOW_QUALITY_MODE` / `ENV_QUALITY` permutations | low |
| 6 | Move the main editor viewport onto the DX11 path, chrome still GL | high — do last |
| — | DXBC→GLSL translation | deferred unless a non-Windows target appears |

Phase 6 is the one to be careful about. Everything before it is additive and reversible; that one changes
what the editor's main window is.

---

## Open questions worth one frame capture

A single RenderDoc capture of the live client would settle several things this project currently guesses:

- the `blendMode` integer → blend-state table (and modes 6/7/8)
- whether `mProj` for particles is really view×projection (inferred correctly, but never confirmed against
  the client)
- the V-axis convention, which is currently reasoned rather than measured
- `miscRenderFlags` (547,010 emitters, bitfield, undecoded)
- `stencilMode` semantics (26,393 emitters)

That is probably the highest information-per-hour task available, and it is worth doing before phase 6.
