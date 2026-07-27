# DX11 Shader Preview — running League's own compiled shaders

**Milestone:** M210 · **Status:** experimental, isolated from the Material Editor and the OpenGL viewport.

This is the follow-on to [`d3d11-spike.md`](d3d11-spike.md), which answered *can* Riot's compiled shaders run
on a Silk.NET D3D11 device (yes) and concluded a full backend port was not justified **on fidelity grounds**.
M210 builds the isolated preview window that spike recommended as the prerequisite for any later decision:
load, bind, render and debug real League material shaders, and apply nothing to a map.

---

## What works

A real `defaultenv_flat` — the canonical Summoner's Rift static-mesh material shader — loads both stages out
of `ShaderCache.dx11.wad.client`, is created on a hardware D3D11 device, gets an input layout generated from
its own signature, has its constant buffers filled by reflection, and draws:

```
  vs blob#6  2,820B trimmed=True      + CreateVertexShader OK (sm5.0)
  ps blob#53 3,696B trimmed=True      + CreatePixelShader  OK (sm5.0)
  D3D11 hardware device, feature level 0xB100
  + CreateInputLayout OK (3 elements)
  + vs cbuffer b1 '$Globals' (64 bytes, 1 vars)
  + vs cbuffer b2 'PerFrameVertexCB' (560 bytes, 19 vars)
  + ps cbuffer b0 '$Globals' (16 bytes, 1 vars)
  + ps cbuffer b1 'PerFramePixelCB' (496 bytes, 29 vars)
  rendered 192x128 in 7.11 ms, 1 draw call
```

Nothing in the pipeline is hardcoded per shader. Every slot, offset, register and vertex attribute comes from
the shader's own `RDEF`/`ISGN` chunks, because nothing else is known per shader.

---

## Where the shaders live

| Thing | Location |
|---|---|
| Compiled bytecode | `ShaderCache.dx11.wad.client` |
| Stage TOC | `assets/shaders/generated/{shader}.{vs\|ps}.dx11` |
| Bytecode blobs | `{tocPath}_{N}` where `N = floor(blobIndex/100)*100` |
| Shader defs (featureDefines, staticSwitch defaults) | `data/shaders/shaders.bin` in `Global.wad.client` |

**833 TOCs, 462 distinct shader names** in the current Live build.

`TOC3.0` layout (833/833 consume byte-exactly):

```
sizedString "TOC3.0" | u32 permCount | u32 defineCount | u32 blobCount | u32 flag
sizedString "baseDefines" | defineCount x (sizedString key, sizedString value)
sizedString "shaders"     | permCount x u64 key | permCount x u32 blobIndex
```

The permutation key is `XXH64(seed 0, concat of ordinal-sorted "NAME=VALUE")`. The empty set hashes to
`0xef46db3751d8e999`, which is pinned by a test. M166's `ShaderPermutationIndex` had already decoded
everything except the **blob-index array** — the half you need to actually fetch bytecode rather than just
answer "is this permutation cooked".

Blob containers hold up to 100 length-prefixed DXBC blobs back to back, so blob 128 is the 29th entry of
`..._100`.

### The gotcha that costs an afternoon

> A container's length prefix runs **one byte longer** than the DXBC it wraps — 1,757 vs the 1,756 the DXBC
> header declares at offset 24. D3D rejects any bytecode whose buffer length disagrees with its own
> `totalSize`, with a bare `E_INVALIDARG` and no further diagnostics.

Chunk parsing never notices, because chunk offsets are absolute. So **disassembly works fine on untrimmed
blobs and only shader creation fails**, which is exactly the wrong way round for diagnosing it.
`ShaderCacheReader.LoadBlob` trims and reports `WasTrimmed`.

---

## Permutations

`defaultenv_flat` ships **352 vertex** permutations over 5 define axes, and **2,816 pixel** permutations over
12 axes:

