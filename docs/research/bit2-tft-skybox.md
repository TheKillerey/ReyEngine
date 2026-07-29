# M276 - `miscRenderFlags` bit 2 is not a camera-relative card, and the TFT skybox is not a quad

The question was why `TFT_Skybox_Darkstar_C` (Map22, `darkstar_supernova.materials.bin`) renders nothing
where the game shows a purple starfield wrapping the arena. All eight of its emitters carry
`miscRenderFlags = 4`, the rarest value in the game, and each one arrives at the renderer with a size of
**0.2** on a map that spans thousands. The hypothesis under test:

> bit 2 selects a camera-relative / screen-space card: position is a distance along the view axis, and
> size is a fraction of the view rather than a world measurement.

The arithmetic was seductive. At a 45 degree FOV, `0.2 / (2 * 150 * tan(22.5))` is 0.16% of the view -
sub-pixel, which matches the symptom exactly - while reading the same 0.2 as a screen fraction gives 20%
of the view at *any* camera distance, which behaves like a backdrop. A factor of 125x on the same
authored number, and only one of the two could be a sky.

**It is refuted.** Not narrowly - the premise it rests on is false.

## The premise was false: these are not quads

All eight emitters are `VfxPrimitiveMesh`, bound to real dome meshes. Their authored "size" is the
**uniform scale of a mesh**, not the edge length of a card. `VfxParticleRenderer` says so in the code
that draws them - *"mesh particles use birthScale.x as a uniform scale; a scale of ~1 means unscaled
geometry"* - and the mesh vertex shader is `local = rotate(aPos) * uScale`.

| emitter | authored | mesh | mesh XZ width | actual world span |
|---|---:|---|---:|---:|
| Base | 0.20 | `TFT_Skybox_BackgroundClouds.scb` | 4,576 | 915 |
| BlueClouds | 0.27 | `TFT_Skybox_MidgroundClouds.scb` | 3,768 | 1,017 |
| Pink01 / Pink02 | 0.20 | `TFT_Skybox_ForegroundClouds.scb` | 1,253 | 251 |
| Glow | 0.20 | `TFT_Skybox_BackgroundClouds_B.scb` | 3,299 | 660 |
| Glow_base | 0.29 | `TFT_Skybox_BackgroundClouds_B.scb` | 3,299 | 957 |
| Star_Detail | 0.20 | `TFT_Skybox_BackgroundClouds.scb` | 4,576 | 915 |
| Star_Detail1 | 0.19 | `TFT_Skybox_BackgroundClouds.scb` | 4,576 | 869 |

So the 0.2 that motivated the entire screen-space arithmetic is `0.2 x 4,576 = 915 units of real
geometry`. The "sub-pixel card" the probes were measuring never existed in the data; it is what our own
renderer *produces* when the mesh is not there (`VfxParticleRenderer.cs:369` falls through to the quad
branch when `MeshVao == 0`, and the quad branch reads `birthScale.x` as a world-space edge).

## The curves

Four readings of the same eight emitters, six camera distances spanning 40x, plus a full yaw sweep. The
metric is CPU-exact and blend-independent: the union of the geometry's own projected footprints, near-
clipped in homogeneous space and rasterised through the very matrix a frame would be drawn with. Two
independently written implementations compute it - `meshsky`, which touches no GPU, and `screencard`,
which also draws the frames over the real Map22/darkstar_supernova scene - and they agree on reading C
to four decimal places at every distance.

| distance | A world quad | B anchor only | C screen fraction | M mesh, as authored | S mesh x scaleOverride |
|---:|---:|---:|---:|---:|---:|
| 1,000 | 0.0000% | 0.0000% | 8.3557% | 73.5855% | 100.0000% |
| 2,500 | 0.0000% | 0.0000% | 8.3557% | 14.2319% | 100.0000% |
| 5,000 | 0.0000% | 0.0000% | 8.3557% | 3.6392% | 100.0000% |
| 10,000 | 0.0000% | 0.0000% | 8.3557% | 0.9323% | 83.3237% |
| 20,000 | 0.0000% | 0.0000% | 8.3557% | 0.2411% | 48.5306% |
| 40,000 | 0.0000% | 0.0000% | 8.3557% | 0.0580% | 11.0321% |

Columns A, C, M and S are `meshsky`; column B is `screencard`, which reproduces A and C to the same four
decimals. On the GPU, `screencard` also measured what the eight materials actually change in the frame:
0.0000% for A and B at every distance beyond 1,000, and a flat 7.4417% for C - the pixel metric and the
geometric one tell the same story, so nothing here rests on a blend mode or a texture.

Yaw 0..315 degrees at 5,000 units: A 0.0000% at all eight angles, C 8.3557% at all eight (spread
0.0000%), M spread 0.0122%, S 100.0000% throughout. Nothing breaks under rotation, which distinguishes
nothing - every reading is rotation-stable.

Four things the numbers say, in order of how much they matter.

