# M259 - Q4: `miscRenderFlags` decodes to "almost always 1"

Q4 of the RenderDoc plan asked what `miscRenderFlags` controls, and budgeted it last because a null
result was a plausible outcome. It is close to a null result, but not for the reason expected - the
premise itself was wrong.

## The premise was wrong

`miscRenderFlags` was recorded as a U8 bitfield on ~39% of emitters with bits 0-2 in use. That framing
implies a space of eight states. Measured over **484,286 emitters** carrying the field (39.7%, sweeping
champion + map WADs):

| value | count | share |
|---|---:|---:|
| 1 | 480,922 | **99.3%** |
| 5 | 1,688 | 0.3% |
| 3 | 995 | 0.2% |
| 4 | 347 | 0.1% |
| 2 | 334 | 0.1% |

It is not a three-bit space in practice. It is the constant 1, with 3,364 exceptions (0.69%). Bit 0 is
set on 99.9% of emitters that have the field at all.

(An earlier note put this at 547,010 emitters / 39.1%. This sweep also walks map WADs and nested structs,
so the absolute count differs; the share matches to within 0.6 points.)

## Nothing drives it

The search was deliberately not steered. For every emitter the harness emits a bag of features -
`field=value` for each scalar, `has:field` for presence, `type:field=Class` for nested types - and then
compares P(target | feature) against the base rate, ranked by deviation scaled by sqrt(support).

Against the decisive target, "value is not 1" (base rate 0.69%):

```
     25.2%  n=       309   has:postRotateOrientationAxis
     18.7%  n=       305   pass=-800
     18.2%  n=     1,908   stencilMode=1
     13.2%  n=     3,516   alphaRef=1
     12.5%  n=     2,665   stencilRef=4
     10.7%  n=     9,623   isFollowingTerrain=true
```

A field that *drove* the flag would sit near 100%. The best result with real support is 18%, and the two
entries above it have n around 300. **No parsed field determines this flag**, which also rules out the
cheap explanation that it is a redundant encoding of something we already read.

The enrichments are real but weak, and they cluster:

- **bit 1** with `isFollowingTerrain=true` - 10.7% against a 0.3% base, a 35x lift
- **bit 2** with the stencil fields - `stencilMode=1` 16.8%, `stencilRef=4` 12.5%, and with `alphaRef=1` 13.2%

## Presence is a schema artifact, not a signal

What predicts an emitter having the field at all (base 39.7%) is *other fields' presence*:

```
     86.1%  n=   260,835   isGroundLayer=true
     83.9%  n=   149,705   useNavmeshMask=true
     80.7%  n=    18,989   has:stencilMode
     22.2%  n=   183,823   has:depthBiasFactors
```

Field sets co-occurring by presence is the signature of content-pipeline versions - bins authored by
different tool revisions carry different field sets - not of meaning. So "39.7% of emitters have it" is
not a selection worth interpreting.

## What this changes

**Do not implement it.** 99.3% of emitters would take the same path, and the entire population that
could behave differently is 3,364 emitters. Any renderer work here is dominated by every other open
item, and treating the flag as a no-op is what the data supports.

**Q4 is retired from the capture plan.** It was budgeted last precisely because it might answer nothing;
it answered something better - that there is nothing there worth capturing.

**One thing to carry into Q5.** Bit 2's enrichment is with the stencil fields, and Q5 (stencil) remains
the only question a capture is genuinely the sole route to. If that capture ever happens, read
`miscRenderFlags` on the same draws - the 2,035 emitters with bit 2 are a small enough set that the
capture could settle both at once.

Raw output: `q4-miscrenderflags-data.txt`. Harness mode: `miscflags`.

---

## M276 amendment: the scope was wrong, and bit 2 is now partly decoded

Two corrections, both measured. Neither changes the conclusion - *do not implement it* still stands, and
now for better reasons - but the numbers above should not be quoted as they are.

### The sweep never read a map WAD

`MiscFlags.cs:71-80` globs `Directory.GetFiles(<GameRoot>/Maps, "*.wad.client")` without recursing. Every
map WAD lives one level down in `Maps/Shipping`, so that glob matches **zero files**, counted directly:

| directory | `*.wad.client`, non-locale |
|---|---:|
| `DATA/FINAL/Maps` | **0** - what the sweep looked at |
| `DATA/FINAL/Maps/Shipping` | 9 |
| `DATA/FINAL/Champions` | 203 |
| `DATA/FINAL` itself | 28, including every `TFTSet*.wad.client` |

So "484,286 emitters, sweeping champion + map WADs" is champion WADs only, and it also missed the whole
root level. It additionally reads WADs through raw `WadFile` rather than `WadArchive`, which drops
zstd-subchunked bins.

Re-swept over 245 WADs (root + Champions + Maps/Shipping + Shaders + Localized), 1,828,445 emitters:

| value | count | share |
|---|---:|---:|
| 1 | 717,899 | **99.2%** |
| 5 | 2,789 | 0.4% |
| 3 | 1,113 | 0.2% |
| 4 | 1,104 | 0.2% |
| 2 | 441 | 0.1% |
| 7 | 4 | - |
| 6 | 2 | - |

**Bit 2 is set on 3,899 emitters, not 2,035**, across 108 WADs and 1,340 systems. The shape of the
result is unchanged: still the constant 1 with well under 1% exceptions, and 39.6% of emitters carry the
field at all against the 39.7% measured here - the two parsers agree on the base rate.

Absolute counts from the two sweeps are **not interchangeable**. On champion WADs alone the new walk
finds value 4 = 411 against 347 above, and value 5 = 1,841 against 1,688; it walks systems reachable from
`VfxSystemDefinitionData` over `WadArchive`, this one walks every nested struct over raw `WadFile`. The
~15% gap is not root-caused.

### "Nothing drives it" was right; here is what bit 2 is not

M276 tested a specific reading - that bit 2 marks a camera-relative / screen-space card - and refuted it.
The negative results are worth recording because they are the cheap explanations:

- **Not a placement mode of any kind.** Only 214 of 1,340 systems have every emitter flagged, and 819
  have under 20% flagged. `Caitlyn_Skin11_R_CameraBoundVFX` flags 19 of its 32 emitters. A placement rule
  cannot be true of 19 emitters and false of 13 inside one effect.
- **Not a backdrop marker.** 96.4% of League's backdrop-named emitters do not carry bit 2, and only 7.5%
  of the bit-2 population is backdrop-named.
- **Not a different unit for size.** With mesh primitives resolved through their bound meshes, **zero** of
  3,843 drawable bit-2 emitters are sub-pixel; 2,127 sit between 50 and 500 units.
- **Not in the shader.** All 16 cooked permutations of `particlesystem/quad_vs` compute `SV_Position` with
  the same four `dp4` against one matrix, and none reads `SCREEN_MATRIX`, `mView`, `mViewInv` or
  `VIEW_PROJECTION_MATRIX`. No value of the flag can select a different behaviour, because there is no
  different behaviour to select.

What survives is the association this document already measured. The bit-2 population is 58.3% mesh
primitives, and its commonest name tokens after `skin` and `tft` are `stencil` (631), `mask` (526),
`write` (525) and `hud` (380) - the same stencil/mask flavour as the `stencilMode=1` / `stencilRef=4` /
`alphaRef=1` enrichments above. **The advice to carry bit 2 into a Q5 stencil capture is strengthened,
not weakened**; the set to read is 3,899 emitters rather than 2,035.

Detail and raw output: `bit2-tft-skybox.md` / `bit2-tft-skybox-data.txt`. Harness modes: `bit2`, `bit2x`,
`skyname`, `meshsky`.
