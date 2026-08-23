# Legacy decal porting — why ported decals drew black (M547)

The reporter's symptom, across several milestones: decals from the Map2 port render black in the
client, and have to be lifted off the ground to be seen at all. Eleven-plus hypotheses were
eliminated against Riot's shipped data before the mesh layout itself was measured.

The reporter's own diagnosis turned out to be the way in: *"the bug is happening because it's a
combined mesh"*. Measuring that opened two further defects underneath it.

## 1. Decals were merged into map-spanning meshes

`LegacyMapPorter` accumulates triangles into a `MeshAccumulator` keyed by
`SurfaceKey(Role, TextureSet, DoubleSided)`, splitting only at the 65,535-vertex ceiling. For opaque
ground that is right. For decals it is not: a decal is **blended**, a blended mesh gets exactly one
sort position, and a mesh spanning the map has no position that sorts correctly against the ground
everywhere.

Measured on the Map2 port into Map453:

| | ported Map2 | Riot Map11 `base_srx.mapgeo` |
|---|---|---|
| decal meshes | 20 | 45 |
| widest, as a share of the map | **96.3%** | **4.1%** |
| typical | 55–96% | 2.0% |
| largest mesh | 1,900 tris, **452 disconnected islands** | 23 tris, 14 islands |

Riot **does** batch decals — but only within roughly a 900-unit box. We batched by texture across
the whole map.

### The split unit: source mesh, and nothing finer

Two finer units were tried first and **both broke decals apart**. The reporter caught it:
*"I have a splitted mesh that halfs or have a splitted texture not the full texture ... they are
messed up and useless."*

| unit | decal meshes | meshes carrying < half a texture tile |
|---|---|---|
| 1,000-unit locality cell x component | 2,166 | ~1,000 |
| connected component | 1,130 | 6 |
| **source mesh** | **1,036** | **0** |

* **Locality cell.** Cells are assigned per TRIANGLE by centroid, so a quad straddling a boundary put
  its two triangles in different meshes — half the texture in each, and neither movable as a plane.
* **Connected component.** Much better, but still wrong where the artist authored a decal as several
  quads with DUPLICATED vertices at the seams. Unwelded, those are separate components, so the decal
  still came apart.
* **Source mesh.** The legacy file's own authoring unit — the artist placed each decal as an object.
  Zero partial decals (the p10 mesh carries 1.35 tiles, so every one holds at least a whole texture),
  in *fewer* meshes than the component split, with identical world extents (p50 6.3% of the map,
  p90 8.3%). This is also what makes a decal selectable and movable as the plane it is meant to be.

Result: 20 → **1,036** decal meshes, widest 96.3% → 48.2%.

What remains is honest rather than fixed: **86 source meshes are themselves map-wide** — the long
seams, authored as one object. Separating those needs retessellation, not repartitioning, and cutting
them is exactly the damage above.

## 2. The legacy source carries non-finite UVs, and the port copied them through

`room.nvr` holds **38 non-finite UVs across 5 of its 4,373 meshes**. Every one landed on a single
ported material: `order_base_circle_tx_dm_d150d919beb1.tex` — one of the two textures the reporter
named as broken.

Interpolation across a NaN corner is NaN over the **whole triangle**, so both the sampled texel and
the alpha it returns are undefined. On a blended decal that is a triangle compositing to garbage.

The porter already rejected triangles with invalid sentinel POSITIONS (`Reasonable`) and never
checked UVs. It does now, dropping 24 triangles of ~800,000 and reporting the count. Dropped rather
than zeroed: a NaN UV cannot be textured correctly by any substitute, so keeping the triangle only
picks which wrong texel it samples.

The editor had been reporting this all along — the Second UV window showed
`UV0: u NaN..NaN v NaN..NaN` on the selected decal mesh.

## 3. Every ported decal tiles, and all of them were clamped

M490 gave the whole decal role `SamplerAddressMode = 1` (Clamp) so a stamp would stop at its edge
instead of repeating. Its own note says clamp "would be the wrong one for a decal authored to tile".

Measured, that is **19 of the 20** ported Map2 decal materials:

| texture | max UV extent | triangles leaving the unit square |
|---|---|---|
| `chaos_root_base_decal_mid` | 13.15 | 41% |
| `order_tile_floor_border` | 5.55 | 100% |
| `order_tile_floor_mark2` | 5.21 | 100% |
| `bluetower_decal` | 5.08 | 98% |
| `order_ground_mix_2` | 3.83 | 89% |
| **`order_seam`** | 2.41 | 34% |
| **`order_base_circle`** | (NaN) | 45% |
| … | | |
| `order_ground_moss_patch1` | 1.57 | **1%** |

