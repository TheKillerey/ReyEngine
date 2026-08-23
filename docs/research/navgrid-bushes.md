# Where the gameplay bush lives (M561 research)

The question: a viewport toggle showing where the **bush areas** are. Bush *foliage* and bush *gameplay
volume* are different data, and the editor only knew about the first.

## Two different things

* **Bush meshes** — the swaying foliage. Already identifiable: materials whose shader is a `VertexDeform`
  variant, which is exactly the set `LegacyMapPorter.CountBushMeshes` uses and the port dialog counts.
  This is art, and it does not define where vision is blocked.
* **Bush regions** — the vision-blocking volume the game actually plays with. Lives in the **navigation
  grid**, which ReyEngine did not parse at all.

## Finding the file

Not at any of the paths guessed from convention (`data/maps/shipping/…`, `levels/…`, `*.ngrid` on disk —
all zero hits). It is:

```
assets/maps/navgrid/<map>/aipath.aimesh_ngrid
```

found by grepping the repo's own resolved path list, `data/hashes/map453_paths.txt`. Present in every map
WAD checked.

| map | bytes | grid | cell size | world span |
|---|---|---|---|---|
| Map11 | 9,361,843 | 295 x 296 | 50 | 14,720 x 14,759 |
| Map12 | 7,362,915 | 248 x 237 | 50 | 12,356 x 11,844 |
| Map453 | 10,462,721 | 321 x 321 | 50 | 16,000 x 16,000 |

## Header, measured

```
u8    major          7   on all three maps
u16   minor          1
f32x3 min            world-space minimum corner
f32x3 max            world-space maximum corner
f32   cellSize       50.00 on all three
u32   countX
u32   countZ
```

`min`/`max` agree with the maps' real extents, so the grid is a world-aligned XZ lattice: cell (x, z)
covers `min.xz + (x, z) * cellSize`.

## The flag plane

The payload after the header is ~107 bytes per cell, so it is several sections, not one array. Rather
than trust a community layout, the file was scanned for a region behaving like *one small enum per cell
laid out on the grid* — few distinct values, and high correlation between vertically adjacent cells,
because terrain is contiguous and noise is not.

Exactly one candidate survives, on Map11 at byte offset **4,191,399**, two bytes per cell: 21 distinct
values, 33.4% zero, **86.5% vertical correlation**.

It is a bitmask. Per-bit coverage:

| bit | mask | cells | share |
|---|---|---|---|
| 1 | 0x0002 | 34,079 | 39.0% |
| 7 | 0x0080 | 26,725 | 30.6% |
| 6 | 0x0040 | 2,670 | 3.1% |
| 0 | 0x0001 | 2,015 | 2.3% |
| **2** | **0x0004** | **1,085** | **1.2%** |
| 10/11/12 | | 66–76 each | 0.1% |

**Bit 2 is the bush**, strongly supported rather than proven. Rendered on its own it draws about 25 small
clusters in a **diagonally symmetric** layout — which is Summoner's Rift's bush arrangement, and the map
is its own control here. Bit 1 at 39% behaves like unwalkable (it fills the whole border), and bit 7 at
31% like its complement.

## What is still missing

**The plane offset is not yet computable.** 4,191,399 was found by scanning Map11; a parser has to walk
the preceding sections to derive it per file, or the same scan has to run at load. That is the next piece
of work, and it is the only thing between this and a working toggle.

Not yet cross-checked against a known bush coordinate. The symmetry, the cluster count and the 1.2%
coverage all agree, but one measured bush position would settle it.
