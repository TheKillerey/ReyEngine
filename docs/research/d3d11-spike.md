# D3D11 spike — can we run Riot's own compiled shaders?

**Date:** 2026-07-26 · **Question:** is a Direct3D 11 backend viable with Silk.NET, and would it buy
enough fidelity to be worth a second render backend?

**Both halves answered, and they point in opposite directions.** Riot's compiled shaders *do* load and
run on a Silk.NET D3D11 device — but our existing GLSL reimplementation matches all **three** shaders
tested to within 8-bit quantisation, so the fidelity argument for the port does not survive.

> Round 1 validated alpha erosion. Round 2 added soft particles and palette, and turned up a genuine
> unrelated gap: the unconditional `PIXEL_COLOR_REMAP_RAMP` stage that ReyEngine does not implement.

---

## What was run

`quad_ps` permutation `{ALPHA_EROSION, DISABLE_FOW}` — 1,756 bytes of DXBC lifted straight out of
`ShaderCache.dx11.wad.client` — handed to `ID3D11Device::CreatePixelShader` with no translation, paired
with a purpose-written vertex shader matching its input signature, rendered to a 256×1 offscreen target
and read back. The erosion map was a red ramp so the sampled erosion value `E` equals the x coordinate
exactly, making every output pixel a directly predictable function of the authored parameters.

## Packages

| Package | Version | Note |
|---|---|---|
| `Silk.NET.Direct3D11` | 2.23.0 | same version line as the `Silk.NET.OpenGL` already referenced |
| `Silk.NET.Direct3D.Compilers` | 2.23.0 | **not** `Silk.NET.D3DCompiler` — that name does not exist |
| `Silk.NET.DXGI` | 2.23.0 | |

## The one real gotcha

`CreatePixelShader` first failed with `E_INVALIDARG` and no further diagnostics. The cause was not D3D,
the device (FL 11_0), or the shader:

> **The blob container's length prefix runs one byte longer than the DXBC itself** — 1,757 vs the 1,756
> the container header declares at offset 24. D3D rejects any bytecode whose buffer length disagrees with
> its own `totalSize`.

Trim to the declared size before handing it over:

```csharp
if (b.Length >= 28 && b[0]=='D' && b[1]=='X' && b[2]=='B' && b[3]=='C')
{
    int declared = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(24));
    if (declared > 0 && declared < b.Length) Array.Resize(ref b, declared);
}
```

Chunk parsing never noticed this because chunk offsets are absolute — which is why the earlier
disassembly work was unaffected. Anything that hands blobs to the API must trim.

## The shader's interface, as the device sees it

```
cbuffer $Globals  (slot 0, 32 bytes)
    +0   16  float4  cAlphaErosionParams
    +16  16  float4  cAlphaErosionTextureMixer
textures : PIXEL_COLOR_REMAP_RAMP_SharedTexture(0)  sAlphaErosionTexture__TX(1)  TEXTURE__TX(2)
samplers : sAlphaErosionTexture__SMP(0)  TEXTURE__SMP(1)  Clamp_No_Mip_SharedSampler(15)
inputs   : SV_Position(r0)  TEXCOORD0(r1.xyzw)  TEXCOORD1(r2.xy)  TEXCOORD2(r3.xy, unread)
           TEXCOORD3(r3.z)  <- the per-particle erosion drive
```

This independently confirms two decode claims from the shader-semantics workflow: the cbuffer layout, and
that the drive arrives per particle through an interpolant rather than a constant.

## Result — Riot's shader vs ReyEngine's GLSL

Six authored configurations × 256 erosion values. `profile` samples the alpha every 32 texels across
E = 0…1 (`#` > 0.75, `+` > 0.4, `.` > 0.05, blank ≈ 0):

