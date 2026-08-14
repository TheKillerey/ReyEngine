# Riot's frame pipeline — measured specification

**Milestone:** M459 · **Status:** research only, no code changed. Everything below is read out of compiled
DXBC in `ShaderCache.dx11.wad.client`, plus `C:\Riot Games\League of Legends\Config\game.cfg`.

`light-system.md` (M454) covered 10 of the 139 stage TOCs under `assets/shaders/hlsl/` and went deep on
lighting. This document covers what happens to the **frame** — the families that run after geometry — and
answers one question: *what is the largest remaining reason ReyEngine's viewport does not look like the
game?*

**Read [§3 The diagnosis](#3-the-diagnosis) first.** It corrects the working hypothesis in a way that
changes what should be built.

---

## 0. Method, and how to reproduce every claim

Same probe as M454, `HDump.cs` (mode `hdump`), plus one new probe added for this milestone:

```
disasm hdump list  <substring>            # TOCs, axes, permutation/blob counts
disasm hdump perms <tocpath>              # every permutation with recovered defines
disasm hdump scan  <tocpath> <needle>     # per DISTINCT blob: does it bind a resource named <needle>?
disasm hdump refl  <tocpath> <blob>       # signature + resources + CB variables incl. IsUsed
disasm hdump dis   <tocpath> <blob> <out> # full listing, one line per disassembly line
disasm mrt <substring> [maxBlobsPerToc]   # NEW: classify every blob's SV_Target1 write        (MrtCensus.cs)
```

**Line-number convention.** Every "line N" below is the index `hdump dis` prints — the 0-based line index
of the D3DDisassemble output *including* its leading comment block. Re-running the same command reproduces
the same numbering. Listings are in `scratchpad/disasm/out459/` (M454's are in `out454/`).

**The two M454 traps still apply** and are used as positive evidence again below: the stage suffix is
`.ps-dx11` with a **hyphen**, and **blobs are deduplicated across define sets**, so a define that maps to
the same blob index as its absence provably does nothing in that stage.

**A third trap, new here: blob 0 is often a stub.** `defaultenv_glow.ps` blob 0 is
`mov o0.xyzw, l(1,1,1,1); ret` — 2 instruction slots, the `GENERATE_SHADOW_MAP` depth-only variant. Always
check `hdump perms` before trusting blob 0 as "the base permutation".

**Config evidence.** Several conclusions rest on shipped defaults in
`C:\Riot Games\League of Legends\Config\game.cfg`. That file is this machine's *current* settings, not a
pristine default; the values quoted are all at their neutral/midpoint or documented-default positions, but
that is a user-state file and is labelled as such wherever it is load-bearing.

---

## 1. Full inventory of `assets/shaders/hlsl/`

**139 stage TOCs** (the "323 entries" figure is these TOCs plus their `_<n>` blob containers). Depth of
examination is marked: **D** = disassembled, **R** = reflected only, **L** = listed only (name + axes +
permutation counts).

### 1.1 Family summary

| family | TOCs | what it is | verdict |
|---|---|---|---|
| `ui/` | 37 | HUD widget shaders — cooldown sweeps, arc fills, gradients, desaturate, glow | **irrelevant to the viewport** |
| `particlesystem/` | 20 | billboard + mesh particle shading, soft particles, distortion, particle shadows | **adopt soft particles + 3 fixes** — §4.9 |
| `filters/` | 16 | separable blurs, the bloom mip chain, shadow-map blur, copy/remap utilities | **adopt the bloom chain** |
| `skinnedmesh/` | 15 | character meshes and character-attached particles | mostly irrelevant (no champions in a map editor) |
| `gamma/` | 11 | the post-process chain: LUT, screen fog, DoF mask, linear depth, greyscale | **adopt selectively** — see §2 |
| `renderer/` | 7 | immediate-mode primitive drawing (texture × vertex colour) | already have equivalents |
| `font/` | 4 | text rasterisation | irrelevant |
| `environment/` | 8 | skybox, reflectionsky, shadowmap, projected decals | **adopt skybox + shadowmap** |
| `fogofwar/` | 3 | the FoW visibility texture pipeline (fade → blur → overlay) | not applicable (no FoW in an editor) |
| `lighting/` | 3 | light-region rasteriser + merge — fully covered in `light-system.md` §1 | zero shipped content, skip |
| `regions/` | 2 | the nav-grid texture pipeline (same fade + blur as FoW, 4-channel) | debug overlay only |
| `decal/` | 2 | trivial editor decal, `texture * Color` | already have |
| `debug/` | 2 | world-space checkerboard error material | nice-to-have |
| `debugdrawnavgrid/` | 2 | nav-grid visualisation | optional editor feature |
| `hud/` | 2 | minimap FoW overlay, tactical-map region painting | irrelevant |
| `gameplaytexture/` | 1 | procedural gameplay-region painter (128-slot shape list) | irrelevant |
| `editor/` | 1 | **`ps_showmiplevels`** — a mip-level visualiser. That is the entire Riot "editor" family | tiny, optional |
| root | 3 | **`fxaa_ps`**, `simple_vs`, `single_color_ps` | FXAA is off by default — see §3 |

### 1.2 Per-TOC listing for the families that matter

`gamma/` — the post-process chain (11 TOCs):

| TOC | perms/blobs/axes | what it is | depth | verdict |
|---|---|---|---|---|
| `ps_gamma.ps` | 16/12/4 | **per-channel 1-D LUT** on the back-buffer copy, + optional bloom screen-composite | **D** (blobs 0,1,6) | adopt the bloom half |
| `ps_gamma_colored.ps` | 16/6/4 | same + unconditional `COLOR_OVERRIDE` lerp | **D** (blob 0) | no |
| `ps_gamma_colorization_post_effect.ps` | 16/6/4 | same + map colorization tint and `pow(luma,1.25)` blend | **D** (blob 0) | no |
| `ps_copy_post.ps` | 16/6/4 | plain copy (the no-LUT path), + optional bloom composite | **D** (blob 0) | adopt as the bloom composite |
| `ps_luminance.ps` | 16/3/4 | Rec.601 greyscale — the death/spectator effect | **D** (blob 0) | no |
| `ps_pause.ps` | 16/3/4 | pause-screen variant | **R** | no |
| `postfog.ps` | 4/2/2 | **screen-space depth + height fog** from the depth buffer | **D** (blob 0), **R** (blob 1) | adopt |
| `postfog.vs` | 2/1/1 | full-screen quad with a world-ray output | **R** | adopt |
| `post_effect.vs` | 2/1/1 | full-screen quad, `uv = (x, 1-y)` | **D** | adopt |
| `dof.ps` | 2/2/1 | **circle-of-confusion mask** from linear depth (not the blur itself) | **D** (blob 0) | no |
| `lineardepth.ps` | 1/1/0 | `1/(d*B + A) * scale` depth linearisation | **D** | adopt if DoF/fog needs it |

`filters/` (16 TOCs):

| TOC | what it is | depth | verdict |
|---|---|---|---|
| `bloom.ps` | 7-tap separable Gaussian along `UVStep` | **D** | **adopt** |
| `bloomhigh.ps` | blob 0 byte-identical to `bloom.ps`; `LOW_QUALITY_MODE` selects blob 1 | **D** (blob 0) | adopt |
| `mipchainbloomdownsample.ps` | the COD/Jimenez **13-tap downsample** | **D** | **adopt** |
| `mipchainbloomupsample.ps` | **3×3 tent upsample**, `(1,2,1;2,4,2;1,2,1)/16` | **D** | **adopt** |
| `blur_shadow_3.ps` | 3-tap shadow-map blur; keeps nearest packed depth in RGB, averages A | **D** | with the shadow map |
| `blur_shadow_5.ps` | 5-tap version | **L** | with the shadow map |
| `gauss.ps` | generic 4-tap weighted blur, weights + 2 offset pairs from `$Globals` | **D** | optional |
| `gauss5.ps`, `gauss5x5.ps` | wider generic blurs | **L** | optional |
| `ps_copy_filter.ps` | pure copy | **D** | trivial |
| `ps_copy_filter_with_color_mod.ps` | copy × colour | **L** | trivial |
| `ps_copy_gradient_remap.ps` | `Gradient.Sample(src.r * REMAP.x, 0.5)`, alpha forced 0.75 | **D** | no |
| `ps_blur_green.ps`, `ps_dilate_green.ps` | green-channel blur/dilate — the mouse-over outline mask | **L** | selection outline, optional |
| `ps_mouseover_outline_blend.ps` (+`_with_color`) | composites that outline | **L** | selection outline, optional |

`environment/` (8 TOCs):

| TOC | what it is | depth | verdict |
|---|---|---|---|
| `skybox.ps` / `.vs` | **parallax-corrected cubemap** (ray/sphere intersect) + height fog + flat depth fog | **D** (M459) | **adopt** |
| `reflectionsky.ps` / `.vs` | sky reflection; binds `SAMPLER_BACK_BUFFER_COPY` and both light-region textures | **R** (M454) | later |
| `shadowmap.ps` / `.vs` | the sun shadow-map write pass | **R** (M454) | **adopt** (see §5) |
| `unlit_decal_ps` / `_vs` | projected decals, 512 perms — fully covered in `light-system.md` §3.2 | **D** (M454) | already have |

Root, `debug/`, `editor/`, `gameplaytexture/`, `renderer/`, `hud/`, `fogofwar/`, `regions/`,
`debugdrawnavgrid/`, `decal/`: all disassembled except where noted in §4.

`ui/` (37) and `font/` (4): **listed only**. They render the HUD into a separate
`UI_PRIMARY_TEXTURE_SharedTexture` layer (evidence: `ui/ui_copyfromoffscreen.ps` and `ui/ui_brightness.ps`
both bind that name and neither ever samples `SAMPLER_GAMMA_LOOK_UP` or `SAMPLER_BACK_BUFFER_COPY`). They
have no bearing on a map viewport.

---

## 2. The frame pipeline, in order

### 2.1 The passes, and what is inferred

DXBC carries no pass ordering — no shader names its successor. The sequence below is reconstructed from
**resource dependencies** (which pass produces the texture another consumes) and **early-out behaviour**.
Each ordering claim is labelled.

| # | pass | shader | reads | writes | ordering evidence |
|---|---|---|---|---|---|
| 1 | shadow map | `environment/shadowmap.*` | scene depth from the sun | shadow map | **measured**: sampled by `DefaultEnv_Flat`/Mantis during shading, so it precedes them |
| 2 | fog of war | `fogofwar/fade` → `fastseparablegaussianblur` → `applyoverlayandcolor` | prior FoW state | `FOW_MAP_SharedTexture` | **measured**: consumed by material shaders |
| 3 | scene | material shaders | geometry | **RT0 colour + RT1 glow** + depth | **measured**: MRT, §2.2 |
| 4 | screen fog | `gamma/postfog.ps` | back-buffer copy + `sDepthTexture` | fogged colour | **measured**: early-outs on `depth == 1.0`, so it runs while depth is live |
| 5 | bloom chain | `filters/mipchainbloom*` / `bloom*` | **RT1** | `BLOOM_TEXTURE_SharedTexture` | **inference** — see §2.2 |
| 6 | final composite | `gamma/ps_gamma.ps` (or `ps_copy_post` / `ps_luminance` / `ps_gamma_colored`) | back-buffer copy + bloom + LUT | back buffer | **measured**: it is the only pass that consumes `BLOOM_TEXTURE`, and it applies the LUT that nothing else applies |
| 7 | FXAA | `fxaa_ps.ps` | generic `tex__TX` | AA'd colour | **UNKNOWN** position; **off by default** |
| 8 | UI | `ui/*` | `UI_PRIMARY_TEXTURE` | back buffer | **measured**: separate texture layer, never LUT-corrected |

Passes 4, 6 and 7 are mutually exclusive alternatives *within* their slots — the game picks **one** of
`ps_gamma` / `ps_gamma_colored` / `ps_gamma_colorization_post_effect` / `ps_copy_post` / `ps_luminance` /
`ps_pause` for slot 6, all six of which share the identical `COLOR_OVERRIDE`/`DEATH_EFFECT`/`BLOOM`/
`ADDITIVE` axis set.

The full-screen quad for all of these is `gamma/post_effect.vs`, lines 24-26:

```hlsl
clip.xy = pos.xy * 2 - 1;   clip.zw = (0, 1);
uv      = (pos.x, 1 - pos.y);
```

### 2.2 THE BLOOM SOURCE IS A SECOND RENDER TARGET — the central finding

**Every environment and character pixel shader declares two render targets.** `SV_Target1` is the
glow/bloom buffer.

Measured, not assumed. `disasm mrt generated/shaders/staticmesh/ 24` disassembles up to 24 distinct blobs
per TOC across all 95 static-mesh pixel TOCs and classifies each `o1` write:

```
# TOTALS: 2244 blobs disassembled, 70 TOCs declare o1, 18 TOCs write a COMPUTED o1 (174 blobs)
```

The 18 TOCs that write a computed `o1` are **exactly** the static-mesh TOCs carrying a `FEATURE_BLOOM`
axis — `defaultenv_glow`, `emissive_basic`, `env_darkstarbase`, `env_glowsign`, `env_glowsign_atlas`,
`env_light_sequence`, `env_scrollingcolor`, `env_scrollingdiffuse`, `env_simplerotate`,
`flickeralpha_flipbook`, `hologram_rotate`, `mantis_env_baked_pbr`, and six `tft_*`. Every one of them
writes the same shape:

```
mul o1.xyz, <mask>, <colour> ; mov o1.w, l(1.000000)
```

Everything else — including all 16 `DefaultEnv_Flat` blobs scanned, all `srx_blend_*`, all `4textureblend*`
— writes the constant `mov o1.xyzw, l(0,0,0,1.000000)`.

Two spot checks with line numbers:

* `staticmesh/mantis_env_baked_pbr.ps` blob 27, lines 1424-1426:
  `mul o1.xyz, r0.xxxx, l(0.146975, 1.381368, 1.232624)` — **exactly 2× the emissive colour** added to
  `o0` at line 1397 (`0.073487, 0.690684, 0.616312`). The glow buffer gets double the on-screen emissive.
* `staticmesh/defaultenv_flat.ps` blob 226, line 750: `mov o1.xyzw, l(0,0,0,1.000000)`. Terrain never glows.

**This is why no bright-pass threshold exists anywhere in `filters/`.** `bloom.ps` and
`mipchainbloomdownsample.ps` are pure blurs with no `max`, no subtract, no luminance test — because the
bright-pass is not a screen-space test at all. **The artist selects what blooms, per material, and the
shader writes it to RT1.** Searching for Riot's threshold constant is searching for something that does
not exist.

The chain from RT1 to the screen is (inference on the exact mip count, measured on each shader's math):

```
RT1 (glow)  --mipchainbloomdownsample-->  1/2  -->  1/4  -->  ...        (13-tap, weights below)
            --mipchainbloomupsample---->  back up, additively            (3x3 tent /16)
            --bloom.ps / bloomhigh.ps -->  BLOOM_TEXTURE_SharedTexture   (7-tap separable Gaussian)
```

`filters/mipchainbloomdownsample.ps` (lines 46-81) — the COD/Jimenez 13-tap, `UVStep` in `$Globals`:

```hlsl
// offsets in units of UVStep; weights before the final 0.25
(-2,-2) .125  (0,-2) .25  (2,-2) .125
       (-1,-1) .5      (1,-1) .5
(-2, 0) .25   (0, 0) .5   (2, 0) .25
       (-1, 1) .5      (1, 1) .5
(-2, 2) .125  (0, 2) .25  (2, 2) .125
result = sum * 0.25;    // line 80.  sum of weights = 4.0, so this normalises
```

`filters/mipchainbloomupsample.ps` (lines 46-68) — 3×3 tent, `mul o0.xyz, r0.xyzx, l(0.0625)` at line 68.

`filters/bloom.ps` (lines 46-66) — 7-tap separable Gaussian, offsets `-3..+3 × UVStep`, weights
`0.005980, 0.060626, 0.241843, 0.383103, 0.241843, 0.060626, 0.005980` (sum 1.000001).
`filters/bloomhigh.ps` blob 0 is **byte-identical**.

### 2.3 `gamma/ps_gamma.ps` — the final composite, and what "gamma" actually means

Blob 0, the base permutation (1064 B, 11 instruction slots). Resources: `t0`
`SAMPLER_BACK_BUFFER_COPY_SharedTexture`, `t1` `SAMPLER_GAMMA_LOOK_UP_SharedTexture`, `s15`
`Clamp_No_Mip_SharedSampler`. **No constant buffer at all.** Lines 36-45:

```hlsl
float4 c = SAMPLER_BACK_BUFFER_COPY.Sample(Clamp_No_Mip, uv);
o0.r = SAMPLER_GAMMA_LOOK_UP.Sample(Clamp_No_Mip, float2(c.r, 0)).r;
o0.g = SAMPLER_GAMMA_LOOK_UP.Sample(Clamp_No_Mip, float2(c.g, 0)).r;
o0.b = SAMPLER_GAMMA_LOOK_UP.Sample(Clamp_No_Mip, float2(c.b, 0)).r;
o0.a = c.a;
```

**It is a per-channel 1-D lookup table.** All three channels read the LUT's **red** channel (the resource
swizzles `t1.xyzw` at lines 39/43 and `t1.yxzw` at line 44 both resolve to component `.x` for their
respective write masks). It is not a `pow()`, not an sRGB encode, not a 3-D colour-grading cube.

Blob 1, `BLOOM=1` (1272 B), adds `t1` `BLOOM_TEXTURE_SharedTexture` and shifts the LUT to `t2`. Lines
38-49:

```hlsl
float3 b = BLOOM_TEXTURE.Sample(uv).rgb;
float4 c = SAMPLER_BACK_BUFFER_COPY.Sample(uv);
float3 s = 1 - (1 - c.rgb) * (1 - b);      // lines 39,41,43 -- SCREEN blend
o0.rgb   = LUT(s);                          // per channel, lines 45-49
o0.a     = c.a;
```

**The bloom composite is a screen blend, not additive.** `1-(1-a)(1-b)` cannot exceed 1, which is why the
whole chain works correctly in an 8-bit LDR buffer.

`ADDITIVE` is **blend-state-only in the non-bloom path** — blob 0 serves both `[]` and `[ADDITIVE=1]`, and
blob 2 serves both `[COLOR_OVERRIDE]` and `[COLOR_OVERRIDE, ADDITIVE]` (byte-identical code). With
`BLOOM=1` it does change the code (blobs 1 vs 3, 5 vs 4, 10 vs 8, 11 vs 9 are distinct).

**`PostEffectPixelCB`** — complete, from the `ps_gamma_colored` and `ps_gamma` blob 6 headers:

| offset | name | used by |
|---|---|---|
| +0 | `float4 COLOR_OVERRIDE_PARAMS` | `ps_gamma_colored`, `ps_gamma_colorization_post_effect` |
| +16 | `float4 MAP_COLORIZATION_COLOR` | `ps_gamma_colorization_post_effect` |
| +32 | `float POST_EFFECT_STRENGTH` | — |
| +36 | `float DEATH_EFFECT_APPLY_FRACTION` | `ps_gamma` blob 6 |
| +48 | `float4 DEATH_EFFECT_COLOR_TINT` | `ps_gamma` blob 6 |
| +64 | `float BLOOM_INTENSITY_SCALE` | — in every blob dumped here |

`BLOOM_INTENSITY_SCALE` is marked unused in all four bloom-binding blobs inspected — the bloom composite in
`ps_gamma` blob 1 has no constant buffer whatsoever. The scale is presumably applied when generating
`BLOOM_TEXTURE`; **not measured**.

The other slot-6 variants, all `D`:

* `ps_copy_post.ps` blob 0 (line 33): a single `sample` straight to `o0`. Pure copy — the no-LUT path.
* `ps_luminance.ps` blob 0 (lines 34-36): `o0 = float4(dot(c.rgb, (0.299, 0.587, 0.114)).xxx, c.a)`.
  Rec.601 greyscale.
* `ps_gamma_colored.ps` blob 0 (lines 53-62):
  `o0.rgb = LUT(c) * (1 - COLOR_OVERRIDE_PARAMS.w) + COLOR_OVERRIDE_PARAMS.rgb`.
* `ps_gamma_colorization_post_effect.ps` blob 0 (lines 53-70):
  ```hlsl
  float3 g = LUT(c.rgb) * MAP_COLORIZATION_COLOR.rgb;
  float  L = dot(g, (0.299, 0.587, 0.114));
  g = g * MAP_COLORIZATION_COLOR.w + (1 - MAP_COLORIZATION_COLOR.w) * pow(L, 1.25);
  o0.rgb = g * (1 - COLOR_OVERRIDE_PARAMS.w) + COLOR_OVERRIDE_PARAMS.rgb;
  ```
* `ps_gamma.ps` blob 6, `DEATH_EFFECT=1` (lines 53-69):
  ```hlsl
  float3 g     = LUT(c.rgb);
  float3 desat = g * 0.2 + luma601(g) * 0.8;
  float3 A     = lerp(g, desat, DEATH_EFFECT_APPLY_FRACTION);
  float3 B     = lerp(g, DEATH_EFFECT_COLOR_TINT.rgb, DEATH_EFFECT_COLOR_TINT.w);
  o0.rgb       = lerp(B, A, DEATH_EFFECT_APPLY_FRACTION);
  ```

### 2.4 `gamma/postfog.ps` — screen-space fog, and it is a *second, separate* fog model

Blob 0 (30 instruction slots). `$Globals`: `float3 CameraPos` +0, `float4x4 WorldViewProjInverse` +16,
`float4 DepthFogParams` +80, `float3 DepthFogColor` +96, `float4 HeightFogParams` +112,
`float3 HeightFogColor` +128. Textures: `t0 sDepthTexture_SharedTexture`, `t1
SAMPLER_BACK_BUFFER_COPY_SharedTexture`. Lines 57-84:

```hlsl
float4 c = BACK_BUFFER_COPY.Sample(uv);
float  d = sDepthTexture.Sample(uv).r;
if (d == 1.0) return c;                                    // lines 59-62: sky / far plane is never fogged

float4 clip  = float4(v2.x, v2.y, d, v2.w);                // v2 = TEXCOORD1 from postfog.vs
float3 world = mul(clip, WorldViewProjInverse).xyz / w;    // lines 65-69

// height fog first
float hf = min(saturate((world.y - HeightFogParams.y) * HeightFogParams.z), HeightFogParams.x);
c.rgb    = lerp(c.rgb, HeightFogColor, hf);                // lines 70-74

// then distance fog
float dist = length(world - CameraPos);                    // lines 75-77
float df   = min(saturate((dist - DepthFogParams.y) * DepthFogParams.z), DepthFogParams.x);
o0.rgb     = lerp(c.rgb, DepthFogColor, df);               // lines 78-82
o0.a       = c.a;
```

So `*FogParams = (maxDensity, start, 1/range, -)` for both. Blob 1
(`FOG_COLOR_FROM_LIGHT_REGIONS=1`) drops `DepthFogParams`/`HeightFogParams` from `$Globals` and instead
reads `PerFramePixelCB.TERRAIN_XFORM` — i.e. it looks the fog colour up per-pixel from the light-region
texture set. Since **nothing on Live authors a light region** (`light-system.md` §1.6), blob 0 is the only
one that can matter.

**What drives blob 0, and whether anything does.** `data/meta/meta.db.json` contains
`PostEffectOptions` (`0xdd3213ec`), whose ten properties map one-to-one onto the shader's `$Globals`:

| bin property | type / default | shader field |
|---|---|---|
| `DepthFog` | Bool, **false** | gates the pass |
| `DepthFogColor` | Vec4, (0,0,0,1) | `DepthFogColor` |
| `DepthFogStart` | F32, 5000 | `DepthFogParams.y` |
| `DepthFogEnd` | F32, 8000 | → `DepthFogParams.z = 1/(End - Start)` |
| `DepthFogMaxIntensity` | F32, 1.0 | `DepthFogParams.x` |
| `HeightFog` | Bool, **false** | gates the pass |
| `HeightFogColor` | Vec4, (0,0,0,1) | `HeightFogColor` |
| `HeightFogStart` | F32, 300 | `HeightFogParams.y` |
| `HeightFogEnd` | F32, -100 | → `HeightFogParams.z = 1/(End - Start)` |
| `HeightFogMaxIntensity` | F32, 1.0 | `HeightFogParams.x` |

The `(maxIntensity, start, 1/range)` triple matches the measured register use exactly, so this is the
authoring surface for blob 0. `LightRegionRenderData` (`0xf15a89f3`) carries the *same ten fields* — which
independently confirms `light-system.md` §1.4's inference that `LightRegionInfo` +48..+111 is the fog block,
and explains what blob 1 reads.

**And nothing ships it.** `disasm lrscan` raw-scans every `.bin` for the class hashes:

```
Map*    : 18 wads, 12,747 .bin entries, 0 failures ->  PostEffectOptions 0 bins, 0x50db156b 0 bins, LightRegionRenderData 0 bins
Global* :  2 wads,  6,304 .bin entries, 0 failures ->  PostEffectOptions 0 bins, 0x50db156b 0 bins, LightRegionRenderData 0 bins
```

**19,051 bins, zero authored `PostEffectOptions`, and both fog booleans default to false.** As with light
regions, the screen-space fog pass is shader-only on Live: the fog you actually see comes from the
per-material `ENV_FOG_*` path below. (Caveat: a raw hash scan is a strong, not perfect, signal, and
champion/other WADs were not scanned — but for a map editor, Map + Global is the relevant corpus.)

**This is not the same fog as the per-material `ENV_FOG_*`.** `DefaultEnv_Flat` blob 226 applies its own
fog inside the pixel shader at lines 727-743, and it is an exponential curve, not this linear one:

```hlsl
float t = saturate((d - ENV_FOG.END) / (ENV_FOG.START - ENV_FOG.END));   // cb1[10] = +160
t = t * t * (3 - 2 * t);                                                 // smoothstep, lines 731-733
float f = max((exp(-2*t) - exp(-2)) / (1 - exp(-2)), 0);                 // lines 734-739
float3 fogCol = lerp(ENV_FOG_COLOR, ENV_FOG_ALT_COLOR, f);               // lines 740-741
colour = lerp(colour, fogCol, f);                                        // lines 742-743
```

(Line 734's literal `2.885390` is `2/ln 2`, so `exp2(t * 2/ln2) = e^(2t)`; line 737's `0.135335` is
`e^-2` and line 738's `1.156518` is `1/(1 - e^-2)`.) **The fog colour is itself interpolated between two
colours by the same factor** — a detail no linear-fog approximation reproduces.

ReyEngine's GL path uses a plain linear fog (`ViewportMeshRenderer.cs:885-889`). That is measurably the
wrong curve.

### 2.5 `gamma/lineardepth.ps` and `gamma/dof.ps`

`lineardepth.ps` (lines 80-83), using `PerFramePixelCB.cDepthConversionParams` (+80):

```hlsl
o0 = (1.0 / (depth * cDepthConversionParams.y + cDepthConversionParams.x)) * cDepthConversionParams.z;
```

`dof.ps` blob 0 (lines 91-98) produces a **circle-of-confusion mask, not a blurred image** —
`$Globals { float2 JitterScale; float4 FocusParams; float2 BlurScale; }`:

```hlsl
float lin  = 1.0 / (depth * cDepthConversionParams.y + cDepthConversionParams.x);
float far  = saturate((lin - FocusParams.x) * FocusParams.y);
float near = saturate((FocusParams.z - lin) * FocusParams.w);
o0.rgb = max(near, far);   o0.a = 1;
```

The blur it drives is one of the `filters/gauss*` shaders; which one, and how the two are composited, is
**not measured**.

### 2.6 `fxaa_ps.ps` — present, and off by default

Root of `assets/shaders/hlsl/`. 11,804 B, 390 disassembly lines, one permutation.
`$Globals { float2 fxaaQualityRcpFrame @0; float4 fxaaQualityOptions @16 }`, textures `tex__TX`/`tex__SMP`.
The parameter names are NVIDIA's FXAA 3.11 quality-preset interface verbatim.

`game.cfg` `[Performance] EnableFXAA=0`.

---

## 3. The diagnosis

### 3.1 What ReyEngine does to the frame today: nothing

Audited across both renderers (evidence in §3.4):

* Render target is **`B8G8R8A8_UNORM`** — plain UNORM, not `_SRGB`, not float
  (`ShaderPreviewRenderer.cs:3099-3111`; the RTV is created with a `null` desc so it inherits that format
  exactly). GL is `GL_RGBA8` (`ViewportControl.cs:1211-1228`) and `GL_FRAMEBUFFER_SRGB` is never enabled
  anywhere in `src/`.
* **Zero post-process passes.** No full-screen triangle is drawn after geometry in either path. The D3D11
  frame ends at `ShaderPreviewRenderer.cs:4272` with `CopyResource(_stage, _rt)` straight after the editor
  overlays; the GL frame ends at `ViewportControl.cs:1156-1161` with a raw `BlitFramebuffer`.
* **One render target bound**: `OMSetRenderTargets(1, _rtv, _dsv)`, `ShaderPreviewRenderer.cs:4058-4060`.
* Single-sample everywhere (`SampleDesc(1, 0)`); no MSAA, no post-process AA.
* **No shadow map at all.** A 1×1 `R32_FLOAT` white texel stands in
  (`ShaderPreviewRenderer.cs:634`), with the comment at `:598` — "there is no shadow map: the stand-in is
  an opaque white."

### 3.2 The hypothesis is HALF RIGHT, and the wrong half is the expensive one

> *Hypothesis: the biggest remaining source of mismatch is the post-process chain — if the game applies
> tonemapping/gamma/bloom to the frame and ReyEngine does not, nothing will ever match.*

**Confirmed:** there is a real post-process chain, and ReyEngine has none of it.

**Refuted, with evidence — the tonemapping/gamma half:**

1. **`gamma/ps_gamma.ps` is not a tonemap and not an sRGB encode.** It is a per-channel 1-D LUT (§2.3).
2. **It is an accessibility control.** The two settings that can drive it live under
   `game.cfg` `[Accessibility]`: `ColorGamma=0.5000`, `ColorBrightness=0.5000` — both at the neutral
   midpoint of a 0..1 slider. *(Inference: that the LUT is generated from these two sliders and is identity
   at 0.5/0.5. The texture is named `*_SharedTexture`, the engine-owned-runtime-resource convention shared
   with `BACK_BUFFER_COPY` and `BLOOM_TEXTURE`, so it is generated at runtime and is not in the WADs. See
   §7 for what would settle it.)*
3. **The real tonemap is inside the material shader, and ReyEngine already runs it.**
   `Mantis_Env_Baked_PBR` blob 27 lines 1384-1394 are **Narkowicz's ACES filmic fit with a 0.6 exposure
   pre-scale**, followed by a `pow(1/2.2)` encode:
   ```hlsl
   float3 y = max(colour, 0) * 0.6;                                   // lines 1384-1385
   float3 t = min( (y*(2.51*y + 0.03)) / (y*(2.43*y + 0.59) + 0.14), 1.0 );  // lines 1386-1391
   out = pow(t, 1.0/2.2);                                             // lines 1392-1394 (log/mul/exp)
   ```
   (Line 1386's `1.506 = 2.51 × 0.6` and line 1388's `1.458 = 2.43 × 0.6` are what identify the fit.)
   Albedo is gamma-decoded on input at lines 431-433. ReyEngine's D3D11 path executes this compiled blob
   verbatim, so it already gets ACES exactly right, for free.
4. **`DefaultEnv_Flat` — the shader most shipped map materials use — has no tonemap and no gamma encode at
   all.** Blob 226 ends at lines 727-750 with fog, the fog-of-war lerp, `mov o0.w, r1.w`, and nothing else.
   It lights directly on sRGB texture values and writes them.
5. **Therefore `B8G8R8A8_UNORM` with no sRGB conversion is CORRECT, not a bug.** The frame buffer is
   display-referred by construction. Switching the render target to `_SRGB` or `R16G16B16A16_FLOAT` would
   apply an encode that Riot does not apply and would make the viewport *less* accurate. This is the single
   most important negative result in this document.
6. **FXAA is off by default** (`EnableFXAA=0`), so it is not a fidelity gap either.

### 3.3 The actual biggest gap: the glow buffer is computed and thrown away

**ReyEngine binds one render target. Riot's shaders — the ones ReyEngine is already running — write the
glow to `SV_Target1`. It goes on the floor.**

This is not "we are missing a nice effect". It is a discarded output of code already executing:

* `OMSetRenderTargets(1, _rtv, _dsv)` at `ShaderPreviewRenderer.cs:4058-4060` binds exactly one RTV.
* 18 static-mesh shader families write a computed `o1` (§2.2), and they are precisely the ones a League map
  uses for anything that is meant to be luminous: `env_glowsign`, `env_glowsign_atlas`,
  `env_light_sequence`, `emissive_basic`, `defaultenv_glow`, `env_scrollingcolor`, `env_scrollingdiffuse`,
  `hologram_rotate`, `flickeralpha_flipbook`, and `mantis_env_baked_pbr`. Those are the shop signs, the
  lane lanterns, the inhibitor and nexus crystals, the Baron-pit runes, the dragon-soul terrain emissives.
* There is no bloom blur and no screen composite, so even the part that *does* reach `o0` never spreads.

The visible consequence is specific and recognisable: **every emissive surface on the map renders as a flat
bright texture with a hard edge instead of a glowing one.** Nothing about the lighting model can fix it,
because it is not a lighting term — it is a second output and three full-screen passes.

Ranked against the alternatives, honestly:

| candidate gap | magnitude | why it is not #1 |
|---|---|---|
| **glow/bloom (RT1 + chain + composite)** | **high on emissive surfaces, and they are the salient ones** | — **it is #1**, and it is also the cheapest |
| sun shadow map | high, but bounded | On baked maps `DefaultEnv_Flat` does `shadow = min(shadow, baked.w)` (`light-system.md` §1.7), so the *baked* shadow mask already darkens static geometry. The dynamic map mostly adds prop and character shadows. Much larger job (§5.3) |
| soft particles + 3 particle fixes | moderate, and cheap | Real and worth doing (§5.2), but it only shows where VFX meet geometry, and ReyEngine already has the depth copy plumbed |
| screen-space `postfog` | **none on Live** | `PostEffectOptions` appears in 0 of 19,051 Map+Global bins and both fog booleans default to false (§2.4). The per-material `ENV_FOG_*` already runs inside Riot's shaders on the D3D11 path |
| colour space / tonemap | **none** | §3.2 — already correct, and "fixing" it would break it |
| FXAA | none | off by default |
| fog of war | none | an editor has no FoW; ReyEngine neutralises it deliberately |
| per-quality permutations | low | `LOW_QUALITY_MODE` is inert in several shaders by blob dedup; `game.cfg` shows all four quality axes at max (4) |

### 3.4 Supporting audit of the current ReyEngine frame

The D3D11 sequence, all in `ShaderPreviewRenderer.RenderFrame` (`:4025-4301`): `EnsureTargets` →
`UpdateClusterLights` (uploads the cluster resources, not a screen pass) → bind RT+DS → clear → `DrawSky`
(depth test and write both off) → one sorted material loop (`:4122-4255`) with lazy `CopyResource`
snapshots for soft-particle depth (`_depthCopy`, `:3184`) and distortion scene colour (`_sceneCopy`,
`:3080`), plus ribbon and mesh-particle branches → `DrawDynamicLights` (an additive geometry re-draw,
`DynamicLights.cs:268`) → editor overlays → staging copy and CPU readback. No depth prepass, no
full-screen pass, no shadow pass.

The GL path's fragment tail (`ViewportMeshRenderer.cs:877-891`) writes `FragColor = vec4(col, outA)` with
no gamma or tonemap; the only `pow()` touching colour is scoped to the baked-lightmap term
(`:428-433`) and is deliberately not applied to the sun/sky term.

---

## 4. The other systems, assessed for adoption

### 4.1 `environment/skybox` — adopt

`skybox.ps` (22 slots), `$Globals`: `VirtualPositionAndCTerm` +0, `CameraPos` +16, `DepthFogInfo` +32,
`HeightFogParams` +48, `HeightFogColor` +64. Texture `ENV_CUBE_SharedTexture` (cube). Lines 50-70 are a
**parallax-corrected cubemap**: a ray/sphere intersection that re-centres the cube sample on a virtual
sphere rather than the camera.

```hlsl
float3 d  = normalize(viewDir);                                  // lines 50-52
float3 oc = CameraPos - VirtualPosition;                         // line 53
float  b  = dot(d, oc);                                          // line 54
float  t  = (sqrt((2b)*(2b) - CTerm) - 2b) * 0.5;                // lines 55-59
float3 P  = (CameraPos + d*t) - VirtualPosition;                 // lines 60-61
float4 c  = ENV_CUBE.Sample(P);                                  // line 62

float hf  = min(saturate((viewDir.y - HeightFogParams.y) * HeightFogParams.z), HeightFogParams.x);
c.rgb     = lerp(c.rgb, HeightFogColor, hf);                     // lines 63-67
o0.rgb    = lerp(c.rgb, DepthFogInfo.rgb, DepthFogInfo.w);       // lines 69-70   <- FLAT amount, not distance
o0.a      = c.a;
```

Two things ReyEngine's hand-written sky does not do: the parallax correction, and the fact that **the sky
is fogged by a flat constant `DepthFogInfo.w`**, which is how Riot makes the horizon meet the terrain fog.
`CTerm` must equal `4*(dot(oc,oc) - R^2)` for the intersection to be exact — that is algebra from the
instruction stream, so the CPU must compute it per frame; **the constant's authored source is inference.**

**Inputs needed:** the cube (already loaded), camera position, a virtual sphere centre + radius, and the
two fog blocks. **Buys:** a correctly-parallaxed horizon and a sky that fogs into the terrain instead of
sitting behind it.

### 4.2 `environment/shadowmap` — adopt (but it is a subsystem, not a pass)

Reflected in M454, not disassembled. It is the depth-only write pass for
`SHADOW_MAP_DEPTH_PCF_SharedTexture`, which every environment shader samples with `SampleCmpLevelZero`.
`filters/blur_shadow_3.ps` / `_5.ps` post-process it; `blur_shadow_3` (lines 46-63) keeps the **nearest**
of 3 taps in RGB (depth packed as `dot(rgb, (1/65536, 1/256, 1))`) while averaging `.w`.

**ReyEngine could drive it** — it has the geometry and the sun direction, and the consuming 5-tap PCF plus
the bias formula are already specified in `light-system.md` §1.7. What must be synthesised CPU-side:
`mShadowProj` (`PerFrameVertexCB` +176), an orthographic sun frustum fitted to the visible scene,
`CONSTANT_DEPTH_BIAS` / `SLOPE_SCALED_DEPTH_BIAS`, and `SHADOW_SAMPLE_OFFSETS`.

### 4.3 `fogofwar/` — not applicable, but now fully specified

Three single-permutation shaders forming a texture pipeline, not a screen pass:

* `fade.ps` (lines 50-62) — moves `SAMPLER_VISIBILITY_CURRENT` toward `SAMPLER_VISIBILITY_TARGET`, clamped
  by per-frame rise/fall rates in `$Globals.xy`, with a hard snap above `$Globals.w`.
* `fastseparablegaussianblur.ps` (lines 46-53) — 3-tap, `centre*w.z + (p+ + p-)*w.w`, offset `w.xy`.
* `applyoverlayandcolor.ps` (lines 84-90) — composites the animated overlay:
  ```hlsl
  float  m   = srcTex.Sample(uv).r;
  float  mm  = saturate(m * FOW_EDGE_CONTROL.z);                       // PerFramePixelCB +192
  float2 ouv = uv * FOG_OVERLAY_UV_ANIMATE.xy + FOG_OVERLAY_UV_ANIMATE.zw;   // +176
  float4 ov  = fowOverlay.Sample(ouv);
  o0.rgb = lerp(ov.rgb, float3(0.2, 0.07, 0.06), mm);
  o0.a   = lerp(ov.a, 1.0, m);
  ```
  The hardcoded `(0.2, 0.07, 0.06)` is the unexplored-terrain tint.

The product is `FOW_MAP_SharedTexture`, which material shaders sample. ReyEngine already neutralises FoW
with a white stand-in, which is the correct editor behaviour. **Verdict: do not adopt**; documented so
nobody re-derives it.

### 4.4 `regions/` — the same machine, for the nav grid

`regions/fade.ps` and `regions/fastseparablegaussianblur.ps` are the FoW pair with the textures renamed to
`SAMPLER_NAV_GRID_TARGET` / `_CURRENT` and widened from 1 channel to `float4` (lines 50-61 and 46-53
respectively). **Verdict: debug overlay only.**

### 4.5 `decal/` and `environment/unlit_decal` — already covered

`decal/decal.ps` is `o0 = Texture.Sample(uv) * Color`, one float4 constant. `unlit_decal` is the real
projected decal, fully specified in `light-system.md` §3.2, and ReyEngine already runs it.

### 4.6 `gameplaytexture/ps_gameplay_texture` — irrelevant

One permutation, `dcl_constantbuffer CB1[128], dynamicIndexed` — a **128-slot shape list** walked by a
`loop` + `switch` (lines 109-136+), where case 1 is a circle with a smoothstep falloff and later cases are
other primitives, modulated by an `AlphaMask` noise texture sampled at 3× and 11× UV (lines 95-101).
`ChannelGrid__TX` is a `Texture2DArray`. This paints gameplay indicator regions into a texture. **Verdict:
irrelevant to a map viewport.**

### 4.7 `renderer/`, `debug/`, `debugdrawnavgrid/`, `editor/`, `hud/`

* `renderer/ps_worldvertex.ps` (line 37): `o0 = texture * vertexColour`. `_greyscale` (lines 36-38) does
  Rec.601 `(0.30, 0.59, 0.11)` first. Four `vs_screenvertex*` variants add scale/bias/rotate/colour. This
  is Riot's immediate-mode primitive path — **ReyEngine already has equivalents** for its overlays.
* `debug/errorindicator.ps` (lines 27-37): a world-space checkerboard at 100-unit scale
  (`floor(pos * 0.01)`, parity, `frac < 0.5`) alternating between vertex colour and its
  `(0.2, 0.7, 0.1)` luma. **Cheap and genuinely useful** as a missing-material indicator.
* `debugdrawnavgrid/drawnavgrid.ps` (line 23): `o0 = vertexColour`, 2 instruction slots — the nav grid is
  coloured entirely in its vertex shader. Optional editor feature.
* Root: `single_color_ps.ps` (line 38) is `o0 = cb0[0]`. `simple_vs.vs` (lines 75-84) is
  `CharacterPerDrawVertexCB` world matrix then `PerFrameVertexCB` view-projection. Both trivial.
* `gamma/ps_pause.ps` (lines 46-52): `o0.rgb = mean(c.rgb) * cb0[0].rgb + c.rgb * cb0[0].w` — a
  greyscale-tint blend for the pause screen, using a plain `$Globals`, not `PostEffectPixelCB`.
* `hud/tacticalmapregionpainting.ps` (lines 42-52): if `dot(mask, channelSelect) == 1`, output either the
  overlay texture (when the incoming colour's alpha is 0) or the incoming colour.
* **`editor/` is one TOC: `ps_showmiplevels.ps`.** The entire shader is
  `sample_l(uv, mip = TEXCOORD.x * cb0[0].x)` (lines 47-48) — a mip-level visualiser that ramps the
  sampled LOD across the screen. That is the whole of Riot's shipped "editor" shader family; there is no
  gizmo, grid, or selection shader hiding there.
* `hud/minimapfow.ps` (lines 35-38): `o0 = float4(0,0,0, (1 - fow) * 0.64)`.
  `hud/tacticalmapregionpainting.ps` — listed only. Both irrelevant.

### 4.8 `lighting/` — done in M454

Covered exhaustively in `light-system.md` §1, including the measurement that **zero shipped maps author a
light region**. No further work.

### 4.9 `particlesystem/` — 20 TOCs, all disassembled

Flagged in M454 as "the one omission that could matter later". It does matter, and it is now fully
measured. Dumps in `out459/p_*.txt`.

**Three headline results.**

**(a) Soft particles exist, and the equation is a two-sided band.** `quad_ps.ps` and `mesh_ps.ps` bind
`sDepthTexture_SharedTexture` in exactly half their blobs (128/256 and 1024/2048 — the `SOFT_PARTICLES=1`
half); all 18 other TOCs bind it in zero. `quad_ps` blob 129, lines 107-135:

```hlsl
float rawZ   = sDepthTexture.Load(int3(SV_Position.xy, 0)).x;                     // 107-109
float sceneL = 1.0 / (rawZ          * cDepthConv.y + cDepthConv.x);               // 110-111
float partL  = 1.0 / (SV_Position.z * cDepthConv.y + cDepthConv.x);               // 112-113
float d      = sceneL - partL;                                                    // 114

float t0 = saturate((d - cSoftParticleParams.x) * cSoftParticleParams.z);         // 115-116
float t1 = saturate((d - cSoftParticleParams.y) * cSoftParticleParams.w);
float fade = smoothstep01(t0) - smoothstep01(t1);                                 // 117-120

OUT.rgb = cSoftParticleControl.x * C + cSoftParticleControl.y * fade * C;         // 132-134
OUT.a   = cSoftParticleControl.z * A + cSoftParticleControl.w * fade * A;         // 132-135
```

Two details worth having: it is a **band**, not a near-fade (`.xy` are two independent depth offsets,
`.zw` two inverse widths, so a particle can fade in near a surface *and* out again past a far distance);
and **`cSoftParticleControl` is a 4-vector of blend weights** `(rgbBase, rgbFade, aBase, aFade)` that
selects *which channels* the fade modulates at runtime — `(1,0,0,1)` fades alpha only (alpha blending),
`(0,1,1,0)` fades colour only (correct for additive, where alpha is ignored). Riot switches soft-particle
behaviour per emitter with a constant, not a recompile.

**(b) Particles are never fogged. Conclusively.** `hdump scan <toc> FOG` returns **0 hits across all 2,884
distinct particle blobs**. The maximal `mesh_ps` permutation (blob 2042, all 11 axes on) reflects
`PerFramePixelCB 560 B, 1/35 used` — the one used field being `cDepthConversionParams`. Every `ENV_FOG_*`
is `[unused]` in every particle blob reflected. The whole family touches exactly two `PerFramePixelCB`
fields ever: `cDepthConversionParams` (+80) and `FOW_EDGE_CONTROL` (+192).

**(c) `PARTICLE_DEPTH_PUSH_PULL`.** Every non-shadow particle VS pushes the vertex along the view ray
before projection — `pos += normalize(pos - vCamera) * PARTICLE_DEPTH_PUSH_PULL` (`quad_vs` lines 83-87,
`mesh_vs` lines 106-110). This is Riot's particle/geometry z-fighting mitigation; the shadow VSs
deliberately omit it. One line.

**`quad_ps.ps` base (blob 1, 13 slots, no constant buffers)** — the reference billboard program, lines
48-59:

```hlsl
float4 c = TEXTURE.Sample(uv0) * PARTICLE_COLOR_TEXTURE.Sample(uv1) * IN.color;   // 48-51
float  L = dot(c.rgb, float3(0.2126, 0.7152, 0.0722));                            // 52   Rec.709
float4 r = PIXEL_COLOR_REMAP_RAMP.Sample(s15, float2(L, 0.5));                    // 53-54
if (r.a > 0) c.rgb = r.rgb;                                                       // 55-56  runtime-gated
OUT.a   = c.a;                                                                    // 57
OUT.rgb = FOW_MAP.Sample(s15, IN.fowUV).a * c.rgb;                                // 58-59
```

**Straight alpha, not premultiplied** — RGB is never multiplied by A anywhere. **FoW affects RGB only**,
and reads the FoW texture's **alpha** channel. The luminance remap ramp is present in *every* permutation
including the base, gated at runtime on `ramp.a > 0` — the same gating ReyEngine already discovered for
`_identityRamp` (`ShaderPreviewRenderer.cs:641-652`).

**`mesh_ps.ps` base (blob 770)** differs from `quad_ps` in four ways: `PARTICLE_COLOR_TEXTURE` is sampled
at a per-draw **constant** `COLOR_LOOKUP_UV` (line 107); an **unconditional per-vertex Fresnel rim** is
added, `c.rgb += TEXCOORD6.rgb * c.a` (line 114, with `TEXCOORD6 = (1 - pow(saturate(dot(-V,N)),
vFresnel.w)) * vFresnel.rgb` from `mesh_vs` lines 124-135) — **there is no define for it**; FoW is
height-blended (`lerp(FOW_EDGE_CONTROL.w, fow, weight)`, lines 116-118); and the output is `mul_sat`
(line 119), so mesh particles cannot emit above 1 while quad particles can.

**Distortion — and ReyEngine's hand-written pass is measurably different.** `distortion_ps` blob 0 reads
`SAMPLER_BACK_BUFFER_COPY_SharedTexture` (lines 62-72):

```hlsl
float4 n = NORMAL_MAP.Sample(uv0);
float2 o = (n.xy - 0.5) * DistortionPower * PARTICLE_COLOR_TEXTURE.Sample(uv1).a;  // 63-66
float3 bb   = BACK_BUFFER_COPY.Sample(s15, IN.screenUV + o * 2.0).rgb;             // 67-68
float3 tint = TEXTURE.Sample(uv0).rgb * IN.color.rgb * p.rgb;                      // 69-71
OUT.rgb = bb * tint;    OUT.a = n.a * p.a;                                         // 72
```

Against ReyEngine's version (`ShaderPreviewRenderer.cs:2905-2915`) there are three real differences: Riot
scales the offset by the **colour-over-life ramp's alpha** so distortion strength animates with the fade
curve for free; Riot **multiplies the refracted sample by a tint** (`diffuse × vertexColour × ramp`), so a
distortion particle is a *tinted* refraction rather than a pure displacement — ReyEngine returns
`float4(refracted, mask)` untinted; and output alpha is `normalMap.a * ramp.a`, not the combined mask.
`screenUV` is computed in the VS (`distortion_vs` lines 92-105: `ndc*0.5 + 0.5`, Y-flipped), not the PS.

**Shorter findings.** `shadow_quad_ps` / `shadow_mesh_ps` are 8-slot depth-only cutouts with a
**hardcoded** `clip(a - 0.025)` — particles do cast shadows, binary cutout only, and the `ALPHA_EROSION`
variant dissolves the shadow in step with the particle. `simple_projected_ps` is a one-texture ground decal
whose UV comes from a **projector matrix applied to world position** in the VS (`simple_projected_vs` lines
91-98), with offset/scale/rotation entirely in constants. `quad_screenspaceuv` locks the diffuse to screen
space via the multiply-by-`w` / divide-by-`w` trick that defeats perspective-correct interpolation
(`.vs` lines 90-97, `.ps` line 48). **Vertex colour is BGRA on the wire** — every particle VS reads
`v1.zyxw`, never `v1.xyzw`.

**Two inert axes, proven by blob dedup.** In `mesh_ps_slice.ps` (128 perms → 96 blobs) `MULT_PASS` and
`SEPARATE_ALPHA_UV` map to the same blob as each other and as their absence; a full disassembly diff of
blob 0 against blob 3 shows **zero instruction differences**, the only delta being a dead `TEXCOORD 3`
interpolator with a blank `Used` column. In `mesh_vs.vs` (256 → 192) all 64 collisions are
`{LOCAL_SPACE_UV}` pairs inside the `SCREEN_SPACE_UV=1` half — screen-space UV overrides local-space UV
completely.

**Per-TOC verdicts** (map-editor viewport: static geometry + map-authored VFX, no champions):

| TOC | perm/blob/axes | what it is | verdict |
|---|---|---|---|
| `quad_ps.ps` / `quad_vs.vs` | 256/256/8, 16/16/4 | the billboard reference program | **adopt** |
| `mesh_ps.ps` / `mesh_vs.vs` | 2048/2048/11, 256/192/8 | mesh emitters: constant ramp UV, Fresnel rim, saturated out | **adopt** |
| `distortion_ps.ps` / `distortion_vs.vs` | 4/4/2, 1/1/0 | heat haze off the back-buffer copy | **adopt** — direct fix for the existing pass |
| `distortion_mesh_ps.ps` / `_vs.vs` | 8/8/3, 2/2/1 | mesh-geometry haze | adopt with it |
| `quad_ps_slice.ps` / `mesh_ps_slice.ps` | 16/16/4, 128/96/7 | iso-band / shockwave-ring extraction | adopt, low priority |
| `quad_ps_fixedalphauv.ps` / `_vs.vs` | 64/64/6, 8/8/3 | alpha at an un-animated corner UV while RGB flipbooks | adopt, low priority |
| `quad_screenspaceuv.ps` / `.vs` | 16/16/4, 2/2/1 | screen-locked diffuse | low priority |
| `simple_projected_ps.ps` / `_vs.vs` | 8/8/3, 2/2/1 | projected ground decal | **adopt (cheap)** |
| `shadow_quad_ps/vs`, `shadow_mesh_ps/vs` | 2/2/1 ×3, 1/1/0 | particle shadow cutouts | irrelevant until §5.3 exists |

Cross-cutting: **adopt** `SOFT_PARTICLES`, `ALPHA_EROSION` (a linear two-edge band, identical in the main
and shadow passes), the `PARTICLE_COLOR_TEXTURE` ramp and `PALETTIZE_TEXTURES` LUT, `PARTICLE_DEPTH_PUSH_PULL`,
and `REFLECTIVE` for mesh emitters. **Do not implement** environment fog on particles (does not exist),
`MASKED` (navmesh — no navmesh in an editor), or `COLORPALETTE_COLORBLIND` (accessibility, runtime-gated
off). Prefer the `DISABLE_FOW=1` permutations, or bind an all-1 FoW texture as ReyEngine already does.

---

## 5. Prioritised roadmap

Ordered by expected visual gain per unit of work, not by interest.

### 5.1 M-next: the glow buffer and the bloom chain — **do this first**

**What:** bind a second render target, run Riot's own blur chain on it, composite with `ps_gamma`'s screen
blend.

**Visual gain: high, and concentrated on the surfaces a viewer looks at.** Every emissive material on the
map stops being a flat bright texture and starts glowing. Today the contribution is computed by Riot's
shader and discarded (§3.3).

**Work — small, because every shader already exists and is measured:**
1. Create `_glowRT` (same size and format as `_rt`; `B8G8R8A8_UNORM` is fine — the screen blend is LDR-safe)
   and change `OMSetRenderTargets(1, ...)` at `ShaderPreviewRenderer.cs:4058` to bind both. Clear RT1 to
   black. Shaders that declare only `o0` (sky, decals) simply leave RT1 untouched.
2. Downsample `_glowRT` with `filters/mipchainbloomdownsample.ps` (§2.2), upsample with
   `mipchainbloomupsample.ps`, final pass `filters/bloom.ps` twice (H then V) → `BLOOM_TEXTURE`.
3. Composite with `gamma/ps_gamma.ps` blob 1's math, or `ps_copy_post` blob 1 if the LUT is skipped:
   `out = 1 - (1 - scene) * (1 - bloom)`.
4. Full-screen quad = `gamma/post_effect.vs`.

**Synthesise CPU-side:** only `UVStep` per mip level (`1/width, 1/height` of the *source* mip) and the mip
count. Nothing else.

**Blocker:** none. All five shaders can be loaded from the cache and run verbatim, exactly as ReyEngine
already runs material shaders.

**Caveat to verify early:** §2.2 measured which *shaders* can glow, not which *permutations shipped maps
select*. `ShaderResolver.Resolve` (`src/ReyEngine.Formats/Shaders/ShaderResolver.cs:54-55`) already passes
a `featureDefines` set through to `ResolvePermutation`, so `FEATURE_BLOOM` should flow from the material
bin without changes — but confirm on a real map that the resolved blob is one of the computed-`o1` blobs
before wiring the chain, or step 1 renders a black RT1 and the whole feature looks broken for the wrong
reason.

### 5.2 Soft particles, and three particle corrections

**Visual gain: moderate and immediate wherever map VFX meet geometry.** **Work: small** — ReyEngine
*already* captures the depth copy for exactly this (`CaptureDepthCopy`, `ShaderPreviewRenderer.cs:3184`)
and already binds it to any `*DepthTexture*` name (`:4387-4393`). Four separate items, all cheap:

1. **Feed the `SOFT_PARTICLES=1` permutation** and supply `cSoftParticleParams` (two depth offsets, two
   inverse widths) and `cSoftParticleControl` (the 4-weight channel selector). §4.9(a) has the equation.
   Without it, every map particle shows a hard intersection line against terrain.
2. **`PARTICLE_DEPTH_PUSH_PULL`** — one line in the VS constant feed, removes particle/geometry
   z-fighting (§4.9(c)).
3. **Correct the distortion pass**: scale the offset by the colour-ramp alpha and multiply the refracted
   sample by the `diffuse × vertexColour × ramp` tint (§4.9). ReyEngine's current version omits both.
4. **`ALPHA_EROSION`** — the dissolve is a two-edge linear band and is very common in map VFX.

**Synthesise:** `cDepthConversionParams` (already needed), the two soft-particle float4s, and the erosion
progress as a per-vertex stream. **Blocker:** none.

### 5.3 The sun shadow map

**Visual gain: high but bounded** — baked maps already carry a static shadow mask
(`shadow = min(shadow, baked.w)`), so this mostly adds prop and dynamic-object shadows and fixes any map
whose bake is stale or absent. **Work: large** — a whole extra render pass with its own camera, which is
why it sits below two cheaper items despite the bigger raw gain.

Needs: `environment/shadowmap.vs/.ps`, an orthographic sun frustum fitted per frame, `mShadowProj`
(`PerFrameVertexCB` +176), the two depth biases, `SHADOW_SAMPLE_OFFSETS`, optionally
`filters/blur_shadow_3/_5`. The consuming PCF is already specified (`light-system.md` §1.7) and ReyEngine's
own experimental shaders already implement it — they are just sampling a 1×1 white stand-in
(`ShaderPreviewRenderer.cs:634`). Particle shadow cutouts (§4.9) become relevant only once this exists.

**Blocker:** none technical; it is simply a big piece of work.

### 5.4 The parallax-corrected skybox

**Visual gain: moderate** — a correct horizon and a sky that fogs into the terrain. **Work: small**, math
complete in §4.1. **Synthesise:** the virtual sphere centre/radius and `CTerm`.

### 5.5 Fix the GL path's fog curve

**Visual gain: small but free.** GL uses linear fog; Riot uses the smoothstep + exponential remap in §2.4,
*and* interpolates the fog colour between `ENV_FOG_COLOR` and `ENV_FOG_ALT_COLOR` by the same factor.
Roughly ten lines in `ViewportMeshRenderer.cs:885-889`.

### 5.6 `debug/errorindicator` as the missing-material shader

**Visual gain: none** (it is a diagnostic), **work: trivial**, 12 instruction slots. Worth it for the
editor.

### 5.7 NOT WORTH IT — things that look attractive and are not

| thing | why not |
|---|---|
| **Switching the render target to `_SRGB` or `R16G16B16A16_FLOAT`** | **Actively harmful.** Riot's shaders emit display-referred colour (Mantis gamma-encodes itself; `DefaultEnv_Flat` never linearises). An sRGB target would double-encode. §3.2. |
| **Implementing a tonemap operator** | Already there, inside Mantis, and ReyEngine already runs it (§3.2 item 3). Adding a frame-level ACES would apply it twice. |
| **The `gamma/` LUT itself** | An accessibility slider at its neutral midpoint. Adopt the *bloom composite* from `ps_gamma`, not the LUT. |
| **FXAA** | `EnableFXAA=0`. Off in the game means off in the reference. |
| **A bloom bright-pass / threshold** | Does not exist. The bright-pass is the artist's `FEATURE_BLOOM` choice written to RT1 (§2.2). Building a luminance threshold would produce a *different* image, not a closer one. |
| **`gamma/postfog.ps` (screen-space fog)** | **Zero shipped content.** `PostEffectOptions` appears in 0 of 19,051 Map+Global bins, and both `DepthFog`/`HeightFog` booleans default to false (§2.4). The fog you see is the per-material `ENV_FOG_*`, which the D3D11 path already runs. |
| **Fog on particles** | Does not exist in any of 2,884 particle blobs (§4.9(b)). Adding it would move the viewport *away* from the game. |
| **`lighting/lightregions*`** | Zero shipped content (`light-system.md` §1.6). |
| **`MASKED` / navmesh mask and `COLORPALETTE_COLORBLIND` on particles** | Navmesh masking is meaningless in an editor; the colour-blind correction is runtime-gated off by default (§4.9). |
| **`fogofwar/`** | An editor should show the map revealed. ReyEngine already neutralises it correctly. |
| **`gameplaytexture/`, `hud/`, `ui/`, `font/`** | Not part of a map viewport. |
| **`gamma/dof.ps`** | Produces a CoC mask only; the compositing half is unmeasured, and League does not use DoF in normal play. |
| **Riot's `editor/` shaders** | It is one mip-level visualiser (§4.7). There is nothing there. |

---

## 6. GL feasibility (GLES 3.0 / ANGLE — no compute, no SSBO, ASCII-only source, no `samplerCubeArray`)

| roadmap item | GLES 3.0? | how, or why not |
|---|---|---|
| Second render target (glow) | **yes** | MRT with 2 colour attachments is core GLES 3.0 (`GL_MAX_COLOR_ATTACHMENTS` ≥ 4). Declare `layout(location=1) out vec4 oGlow;` |
| Bloom downsample / upsample / Gaussian | **yes, exactly** | Plain `texture()` taps and MADs. All three are ≤ 37 instruction slots and use no integer or derivative ops |
| Screen composite `1-(1-a)(1-b)` | **yes, exactly** | Arithmetic only |
| The gamma LUT | **yes** (if ever wanted) | A 256×1 `sampler2D`, three `texture()` calls |
| Soft particles (§4.9a) | **yes**, after one change | The equation is arithmetic plus a `texelFetch` of the depth buffer. **But GL currently cannot sample depth at all**: `ViewportControl.cs:1226` allocates `Depth24Stencil8` as a **renderbuffer**. It must become a depth *texture* attachment (`GL_DEPTH_COMPONENT24`/`32F`) first. That is the single structural GL change on this roadmap |
| `PARTICLE_DEPTH_PUSH_PULL`, alpha erosion, palette LUT, particle ramp | **yes, exactly** | Arithmetic and 2-D texture samples only |
| Distortion tint + ramp-scaled offset | **yes, exactly** | Arithmetic; GL already has a scene-copy equivalent path |
| `postfog` (if ever needed) | **yes** | Same depth-texture prerequisite as soft particles. Demoted anyway — §5.7 |
| Skybox parallax | **yes, exactly** | `samplerCube` + arithmetic |
| Sun shadow map + 5-tap PCF | **yes** | `sampler2DShadow` gives `SampleCmpLevelZero` natively. `blur_shadow_3`'s packed-depth `dot` is plain arithmetic |
| `lineardepth` / DoF CoC | **yes** | Arithmetic |
| FXAA | **yes** but pointless | FXAA 3.11 has a GLES path; it is off by default anyway |
| `ps_luminance`, `errorindicator`, `renderer/*` | **yes, exactly** | Trivial |
| `gameplaytexture` | **no, and unnecessary** | A `dynamicIndexed` 128-slot cbuffer walked by a `loop`+`switch`. GLES 3.0 UBO dynamic indexing is permitted but driver-fragile, and the shader is irrelevant anyway |
| `lightregions*` | **impractical, unnecessary** | Unchanged from `light-system.md` §5 |

**Where GL genuinely cannot follow: nothing on this roadmap.** Every adoptable item is expressible in
GLES 3.0. The only structural change required is making the depth buffer *sampleable* — a depth texture
attachment instead of the current renderbuffer — which gates soft particles (and `postfog`, were it ever
wanted). MRT for the glow buffer, all three bloom filters, the screen composite, the skybox parallax and
the shadow-map PCF all translate one-to-one.

**ASCII-only rule.** Every formula in this document must become ASCII in emitted GLSL — `×` → `*`,
`≥` → `>=`, `π` → `PI`. This has broken the GL viewport before (`shader-strings-ascii-only`).

---

## 7. UNKNOWN / NOT MEASURED

Each with the evidence that would settle it.

1. **Is `SAMPLER_GAMMA_LOOK_UP` identity at `ColorGamma=0.5 / ColorBrightness=0.5`?** The `_SharedTexture`
   naming says runtime-generated, and the settings are `[Accessibility]` sliders at their midpoint, but the
   LUT's contents are inference. **Settle it:** RenderDoc capture of the texture bound to `t1`/`t2` in
   `ps_gamma`, or find the CPU-side generator. This is the one assumption §3.2 rests on; if the LUT turns
   out to be a real curve at default, item 5.1 gains a step but does not change order.
2. **The exact bloom mip count and per-mip `UVStep`.** The three filter shaders are measured; how many
   times they are chained, and whether the upsample is additive or replacing, is not.
   **Settle it:** RenderDoc pass list.
3. **Where `BLOOM_INTENSITY_SCALE` (`PostEffectPixelCB` +64) is consumed.** Unused in all four
   bloom-binding blobs inspected. Presumably scales `BLOOM_TEXTURE` during generation. **Settle it:** scan
   every blob of every `gamma/` and `filters/` TOC for a `PostEffectPixelCB` binding.
4. ~~Whether shipped maps drive `gamma/postfog.ps`.~~ **RESOLVED in this milestone** (§2.4): the
   authoring class is `PostEffectOptions`, both fog booleans default to false, and it appears in 0 of
   19,051 Map+Global bins. Residual doubt: a raw hash scan is a strong-not-perfect signal, and champion
   and non-map WADs were not scanned.
5. **How `dof.ps`'s CoC mask is composited**, and which `gauss*` shader supplies the blur.
6. **FXAA's position in the chain** — before or after the gamma composite.
7. **`environment/reflectionsky.ps` and `shadowmap.ps` are reflected, not disassembled.** `reflectionsky`
   binds `SAMPLER_BACK_BUFFER_COPY` plus both light-region textures.
8. **`skybox`'s `CTerm`.** The algebra requires `4*(dot(oc,oc) - R^2)`; the authored source of the
   constant is inference.
9. **Blend/depth state for every pass.** DXBC carries none. Same caveat as `light-system.md` §6.6.
10. **`game.cfg` is this machine's user state**, not a pristine default. Every value quoted is at a neutral
    or maximum position, but a clean-install comparison would be firmer.
11. **PBE.** Live only. No PBE diff run.
12. **Particle axis interactions.** §4.9's axis behaviours rest on single-axis blobs versus the base, plus
    the maximal `mesh_ps` reflection. `quad_ps_slice` / `mesh_ps_slice` / `quad_ps_fixedalphauv` were
    characterised from their base permutation only, so their axis-by-axis behaviour is inferred by analogy
    with `quad_ps`. `SLICE_RANGE`'s half-width/sharpness split is a reading of the algebra, not a
    name-confirmed fact.

### Coverage statement

**`assets/shaders/hlsl/`: 139 stage TOCs. Disassembled and analysed 66 (47%). Reflection/header inspected
only, 2 (1%). Enumerated (name, axes, permutation/blob counts) 139 (100%).**

Newly disassembled and analysed in M459 (57):

* **`particlesystem/` — all 20 of 20**, base permutation minimum, plus single-axis blobs for every axis of
  `quad_ps` (blobs 0,1,5,6,8,15,21,25,129), `mesh_ps` (20,770,773,795), `mesh_vs` (10,20,21,101),
  `mesh_ps_slice` (0,3), `quad_vs` (1,5) and `shadow_quad_ps` (0,1)

* `gamma/` (11 of 11) — `ps_gamma` blobs 0/1/6, `ps_gamma_colored`, `ps_gamma_colorization_post_effect`,
  `ps_copy_post`, `ps_luminance`, `ps_pause`, `postfog` blob 0, `post_effect.vs`, `dof`, `lineardepth`
* `filters/` (8 of 16) — `bloom`, `bloomhigh`, `mipchainbloomdownsample`, `mipchainbloomupsample`, `gauss`,
  `blur_shadow_3`, `ps_copy_filter`, `ps_copy_gradient_remap`
* root (3 of 3) — `fxaa_ps`, `single_color_ps`, `simple_vs`
* `environment/skybox.ps`; `fogofwar/` all 3; `regions/` both; `renderer/ps_worldvertex` + `_greyscale`;
  `hud/` both; `debug/errorindicator`; `debugdrawnavgrid/drawnavgrid.ps`; `editor/ps_showmiplevels`;
  `gameplaytexture/ps_gameplay_texture`; `ui/ui_copyfromoffscreen`; `ui/ui_brightness`

Carried from M454 (9): `lighting/` all 3, `decal/` both, `environment/unlit_decal_ps` + `_vs`,
`environment/shadowmap.ps`, `skinnedmesh/lit_uber_ps` blob 400.
Reflection only: `environment/reflectionsky.ps`, `gamma/postfog.ps` blob 1, `mesh_ps.ps` blob 2042.

**Outside `hlsl/`:** the `mrt` census disassembled **2244 blobs across all 95 `generated/shaders/staticmesh/`
pixel TOCs** (up to 24 distinct blobs each) — that is the evidence base for §2.2. Additionally
`defaultenv_glow.ps` blobs 0 and 8, and (from M454) `mantis_env_baked_pbr.ps` blobs 18/27 and
`defaultenv_flat.ps` blobs 48/152/153/154/226.

**Only listed, never opened (71):** `ui/` 35 of 37, `font/` 4, `skinnedmesh/` 14 of 15, `filters/` 8 of 16
(`blur_shadow_5`, `gauss5`, `gauss5x5`, `ps_copy_filter_with_color_mod`, `ps_blur_green`,
`ps_dilate_green`, both `ps_mouseover_outline_blend*`), `gamma/postfog.vs`, `renderer/` 5 of 7 (the four
`vs_screenvertex*` and `vs_simpleworldvertex`), `environment/` `skybox.vs`, `reflectionsky.vs`,
`shadowmap.vs`, `debug/errorindicator.vs`, `debugdrawnavgrid/drawnavgrid.vs`.

---

## 8. Files produced (scratchpad, not committed)

`scratchpad/disasm/out459/` — `all_hlsl_tocs.txt`, `gen_all.txt`, `mrt_staticmesh.txt`,
`ps_gamma_b{0,1,6}.txt`, `g_{ps_luminance,ps_copy_post,ps_gamma_colored,ps_gamma_colorization_post_effect,
postfog,postfog_b1,dof,lineardepth,posteffect_vs}.txt`,
`f_{bloom,bloomhigh,mipchainbloomdownsample,mipchainbloomupsample}.txt`, `fxaa.txt`,
`z_{ps_copy_filter,ps_copy_gradient_remap,gauss,blur_shadow_3,...}.txt`,
`x_{fogofwar_*,regions_*,gameplaytexture_*,editor_*,debug_*,hud_*}.txt`,
`y_{renderer_*,environment_skybox,environment_reflectionsky}.txt`, `glow_env.txt`, `glow_env8.txt`,
`ui_copy.txt`, `ui_bright.txt`, and the particle family as `p_*.txt` (`p_quadps_b{0,1,5,6,8,15,21,25,129}`,
`p_meshps_b{20,770,773,795}`, `p_meshvs_b{10,20,21,101}`, `p_distortion_*`, `p_shadow_*`,
`p_simple_projected_*`, `p_scan_fog.txt`, `p_scan_depth_fog_summary.txt`).
New probe: `MrtCensus.cs` (mode `mrt`). `LrScan.cs` retargeted to the `PostEffectOptions` hashes.