**The position half of the hypothesis is inert.** Reading B - anchored at `D = |position|` but keeping
the authored world size - is 0.0000% at every distance, a card 0.60 px wide, indistinguishable from the
world reading. Anchoring to the camera changes nothing observable. Everything the hypothesis buys comes
from the size claim alone, and the measurement provably cannot separate "position is a distance along the
view axis" from "position is ignored": reading E, which pins the card at a fixed 50,000 units instead,
produces an identical 8.3557%.

**C's flat curve is arithmetic, not evidence.** `size = 2 * D * tan(fov/2) * fraction` divides out the
`D` it multiplies in, so coverage is distance-invariant *by construction*. The dolly test was supposed to
be the discriminator; against a formula built to be distance-invariant it can only confirm that the
formula was typed correctly. A flat C was never capable of confirming anything.

**Wrong size.** 8.3557% of the frame is one 74-of-256-pixel square - and since all eight cards are
concentric, the union *is* the largest card (0.29^2 = 8.41%). A backdrop that wraps a TFT arena has to
cover the frame.

**Wrong side.** With depth testing on, 100% of the pixels reading C changes at 1,000 units land on arena
geometry (81% / 67% / 59% / 22% / 5% at 2.5k / 5k / 10k / 20k / 40k). It is a glowing decal in front of
the board, not a sky behind it.

## What the corpus says, independently of any render

A full-corpus census - 245 WADs, 1,828,445 emitters - was run without drawing a frame, and it refutes the
same claim from a different direction.

- **Not one bit-2 emitter in the game is sub-pixel.** Of 3,843 drawable bit-2 emitters, with mesh
  primitives resolved through their bound `.scb`/`.sco`: 0 are at or below 0.5 units, 111 are 0.5-5,
  549 are 5-50, 2,127 are 50-500 and 1,056 exceed 500. The hypothesis needs a population of implausibly
  small authored numbers. It does not exist.
- **96.4% of League's backdrop emitters do not carry bit 2.** Of 7,174 drawable emitters in backdrop-named
  systems, 4,371 have no `miscRenderFlags` at all and 2,545 have value 1; 219 have value 4. Of 149
  `*Skybox*`-named system groups, 114 contain no bit-2 emitter whatsoever. Riot builds skies as big world
  cards and domes: `BackStage_Dusk_Skybox` at size 2,200, `L1_7YAnniversary_Skybox_VFX` at 2,700.
- **The flag is not a whole-system decision**, and a placement mode would have to be. Only 214 of 1,340
  systems have every emitter flagged; 819 have under 20% flagged. `Caitlyn_Skin11_R_CameraBoundVFX` flags
  19 of its 32 emitters - a placement rule cannot be true of 19 emitters and false of 13 inside one
  effect. Even the `*CameraBoundVFX*` family, whose name asserts the hypothesis, is only 26.2% flagged.
- **Camera-relative placement already exists in the data, elsewhere.** `MapParticlePlacement.AttachToCamera`
  (M195) is set on exactly 7 placements corpus-wide, all on Map22. None is a skybox, and none of Map22's
  173 skybox-named placements sets it.

## And the shader has no such path

All 16 cooked permutations of `assets/shaders/hlsl/particlesystem/quad_vs` were disassembled. The vertex
program takes a world-space `POSITION`, pushes it along the eye ray by `PARTICLE_DEPTH_PUSH_PULL` (a pure
depth bias - a point displaced along the ray through the eye projects to the same screen point), and
applies exactly one matrix. It proves its input is world-space twice on its own: it differences `POSITION`
against `vCamera`, and it builds a fog-of-war UV from `POSITION.xz`.

There is **no size input at all** - four attributes, no per-instance stream, no `SV_VertexID`, and
`dcl_temps 1` leaves no room to reconstruct a corner. M231 is confirmed from shipped bytecode: the CPU
hands the shader four finished world-space corners.

`SCREEN_MATRIX`, `mView`, `mViewInv` and `VIEW_PROJECTION_MATRIX` are declared in `PerFrameVertexCB` and
read by zero instructions in every permutation. `dcl_constantbuffer CB2[20]` caps the addressable range
at byte 319, so `mView` (+384) and `mViewInv` (+448) are outside what any cooked `quad_vs` can even
address. Riot has a screen matrix on hand in the particle vertex shader and uses it nowhere.

## So why does it render nothing?

Two different reasons in the two viewports, and neither is the flag.

**D3D11.** `D3D11MapParticles.cs:208` is `if (def.IsMeshPrimitive) { SkippedMeshEmitters++; continue; }`.
The map-particle path drops mesh emitters outright and says so in its own log. That is 58.3% of the bit-2
population and 2,241 emitters corpus-wide. This is a missing feature, not a wrong placement model.

**OpenGL.** Mesh emitters draw their geometry only when the mesh uploaded; otherwise
`VfxParticleRenderer.cs:369` falls through to the quad branch and `birthScale.x` becomes a world-space
edge length - which is exactly the 0.2-unit sub-pixel card the original probe measured. One `DrawElements`
per particle instance, eight emitters with one long-lived particle each, is also the most likely source of
the "8 draws" in the original report; the D3D11 path would have issued zero.