```
CLOUD_SHADOWS  FEATURE_MASKED  DISABLE_SHADOWS  DISCARD_ALPHA_TEXELS  NO_BAKED_LIGHTING
DISABLE_FOW    PREMULTIPLIED_ALPHA  LOW_QUALITY_MODE  BLOOM  DISABLE_DEPTH_FOG
USE_DYNAMIC_LIGHTING  GENERATE_SHADOW_MAP
```

Two things worth knowing:

- **Permutation count ≫ blob count** (2,816 perms → 238 blobs). Many define combinations compile to identical
  code and share a blob, so a blob index is not a permutation identity.
- Every axis observed so far is presence/absence — the pool value is `1` for all of them except `BLOOM`,
  which is `0`. The define is either in the set or absent; there is no third state.

The TOC stores only key hashes, so recovering *which* defines a permutation corresponds to means enumerating
the pool and hashing. That is bounded and reports truncation rather than presenting a partial map as complete.

---

## What a material shader needs bound

Reflected from `defaultenv_flat` blob#53 / blob#6:

**Constant buffers** — note the slots differ per stage, so `$Globals` is *not* reliably `b0`:

| Stage | Slot | Buffer | Size |
|---|---|---|---|
| VS | b1 | `$Globals` | 64 (just `WORLD_MATRIX`) |
| VS | b2 | `PerFrameVertexCB` | 560 (19 vars) |
| PS | b0 | `$Globals` | 16 (just `TintColor`) |
| PS | b1 | `PerFramePixelCB` | 496 (29 vars) |

Named, byte-offset engine constants recovered from `PerFramePixelCB` include `vCamera`, `TIME`,
`TERRAIN_XFORM`, `SHADOW_COLOR`, `cDepthConversionParams`, `SUN_LIGHT_COLOR`, `SUN_LIGHT_DIRECTION`,
`LIGHT_MAP_COLOR_SCALE_AND_INTENSITY`, `ENV_FOG_*`. RDEF also carries a per-variable **USED** flag, so the
preview can distinguish "this permutation reads it" from "declared but compiled out".

**Textures / samplers**: `t0 FOW_MAP_SharedTexture`, `t1 DiffuseTexture__TX`; `s0 DiffuseTexture__SMP`,
`s15 Clamp_No_Mip_SharedSampler`.

**Vertex inputs**: `POSITION0` xyz, `NORMAL0` xyz, `TEXCOORD0` xy — a plain mapgeo static mesh.

---

## How material properties map to shader parameters — MEASURED

The obvious guess is that a material's sampler name equals the shader's texture name. **It does not.** Over
**18,516 map materials** in the 8 shipping map WADs:

| | count | share |
|---|---|---|
| material has a `renderShader` | 10,659 | 57.6% of materials |
| …whose shader was found in the cache | **10,659** | **100%** |
| sampler ↔ shader-texture pairs checked | 16,279 | |
| exact name match | 0 | **0.0%** |
| match with `__TX` appended | 12,765 | **78.4%** |
| match ignoring underscores | 0 | 0.0% |
| no match | 3,514 | 21.6% |

So the rule is **`shaderTextureName == materialSamplerName + "__TX"`**, and the sampler-state counterpart is
`+ "__SMP"`. Underscores inside the name are preserved exactly: both `DiffuseTexture` (8,588 materials) and
`Diffuse_Texture` (1,814) occur, and each maps to its own literal `..__TX`.

`assets/shaders/generated/{renderShader}` resolving for **100%** of materials that name one also confirms the
material→shader path convention outright.

**M210 guessed that the 21.6% was mostly a measurement artifact. M213 measured it, and that guess was
wrong.** Re-running against each material's *own* resolved permutation — now that the resolver exists — moved
the match rate only from **78.4% to 80.4%**. The permutation choice accounted for two percentage points, not
twenty.

What the residue actually is, classified over all 3,190 unmatched pairs:

| | count | share of unmatched |
|---|---|---|
| shader declares something *similar* (the `__TX` rule would be wrong) | 167 | **5.2%** |
| shader declares *nothing like it* — a surplus material slot | 3,023 | **94.8%** |

