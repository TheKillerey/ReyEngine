# The flipbook frame and the scroll ramp

M719. The sixth reading taken from `LeagueToolkit/ltk-manager`'s particle renderer plan
(`docs/plans/vfx-particle-renderer.md`): the two corrections recorded at the end of section 2.11, with the
transform of section 3.4 they correct. Checked against their implementation (`uvTransform.ts`, `emit.ts`,
`readSurface.ts`) and their own tests (`uvTransform.test.ts`, `integrate.test.ts`) at clone HEAD `13467f3`;
the corrections arrived in `3057fce`, the squash of their PR #520 (2026-09-11).

The reading could not be ported on its own. Removing the clamp it corrects without the address mode that
replaces it, moving the base layer's scroll into cells without the multiplier's, and rewriting the
transform with the flip still in front would each have shipped a regression, so 2.13's flip order and
3.2's address enum came with it.

## The rules

| | the engine, per the reading | what we did |
| --- | --- | --- |
| the frame | `startFrame + fmod(phase + age * rate, numFrames)` - the run wraps first | `fmod(start + age * rate, numFrames)` |
| a random start | `rand * numFrames` as a float phase inside the run; `startFrame` still lands on top | a whole cell that replaced `startFrame` |
| `startFrame` | never clamped, and honoured at `numFrames` 1 | clamped to `numFrames - 1`; ignored below 2 frames |
| the birth ramp | `birthUVOffset + age * birthUvScrollRate`, held to [-1, 1] under `uvScrollClamp`, otherwise wrapped into [0, 1) | GL clamped the WHOLE coordinate to [0, 1]; D3D11 applied the birth scroll alone |
| integrated and emitter scroll | added after the ramp, never clamped | GL clamped them with everything; D3D11 had neither |
| a flip (2.13) | a post-multiply, after the transform, so a flipped scroll reverses | GL flipped first; D3D11 never flipped |
| `texAddressModeBase` (3.2) | `ParticleSystem::TEXTUREADDRESS`: 0 WRAP, 1 MIRROR, 2 CLAMP, 3 BORDER | parked and unread; its note said 2 was a mirror |

Evidence in the clone: `uvTransformInto` computes `played = wrap(phase + age * frameRate, frames)` then
`cell = trunc(start + played)`, with the comment "The run wraps before `startFrame` is added, so it cycles
from that frame onward rather than back to the grid's first cell"; `bornUv` draws
`phase = roll * frames`; `ramp()` is `clamp(v, -1, 1)` or `v - floor(v)`, and the integrated accumulator and
`emitterScrollRate * now` are added outside it. Their test "runs from startFrame and wraps back to it rather
than to the grid's first cell" is start 3, four frames, phase 5 -> cell 4; "holds the ramp at one cell out
under uvScrollClamp and leaves the rest alone" is a ramp of 2.5 plus an integrated 3 -> 4. Both are carried
over number for number into `FlipbookAndUvRampTests`.

## The population

Measured over 180 archives, 15,581 bins, 1,581,956 emitters, 1,523,119 of them reachable (the population
`IsVisual` lets into a renderer). The probe is `flipbook` in `.codex_tmp/CatalogProbe`; presence comes from
the raw tree, because a bin omits every value equal to its declared default.

| what changes | reachable |
| --- | ---: |
| books that author a `startFrame` and a rate: the wrap target moves from cell 0 to the start | 6,433 |
| books with a random start and a `startFrame`: the start is no longer dropped | 10,268 |
| books whose `startFrame` is at or past `numFrames`: no longer clamped | 2,184 |
| single-frame emitters over a grid picking a still cell with `startFrame` (quad 2,033) | 2,961 |
| `uvScrollClamp` authors (mesh 26,458, quad 3,219, trail 1,767, beam 955, ray 530) | 32,929 |
| ... on the quad path, where the ramp is built | 3,749 |
| quads and rays authoring `texAddressModeBase` (1: 300, 2: 17,710, 3: 603) | 18,613 |
| quads and rays where the flip order matters (a flip with a translation, rotation or scale) | 706 |
| quads with `birthUvScrollRate` over a grid > 1: the scroll moves from texture units to cells | 2,611 |
| multiplier layers with `birthUvScrollRateMult` over a mult grid > 1 (quad 9,548) | 11,208 |

`texAddressModeBase` is never written as 0 - the omit-default rule holding again - and 68% of the
`uvScrollClamp` authors write a 2.

## Four decisions, and what backs each

**The unit is the cell, and that is inferred.** Riot's `quad_vs` computes `(col + u) / cols` from what the
CPU writes into the vertex, and has no scroll constant and no TIME, so any translation the CPU adds is in
cells unless it multiplies by the grid first. The reading clamps offset and scroll together to one cell with
no factor of the grid anywhere. Against that: M634 pre-multiplied by the grid, but its only justification was
landing where the GL viewport's after-the-divide term landed, and that term (M59) was never measured for its
unit either. Neither side was measured against the client; this one agrees with the reading and the shader.
It slows the scroll on the 2,611 grid quads by a factor of their column count.

