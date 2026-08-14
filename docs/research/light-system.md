# Riot's light system — measured specification

**Milestone:** M454 · **Status:** research only, no code changed. Everything below is read out of
compiled DXBC in `ShaderCache.dx11.wad.client` plus `data/meta/meta.db.json` and the shipped map WADs.

The goal is a map an implementer can build against without re-deriving anything: what Riot's lighting
actually computes, where every number comes from, and what our two viewports can and cannot mirror.

---

## 0. Method, and how to reproduce every claim

All disassembly came from one scratchpad probe, `HDump.cs` (mode `hdump`), on top of
`ShaderCacheReader` + `DxbcReflection` + `D3DCompiler.Disassemble`:

```
disasm hdump list  <substring>            # TOCs, axes, permutation/blob counts
disasm hdump perms <tocpath>              # every permutation with recovered defines
disasm hdump scan  <tocpath> <needle>     # per DISTINCT blob: does it bind a resource named <needle>?
disasm hdump refl  <tocpath> <blob>       # signature + resources + CB variables incl. IsUsed
disasm hdump dis   <tocpath> <blob> <out> # full listing, one line per disassembly line
```

**Line-number convention.** Every "line N" in this document is the index `hdump dis` prints, which is the
0-based line index of the D3DDisassemble output *including* its leading comment block. Re-running the same
command reproduces the same numbering.

**Two traps that this probe is built to avoid, both of which have produced false results here before:**

1. **The stage suffix is `.ps-dx11` with a HYPHEN.** Filtering on `.ps.dx11` silently matches nothing and
   looks exactly like a clean negative result (documented in `PbeWater.cs`).
2. **Blobs are deduplicated across define sets.** A permutation's define list is *not* evidence that the
   compiled code honours those defines — if `X=1` and `X` absent map to the same blob index, the code is
   byte-identical and `X` does nothing in that stage. This is used below as positive proof twice
   (`DISABLE_SHADOWS` on `unlit_decal_ps`, `PREMULTIPLIED_ALPHA` on `DefaultEnv_Flat`).

**`assets/shaders/hlsl/` is not HLSL source.** A raw byte classification of all 323 entries returns
0 ASCII-text, 0 starting with the `DXBC` magic, 323 "other" — the last because each blob container begins
with a 4-byte length prefix ahead of the first `DXBC`. Read through `ShaderCacheReader.ReadToc` /
`LoadBlob` they are ordinary TOC3.0 + compiled DXBC, byte-for-byte the same layout as
`assets/shaders/generated/`. Only the directory name suggests source; nothing in the data does.

**Whole-cache facts.** 841 stage TOCs total, of which **139 are under `assets/shaders/hlsl/`** (the 323
raw entries are those TOCs plus their `_<n>` blob containers). **Zero `.cs-dx11` (compute) and zero
`.gs-dx11` (geometry) entries exist anywhere in the cache.** Everything Riot does is VS+PS. Any structure
the pixel shaders consume that is not a render target (the cluster map, the cluster data buffer, the
light-region info buffer) is therefore built on the **CPU**, not by a GPU pass we can read.

---

## 1. The light-region system

### 1.1 What it is

A **map-sized, top-down, world-XZ-indexed texture set** that assigns each ground texel up to **four
weighted light-region IDs**, which environment and character pixel shaders use to blend a per-region
**ambient/sun colour** and a per-region **IBL cubemap index**. It is *not* a point-light system and has no
positional falloff in 3D — the regions are 2D convex/concave polygons in the XZ plane with a soft edge.

Three shaders implement it, all single-permutation:

| Path | Blob | Role |
|---|---|---|
| `assets/shaders/hlsl/lighting/lightregions.ps-dx11` | 0 | rasterises N polygons into (weights, ids, priorities) |
| `assets/shaders/hlsl/lighting/lightregiontextureupdate.vs-dx11` | 0 | full-screen quad |
| `assets/shaders/hlsl/lighting/lightregiontextureupdate.ps-dx11` | 0 | merges a newly-rasterised 4-channel update into the existing set |

### 1.2 `lightregions.ps` — the polygon rasteriser

Reflection (`hdump refl .../lightregions.ps-dx11 0`):

```
inputs   v0 SV_Position0 (xy used)
outputs  o0 SV_Target0 float4   o1 SV_Target1 uint4   o2 SV_Target2 uint4
t0       PolygonData__TX          Texture2D<float4>, used purely as a linear data buffer (ld, no sampler)
cb0      $Globals  32 B
           +0  NumPolygons   float   USED
           +16 PixelToWorld  float4  USED
```

Per pixel (line 70):

```hlsl
// NOTE the xy swap: world.x comes from SV_Position.y
float2 P;
P.x = SV_Position.y * PixelToWorld.y + PixelToWorld.w;
P.y = SV_Position.x * PixelToWorld.x + PixelToWorld.z;
```

`PolygonData__TX` is addressed as a 1-D array of float4 texels via `ld(int2(i,0))`. **Record layout,
stride = `vertexCount + 2` texels** (the skip path at lines 91-96 advances by exactly that):

| texel | contents | evidence |
|---|---|---|
| base+0 | AABB `(minA, minB, maxA, maxB)` in the same swapped axes as `P` | lines 83, 86-89 (four `lt` rejects) |
| base+1 | `(vertexCount, strength, invRadius, regionIndex)` | line 84 `ld_aoffimmi(1,0,0)`; `ftoi`→count L85; `strength` L148; `invRadius` L144; `ftou(regionIndex)` L205 |
| base+2 | `(v0.x, v0.y, falloffPower, priority)` | line 99; `falloffPower` used as the `log/mul/exp` exponent L146; `ftou(priority)` L143 |
| base+2+i | `(vi.x, vi.y, –, –)` for i = 1..count-1 | line 136 `ld ... t0.zwxy` reads only .xy |

Inner loop (lines 111-140) walks the closed edge list once, accumulating **both**:

```hlsl
// per edge (cur -> prev):
float2 e  = prev - cur;
float2 tp = P    - cur;
float  t  = saturate(dot(tp, e) / dot(e, e));   // div_sat, line 118
float2 d  = tp - e * t;                          // line 119
minDistSq = min(minDistSq, dot(d, d));           // line 121

// classic even-odd crossing test, lines 122-134
sign = crossed ? -sign : sign;                   // starts at +1, becomes -1 INSIDE
```

Then the weight (lines 141-151), which is the whole point of the shader:

```hlsl
float d      = sqrt(minDistSq) * sign;              // negative inside
float t      = saturate(-d * invRadius);            // 0 outside, ramps to 1 at `radius` inside the edge
float f      = pow(t, falloffPower);                // log/mul/exp, lines 145-147
float weight = strength * f;
if (1.0 - weight < 0.001961)  weight = 1.0;         // 1/510 snap, lines 149-151
```

Priority handling (lines 152-249), in order:

1. If `weight >= 1.0` **and** `priority >= runningMaxPriority`: every already-stored slot whose priority is
   lower is **compacted out and zero-filled** (lines 158-184), and `runningMaxPriority = priority`. A
   fully-opaque high-priority region therefore *occludes* everything below it rather than blending with it.