So the rule is not failing: in 94.8% of the misses the shader's resolved permutation declares no texture
resembling that sampler at all. Materials routinely carry sampler entries the permutation they resolve to
never uses — `Outline_MatCap_RMA_VertexDeform` materials author `MatCap_Tex`, `Vertex_Deform_Tex`,
`BloomMask_Texture` and `iridescentTex` against a permutation declaring only `OutlineMask_Texture__TX` and
`Diffuse_Texture__TX`. That is surplus authoring data, not a mapping failure.

The genuine residue is the **167 near-misses (1.0% of all pairs)**, which have *not* been examined
individually and are the only place a second naming rule could still be hiding.

### Engine-supplied vs material-supplied

Names ending `_SharedTexture` / `_SharedSampler` are engine-supplied and never come from a material —
`FOW_MAP_SharedTexture` appears 10,572 times with no corresponding material sampler anywhere, as do
`PIXEL_COLOR_REMAP_RAMP_SharedTexture` (1,097) and `TERRAIN_BLEND_SharedTexture` (320). The preview binds
opaque white to anything unbound and lists it under "unbound textures" rather than pretending it is fine.

---

## Design decisions

**Offscreen render + CPU readback, not a swapchain.** Avalonia offers `OpenGlControlBase` and no D3D11
equivalent. A real swapchain means either a `NativeControlHost` child HWND — which sits above the Avalonia
compositor and would draw over every toolbar and overlay — or shared-texture interop back into the GL
context. The spike named this as the deciding cost. Rendering to an offscreen `B8G8R8A8_UNORM` target,
copying to a staging texture and blitting into a `WriteableBitmap` sidesteps both, costs a memcpy per frame,
and is irrelevant at preview sizes (7 ms at 192×128, comfortably real-time at 640×480).

**A "fat vertex" plus a generated input layout.** D3D validates the input layout against the shader's *whole*
input signature, so every non-system element needs an entry even when the test mesh has no such attribute.
Preview meshes are expanded into one fixed 136-byte vertex, and unmatched semantics are aliased onto a
zero-filled pad and **reported** — a shader reading an attribute the mesh lacks renders with zeros there and
says so, rather than silently looking plausible.

**A separate assembly.** `ReyEngine.Rendering.D3D11` is its own project so the Direct3D dependency cannot
grow into the OpenGL renderer, and so the experiment can be deleted in one step.

---

## Open / unverified

1. **Matrix convention.** HLSL packs cbuffer matrices column-major by default, so a row-major `Matrix4x4` is
   conventionally transposed before upload. Whether Riot's shaders do `mul(v, M)` or `mul(M, v)` is not
   recorded anywhere in the bytecode we read. Exposed as a **toggle**, defaulting to transposed, rather than
   guessed silently — the same treatment M208 gave the mesh winding.
2. **Engine constant values** are preview stand-ins, not measured engine values, and are labelled as such in
   the code — with the exception of the shadow pair, which M212 measured (below).
3. **The 167 near-miss sampler/texture pairs** (1.0% of all pairs) — the only place a second naming rule
   could still be hiding. Not examined individually.
4. **Blend / depth / stencil / raster state per material.** The preview exposes these as manual toggles. Where
   the game sources them per material is not established here.
5. **The comparison number is not a fidelity verdict.** The window can swap Riot's pixel shader for
   ReyEngine's own model (diffuse × lambert + ambient), generated as HLSL from Riot's *vertex* shader output
   signature so it always links. On `defaultenv_flat`/sphere/checker it reports mean |diff| 49/255. That is a
   real measurement of two different images, but our model has no fog, FOW, shadow or tint stage and the
   engine constants are stand-ins, so it measures "how much those stages change the picture", **not** an
   accuracy claim about either renderer.

---

## Resolving a material's actual permutation (M213)