| case | drive | max abs diff | alpha range | lit | profile |
|---|---|---|---|---|---|
| featherIn only | 0.50 | 0.0000 | 0.00–1.00 | 128 | `###+    ` |
| featherOut only | 0.50 | 0.0000 | 0.00–0.99 | 63 | `   +    ` |
| slice + both | 0.50 | 0.0013 | 0.00–1.00 | 114 | `   .##. ` |
| hard edge | 0.50 | 0.0000 | 0.00–1.00 | 128 | `####    ` |
| drive 0.00 | 0.00 | 0.0013 | 0.00–1.00 | 77 | `##.     ` |
| drive 1.00 | 1.00 | 0.0013 | 0.00–1.00 | 39 | `       .` |

**Worst disagreement across all cases and all 256 values: 0.0013** — one third of an 8-bit quantisation
step. The comparison is not vacuous: alpha spans the full 0–1 range in every case, the lit-texel count
varies 39–128, and the shapes differ per configuration. `slice + both` renders the trapezoid the decode
predicted, and the band sweeps toward higher E as the drive rises.

## Round 2 - soft particles, palette, UV modes

Same harness, same method. Permutations located by scanning the 300 locally extracted `quad_ps` blobs
and reading each RDEF.

### Soft particles - `quad_ps` blob#128 (4,288 bytes) - **MATCH**

`$Globals` holds `cSoftParticleParams` @+0 and `cSoftParticleControl` @+16; `cDepthConversionParams`
sits at `PerFramePixelCB`+80, exactly where the decode placed it. Depth is `Load()`ed from
`sDepthTexture_SharedTexture` with integer coordinates, so the test bound an R32_FLOAT ramp as the scene
depth and held the particle at z = 0.5.

| case | max abs diff | alpha range | profile |
|---|---|---|---|
| fade in only | 0.0019 | 0.00-1.00 | `####    ` |
| band in+out | 0.0021 | 0.00-1.00 | ` ##     ` |
| base + fade alpha | 0.0021 | 0.25-1.00 | `####....` |

**Worst 0.0021.** The decoded formula - `lin(z) = 1/(z*dc.y + dc.x)`, `diff = lin(scene) - lin(self)`,
`t = saturate((diff - P.xy)*P.zw)`, smoothstep each, `fade = s.x - s.y`, `a *= C.z + C.w*fade` -
reproduces Riot's shader, including the `.x`-pairs-with-`.z` swizzle and the genuine smoothstep, in
contrast to erosion's linear ramps in the same shader family. The `base + fade` case correctly floors
at 0.25.

### Palette - `quad_ps` blob#12 (1,852 bytes) - **MATCH**

| case | max abs diff | sample at x=128 |
|---|---|---|
| mixer = red, no offset | 0.0027 | riot L=0.502, ours L=0.501 (palette[128]) |
| mixer = red, U+0.25 | 0.0027 | riot L=0.286, ours L=0.286 (palette[192]) |
| mixer = luma-ish | 0.0096 | riot L=0.510, ours L=0.511 (palette[38]) |

**Worst 0.0096.** The `U+0.25` row is decisive: the offset moved the lookup from `palette[128]` to
`palette[192]`, exactly `+0.25 x 255`, confirming `U = saturate(dot(src, mixer)) + cPaletteSelectMain.z`.