2. If `priority < runningMaxPriority`: the polygon is skipped entirely (lines 195-202).
3. Otherwise the (weight, id, priority) triple is inserted into a **top-4 list kept in descending weight
   order**, shifting the tail down and dropping the 4th (lines 203-246).

Outputs (lines 251-262 and the tail): `o0 = float4 weights`, `o1 = uint4 region IDs`, `o2 = uint4
priorities`. If the four weights sum to more than 1 the tail renormalises them (same `sum > 1` branch as
the update shader below, lines 263-269 onward).

### 1.3 `lightregiontextureupdate` — the incremental merge

VS (lines 24-26) is a plain full-screen quad: `clip.xy = pos.xy*2-1`, `uv = (pos.x, 1-pos.y)`.

PS reflection:

```
outputs  o0 float4 (weights)   o1 uint4 (ids)   o2 uint4 (priorities)
t0  LIGHT_REGION_TEXTURE_UPDATE_SharedTexture  float4   <- 4 newly-rasterised weights, one per channel
t1  WeightsSampler__TX                         float4   <- existing weights
t2  IDsSampler__TX                             uint4    <- existing ids
t3  PrioritiesSampler__TX                      uint4    <- existing priorities
cb0 $Globals 48 B: RenderDataIndexes float4, RenderDataPriorities float4, TextureResolution float2
cb1 MantisSharedMapParameters 4128 B: LIGHT_REGION_TEXTURE_SIZE float2 (USED), + 4 unused
```

Addressing (lines 80-89): the existing set is loaded at `uv * LIGHT_REGION_TEXTURE_SIZE`, the update
texture at `uv * TextureResolution` — **the two can be different resolutions**.

Semantics: the update texture's **R,G,B,A channels are four independent regions rasterised in one pass**,
whose IDs and priorities are *uniform for the whole pass* and come from `RenderDataIndexes` /
`RenderDataPriorities` (lines 90-91, `ftou` of float4s). The shader then:

* sorts the four incoming tuples by (weight, priority) — lines 92-145;
* finds the highest priority among all candidates with `weight >= 1` and zeroes everything below it —
  lines 147-223 (`dp4 r,icb[i]` against an identity immediate CB is component selection: `icb[i]` picks
  lane `i`);
* 4-way merges the two sorted lists — lines 271-436;
* if the merged weights sum to > 1, runs a water-filling loop that reduces them until they sum to 1 and
  coalesces duplicate IDs — lines 437-587;
* writes the surviving top-4 to o0/o1/o2.

### 1.4 How a shading pixel consumes it — `Mantis_Env_Baked_PBR`, pixel, blob 27

Blob 27 re-derived, not assumed: `hdump scan .../mantis_env_baked_pbr.ps-dx11 CLUSTER` reports 30 distinct
blobs, of which 18-29 bind `CLUSTER_MAP_SharedTexture` + `CLUSTER_DATA_BUFFER_SharedDataBuffer` +
`ClusterData`; **blob 27** is the full-quality one (`USE_DYNAMIC_LIGHTING=1, USE_VOID=1`, no
`DISABLE_SHADOWS`, no `LOW_QUALITY_MODE`, no `CLOUD_SHADOWS`, no `GENERATE_SHADOW_MAP`), 43,076 B.

```hlsl
// UV: the map-wide top-down terrain transform, line 300
float2 rUV = worldXZ * TERRAIN_XFORM.xy + TERRAIN_XFORM.zw;

float4 w  = DYNAMIC_ENV_LIGHT_FACTOR_SharedTexture.Sample(Clamp_No_Mip, rUV);          // line 478
uint4  id = DYNAMIC_ENV_LIGHT_IDS_SharedTexture.Load(int3(saturate(rUV) *
                                                     LIGHT_REGION_TEXTURE_SIZE, 0));   // lines 479-483

float sum = w.x + w.y + w.z + w.w;                    // lines 484-486
if (sum > 1.0) w /= sum;                              // lines 487-489
float wDefault = 1.0 - saturate(sum);                 // lines 490-491  -> region 0 takes the remainder

// per slot k in {x,y,z,w} plus the default slot with index 0:
LightRegionInfo I = LightRegionInfo_SharedDataBuffer[id[k]];   // stride 112, offset 0, lines 492/500/510/518/525
float3 cube_diff  = IBL_CUBEMAP.SampleLevel(s4, float4(N,   I.probeIndex), 5.0)       * IBL_CUBEMAP_SCALES[I.probeIndex].x;
float3 cube_spec  = IBL_CUBEMAP.SampleLevel(s4, float4(R,   I.probeIndex), rough*4.0) * IBL_CUBEMAP_SCALES[I.probeIndex].x;
// accumulate all three by weight, lines 532-543
colour   += I.colour   * w[k];
diffIBL  += cube_diff  * w[k];
specIBL  += cube_spec  * w[k];
```

**`LightRegionInfo_SharedDataBuffer` field map (stride 112 B).** Offsets 0/16/32 are read; the rest is
never touched by the two shaders disassembled. Offsets 0-12 come from Mantis blob 27; offsets 16-32 from
`assets/shaders/hlsl/skinnedmesh/lit_uber_ps.ps-dx11` blob 400 (`FORCE_MANTIS_LIGHTING=1`), lines 298-350,
which issues `ld_structured ... l(16)` and `l(32)` alongside `l(0)`:

| offset | type | shader use | matching `LightRegionRenderData` property |
|---|---|---|---|
| +0 | float3 | env sun radiance — **sun only, not ambient** (see M463 note below) | `SunLightColor` (Vec3, default 1,1,1) |
| +12 | uint | env IBL cubemap array index → `IBL_CUBEMAP_SCALES[i].x` | `ProbeIndex` (U32) |
| +16 | float3 | character sun colour (`lit_uber`) | `CharacterSunLightColor` |
| +28 | uint | character IBL cubemap index (`lit_uber` line 299-303) | `CharacterProbeIndex` (U32) |
| +32 | float3 | character sun **direction**, normalised at `lit_uber` lines 369-371 | `CharacterSunLightDirection` |
| +48..+111 | 64 B | **never read** by either shader | fog fields (`DepthFog*`, `HeightFog*`), `priority`, `0x2119af58` Vec3 |

**M463 correction — the struct is DECLARED in the bytecode, names and all.** Blob 27's own resource-bind
comment block (`hdump dis` output lines 85-110) carries the full HLSL declaration, so the offsets above are
no longer a reconstruction and the "never read" tail is no longer unnamed:

```hlsl
struct LightRegionRenderData          // stride 112
{
    float3 SunLightColor;             //   0        float3 CharacterSunLightDirection; //  32
    uint   ProbeIndex;                //  12        uint   Priority;                   //  44
    float3 CharacterSunLightColor;    //  16        float3 DepthFogColor;              //  48
    uint   CharacterProbeIndex;       //  28        float  DepthFogMaxIntensity;       //  60
    float3 HeightFogColor;            //  64        float  DepthFogStart;              //  80
    float  HeightFogMaxIntensity;     //  76        float  DepthFogEnd;                //  84
    float  HeightFogStart;            //  88        float3 ReflectionSkyTint;          //  96
    float  HeightFogEnd;              //  92        float  mUnusedPadding0;            // 108
}
```

