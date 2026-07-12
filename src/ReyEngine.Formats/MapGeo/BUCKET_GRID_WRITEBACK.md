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
- Use square dynamic resolution with approximately 500 world units along the larger axis and a 4x4
  minimum (capped at 256x256 as a defensive format/memory limit).
- Assign a triangle to every X/Z cell it geometrically overlaps, not only its centroid cell.
- Store inside faces first and sticking-out faces second. Each cell owns a contiguous vertex slice,
  local `u16` indices, stick-out distances, and `u16` face counts.
- Emit one visibility byte per duplicated face from the source mesh's `VisibilityFlags`.

The existing map save path calls this automatically after surgical transform/AABB patching, so the
project override receives moved geometry and matching regenerated culling grids in one byte array.
