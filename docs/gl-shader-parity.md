# GL shader parity — scope and plan

Written 2026-08-08, from measurements taken while diagnosing the ENV_GlowSign and SRX_DynamicEffect
reports. Everything stated as measured here was verified against the shipping game data or the running
renderer; everything else is marked as an assumption.

## The problem, stated precisely

The D3D11 viewport runs **Riot's own compiled shaders**. The OpenGL viewport runs **one hand-written
approximation** (`ViewportMeshRenderer`, `_meshProgram`). The two are not close, and the gap is not evenly
distributed — GL is missing whole features, and separately it **invents** one it should not have.

Measured differences:

| Feature | D3D11 | GL |
|---|---|---|
| Emissive glow | `Emissive_Texture x Emissive_Color x Emissive_Intensity`, additive | **absent** — `uEmissive` is only read as a terrain-blend layer (`COLOR_MAP_2`) and in debug mode 6 |
| GlowSign flicker | vertex-stage, `TEXCOORD5.xyz` + `TEXCOORD6.w` | **absent** — no such term exists |
| VertexDeform wave | authored `WaveFrequency/Amplitude/Offset`, `TIME`-driven | **absent** |
| DynamicEffect animation | `EMISSION_*` switch-gated | **absent** |
| Flowmap / water | `uTime`-driven | present (GL's one real animated feature) |
| Sun + sky diffuse | **none** — `SUN_LIGHT_COLOR` is `[unused]` in these blobs | **fabricated**, `ViewportMeshRenderer.cs:579`, worth 1.00x–1.37x |

That last row is the one to fix first, and it is a DELETION, not an addition. GL applies
`base * bakedLightColour(uSkyLight + uSunColor * d)` to surfaces whose real shader has no N·L term at all.
It is why D3D11 was reported as "too dark": D3D11 was right and GL was up to 37% too bright. Any parity
work that adds features without removing this will be calibrating against a wrong baseline.

## Two ways to do this, and why only one is viable

**(A) Transpile Riot's DXBC to GLSL.** Rejected. `env_glowsign.ps.dx11` has 148 distinct blobs over 14
define axes; `staticmesh/vertexdeform.ps.dx11` has 896 permutations. Handling the general case means
writing a DXBC decompiler, and handling the specific cases means hand-porting hundreds of blobs. Neither
is proportionate, and both would need redoing every patch.

**(B) Implement the missing features in GL's own shader, from formulas recovered by disassembly.**
Recommended. The features are few, the formulas are short, and they are recoverable — two of them already
have been (below). GL stays an approximation, but a HONEST one: every term traceable to a disassembled
instruction rather than to what looked right.

**The rule for (B), learned the hard way this session:** do not fabricate a term. GL's invented sun+sky
term is exactly that mistake already shipped, and M212's "the shader doubles the texture" note was a
harness artefact recorded as fact for months. If a formula cannot be recovered, leave the feature out and
say so, rather than approximating it.

## Recovered formulas

**GlowSign flicker** — `env_glowsign.vs.dx11` blob 17, instructions 123-143:

```
seed   = MESH_CENTER.x + MESH_CENTER.y + MESH_CENTER.z      // per-sign phase, so signs are out of step
phase  = TIME.x * float2(Switch_Time_Speed, Time_Speed) + seed
o5.xyz = (phase.x - Switch_MinMax.x) * (Switch_Color - 0.5) + 0.5   // flicker colour  -> TEXCOORD5
o5.w   = 1
o3.w   = phase.y * 0.5 + Noise_MinMax.x                             // noise           -> TEXCOORD6.w
```

Gated by the **`USE_FLICKER`** switch (define axis `[0,1]`), with `USE_COLOR_SWITCH` as a companion.
NOTE: all three Winter Rift GlowSign materials author `USE_FLICKER=False`, so on that map this correctly
does nothing. Honour the switch — do not make GL flicker unconditionally, or GL becomes wrong in the
other direction.

**VertexDeform wave** — `staticmesh/vertexdeform.vs.dx11`, any of the 24 blobs of 40 that mention
`WaveAmplitude` (blob 19 was read):

```
seed  = sin(MESH_CENTER.x + MESH_CENTER.y + MESH_CENTER.z)   // per-bush phase, so foliage is out of step
wave  = sin(seed + WaveFrequency * TIME.x)
sway  = wave * WaveAmplitude.xy + WaveOffset.xy              // WaveAmplitude @80, WaveOffset @88
```

Applied to world position, gated by **`COLOR0.z`** — the vertex colour's blue channel, used as
`COLOR0.z - 1`, which is what keeps roots planted while tips move. GL must therefore supply vertex
colours to the mesh program, and `MESH_CENTER` per submesh, neither of which it does today.

**NOT portable:** the same shader then loops 10 entries of a `GrassDisortVS` cbuffer (cb3) with
`DistControlFactor` / `MinDistance` / `SpreadStrength` — that is units parting the grass as they walk
through it. There are no units in the editor and no such buffer in GL. Leave it out and say so; do not
approximate it.

**Emissive** — `env_glowsign.ps.dx11` blob 69, tail:

```
em     = Emissive_Texture.rgb * Emissive_Color.rgb
colour += em * Emissive_Intensity * fowFactor
bloomRT = em * Bloom_Intensity * fowFactor
o0.w   += Alpha_Offset
```

## Suggested order

1. **Remove GL's fabricated sun+sky term** (`ViewportMeshRenderer.cs:579`). Deletion, not addition.
   Expect the GL viewport to get darker and to MATCH D3D11 — verify with the readback harness
   (`scratchpad/readback-a`) rather than by eye. This is the calibration baseline for everything after.
2. **Emissive glow**, formula above. Biggest visual win per line, and it is additive so it cannot
   regress an unlit surface.
3. **VertexDeform wave** — formula RECOVERED (below); implementation not started.
4. **GlowSign flicker**, formula above, gated on `USE_FLICKER`.
5. **DynamicEffect** — lowest priority. Measured: its emission needs `EMISSION_ROTATE_ON` and
   `EMISSION_SINGLE_DIRECTION_ON` BOTH set, and all 33 Winter Rift materials author both false, so there
   is nothing to show on that map. Do this only if a map is found that enables it.

## How to verify each step

Do not verify by eye. The harnesses exist:

- `scratchpad/readback-a` — screen mean vs source texel, per material, on the real scene.
- `scratchpad/readback-t`, mode `timesweep` — sweeps `TimeSeconds`; a static value across the sweep means
  the animation is dead. This is what proved the GlowSign flicker was disconnected.
- `scratchpad/disasm`, modes `disasm` / `findv5` / `axes` — blob disassembly, interpolant consumers, and
  the define axes of a stage TOC.

For parity specifically: render the same material in both viewports with the same camera and compare the
ratios. Equal ratios is the goal; "looks similar" is not a measurement.

## What this does NOT cover

Shadows, clustered/stationary lights, cloud shadows, IBL and the fog stack all differ too. They are out of
scope here: none was reported, and each is a larger job than the five above combined.