Clamping a surface whose UV runs 0→13 smears its edge texel across the entire thing. Both textures
the reporter named were being clamped.

The address mode is now decided from the geometry rather than the role: a material is authored to
tile when more than 10% of its triangles leave the unit square (with 1.05 slack for edge rounding).
That threshold sits in a real gap in the measured distribution — `order_ground_moss_patch1` at 1%,
the next lowest at 12%. It flips 19 materials to wrap and leaves `order_ground_moss_patch1` clamped,
so M490's fix still holds for the case M490 actually measured.

## Status

All three are measured and fixed in the porter, verified by re-porting Map2 into a pristine Map453
extracted from the shipped WAD. **Whether this resolves the in-game black is not yet confirmed** —
that needs the reporter to re-port and look.

## 4. These "decals" are not stamps — they are a tiled ground overlay (M549)

The reporter asked for the obvious next thing: *"The mesh should contain the full texture when its
clamped."* It cannot, and the reason is worth recording, because it invalidates the mental model
everything above was built on.

Measured on the M548 port, a ported decal piece is:

* an **irregular terrain patch**, not a quad — triangle counts are odd (21, 19, 13), so these follow
  the ground rather than being planes laid on it;
* about **1,300 world units** across;
* UV-mapped roughly **−0.5 … +1.7**, i.e. the texture **tiles about twice** across each patch,
  touching 5–9 distinct UV tile cells;
* **95.2%** of all decal triangles straddle a 0..1 tile boundary and **0%** sit entirely outside one.

And they are not separate objects. Welding vertices by position and taking connected components
collapses 1,036 pieces into **90 continuous sheets**, some covering 82% of the map. Every decal of a
material is welded into one continuous surface.

So the legacy source has no notion of "one decal instance" here. It is a second ground layer with a
repeating texture, drawn after the terrain with blending. Consequences:

1. **No split can make a mesh hold exactly one full texture.** The UV mapping is continuous and
   tiling across a welded sheet; there is no boundary where one "full texture" ends. Splitting units
   measured, by share of pieces covering a whole 0..1 tile: source mesh 45%, index component 40%,
   position-welded component 100% *but only 90 pieces spanning up to 82% of the map*, world-AABB
   overlap 100% *but only 84 pieces, chaining across the map*. There is no unit that is both whole
   and local, because the geometry is not made of whole local things.
2. **Clamp is the wrong address mode for them, and wrap is right** — which is what §3 already
   concluded from the UV ranges, now confirmed structurally.
3. Clamp would not smear a visible border anyway: measured, the border alpha of these textures is
   4–18 of 255 against an alpha cutoff of 0.3 (76/255), so it is discarded. The black was never a
   clamped border.

If discrete, movable decal planes are wanted, they have to be **authored**, not recovered — generate a
quad per decal at a chosen position with UV 0..1 and drop the legacy patch. That is a different
feature from porting, and it trades away the terrain conformance the legacy patches have.

## 5. The quad generator (M550)

Given §4 — the geometry has no boundary where one texture ends — discrete decal planes have to be
**authored**, not recovered. The UV field is the thing that does carry the information: the texture
repeats once per integer UV tile, so **the tile is the unit**.

For each occupied UV tile, the generator fits `world = origin + du*u + dv*v` by least squares over that
tile's vertices, then places a quad at the tile's UV square, giving it a clean 0..1 of its own.

### Scope the fit to ONE PATCH (M551)

M550 first ran the generator over a whole material at once. That is wrong, and the reporter saw it
immediately: *"it works sometimes on a few meshes. Also the position is not more correct."*

UV tile indices are not unique across the map. Patches in completely different places share tile (0,0),
so grouping by tile globally put them all in one group — 403 patches of `order_base_circle` collapsed
into 30 tiles — and least squares placed the plane at their **average**. The few tiles that happened to
hold a single patch came out right, which is exactly "works sometimes".

| fit scope | planes | distance to the nearest real patch |
|---|---|---|
| per material (M550) | 241 | p50 **1,241** units, max 6,956 |
| per patch, one plane per UV tile (M551) | 2,630 | p50 277 units, max 1,220 |
| **per patch, ONE plane (M552)** | **1,036** | p50 **33** units, max 471 |