**The address enum is the engine's, and the old note was a guess.** Section 3.2 names the field's enum
`ParticleSystem::TEXTUREADDRESS` - WRAP, MIRROR, CLAMP, BORDER. Section 2.13 records separately that
`erosionMapAddressMode` reaches the sampler *unremapped*, and the sampler's enum swaps mirror and clamp; that
is why M717's erosion mapping (2 = mirror) is right and does not transfer. The M175 note read this field in
Unity's order, 2 = mirror. The corpus sides with the engine's name: a held scroll over a clamped edge is a
wipe, and two thirds of the emitters that hold their scroll write a 2. BORDER folds to CLAMP - neither
renderer carries a border sampler and GLES 3.0 has none.

**No wrap over the grid.** The clone wraps the cell index over `cols * rows` on the CPU. Riot's `quad_vs`
computes the row as `floor(frame / cols)` with nothing bounding it, so both renderers are handed the
unwrapped frame and the sampler decides. Under the default WRAP the two are the same cell. Under a CLAMP base
they are not: 733 reachable emitters run past their grid with a non-wrap base address, and an erosion map
under its default mirror sampler flips on odd rows past the grid (9,318) - both as the engine's own shader
would draw them, if its CPU does not wrap either, which the reading does not say.

**The multiplier moves with the base.** Its birth scroll was already parsed and pre-multiplied by its own
grid; `quad_vs` divides TEXCOORD1 by the second descriptor exactly as it divides TEXCOORD0 by the first. So
its offset, integrated scroll, emitter scroll and clamp are parsed now too and it runs through the same
ramp. Leaving it behind would have drifted the two layers apart by a factor of cols on 11,208 layers.

One definition feeds both renderers. `VfxUvLayer.BaseOf` and `MultOf` gather the fields,
`VfxUvTransform.Cell` is what the Direct3D 11 quad builder runs per corner, and `VfxUvTransform.Glsl` is the
same formula, concatenated into the OpenGL quad vertex shader. Both D3D11 hosts build the record through one
factory; the preview window's host had passed no scroll at all since M634.

## The seeded stream did not move

The phase draw replaced `_rng.Next(numFrames)` in the same place in the particle initializer, under the same
condition. `Next(n)` and `NextDouble()` each consume one sample of .NET's seeded `CompatSeedImpl`, verified
by a 2,000-draw probe, and `Next(4)` is exactly `(int)(NextDouble() * 4)` - so a random-start particle opens
on the cell it did before whenever its `startFrame` is 0, and every later roll reads the same sample. A test
pins it against a fresh `Random` of the same seed.

## Recorded, not built

- **`birthFrameRate`: a replacement or a multiplier.** We use it in place of `frameRate`; ltk-manager
  multiplies, and the declared default of 1.0 leans their way. No reading covers it. 5,390 reachable books
  author it with no `frameRate` - they animate under our reading and hold under theirs - and 5,048 author
  both.
- **Meshes.** 80% of `uvScrollClamp` authors, and the hold bites on 11,694 of them. The GL mesh path and the
  D3D11 mesh constant scroll per emitter on the emitter's age; the per-particle ramp is not built there.
  The base address mode *is* honoured on meshes in both renderers.
- **Ribbons** take no uv transform at all.
- **Per-particle birth values.** The resolver reads constants. Keys or probability tables sit on
  `birthUVOffset` in 59,438 reachable emitters and on `birthUvScrollRate` in 41,338; 2,242 and 3,373 of those
  omit the constant entirely and read as 0.
- **The multiplier's scale, rotation and flips** (`uvScaleMult` 119,501, `UvRotationMult` 23,247, flips
  4,248) and **`texAddressModeMult`** (40,392; a 2 on 37,189).
- **LOCK_ALPHA's alpha coordinate.** 2.34 has the locked alpha sample the untranslated corner; 10,292
  reachable lock-alpha quads author a translation their alpha now follows.
- **Negative and sub-1 `texDiv`.** GL divides by the signed divisor and D3D11 reads anything below 1 as 1,
  a divergence the image already had; 453 reachable quads also author a translation, which now diverges with
  it.
- **Three choices**, not readings: a rate at or below zero holds (5 emitters), a written `numFrames` of 0
  counts as 1 (1,652), and a random phase is kept strictly below `numFrames`.
- **The screen.** Nothing here was compared against the running client. The subjects to check are M60's
  FireTorch on OldSummonersRiftV2, M535's FireTorch_Med/Flat and LavaCauldron/Surface on Map2 (still cells),
  `Fizz_Skin09_Q_debuff / Flashing1` (wrap target) and a census-S6 grid quad (the scroll unit).

## History this supersedes

- `4bbd179` M36-2: `(start + t * numFrames) % numFrames` over the particle's life; random start as a whole cell.
- `21aa772` M59: `BirthFrameRate ?? FrameRate` as a replacement; `+ uUvScrollRate * age` after the divide.
- `fb2522a` M60: the clamp of `startFrame` to `numFrames - 1`, verified on FireTorch at cell 0.
- `41a3639` M174: the uv stack with the whole-coordinate clamp, order marked as inferred.
- `07713c5` M634: the D3D11 birth scroll, pre-multiplied by the grid.
- `e4cf1e0` M595: no rate holds the cell - which survives inside the new formula.