**And +0 is SUN RADIANCE ONLY — the "sun-and-ambient" label above was too loose.** Re-traced in blob 27:
the weighted region colour accumulates into `r10` (lines 532, 535, 538, 541), and `r10` has exactly ONE
consumer — the `movc` at line 545 that picks between it and white on the RMA sentinel. Line 550 then
overwrites `r10`. The value flows 545 → 548 (`* (1 - CLOUD_CARDS)`) → 549 (`* sunShadow`) → 634
(`* BRDF * NdotL`) and nowhere else. **Mantis's AMBIENT is a separate quantity**: the two
`texturecubearray` samples at lines 494/498, each scaled by `IBL_CUBEMAP_SCALES[probeIndex].x` at lines
495/499 and accumulated by the same weights. Anything wiring a map's *sky* colour must therefore target the
IBL pair, not this field.

### 1.5 Cross-check against the bin schema — it matches exactly

From `data/meta/meta.db.json`:

`LightRegionGeComponentDef` (`0x972a7491`) → the per-polygon record in `PolygonData__TX`:

| bin property | type / default | shader field |
|---|---|---|
| `Polygon` | `List2<Vec3>`, default a 4-vertex quad | the vertex texels at base+2.. |
| `strength` | F32, 1.0 | base+1 `.y` |
| `radius` | F32, 0.0 | base+1 `.z` is its **reciprocal** |
| `FalloffPower` | F32, 1.0 | base+2 `.z`, the `pow` exponent |
| `PriorityOverride` | I8, −1 | base+2 `.w`, `ftou` |
| `parameters` | `Link → LightRegionRenderData` | the index into `LightRegionInfo_SharedDataBuffer` (base+1 `.w`) |

`MapLightRegions` (`0xfa8824c0`) → `TextureWidth`/`TextureHeight` U16 default **1024** = the
`LIGHT_REGION_TEXTURE_SIZE` float2; `DefaultRenderData` (Embed of `LightRegionRenderData`) = the slot the
`1 - saturate(sum)` remainder weight is applied to, i.e. **element 0 of the structured buffer**;
`TextureRenderDataList` (`List2<Embed<LightRegionTextureData>>`) = one entry per update pass.

`LightRegionTextureData` (`0xfadcd386`) → exactly `lightregiontextureupdate.ps`'s CB: `Red`/`Green`/`Blue`/
`Alpha` links to `LightRegionRenderData` = `RenderDataIndexes.xyzw`; the four unnamed `I8` (default −1)
= `RenderDataPriorities.xyzw`; `SharedTextureName` names the texture bound as
`LIGHT_REGION_TEXTURE_UPDATE_SharedTexture`.

### 1.6 Authored-vs-shipped status — **measured, and it is zero**

`disasm lrscan Map` raw-scans every `.bin` in every `*Map*.wad.client` for the meta class hashes:

```
# scanned 18 wads, 12,747 .bin entries, 0 failures
   0xfa8824c0 MapLightRegions                  0 bins
   0x3069f601 LightRegionRenderData            0 bins
   0xfadcd386 LightRegionTextureData           0 bins
   0x972a7491 LightRegionGeComponentDef        0 bins
   0x853d9e9a LightRegionGeComponent           0 bins
```

**Nothing on Live authors a light region.** Everything in §1.2-§1.5 is evidenced by shader code and the
meta schema; **nothing** in it is evidenced by shipped content. In practice this means every Mantis pixel
samples `DYNAMIC_ENV_LIGHT_FACTOR` and gets zero, so `wDefault = 1` and the whole system collapses to
`LightRegionInfo[0]` — the `DefaultRenderData`. **For a ReyEngine viewport, "light regions" reduce to one
global ambient colour + one global IBL cubemap index.**

### 1.7 The consequence nobody expects: Mantis never reads `SUN_LIGHT_COLOR`

`hdump refl` marks `PerFramePixelCB.SUN_LIGHT_COLOR` (offset 96) **unused** in Mantis blob 27, and
`grep -c "cb1\[6\]"` over the listing returns **0**. Also unused: `SHADOW_COLOR`, `SHADOW_COLOR_COMPLEMENT`,
`LIGHT_MAP_COLOR_SCALE_AND_INTENSITY`.

Mantis's sun radiance is the **light-region colour** instead (lines 546-549, 634):

```hlsl
float3 sunRadiance = regionColour * (1.0 - CLOUD_CARDS.Sample(uv).rgb) * sunShadowPCF;
if (roughness < 0) sunRadiance = float3(1,1,1);        // RMA sentinel, line 545
...
lit += sunRadiance * BRDF(N, V, SUN_LIGHT_DIRECTION) * NdotL_sun;   // lines 633-635
```

`DefaultEnv_Flat` — the shader almost every shipped map material actually uses — **does** read
`SUN_LIGHT_COLOR` and `LIGHT_MAP_COLOR_SCALE_AND_INTENSITY`. Its sun/baked equation, blob 226 lines
163-205, is:

```hlsl
float shadow = (5 PCF taps at SHADOW_SAMPLE_OFFSETS) * 0.2;               // lines 179-197
float  bias  = SLOPE_SCALED_DEPTH_BIAS * (1 - dot(Ngeo, normalize(SUN_LIGHT_DIRECTION)))
             + CONSTANT_DEPTH_BIAS;                                       // lines 172-177
                                                                          // Ngeo = ddx/ddy of world pos, lines 163-171
float4 baked = BAKED_LIGHT__TX.Sample(TEXCOORD1.zw * BAKED_LIGHT_SCALE_AND_BIAS.xy
                                                   + BAKED_LIGHT_SCALE_AND_BIAS.zw);   // lines 198-199
shadow = min(shadow, baked.w);                                            // line 200  <- baked shadow mask
float NdotL = max(dot(N, normalize(SUN_LIGHT_DIRECTION)), 0);             // lines 202-203
float3 light = baked.rgb * LIGHT_MAP_COLOR_SCALE_AND_INTENSITY            // line 204
             + NdotL * shadow * SUN_LIGHT_COLOR.rgb;                      // lines 201, 205
```

**Two different sun models ship in the same frame.** Anything that "fixes the sun" has to be told which
family it is fixing.

---

## 2. The clustered forward path (point and spot lights)

This is the same code in `Mantis_Env_Baked_PBR` (blob 27), `DefaultEnv_Flat` (blob 226) and
`skinnedmesh/lit_uber_ps` (blob 504/945) — the only difference is the BRDF each plugs the light into.
The `USE_DYNAMIC_LIGHTING` axis is what turns it on, and it exists on **all three** families.

### 2.1 Addressing a cluster (Mantis blob 27, lines 636-644)

```hlsl
cbuffer ClusterData : b4 {          // 80 B
  float4x4 WORLD_TO_CLUSTER_TRANSFORM;   // +0
  float3   CLUSTER_MAX_CLAMP;            // +64
};
Texture3D<uint> CLUSTER_MAP_SharedTexture;                     // t10, NO mips (flags 0x0)
StructuredBuffer<uint4> CLUSTER_DATA_BUFFER_SharedDataBuffer;  // t14, stride 16

float4 P4 = float4(worldPos, 1);                    // r7; .w set to 1 at line 453, xyz at 469-470
float3 c  = float3(dot(P4, WORLD_TO_CLUSTER_TRANSFORM[0]),
                   dot(P4, WORLD_TO_CLUSTER_TRANSFORM[1]),
                   dot(P4, WORLD_TO_CLUSTER_TRANSFORM[2]));
c = clamp(c, 0, CLUSTER_MAX_CLAMP);                 // lines 639-640
uint  clusterIdx = CLUSTER_MAP.Load(int4((int3)c, 0));         // lines 641-643
uint4 header     = CLUSTER_DATA_BUFFER[clusterIdx];            // line 644
```