Fitted per patch, a plane can only land on the geometry it came from, because that is the only geometry
in the fit. The residual 277 is a tile's offset inside its own ~1,300-unit patch, not misplacement.

### One plane per patch, not one per tile (M552)

M551 still emitted one plane per UV TILE inside each patch. A patch spans about 2.2 tiles, so the same
decal was drawn two or three times over itself - the reporter again: *"I get often double pasted meshes
or more for decals ... It looks now as a not clamped version so repeated images."*

The patch's whole UV extent becomes a single 0..1 instead. That is exactly one plane per decal - 1,036
planes for 1,036 patches - each carrying its image once, with **zero** planes whose UV passes 1.0. The
plane now sits a median of **33 world units** from its patch centre.

All 1,036 are exactly 2 triangles and face up.

The size guard is now **two-sided**, and the second direction is the one that matters: a source far
larger than the quad it produced means the samples never belonged to one tile. Given two patches 5,000
units apart sharing tile (0,0), least squares returns a perfectly ordinary 100-unit plane at their
average, 2,500 units from either — nothing about it looks wrong except where it is. A mis-scoped call
now returns nothing rather than something plausible and misplaced.

The per-tile coverage gate went with it: with one plane per patch there are no partly-entered tiles
left to reject.

### Two guards, and one that did not work

* **Winding.** The quad is flipped to match the average normal of its source triangles. An inverted
  decal is invisible under backface culling, and least squares has no opinion about facing.
* **Size ratio.** A tile whose UVs do not describe a consistent mapping still gets an answer out of
  least squares, and it is a plane at an angle through the ground. A quad more than 3x its source's
  bounding diagonal is dropped. **On Map2 this fires zero times** — the largest quad (12,422 units
  against a p50 of 1,028) is *not* an artifact; its source triangles span the same distance, being a
  seam whose UV stays in one tile while the strip runs across the map.
* **Fit residual — tried and removed.** Rejecting tiles whose vertices sit far from the fitted plane
  sounds right and is not: these patches follow the TERRAIN, so their vertices genuinely do not lie on
  any plane. The residual measures ground unevenness, which is exactly what flattening is meant to
  discard. At a 15% tolerance it rejected 182 of the 241 tiles.

### The trade

A plane does not follow the ground. Over uneven terrain it will clip, which is why the option is **off
by default** and the dialog says so. The lift (default 4 units) keeps them clear of a flat surface;
nothing keeps them clear of a slope.

### Note on Avalonia bindings

The port dialog sets `x:DataType` but bindings still resolve at RUNTIME — verified by pointing one at a
nonexistent property and watching the build succeed. `LegacyDecalQuadTests` walks the axaml's binding
paths against the view models by reflection instead, which is what catches a typo before the window is
ever shown.

### Keep the source texture scale (M554)

M552 gave every plane a clean 0..1. That is stretching, and the reporter saw it: *"the decals are good
placed yeah but somehow they are stretched now so scaled to match the space."*

A legacy patch spans a median of **1.92 x 1.78 UV tiles** - only 30 of 1,036 already fit inside one - so
its texture was authored to REPEAT across it. Squeezing that whole range into 0..1 enlarges the image by
that factor.

The plane now carries the patch's **own UV values**, so the texture lands at the size and density it
had. Measured over the Map2 port, texture size against the original is **p50 1.07** (p10 1.00, p90 1.37),
plane world size against the patch p50 1.07, position error p50 33 units.

The three options are not interchangeable, and only one is right:

| | texture size | coverage |
|---|---|---|
| patch UV range (default) | **unchanged** | full patch |
| clean 0..1 (`SingleImage`) | authored scale, one image | **median 30% of the patch** |
| full range mapped onto 0..1 (M552) | **stretched ~1.9x** | full patch |

`SingleImage` is exposed in the dialog for the cases where one whole image matters more than coverage,
but it is off by default: shrinking a decal to a third of the ground it covered is a bigger change than
letting its texture repeat the way it always did.

## 6. RETRACTED: the blend factor was not the cause (M556, corrected in M557)

M556 concluded that `dstColorBlendFactor = 9` (InvDstAlpha) was the black. **It was not.** The reporter
had set 9 as a deliberate PROBE and I read it as the shipped state. Their actual finding:

| | editor | in game |
|---|---|---|
| **6/7** (the real, authored value) | clean | **BLACK** |
| 6/9 (their test only) | BLACK | BLACK |

