# The navigation grid, and what its flags are not (M561-M565)

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

## The offset is derivable (M562)

It did not need a section walk. On Map11 the plane sits at 4,191,399, and

```
4,191,399 - 39 header = 4,191,360 = 87,320 cells x 48
```

exactly. So the section before it is a fixed **48-byte cell record**, and

```
flagPlane = 39 + cellCount * 48        u16 per cell, row-major, X fastest
```

Checked against every shipped map. It lands on a valid plane in all of them:

| map | grid | plane at | distinct | vertical correlation | bush cells |
|---|---|---|---|---|---|
| map11 (Summoner's Rift) | 295 x 296 | 4,191,399 | 21 | 86.5% | 1,085 (1.2%) |
| map12 (Howling Abyss) | 248 x 237 | 2,821,287 | 9 | 98.0% | **0** |
| map21 | 309 x 233 | 3,455,895 | 17 | 95.5% | 1,223 (1.7%) |
| map22 (TFT) | 301 x 301 | 4,348,887 | 2 | 99.9% | **0** |
| map453 | 321 x 321 | 4,946,007 | 23 | 94.3% | 63 (0.1%) |
| map33 | 361 x 361 | 6,255,447 | 1 | - | all zero |

**The zeroes are the confirmation.** Howling Abyss and TFT have no brush, and bit 2 is empty on exactly
those two. That is a fact about the game rather than about the file, which is what makes it worth more
than the pattern-matching that found the bit.

map33's plane is entirely zero. Read as an unused or stub grid rather than a parse failure - the offset
arithmetic works there too, there is simply nothing in it - but it is untested either way.

## Shipped

`NavGrid` in `ReyEngine.Formats.MapGeo` reads the header and the plane, refuses any major other than 7
rather than reading noise off a layout it has not measured, and fails quietly because it is opened
opportunistically beside a map.

The viewport toggle is **Bush**, drawing each bush cell as a flat quad on the navgrid lattice, 12 units
off the floor, in green. It reuses the barycentric wireframe program the bucket-grid overlay already
uses - identical vertex format, different colour.

Still not cross-checked against a single measured bush coordinate in world space. The two empty maps, the
diagonal symmetry and the 1.2% coverage all agree; one known position would close it completely.

## RETRACTED: 0x0004 is not the bush (M565)

The reporter switched the overlay on and looked at what it drew:

> *"It display something but not the bushes. ... It displayed the areas where only the blue team can
> walk."*

So bit 2 is a **team-restricted walk area**, not brush. The M562 argument for it looked strong and was
wrong on every count that mattered:

* ~25 clusters with the diagonal symmetry Summoner's Rift's brush has — team-restricted zones are also
  diagonally symmetric on a symmetric map. The shape did not distinguish them.
* Howling Abyss and TFT scoring exactly zero — read as "no brush there", and both also have no
  team-restricted walking. Same coincidence.
* 1.2% coverage — an argument that a small thing is small.

**The distribution is still true; the label was invented.** A direct look at where the cells land beat
every inference drawn from their shape, and it took one screenshot.

## Every flag is a layer now

The bits are not named anywhere in the code. `NavGrid.PresentFlags()` reports which single-bit flags a
grid contains and how many cells carry each, commonest first, and the editor turns each into its own
toggleable layer with its own colour — a `NavGrid` split button whose flyout lists them as
`0x0004 · bit 2` with a cell count and a percentage.

Naming them is left to whoever is looking at the map, because that is the only method here that has
actually worked. What Summoner's Rift contains:

| mask | bit | cells | share |
|---|---|---|---|
| 0x0002 | 1 | 34,079 | 39.0% |
| 0x0080 | 7 | 26,725 | 30.6% |
| 0x0040 | 6 | 2,670 | 3.1% |
| 0x0001 | 0 | 2,015 | 2.3% |
| 0x0004 | 2 | 1,085 | 1.2% |
| 0x0400 / 0x0800 / 0x1000 | 10/11/12 | 66–76 each | 0.1% |

Only two keep a name in code, and only because they are load-bearing rather than interesting:
`BlockedFlag` (0x0002, which fills the border and is how the plane's alignment is checked) and
`TeamRestrictedFlag` (0x0004, named for what the reporter observed).

## The bits, identified (M568)

Drawn one at a time over a map and read off. **The bush is bit 0**, not bit 2:

| mask | bit | what it is | SR cells |
|---|---|---|---|
| 0x0001 | 0 | **bush** — the vision blocker | 2,015 |
| 0x0002 | 1 | not walkable | 34,079 |
| 0x0004 | 2 | blue side only | 1,085 |
| 0x0008 | 3 | both teams' restricted areas | — |
| 0x0040 | 6 | the outline of the not-walkable area | 2,670 |
| 0x0200 | 9 | another not-walkable region, one area; purpose unclear | — |
| 0x0400 | 10 | blue side only | 70 |
| 0x0800 | 11 | red side only | 66 |
| 0x1000 | 12 | much the same as bit 3 | 76 |
| 0x0080 | 7 | **unidentified** — 31% of SR, nobody has looked | 26,725 |

Look how crowded that is: **2, 3, 10, 11 and 12 are all team restrictions** of one kind or another. That
is why picking the bush from a distribution was never going to work — several bits share the shape, the
symmetry and the rough size, and only one of them is the brush.

Bit 7 keeps no name. It covers 31% of the map and nobody has looked at it, and inventing a label for it
is precisely the mistake this page exists to record.

`NavGrid.LabelFor` carries these, and the layer list shows the name where there is one and
`0x0080 · bit 7 · unidentified` where there is not.
