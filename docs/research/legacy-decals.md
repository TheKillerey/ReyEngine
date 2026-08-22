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

Two cuts are needed, because each alone leaves the other case standing:

* **Connected component alone** took the widest mesh only from 96.3% to 72.8%, with 107 still over
  10% of the map. The long seam decals are ONE connected strip, so connectivity never separates them.
* **Locality cell alone** would leave two unrelated decals that merely share a cell batched together.

Keying each triangle on `(connected component, floor(centroid / 1000))` does both in one pass. The
cell is 1,000 units: the same size as the bucket grid the port regenerates, and the scale of Riot's
observed clusters.

Result: 20 → 2,166 decal meshes, widest 96.3% → 48.2%. **All 86 meshes still over 10% of the map hold
exactly one triangle** — the floor of what splitting can reach without retessellating.

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
