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
