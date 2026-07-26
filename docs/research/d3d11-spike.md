# D3D11 spike — can we run Riot's own compiled shaders?

**Date:** 2026-07-26 · **Question:** is a Direct3D 11 backend viable with Silk.NET, and would it buy
enough fidelity to be worth a second render backend?

**Both halves answered, and they point in opposite directions.** Riot's compiled shaders *do* load and
run on a Silk.NET D3D11 device — but our existing GLSL reimplementation already matches one of them to
within a third of an 8-bit step, so the fidelity argument for the port is much weaker than assumed.

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

## What this means for the backend decision

**For a port:** it works. Riot's shaders are loadable and runnable, which would in principle remove the
whole class of "our GLSL approximates their HLSL" bugs.

**Against:** the specific bug class it would remove appears to be empty, at least here. The GLSL written
from the disassembly reproduces the real shader to within quantisation. That was the strongest argument
for the port and this spike substantially weakens it.

Unchanged, and still the deciding cost: Avalonia offers `OpenGlControlBase` and no D3D11 equivalent.
Presenting a D3D11 swapchain means either a `NativeControlHost` child HWND — which sits above the
Avalonia compositor and would break the brush palette, gizmos and toolbars that currently draw over the
viewport — or shared-texture interop back into the GL context. Neither is small, and the first visibly
regresses the UI.

Also worth keeping in view: on Windows the app already runs on D3D11 underneath, via ANGLE. A native port
removes a translation layer rather than adding a capability.

**Recommendation.** Do not port for fidelity on this evidence. The remaining honest arguments are (a)
shaders whose behaviour we have *not* validated — soft particles, palette and the UV modes are decoded
but unverified against the real thing, and the same harness would settle each in about an hour, and (b)
reasons unrelated to fidelity, such as a hard requirement to run Riot's pipeline verbatim. If more
shaders are validated this way and they also match, the port has no fidelity case left at all.

## Caveats

- One shader, one permutation. It is the largest single VFX feature (22% of emitters) but it is not proof
  about the others.
- The comparison covers the **alpha** channel, which is what erosion drives. RGB additionally passes
  through an unconditional `PIXEL_COLOR_REMAP_RAMP` luminance lookup that this harness stubbed with a
  white texture, so RGB was not compared.
- Riot's *authored* parameter packing — which field lands in `cAlphaErosionParams.y/.z/.w` — remains
  undecidable from the shader, because the CPU packs that vector before upload. Running the real shader
  cannot resolve it, and did not.
- Harness: `scratchpad/spike-d3d11` (reads blobs extracted by `scratchpad/erosion-dxbc`).