Previewing a *shader* means picking a permutation by hand. Previewing a **material** means picking the one the
engine would. A material authors only the switches it changes; the rest of the define set comes from the
shader's own `featureDefines` and `staticSwitch` defaults in `shaders.bin`, and some macros are injected
per-mesh and appear in neither. So resolution pins every axis it can, leaves the rest free, and enumerates
the free ones until a combination hashes to a key the TOC contains.

**Result over the 8 shipping map WADs: 10,659 of 10,659 materials resolved BOTH stages — 100%.**

Failing to resolve is a real answer and is reported as one, never papered over with an arbitrary blob: it is
exactly the condition that makes the live client fail with *"Unable to find correct hash for shader"* and
render nothing.

### One correctness bug, found by a test rather than by the corpus

Almost every axis in a shipped TOC is presence/absence — the pool carries `1` and nothing else. So a switch
that is **off** means the define is **absent**, not `NAME=0`. The first implementation emitted `=0`, which
produces a key that exists nowhere and would report an ordinary material as unresolvable.

The shipped corpus never exercised that branch: the census reported 100% resolution both before and after the
fix. It was caught only because a unit test asserted the behaviour directly. Worth recording as a case where
a green measurement over 10,659 real inputs was not sufficient evidence.

## The light term, measured (M212)

The first build rendered a garish white-and-lavender sphere. The cause was the preview setting both
`SHADOW_COLOR` and `SHADOW_COLOR_COMPLEMENT` to 1.0, and the fix required knowing what the shader actually
does with them, so it was measured rather than tuned.

**Method.** Bind a flat texture of known level, render, average the lit pixels. Sweep one thing at a time.

**The first attempt was vacuous** and is worth recording as a warning: run at texture level 128, every
constant setting reported a mean of exactly 255.0, so the sweep "showed" that `SHADOW_COLOR` had no effect.
The image was fully saturated and the measurement had no resolution at all. Re-run at level 32 the same
sweep separated cleanly.

**Result**, on `staticmesh/defaultenv_flat` blob#53:

```
output = saturate(2 x texture) x (SHADOW_COLOR + SHADOW_COLOR_COMPLEMENT) x TintColor
```

| evidence | observation |
|---|---|
| four splits summing to 1.0 (`1,0` `0,1` `0.35,0.65` `0.5,0.5`) | all exactly **2.00x** on a 0.125 texture |
| sum 2.0 (`1,1`) | **4.00x** |
| sum 0.0 | **0.00x** |
| texture sweep at sum 1.0 | 64→128, 100→200, 126→252 — exactly 2.00x |
| texture sweep, continued | 130→255, 140→255, 160→255, 200→255 — hard clamp |

So the two terms **add** (the split is not observable here — `DISABLE_SHADOWS` forces the shadow term), and
the diffuse is **doubled and clamped before** the light term is applied. Only three constants are `USED` in
this permutation, so the ×2 is a literal in the shader, not something the preview supplies.

**That ×2 is intentional** — it is the overbright-albedo convention, of a piece with the `lightMapColorScale=2`
that map data records, and it means League's environment diffuse is authored at roughly half scale. The
preview deliberately does **not** cancel it: a sum of 0.5 would undo the doubling and make a mid-grey test
texture look tidy while misrepresenting what the game draws. The defect was the sum being 2.0, which
double-brightened on top of the shader's own doubling. It is now 1.0 — the neutral value for a multiplying
term, and what a colour plus its complement ought to add to.

The **split** (0.35 / 0.65) remains a stand-in and is labelled as one. Nothing measured constrains it,
because only the sum is observable in a `DISABLE_SHADOWS` permutation.

A knock-on: the built-in checker was 235/130/70, above the half-scale range the shader is built for, so it
flattened to a solid block and read as a renderer bug. It is now authored under 0.5.

## Harnesses

`scratchpad/vfxcensus` modes: `shaders` (TOC + reflection dump), `dxpreview` (end-to-end render with an ASCII
preview and the comparison A/B), `matshader` (the material↔shader mapping census). Tests live in
`tests/ReyEngine.Formats.Tests/ShaderCacheTests.cs` and need no game install.