So 6/7 was always correct, and the real question is the one they asked: **why does the editor not
reproduce the black at 6/7?**

What M556 does leave standing: 9 really is a value Riot never ships (0 occurrences across 18 map WADs,
against 164 of 165 decal materials at 6/7), the material has been put back to 7, and the validator now
reports 8/9 as a shape issue. Useful, just not the bug.

## 7. The editor was kinder than the game (M557)

Eliminated first, each measured rather than argued:

* **Premultiplied alpha** - 0 of 20 ported decals carry `PREMULTIPLIED_ALPHA` or `MULTIPLY_ALPHA` (no
  macros or switches at all), and Riot pairs that macro with 6/7 anyway, 272 times.
* **Missing textures** - all 86 project-owned texture references resolve to real files.
* **The lightmap** - ported decals and ported ground are IDENTICAL here: both declare
  `Position+Normal+Texcoord0+Texcoord7`, both bind zero lightmap atlases. The ground renders correctly in
  game with that exact setup, so it cannot be what separates them. This independently confirms what the
  reporter said back in M542: *"The issue is not the lightmap stuff."*

That leaves the one real difference between ported Normal (fine in game) and ported Decal (black):
**the blending, and the depth state that goes with it.**

Our own code already describes the mechanism, in M279:

> *A decal authored "transparent cutout - blend - no depth-write" was still given the depth mask, so it
> stamped depth at its own plane and then DEPTH-REJECTED the paving it was supposed to composite over.*

M279 measured that on `base_chasm1`, `new_stone_road` and `grasstuft` - **the same textures being reported
now** - and fixed it by dropping the depth mask on transparents and sorting them into a tail after all
solid geometry. That fixed the EDITOR. The client derives depth-write from the shader CLASS rather than
from the material's blend state, so it still does the old thing, and the map still goes black there.

An editor kinder than its target is worse than one that is wrong in an obvious way: the decal reads
correctly on screen, and there is nothing to explain why the game disagrees.

### The Game Depth toggle

`Dx11SceneBuilder.EmulateClientDepthRules`, exposed as **Game Depth** in the DX11 viewport bar. On,
transparents keep the depth mask and sort with everything else - pre-M279 behaviour, and the client's.

Off by default: it is a diagnostic for "looks right here, wrong in game", not an authoring mode.

One trap worth naming: `UsesAuthoredColorBlend` used to be keyed off `depthWrite`, which the mode forces
true - reading it there would silently drop the authored blend, and a decal that stops compositing
altogether hides the very thing the mode exists to show. It is keyed off the material's own blend state
instead.

**CONFIRMED (M558).** The reporter turned the toggle on: *"tested it, decals are black now with game
depth on."* The editor reproduces the game for the first time in this whole investigation, which settles
the mechanism:

> the client gives the decal the depth mask -> it stamps depth at its own plane -> the ground beneath is
> depth-rejected -> the decal composites over nothing -> BLACK.

That also retires the guessing phase. Any candidate fix can now be judged in the editor with Game Depth
on, instead of by building a package and looking at the game.

## 8. What Riot's decals carry that ours do not (M558)

Riot's decal materials across 18 shipped map WADs, by shader:

| shader | count |
|---|---|
| `SRX_DynamicEffect` | 110 |
| `DefaultEnv_Flat_AlphaTest` (what the porter authors) | 36 |
| `SRX_Blend_Chemtech_Decal` | 16 |
| `DefaultEnv_Flat` | 7 |

So the shader is not disqualifying - Riot ships 36 decals on the same one. Comparing those 36 against our
20, field by field, they agree on everything except one thing:

```
ours   blend=6/7  switches=[]                  macros=[]  params=[TintColor,AlphaTestValue]
Riot   blend=6/7  switches=[MULTIPLY_ALPHA=0]  macros=[]  params=[AlphaTestValue,TintColor]
```

**All 36 author `MULTIPLY_ALPHA` explicitly** - 32 with it off, 4 with it on. We author no switches at
all, and an absent switch takes whatever the client defaults to, which is not necessarily off (M103
recorded the related trap that an absent `on` field reads as ENABLED).

Untested as a fix. It is the only measured difference, the editor can now judge it, and that is the next
thing to try - along with draw ORDER, which is what M279 actually measured going wrong: *"base_chasm1's
decal sorted to draw position 395 of 426 while the ground under it drew at 407-414."* A decal that writes
depth is harmless if it draws after the ground; the damage needs both.