The original "8 draws and renders nothing" could not be reproduced this session, because the shader cache
is currently unreadable (see below) and the D3D11 map scene builds 0 materials.

## What bit 2 is now known to be

Enough to amend M259, not enough to implement:

- It is **not** a camera-relative or screen-space placement mode.
- It is **not** a backdrop marker: only 7.5% of bit-2 emitters are backdrop-named, and 96.4% of
  backdrop emitters do not carry it.
- It is **not a whole-system property**, so it is not a placement mode of any kind.
- Its population is 58.3% mesh, name-dominated by `stencil` (631), `mask` (526), `write` (525) and
  `hud` (380). That corroborates M259's own measured enrichment of bit 2 with `stencilMode=1`,
  `stencilRef=4` and `alphaRef=1` - the association M259 already flagged for Q5.
- The population is 3,899 emitters, not 347. See the scope correction in `q4-miscrenderflags.md`.

## The real lead, and why it is not finished either

All eight darkstar emitters carry `scaleOverride` = 17-18, and five carry
`translationOverride` = (0, -6000, 0). ReyEngine parses both and applies neither. Within
`darkstar_supernova.materials.bin` the separation is perfect: 8/8 flag-4 emitters have a `scaleOverride`
and 0/32 others do; the flag-4 median `birthScale` is 0.20 against 200.00 for flag 1 in the same file.

Applying it would matter. The largest dome at the authored 0.2 is 915 units wide inside a 4,289-unit
arena - a saucer on the board. At `0.2 x 18` it is 16,000 units and **encloses** both the arena and the
camera, which is what a sky does. The measured coverage says the same thing: under reading S the eye is
inside the dome out to 10,000 units and coverage is 100% of the frame out to 5,000; under reading M the
eye is never inside it at any distance tested.

Two further facts point the same way. The domes are **100% inward-facing shells** - every one of
240/240/40/240 triangles faces the mesh's own centre - which is the construction of a sky you stand
inside, not of an object you look at. Across the same WAD, 101 of 121 skybox-named meshes are fully
inward against 680 of 5,148 others. And skybox meshes that carry **no** override reach a median world
span of 10,313 units, while those that carry one reach only 2,160 when it is ignored.

**But do not implement a naive multiply.** Folding `scaleOverride` in as a plain factor moves that same
population to a median of 37,800 - 3.7x *past* the unoverridden reference of 10,313 - with a worst case
of 1,047,939 units. The direction is right and the magnitude is not established. `scaleOverride` is also
accompanied in the schema by a system-level `overrideScaleCap` ("caps how far the system may be scaled
up", 6,361 occurrences, absent here), which hints the mechanism is conditional rather than a bare
multiplier. Its semantics are the next milestone's question.

Ranked, what the next milestone should do:

1. Render mesh-primitive emitters in the D3D11 map-particle path. It is a missing feature with a known
   site and it accounts for the black screen.
2. Establish what `scaleOverride` / `translationOverride` mean, against the three-population size test in
   the data file, before anything applies them.
3. Check the winding. These domes are one-sided inward shells with `disableBackfaceCull = false`. Whether
   our cull convention keeps the faces a camera *inside* the dome needs to see is unverified, and if it
   does not, a correctly-scaled dome would still render nothing.

## Unrelated and urgent: the shader cache moved

Every shader-cache TOC is now named `{name}.vs-dx11` where the engine builds `{name}.vs.dx11` - a dot
became a hyphen. The WAD keys on XXH64 of the path string, so the old spelling hashes to nothing.
Measured: the cache holds 2,176 chunks, 2,154 resolve, **zero** end in `.vs.dx11` or `.ps.dx11`, and
reading `assets/shaders/hlsl/particlesystem/quad_vs.vs-dx11` parses a valid TOC3.0 with 16 permutations.
That the rename arrived with a client patch during this investigation is a strong inference - every WAD
under `DATA/FINAL` was rewritten the same day, and milestones through M275 demonstrably used the dotted
spelling - but it is an inference. What is measured is the current state. `ShaderCacheReader.TocPaths` is therefore empty, `ShaderDatabase.ScanCache` finds zero
shaders, and `mapparticles Map22 darkstar_supernova` now reports "0 materials, 0 slices, 0 texture
bindings". Nothing on the D3D11 side works against the patched client.

Affected sites, all in `src`: `ShaderCacheReader.cs` lines 88-89 (the `TocPaths` filter), 113-114
(`StripStage`), 117-118 (`TocPathFor`), 218 (`ParseToc` stage detection); `ShaderDatabase.cs` lines 54-62;
`ShaderPermutationIndex.cs` line 144. Whether to accept both spellings or only the new one is a
compatibility decision, and it deserves its own milestone and its own test rather than a drive-by fix
inside a measurement commit. Any experiment run before it is fixed is measuring a renderer with no
shaders.

## Cost

One milestone, no renderer change. Raw output: `bit2-tft-skybox-data.txt`. Harness modes: `meshsky`,
`screencard`, `bit2`, `bit2x`, `skyname`, `skyplace`, `domesize`, `arenasize`, `disasm`, `permscan`,
`cachels`.