It is a **world-space uniform grid**, not a camera froxel volume: an affine world→grid transform, integer
truncation, clamp, one `Load`. `worldPos` here is `(TEXCOORD3.x, TEXCOORD0.w, TEXCOORD3.y)`.

### 2.2 The cluster header — eight uint16s in one uint4 (lines 645-661)

```
header.x & 0xFFFF   count of PLAIN POINT lights                (loop 1, 2 words each)
header.x >> 16      count of POINT + stationary-mask lights    (loop 2, 3 words each)
header.z & 0xFFFF   count of POINT + cube-shadow lights        (loop 3, 3 words each)
header.y & 0xFFFF   count of SPOT + cookie lights              (loop 4, 7 words each)
header.y >> 16      count of SPOT + cookie + stationary-mask   (loop 5, 8 words each)
header.z >> 16      count of SPOT + cookie + PCF-shadow lights (loop 6, 8 words each)
header.w & 0xFFFF   the CLUSTER's aggregate visibility mask
header.w >> 16      number of uint4s occupied by the per-light mask list
```

Immediately after the header, at `clusterIdx + 1`, comes a **packed list of 16-bit per-light visibility
masks, 8 per uint4**, in the same order as the six loops (walked by the `movc`/`ushr`/`and 0xFFFF` dance at
lines 665-676, with an 8-step countdown seeded at line 655). The light **payload** records start at
`clusterIdx + 1 + (header.w >> 16)` (line 654) and are one contiguous stream; each loop advances the same
cursor by its own stride (lines 746, 845, 951, 1081, 1218 → 2, 3, 3, 7, 8).

### 2.3 The visibility mask — a two-byte AND, both halves must hit

Applied first to the cluster (lines 645-649), then per light (lines 676-679):

```hlsl
uint objMaskLo = ENV_LIGHTING_MASK & 0x00FF;     // ENV_LIGHTING_MASK is a per-object $Globals uint, b0+0
uint objMaskHi = ENV_LIGHTING_MASK & 0xFF00;
bool visible = ((lightMask16 & objMaskLo) != 0) && ((lightMask16 & objMaskHi) != 0);
```

A light is skipped when either byte fails to overlap. Two independent 8-bit channels — most plausibly
layer and team/mode, but that reading is **not measured**.

### 2.4 THE POINT LIGHT — record layout and exact math

Loop 1, Mantis blob 27, lines 689-741. This is the thing our hand-written approximation was standing in for.

**Record: 2 × uint4 = 32 bytes.**

| word | field | evidence |
|---|---|---|
| +0 | `float3 position; float invRadius;` | L689-693: `L = w0.xyz - P`, `saturate(w0.w * dist)` |
| +1 | `float3 colour; float intensity;` | L698-701: `colour * atten`, then `* w1.w` |

```hlsl
float3 L    = light.position - P;
float  dist = length(L);                                  // dp3 + sqrt, lines 691-692
float  atten = 1.0 - saturate(dist * light.invRadius);    // lines 693-694   <- LINEAR. no inverse square.
if (atten <= 0) continue;                                 // line 695

float3 Ldir = L / dist;                                   // line 699
float3 radiance = light.colour * atten * light.intensity; // lines 700-701
float  NdotL = max(dot(N, Ldir), 0);                      // lines 702, 708
float3 H = normalize(V + Ldir);                           // lines 703-706
float  NdotH = max(dot(N, H), 0);                         // lines 707-708

// --- GGX / Smith-Schlick / Schlick, exactly UE4's formulation ---
// pre-computed once for the whole shader, lines 585-608:
//   rough = clamp(rma.roughness, 0.04, 1.0)   (line 586 substitutes 0.075 when the RMA channel is < 0)
//   a     = rough*rough                        (line 590)   a2 = a*a  (line 591)
//   k     = (rough+1)^2 / 8                    (lines 600-602)
//   G_V   = NdotV / (NdotV*(1-k) + k)          (lines 604-605)
//   nv4   = NdotV * 4.0                        (line 570)
float D = a2 / (PI * pow(NdotH*NdotH*(a2-1) + 1, 2));     // lines 709-713
float G = G_V * (NdotL / (NdotL*(1-k) + k));              // lines 714-716
float3 F = F0 + (1-F0) * pow(1 - max(dot(H,V),0), 5);     // lines 717-724
float3 spec = F * D * G / (nv4 * NdotL + 1e-4);           // lines 727-730
float3 kD   = albedo * (1 - F) * oneMinusMetal;           // lines 725-726, 731 (oneMinusMetal = r1.x)
float3 brdf = kD * (1/PI) + spec;                         // line 739, literal 0.318310
out += radiance * brdf * NdotL;                           // lines 740-741
```

Two variants of the specular term exist behind the same `roughness < 0` sentinel that forces
`sunRadiance = white` (lines 732-738): `spec = pow(spec, 0.8) + (spec >= 0.1 ? 0.5 : 0)`, a hard-stepped
stylised highlight. It applies identically in all six loops.

**The Lambert family differs.** `DefaultEnv_Flat` blob 226 loop 1 (lines 261-276) is:

```hlsl
float3 L = w0.xyz - P;  float dist = length(L);  float3 Ldir = L / dist;
float atten = 1.0 - saturate(dist * w0.w);
if (atten > 0)
    out += w1.xyz * albedo * max(dot(Ldir, N), 0) * atten;   // NO 1/PI, NO specular
```

It loads word 1 as `.xyz` only (`t6.xyzx`, line 263) — **`intensity` (word1.w) is read by the PBR family
and ignored by the Lambert family**, in loop 1 and in the shadowed loop (line 390) alike.

### 2.5 The other five light types

**Loop 2 — point + stationary mask.** 3 words; word2.xyz is a **channel selector** dotted with a baked
lightmap-space texture (lines 750-751, 787-788):

```hlsl
float2 slUV = TEXCOORD2.zw * STATIONARY_LIGHT_SCALE_AND_BIAS.xy + STATIONARY_LIGHT_SCALE_AND_BIAS.zw;
float3 SL   = STATIONARY_LIGHT__TX.Sample(s9, slUV).rgb;       // hoisted out of the loop
float  mask = dot(light.w2.xyz, SL);                            // per light
float  atten = mask * (1 - saturate(dist * invRadius));
```

So `STATIONARY_LIGHT__TX` is a **baked static shadow/visibility mask for up to 3 stationary lights per
texel**, each light picking its channel by a float3 dot. `STATIONARY_LIGHT_SCALE_AND_BIAS` lives in the
per-object `$Globals` (b0 +48).

**Loop 3 — point + cube shadow.** 3 words; word2 = `(A, B, –, –)` forming the cube-face depth reference
(lines 895-906):

