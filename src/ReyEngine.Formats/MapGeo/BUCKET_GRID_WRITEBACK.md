# M58 bucket-grid write-back

ReyEngine does not construct `LeagueToolkit.Core.SceneGraph.BucketedGeometry` objects and does not
call `EnvironmentAsset.Write`. In LeagueToolkit 4.1.0-beta.53 the useful constructors are internal,
the v18 grid's second hash is discarded while reading, and the writer hardcodes mapgeo v17. Earlier
ReyEngine milestones also proved that whole-file serialization corrupts real mapgeo variants.

`MapGeoWriter.WriteWithRegeneratedBucketGrids` therefore performs section-level binary replacement:

1. Open the original bytes with LeagueToolkit and use the first decoded grid's eight-float header as
   an exact signature.
2. Parse every raw grid from that candidate offset and require the parse to end exactly where the
   planar-reflector tail begins. Decoded hashes, bounds, resolution, and geometry counts must agree
   with LeagueToolkit. This makes the signature match unambiguous without reimplementing every mesh
   record that precedes it.
3. Build new grids from the decoded map's current world-space triangles.
4. Copy the original prefix, append the regenerated scene-graph section, then copy the original
   planar-reflector suffix byte-for-byte.
5. Reopen the result with LeagueToolkit and independently reparse the raw section. Validate grid
   hashes, resolutions, geometry counts, flags, and every per-face visibility byte before returning.

## Raw grid layout

For mapgeo v15 and newer the section begins with a `u32` grid count. Each enabled grid contains:

```text
v15/v17: u32 controllerHash
v18:     u32 renderRegionHash, u32 controllerHash
          8 x f32: minX, minZ, maxX, maxZ,
                   maxStickOutX, maxStickOutZ, bucketSizeX, bucketSizeZ
          u16 bucketsPerSide, u8 isDisabled, u8 flags
          u32 vertexCount, u32 indexCount
          vertexCount x vec3-f32
          indexCount x u16
          bucketsPerSide^2 x 20-byte GeometryBucket
          indexCount/3 x u8 faceVisibility (when flags bit 0 is set)
```

The 20-byte bucket descriptor is `f32 maxStickOutX`, `f32 maxStickOutZ`, `u32 startIndex`,
`u32 baseVertex`, `u16 insideFaceCount`, `u16 stickingOutFaceCount`. Versions before 15 store one
implicit grid without a count or hash.

The v18 hash order above was verified against real data and is intentionally different from the
names exposed by the pinned LeagueToolkit API: its `BucketedGeometry.VisibilityControllerPathHash`
returns the first raw v18 slot, which real files use for the render-region hash. The mesh-side
`UnknownVersion18Int` is that same render-region identity; the mesh controller hash maps to the
second grid slot. In v15/v17 the only slot maps to the mesh controller hash.

## Regeneration rules

- Group meshes by the logical pair `(controllerHash, renderRegionHash)`; ordinary meshes form the
  `(0, 0)` master grid.
- Ignore triangles wholly outside mapgeo height `Y = -120..5000`.
- M848: leave out VertexDeform meshes without lightmap UVs (`MapBucketGridMeshSelection`, decided by the
  material's shader from the map's materials.bin). That is what Riot's grids show across all 202 shipped
  mapgeos: every such mesh missing, every lightmapped VertexDeform mesh (ARAM, Arena) present.
- Use square dynamic resolution with approximately 120 world units along the larger axis
  (`ceil(extent / 120)`) and a 4x4 minimum (capped at 256x256 as a defensive format/memory limit).
  Riot's master grid is 128x128 buckets of ~120 units; its formula for the smaller grids is not known.
- M848: store every face ONCE per grid, in the cell holding the min-X/min-Z corner of its X/Z box
  (`floor((min - gridMin) / bucketSize)` in single precision). It is inside when its max corner falls
  in the same cell, otherwise sticking out. The client draws planar projection VFX onto these
  triangles additively, so a face copied into every cell it overlapped was painted once per copy:
  bright, stepped washes along the cell lines. Checked against all 13,970,431 faces in Riot's shipped
  grids, the rule reproduces every face's cell, and every inside flag except 82 faces whose max corner
  sits on a cell edge or one float step before it.
- Store inside faces first and sticking-out faces second. Each cell owns a contiguous vertex slice,
  local `u16` indices, stick-out distances, and `u16` face counts. A cell's `MaxStickOutX/Z` is the
  largest overhang of its sticking-out faces past the cell's +X/+Z edge; the grid's is the largest
  of its cells'.
- Emit one visibility byte per face from the source mesh's `VisibilityFlags`.

### Known differences from Riot's grids (measured, not reproduced)

- Riot's grid bounds are the range of the faces' min corners, padded by 10 units; ours are the full
  vertex extent.
- Riot stores render-region grids (`renderRegionHash != 0`, e.g. the FaeLights cards and a Hall of
  Legends statue on Summoner's Rift) in the mesh's local space; ours are in world space.
- Riot leaves out more meshes and triangles than the VertexDeform rule (water lilies, some
  spawn/periphery walls) for reasons not established, and its per-grid bucket counts are not derived.

The existing map save path calls this automatically after surgical transform/AABB patching, so the
project override receives moved geometry and matching regenerated culling grids in one byte array.