> **A separate finding, and the more useful one.** The first palette run returned pure white for every
> case. That was a flaw in the test, not the decode - `PIXEL_COLOR_REMAP_RAMP` **replaces** RGB with
> `ramp.Sample(luma(rgb), 0.5)` rather than multiplying it, so the white stub forced white output and hid
> the palette completely. Re-running with a greyscale-identity ramp made the lookup observable.
>
> **ReyEngine does not implement this remap at all.** It is declared by every colour-output particle pixel
> shader, so our particle RGB skips a stage the game can apply. What the game puts in that shared texture
> is UNKNOWN - it is engine-supplied and plausibly identity under normal conditions, which would make this
> harmless - but that is an assumption, not a measurement. Nothing to do with D3D11; the same gap exists on
> either backend.
>
> **CORRECTED at M221.** "Unconditional" was wrong, and it cost two milestones of wrong guesses downstream.
> Disassembling `skinnedmesh/diffuse_alpha` ps blob 5 shows the stage is **gated on the sampled ramp's
> alpha**:
>
> ```
> dp3  r1.x, r0.yzwy, l(0.2126, 0.7152, 0.0722)   // luma, Rec.709
> mov  r1.y, l(0.5)
> sample r1.xyzw, r1.xyxx, t1.xyzw, s15           // ramp.Sample(luma, 0.5)
> lt   r1.w, l(0.000000), r1.w                    // sampled ALPHA > 0 ?
> movc r0.yzw, r1.wwww, r1.xxyz, r0.yyzw          // yes -> replace rgb; no -> keep it
> ```
>
> With alpha 0 the shader keeps the lit diffuse untouched, so "identity under normal conditions" was right
> for the wrong reason - the engine does not need an identity *ramp*, it needs a *transparent* one. Any
> opaque stand-in forces the replacement: white produces a white model, a greyscale ramp a black-and-white
> one. Both were shipped and both were wrong before the disassembly settled it.

### UV modes - not decidable this way, and not attempted

`uvMode` is a CPU-side enum. The shader-side switches are `LOCAL_SPACE_UV` / `SCREEN_SPACE_UV` in
`mesh_vs`, and nothing in the bytecode records which authored `uvMode` value selects which - the same
structural wall as the erosion parameter packing, because the CPU resolves it before anything reaches the
GPU. Running `mesh_vs` could validate the planar-projection maths for `LOCAL_SPACE_UV`, but that needs
its full cbuffer set plus a stand-in pixel shader to read the interpolant back, and it would still leave
the enum mapping unanswered. ReyEngine implements no `uvMode` behaviour today, so there is nothing to
compare against either. Deliberately skipped rather than half-done.

## What this means for the backend decision

**For a port:** it works. Riot's shaders are loadable and runnable, which would in principle remove the
whole class of "our GLSL approximates their HLSL" bugs.

**Against:** the specific bug class it would remove is empty on everything measured. **Three of three**
decoded formulas reproduce Riot's real shaders to within 8-bit quantisation - erosion 0.0013, soft
particles 0.0021, palette 0.0096 - covering 307,050 + 95,671 + 43,621 emitters between them. That was the
strongest argument for the port, and it no longer stands.

Unchanged, and still the deciding cost: Avalonia offers `OpenGlControlBase` and no D3D11 equivalent.
Presenting a D3D11 swapchain means either a `NativeControlHost` child HWND — which sits above the
Avalonia compositor and would break the brush palette, gizmos and toolbars that currently draw over the
viewport — or shared-texture interop back into the GL context. Neither is small, and the first visibly
regresses the UI.

Also worth keeping in view: on Windows the app already runs on D3D11 underneath, via ANGLE. A native port
removes a translation layer rather than adding a capability.

**Recommendation: do not port for fidelity.** Every formula tested reproduces Riot's shader within
quantisation, so translating rather than running their bytecode costs nothing measurable in accuracy. The
arguments that remain are unrelated to fidelity - a hard requirement to run Riot's pipeline verbatim, or
wanting new permutations to work without hand-porting each one.

Where effort actually pays is the **gaps this exposed**, all of which exist on either backend: soft
particles and palette are now validated but still unimplemented in ReyEngine, and the unconditional
`PIXEL_COLOR_REMAP_RAMP` stage is not implemented at all.

## Caveats

- Three shaders, one permutation each, covering the three largest decoded VFX features. Not proof about
  the rest of the schema.
- Erosion and soft particles were compared on the **alpha** channel, which is what both drive. Palette
  was compared through the remap ramp as luminance, which is what that stage leaves observable.
- Riot's *authored* parameter packing — which field lands in `cAlphaErosionParams.y/.z/.w` — remains
  undecidable from the shader, because the CPU packs that vector before upload. Running the real shader
  cannot resolve it, and did not.
- Harness: `scratchpad/spike-d3d11` (reads blobs extracted by `scratchpad/erosion-dxbc`).