```hlsl
float maxAxis = max(max(abs(L.x), abs(L.y)), abs(L.z));         // chebyshev, lines 899-900
float ref     = saturate(w2.x - w2.y / maxAxis - 1e-4);         // lines 901-903
float shadow  = POINT_SHADOW_MAP_DEPTH_PCF_SharedTexture.SampleCmpLevelZero(s7, -Ldir, ref);  // L905
atten *= shadow;                                                // line 906
```

`(A, B)` is the standard perspective depth pair `A = f/(f−n)`, `B = n·f/(f−n)`. Single tap, no PCF kernel.

**Loop 4 — spot + cookie.** 7 words (lines 989-1078):

```
+0..+3  the four COLUMNS of a world->light projection matrix, gathered per component (lines 994-1012)
+4      float3 colour; float intensity
+5      float3 position; float invRadius
+6      float4 cookie UV scale.xy / bias.zw
```

```hlsl
float4 lp = mul(float4(P,1), M);  float3 uvw = lp.xyz / lp.w;      // lines 998-1013
bool inCone = (uvw.z <= 1.0) && all(uvw.xy == saturate(uvw.xy));   // lines 1020-1027
float2 cUV  = saturate(uvw.xy) * w6.xy + w6.zw;                    // line 1033
float3 cookie = SPOT_PROJECTED_TEXTURE.SampleLevel(s5, cUV, 0).rgb;// line 1034
float3 radiance = cookie * w4.rgb * atten * w4.w;                  // lines 1035-1037
```

**Loop 5 — spot + cookie + stationary mask.** 8 words: loop 4's layout plus `+7 = float3 channel selector`
dotted with the `STATIONARY_LIGHT` sample (lines 1157-1160).

**Loop 6 — spot + cookie + PCF shadow.** 8 words: loop 4's layout plus `+7 = float4 shadow-map UV
scale.xy / bias.zw`. **5-tap PCF** using `SPOT_SHADOW_SAMPLE_OFFSETS` (`PerFramePixelCB` +336, read into
two offset vectors at lines 1228-1231) with a −0.0005 depth bias (lines 1311-1330):

```hlsl
float3 b = uvw - float3(0.0005, 0, 0.0005);  b.z = saturate(b.z);
float2 sUV = b.xz * w7.xy + w7.zw;
float s = T.SampleCmpLevelZero(s6, sUV, b.y)
        + T.SampleCmpLevelZero(s6, sUV + O1.xy, b.y + O1.z)
        + T.SampleCmpLevelZero(s6, sUV - O1.xy, b.y - O1.z)
        + T.SampleCmpLevelZero(s6, sUV + O2.xy, b.y + O2.z)
        + T.SampleCmpLevelZero(s6, sUV - O2.xy, b.y - O2.z);
atten *= s * 0.2;
```

### 2.6 Where the cluster grid is built — nowhere we can read

`assets/shaders/hlsl/renderer/` (7 stage TOCs: `ps_worldvertex`, `ps_worldvertex_greyscale`, four
`vs_screenvertex*` variants and `vs_simpleworldvertex`) contains **no** cluster or light-binning shader.
Combined with the whole-cache fact that **zero compute shaders exist**, the `CLUSTER_MAP` 3D texture, the
`CLUSTER_DATA_BUFFER` stream and the light-mask lists are **built on the CPU each frame** and uploaded.
ReyEngine must therefore author that data itself; there is no Riot shader to copy for it.

---

## 3. Decals and alpha

### 3.1 `decal/decal.ps` — the trivial one

880 B, one permutation. Entire body (lines 46-48):

```hlsl
o0 = Texture__TX.Sample(Texture__SMP, uv) * Color;
```

One float4 `Color` constant, no lighting, no discard, one output. Its VS (`decal.vs`, lines 74-81) is
`WORLD_MATRIX` then `VIEW_PROJECTION_MATRIX`. This is an editor/debug decal.

### 3.2 `environment/unlit_decal` — the real projected decal, and it is genuinely unlit

512 permutations over 128 blobs, 9 axes: `DISABLE_SHADOWS`, `DISABLE_FOW`, `MASKED`, `LOW_QUALITY_MODE`,
`COLORPALETTE_COLORBLIND`, `PALETTIZE_TEXTURES`, `ALPHA_EROSION`, `ALPHA_TEST`, `MULT_PASS`.

**`DISABLE_SHADOWS=1` and `LOW_QUALITY_MODE=1` map to the SAME blob index as their absence** (blob 1 serves
`[DISABLE_FOW]`, `[DISABLE_SHADOWS, DISABLE_FOW]`, `[DISABLE_FOW, LOW_QUALITY_MODE]` and
`[DISABLE_SHADOWS, DISABLE_FOW, LOW_QUALITY_MODE]`). Byte-identical code ⇒ **shadows and quality have no
effect on this shader at all.** Reflection agrees: no shadow map, no cluster texture, no light-region
texture, no `SUN_*` constant is bound in any of them.

Base permutation, blob 6 (lines 57-64) — the whole shader:

```hlsl
float4 c = DIFFUSE_MAP__TX.Sample(s0, v1.xy)
         * PARTICLE_COLOR_TEXTURE__TX.Sample(s1, COLOR_UV.xy)
         * MODULATE_COLOR;
o0.w   = c.a * max(v1.z, 0);                              // v1.z = the VS fade factor
o0.rgb = c.rgb * FOW_MAP_SharedTexture.Sample(s15, v2.xy).r;
```

The VS (`unlit_decal_vs`, blob 0, lines 72-90) projects through
`ParticleDecalVS { DECAL_WORLD_MATRIX, DECAL_WORLD_TO_UV_MATRIX, DECAL_PROJECTION_Y_RANGE }` and computes
the fade as a vertical band:

```hlsl
float d = abs(worldY - RANGE.x);
o1.z = (d <= RANGE.y) ? 1.0 : 1.0 - (d - RANGE.y) / RANGE.z;
```

`ALPHA_TEST` (blob 14, lines 63-67) adds `if (alpha - cAlphaTestValue < 0) discard;` **after** writing
`o0.w = alpha` — the discard is on the *faded* alpha, and the shader still emits the alpha it would have.

**`o0.rgb` is never multiplied by `o0.a`. The output is STRAIGHT alpha, not premultiplied.**

### 3.3 What the *environment* shaders do with alpha

`DefaultEnv_Flat` blob 152 (base) line 181: `mov o0.w, r3.w` — alpha is the raw diffuse-texture alpha.
Line 190 writes `o0.rgb` as the fog-of-war lerp of the lit colour, with **no alpha multiply**. Blobs 153
(`FEATURE_MASKED`) and 154 (`FEATURE_MASKED + DISCARD_ALPHA_TEXELS`) do the same at line 192.

**`PREMULTIPLIED_ALPHA=1` maps to the same blob (48) as its absence.** Byte-identical ⇒ the define does
**nothing in the pixel shader**; it only selects a blend state CPU-side. Separately, every one of the 128
cooked `PREMULTIPLIED_ALPHA` permutations also carries `NO_BAKED_LIGHTING`, `DISABLE_SHADOWS`,
`DISABLE_FOW`, `DISABLE_DEPTH_FOG` and `LOW_QUALITY_MODE` — Riot never cooks a lit premultiplied variant.

`Mantis_Env_Baked_PBR` blob 27 line 1425: `mov o0.w, l(1.000000)` — Mantis is unconditionally opaque, and
gamma-encodes its colour with `pow(x, 1/2.2)` at lines 1392-1394 before writing.

### 3.4 The answer to the live bug

**Riot has no additive light pass.** Every dynamic light is evaluated *inside* the single forward pixel
shader that also computes the base colour, and the result leaves the shader as one colour that the material's
own blend state consumes exactly once. So:

* **An alpha-blended surface**: under `SrcAlpha / InvSrcAlpha`, its framebuffer contribution in Riot's
  single pass is `(base + Σlight) * a`. An overlay pass that draws only `Σlight` must therefore be blended
  as `SrcAlpha / One` — i.e. **the additive term must be scaled by the same surface alpha**, from the same
  texture channel, or the light lands at full strength on a 10%-opaque surface.
* **An alpha-tested surface**: the overlay pass **must run the identical `discard`**. Riot's `ALPHA_TEST`
  discards before anything reaches the framebuffer; an overlay that skips the test paints light onto texels
  the base pass killed.
* **A decal (`unlit_decal` and anything modelled on it)**: **exclude it from the light pass entirely.**
  Not "scale it down" — Riot's decal shader contains no lighting term of any kind, and the dedup evidence in
  §3.2 proves that is deliberate rather than an omission in one permutation.
* **A premultiplied / additive (`ONE, ONE`) surface**: exclude it too. Riot only ever cooks these unlit
  (§3.3), and adding `Σlight` to a layer that is already an additive contribution double-counts it.

---

## 4. The frame's lighting constants

### 4.1 `PerFramePixelCB` — 560 B, 35 variables, complete

Dumped from `Mantis_Env_Baked_PBR` blob 27; the USED column is the union over the three shaders measured
(M = Mantis blob 27, D = `DefaultEnv_Flat` blob 226, L = `lit_uber_ps` blob 400). Register `cN.c` is the
float4 slot and component the offset lands on.

| offset | reg | name | type | used by |
|---|---|---|---|---|
| 0 | c0.x | `vCamera` | float3 | M, L |
| 16 | c1.x | `TIME` | float4 | M |
| 32 | c2.x | `TERRAIN_XFORM` | float4 | M, L |
| 48 | c3.x | `SHADOW_COLOR` | float3 | — (used by the unlit `DefaultEnv_Flat` blob 48) |
| 64 | c4.x | `SHADOW_COLOR_COMPLEMENT` | float3 | — (ditto) |
| 80 | c5.x | `cDepthConversionParams` | float4 | — |
| 96 | c6.x | `SUN_LIGHT_COLOR` | float4 | **D only** |
| 112 | c7.x | `SUN_PENUMBRA_SATURATION` | float | L |
| 116 | c7.y | `SUN_LIGHT_DIRECTION` | float3 | M, D, L |
| 128 | c8.x | `LIGHT_MAP_COLOR_SCALE_AND_INTENSITY` | float | **D only** |
| 132 | c8.y | `ENV_FOG_COLOR` | float3 | M, D |
| 144 | c9.x | `ENV_FOG_ALT_COLOR` | float3 | M, D |
| 160 | c10.x | `ENV_FOG_START_END_SCALE_EMISSIVE_REMAP` | float4 | M, D |
| 176 | c11.x | `FOG_OVERLAY_UV_ANIMATE` | float4 | — |
| 192 | c12.x | `FOW_EDGE_CONTROL` | float4 | — |
| 208 | c13.x | `SUN_LIGHT_DIRECTION_FOR_SPEC` | float3 | — |
| 224 | c14.x | `SHADOW_SAMPLE_OFFSETS` | float4 | M, D, L |
| 240 | c15.x | `LIGHT_GRID_WORLD_TO_GRID` | float4 | L |
| 256 | c16.x | `LIGHT_GRID_TEXTURE_SCALE` | float | — |
| 260 | c16.y | `GRASS_INTERP` | float | — |
| 264 | c16.z | `ENV_BRIGHTNESS` | float | — |
| 272 | c17.x | `mView` | float4x4 | — |
| 336 | c21.x | `SPOT_SHADOW_SAMPLE_OFFSETS` | float4 | M, D |
| 352 | c22.x | `HDR_ENV_DIFFUSE_SCALE` | float | — |
| 368 | c23.x | `NAV_GRID_XFORM` | float4 | — |
| 384 | c24.x | `IBL_CUBEMAP_INDEX` | float | — |
| 400 | c25.x | `UI_FRAMEBUFFER_COPY_SIZE` | float4 | — |
| 416 | c26.x | `mViewInv` | float4x4 | — |
| 480 | c30.x | `ENV_QUALITY` | uint | — |
| 484 | c30.y | `CONSTANT_DEPTH_BIAS` | float | M, D |
| 488 | c30.z | `SLOPE_SCALED_DEPTH_BIAS` | float | M, D |
| 496 | c31.x | `WATER_DISTURBANCE_XFORM` | float4 | — |
| 512 | c32.x | `RIM_LIGHT_DIR` | float3 | — |
| 528 | c33.x | `RIM_LIGHT_COLOR` | float4 | — |
| 544 | c34.x | `RIM_LIGHT_CONTRAST` | float | — |

An "unused" mark is per-permutation. `HDR_ENV_DIFFUSE_SCALE`, `ENV_BRIGHTNESS`, `IBL_CUBEMAP_INDEX`,
`RIM_LIGHT_*` and `LIGHT_GRID_*` are unread by the three permutations dumped here and may well be read by
others; do not treat the dashes as "Riot never uses this".

### 4.2 The other lighting cbuffers

**`PerFrameVertexCB` — 560 B, 19 variables** (from `decal.vs` / `unlit_decal_vs`): `mProj`, `vCamera`,
`TIME`, `TERRAIN_XFORM`, `VIEW_PROJECTION_MATRIX` (+112), `mShadowProj` (+176), `SCREEN_MATRIX`,
`FOG_OF_WAR_PARAMS`, `FOG_OF_WAR_ALWAYS_BELOW_Y`, `FOW_HEIGHT_FADE`, `NAV_GRID_XFORM`,
`MANTIS_FORCE_DATA`, `mView`, `mViewInv`, `GLOBAL_ENVIRONMENT_VALUES`, `SUN_LIGHT_DIRECTION` (+528),
`NORMAL_OFFSET_BIAS` (+540), `DRAGON_TERRAIN`, `ENV_QUALITY`. **Per frame.**

**`ClusterData` — 80 B, b4 (Mantis) / b2 (`DefaultEnv_Flat`).** `WORLD_TO_CLUSTER_TRANSFORM` float4x4 +0,
`CLUSTER_MAX_CLAMP` float3 +64. **Per frame**, and **we would have to compute it ourselves** — it is
whatever affine map takes world space onto the grid we choose to bin lights into.

**`MantisSharedMapParameters` — 4128 B, per map.** `LIGHT_REGION_TEXTURE_SIZE` float2 +0,
`EnvEffectorInfo` float4[128] +16, `TEEMO_ACTIVE` float +2064, `DRAGON_TERRAIN` float +2068,
`WaterDisturbanceInfo` float4[128] +2080.

**`IBL_CUBEMAP_SCALES_BUFFER` — 512 B.** `IBL_CUBEMAP_SCALES` float4[32]; only `.x` of the indexed element
is read, as a linear scale on both cubemap samples. **Per map** (32 probes max).

**`$Globals` — per object / per draw.** Mantis: `ENV_LIGHTING_MASK` uint +0, `BAKED_PAINT_UV_SCALE_BIAS`
+16, `VoidTendrilNormalIntensity` +32, `VoidTendrilSoftness` +36, `switch_USE_VOID_PREVIEW_IN_MM` +40,
`STATIONARY_LIGHT_SCALE_AND_BIAS` float4 +48. `DefaultEnv_Flat`: `ENV_LIGHTING_MASK` +0,
`BAKED_LIGHT_SCALE_AND_BIAS` +16, `TintColor` +32, `STATIONARY_LIGHT_SCALE_AND_BIAS` +48.

### 4.3 What we would have to synthesise

| input | who owns it | note |
|---|---|---|
| `WORLD_TO_CLUSTER_TRANSFORM`, `CLUSTER_MAX_CLAMP` | us | no shader builds them; pick a grid, derive the affine map |
| `CLUSTER_MAP_SharedTexture` (3D uint) | us | one uint per cell = index into the data buffer |
| `CLUSTER_DATA_BUFFER` stream | us | header uint4 + packed uint16 mask list + typed light records, §2.2/§2.4 |
| `LightRegionInfo_SharedDataBuffer` | us | 112 B stride; only +0/+12/+16/+28/+32 are read (§1.4) |
| `DYNAMIC_ENV_LIGHT_FACTOR` / `_IDS` | us | zero-fill is a valid, and on Live the *only* correct, answer |
| spot `world→light` matrices | us | per light, packed as 4 columns in the record |
| point cube-shadow `(A,B)` pair | us | `A = f/(f−n)`, `B = n·f/(f−n)` |
| `STATIONARY_LIGHT__TX` | baked | a lightmap-UV RGB mask; nothing generates it at runtime |
| `SHADOW_SAMPLE_OFFSETS`, `SPOT_SHADOW_SAMPLE_OFFSETS` | us | 2 offset vectors each, `(du,dv,dz)` — the 5-tap kernel |
| `CONSTANT_DEPTH_BIAS`, `SLOPE_SCALED_DEPTH_BIAS` | us | see the bias formula in §1.7 |

---

## 5. GL translation feasibility (GLES 3.0 / ANGLE, no compute, no SSBO, ASCII-only source)

| subsystem | GLES 3.0? | how, or why not |
|---|---|---|
| Sun + baked lightmap (`DefaultEnv_Flat`, §1.7) | **yes, exactly** | plain texture sample + 5 `textureLod` compares; `sampler2DShadow` gives the `SampleCmpLevelZero` semantics natively |
| GGX/Smith-Schlick/Schlick BRDF (§2.4) | **yes, exactly** | arithmetic only |
| Point-light attenuation + N·L (§2.4) | **yes, exactly** | trivially portable; this is the highest-value port and it is free |
| Cluster addressing (§2.1) | **yes** | `texture3D` with `isampler3D`/`usampler3D` and `texelFetch` is core GLES 3.0 |
| `CLUSTER_DATA_BUFFER` (StructuredBuffer) | **yes, with a re-pack** | no SSBO in GLES 3.0; store as an RGBA32UI `usampler2D` (or `usamplerBuffer` where the ANGLE backend exposes it) and index with `texelFetch(ivec2(i % W, i / W))`. All accesses are `buffer[i]` and `buffer[i+k]` with 16-byte granularity, so a 1:1 re-pack works |
| The 16-bit mask walk (§2.2) | **yes** | GLES 3.0 has full integer ops incl. `>>`, `&`; the `movc` shuffle collapses to `((v[i>>1] >> ((i&1)*16)) & 0xFFFFu)` |
| Six light loops with dynamic trip counts | **yes, with a cap** | GLES 3.0 permits dynamic loops but drivers vary; bound each loop by a compile-time `MAX_*` and `break`. Riot's own shader is already 6 sequential dependent loops with a shared cursor, which is expensive — consider collapsing to loops 1 and 4 only |
| Point cube shadows | **yes** | `samplerCubeShadow` + `texture(sampler, vec4(dir, ref))` is core GLES 3.0 |
| Spot PCF shadows | **yes** | `sampler2DShadow`, 5 taps |
| `LightRegionInfo` structured buffer | **yes** | 112 B stride, only 5 fields read → a small RGBA32F texture, or (better) plain uniforms, since on Live there is exactly one element (§1.6) |
| `lightregions.ps` polygon rasteriser | **impractical, and unnecessary** | an unbounded nested loop over a texture-as-buffer with per-pixel polygon scan. Nothing on Live authors regions (§1.6). If we ever need it, rasterise the polygons on the CPU into the two textures — the GPU shader buys nothing at editor frame rates |
| `lightregiontextureupdate.ps` merge | **impractical, and unnecessary** | 649 instruction slots of sort/merge with two uint4 render targets. GLES 3.0 does support integer MRT, so it is not *impossible*; it is just not worth it for a system with zero shipped content |
| IBL cubemap array | **needs a fallback** | `samplerCubeArray` is **not** in GLES 3.0 (it is 3.2 / `EXT_texture_cube_map_array`). Either require the extension, or bind one cubemap and drop the array index. Since Live has a single region, dropping the index is lossless in practice |

**ASCII-only rule.** Nothing in the extracted math needs a non-ASCII character; the `π` and `·` in this
document must become `PI` and `dot()` in any emitted GLSL string. That rule has broken the GL viewport
before (see the `shader-strings-ascii-only` note).

**Honest summary for the plan:** the GL viewport *can* mirror the sun path, the BRDF, the point/spot loops,
the cluster lookup and both shadow kinds with no loss. It **cannot** mirror the light-region rasteriser or
its merge pass, and would need an extension for cubemap arrays — but neither matters on Live content.

---

## 6. UNKNOWN / NOT MEASURED

Listed with what evidence would settle each.

1. **What `word1.w` on a point light actually is.** The PBR family multiplies radiance by it; the Lambert
   family never reads it (§2.4). "Intensity" is the obvious reading but it is a guess. *Settle it:* find a
   shipped `MapDynamicPointLight`/`MapPointLight` bin instance and correlate its authored fields with the
   CPU-side packer — or capture a frame in RenderDoc and read the buffer.
2. **The two bytes of `ENV_LIGHTING_MASK`.** Measured: both halves must overlap. Not measured: what layer
   each byte means. *Settle it:* find the CPU-side writer, or diff the mask across objects in a capture.
3. **The exact bit-walk of the packed 16-bit mask list.** The reading in §2.2 ("mask *k* is the *k*-th
   uint16 after the header") is consistent with every instruction but was reconstructed through heavy
   register aliasing (`movc` on a rotating vector, lines 665-676). *Settle it:* a RenderDoc capture of the
   buffer with a known light count.
4. **Which of the six loops the six header fields belong to, if a cluster has zero of some types.** The
   payload cursor is shared and advances per *processed* light, and the skip path advances it too (lines
   681-687), so the ordering claim in §2.2 holds. It has not been checked against real data.
5. ~~**Whether `LightRegionInfo` +48..+111 is the fog block.**~~ **SETTLED (M463): it is.** Blob 27's
   resource-bind comment block declares the whole struct with field names — see the correction in §1.4.
   `Priority` is at +44, `DepthFog*` at +48..+60 and +80..+84, `HeightFog*` at +64..+76 and +88..+92,
   `ReflectionSkyTint` at +96, padding at +108. Still unmeasured, and the reason M463 leaves these bytes
   zero: whether `DepthFogStart`/`DepthFogEnd` use the same negative, reversed convention as
   `MapSunProperties.fogStartAndEnd`. Nothing ReyEngine renders reads them, so nothing can reveal it.
6. **Blend/depth/cull state for any of these shaders.** DXBC carries no state. All statements about
   blending in §3 are about what the *pixel shader emits*; the actual `D3D11_BLEND_DESC` comes from the
   material bin and was not read in this milestone.
7. **`GENERATE_SHADOW_MAP` blobs.** Skipped as stubs (548-692 B). They are presumably the depth-only pass;
   not disassembled.
8. **Coverage of `assets/shaders/hlsl/` — 139 stage TOCs, 10 disassembled (7%).** Disassembled:
   `lighting/` (all 3), `decal/` (both), `environment/unlit_decal_ps` (4 blobs) + `unlit_decal_vs`,
   `environment/skybox.ps`, `environment/reflectionsky.ps`, `environment/shadowmap.ps`,
   `skinnedmesh/lit_uber_ps` blob 400. Outside `hlsl/`, also disassembled:
   `generated/.../mantis_env_baked_pbr.ps` blobs 18+27 and `generated/.../defaultenv_flat.ps`
   blobs 48/152/153/154/226.
   **Enumerated only (name, axes, permutation counts — not decoded):** `ui/` 37 TOCs,
   `particlesystem/` 20, `skinnedmesh/` 15 (of which 1 decoded), `filters/` 16, `gamma/` 11,
   `renderer/` 7, `font/` 4, `fogofwar/` 3, `debug/` 2, `debugdrawnavgrid/` 2, `hud/` 2, `regions/` 2,
   `editor/` 1, `gameplaytexture/` 1. Rationale: `renderer/` and `regions/` were checked specifically
   because the task asked whether they build the cluster map (they do not — §2.6); the rest are
   post-process, UI and font paths with no bearing on map lighting. **`particlesystem/` (20 TOCs) is the
   one omission that could matter later** — VFX lighting was not examined at all.
9. **`environment/skybox.ps` / `reflectionsky.ps`** were reflected but not decoded. Noted for the record:
   `skybox.ps` reads `ENV_CUBE_SharedTexture` + `VirtualPositionAndCTerm`, `CameraPos`, `DepthFogInfo`,
   `HeightFogParams`, `HeightFogColor` — a **separate** fog model from `PerFramePixelCB.ENV_FOG_*`.
   `reflectionsky.ps` binds `SAMPLER_BACK_BUFFER_COPY`, both `DYNAMIC_ENV_LIGHT_*` textures and
   `LightRegionInfo` — i.e. **the sky reflection is also light-region aware**.
10. **PBE.** Everything here is Live (`C:\Riot Games\League of Legends\Game\DATA\FINAL`). No PBE diff was run.

---

## 7. Implications for ReyEngine

### 7.1 The DX11 path should delete the additive overlay

The overlay pass is structurally wrong, not mis-tuned. Riot evaluates every light **inside the same pixel
shader as the base colour** (§2), so the correct architecture is:

1. **Feed the real permutation.** Both `Mantis_Env_Baked_PBR` and `DefaultEnv_Flat` already ship a
   `USE_DYNAMIC_LIGHTING=1` permutation with the complete point/spot loop compiled in. Select it instead of
   the baseline blob and the whole light system arrives for free — no shader authoring at all.
2. **Build the three CPU-side structures** listed in §4.3: the world→cluster affine transform, the 3D uint
   cluster map, and the `CLUSTER_DATA_BUFFER` stream (header uint4 → packed uint16 masks → typed records).
   The record layouts are fully specified in §2.4/§2.5. Start with loop 1 only (2-word plain point lights):
   set `header.x & 0xFFFF` to the light count, all other counts to 0, `header.w` to
   `(maskWords << 16) | 0xFFFF`, and set every object's `ENV_LIGHTING_MASK` to `0xFFFF`.
3. **Upload `ENV_LIGHTING_MASK = 0xFFFF`** per object or every light is silently culled by §2.3 — this is
   the single easiest way to build the whole thing and see nothing.
4. **Only then** consider a fallback overlay for permutations that genuinely lack the axis.

If an overlay must survive as a stopgap, §3.4 gives the three rules: scale the additive term by surface
alpha, re-run the alpha test, and skip decals and premultiplied/additive materials outright.

### 7.2 What the GL path can and cannot mirror

**Can, at parity:** the sun + baked-lightmap equation (§1.7), the GGX BRDF, the linear point attenuation,
the cluster lookup, cube and 2D shadow taps. That is the entire *visible* lighting model for shipped maps.

**Cannot:** the light-region rasteriser and its merge pass (§5) — and it does not need to, because zero
shipped maps author regions (§1.6). The one real gap is `samplerCubeArray` for IBL, which needs
`EXT_texture_cube_map_array` or a single-cubemap fallback.

**Must re-pack:** `CLUSTER_DATA_BUFFER` and `LightRegionInfo` become integer textures; there are no SSBOs.

### 7.3 Three specific corrections to what we do today

* **Our point-light falloff should be `1 - saturate(dist / range)`, linear.** Not inverse-square, not
  smoothstep. If our current approximation is physical, it is wrong in exactly the way that makes League
  lights look unfamiliar.
* **`Mantis_Env_Baked_PBR` ignores `SUN_LIGHT_COLOR`** (§1.7). Any code that tunes map brightness by
  pushing that constant will move `DefaultEnv_Flat` materials and leave Mantis materials untouched — which
  looks like a "some meshes are dark" bug and is not one.
* **Albedo is gamma-decoded (`pow(2.2)`) on input and the result is gamma-encoded (`pow(1/2.2)`) on
  output** in Mantis (lines 431-433, 1392-1394). Lighting happens in linear space between those two. A
  viewport that lights in sRGB will be wrong by roughly that curve everywhere, in a way no per-light scale
  can correct.

---

## 8. Files produced (scratchpad, not committed)

`.../scratchpad/disasm/out454/` — `lightregions_ps.txt`, `lrtu_vs.txt`, `lrtu_ps.txt`, `mantis_dyn27.txt`,
`mantis_dyn18.txt`, `defflat_dyn226.txt`, `defflat_{48,152,153,154}.txt`, `defflat_perms.txt`,
`lituber400_mantis.txt`, `decal_ps.txt`, `decal_vs.txt`, `unlitdecal{6,3_multpass,14_alphatest,17_erosion}.txt`,
`unlitdecal_vs0.txt`, `skybox.ps.txt`, `reflectionsky.ps.txt`, `shadowmap.ps.txt`.
Probes: `HDump.cs` (modes `list`/`perms`/`scan`/`refl`/`dis`) and `LrScan.cs` (mode `lrscan`).
