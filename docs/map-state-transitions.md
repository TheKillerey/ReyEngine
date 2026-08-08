# Map state transitions (Summoner's Rift elemental states)

Everything here was read off Riot's shipped data or disassembled bytecode. Anything not measured is
marked as such. Written 2026-08-08 (M384-M402).

## Where the state machine lives

`data/maps/shipping/map11/map11.bin`, on the `Map` object:

```
Maps/Shipping/Map11 [Map]
  VisibilityFlagDefines {MapVisibilityFlagDefinitions}
    FlagDefinitions [8] of {MapVisibilityFlagDefinition}
      name          HASH - Fire, earth, Ocean, CLOUD, Hextech, Chemtech, Void
      PublicName    "Infernal", "Mountain", ...
      BitIndex      1..7 - the mapgeo group visibility BIT INDEX (not a mask)
      TransitionTime SECONDS
    FlagRange { minIndex=1 maxIndex=6 }
    SelectRandomVisibilityFlagForMap = True
```

| flag | PublicName | BitIndex | TransitionTime |
|---|---|---|---|
| Fire | Infernal | 1 | 8 |
| earth | Mountain | 2 | 8 |
| Ocean | Ocean | 3 | 9 |
| CLOUD | Cloud | 4 | 4.5 |
| Hextech | Hextech | 5 | *(none)* |
| Chemtech | Chemtech | 6 | *(none)* |
| Void | Void | 7 | 1 |

Hextech and Chemtech author **no** TransitionTime. That means instant. Absent is not zero and must not
be defaulted. Base is not a named state at all (no BitIndex/PublicName/TransitionTime), so returning to
base is likewise instant.

**Units trap:** `BitIndex` is an INDEX. ReyEngine's `VisibilityLayer.Bit` is a MASK (`1 << i`). Comparing
them directly shifts every state by one position and resolves a real-but-wrong state for every selection
(mask 1 = "Base" matches BitIndex 1 = Fire). Cost one milestone (M387); use
`MapStateData.ResolveGrassTintForMask`.

## Per-state assets

`MapSkin.mAlternateAssets` -> `{MapAlternateAssets}` -> `.mAlternateAssets [n]` of `{MapAlternateAsset}`.
**Two levels, same name** - reading the outer one as a container silently yields zero alternates.

```
mGrassTintTextureName     ("...GrassTint_SRX_Infernal.tex")  <- NOTE: differs from the default's
                                                                mGrassTintTexture
mFowOverlayTextureName
mParticleResourceResolver -> link
mVisibilityFlagName       HASH, not a string
AudioBankUnits
```

### mParticleResourceResolver is NOT a trigger

Measured: all six link targets are class `ResourceResolver`, whose only property is `resourceMap`, a map
of `particleName -> link to a VfxSystemDefinitionData`. It is an **alias/override table** - "while this
state is active, name X means the system at path Y". Proven by 15 entries whose key differs from the
target's leaf name (`SRU_Dragon_P_Infernal_Pickup -> SRU_Dragon_Base_P_Infernal_Pickup`).

Two disproofs of the trigger reading:
- `Ruby_SR_TrialOfDoom_Overload` lists all six resolvers flat in `mResourceResolvers[16]`, ungated. A
  trigger would fire all six states at once.
- Zero of the 27 `*_Transition_*` VfxSystemDefinitionData appear in any resolver, or anywhere in
  map11.bin.

## How geometry actually transitions: it BLENDS

Superseded belief: "geometry swaps instantly, masked by VFX". **Wrong.** There is a dedicated constant
buffer, which is why every sweep of `PerFramePixelCB` missed it:

```
// cbuffer EnvironmentTransitionPixelCB   (also ...VertexCB)
//   float3 TransitionFactorAndDirection;  // Offset: 0  Size: 12
// bound cb2 (PS) / cb3-cb4 (VS)   - NOT marked [unused]
```

Semantics, measured from which components each shader reads:
- `.x` transition-IN factor (0..1)
- `.y` direction flag (always tested `eq ... l(0)`)
- `.z` transition-OUT factor

Three mutually-confirming uses:

1. **Vertex morph** (`vertexdeform.vs`) - geometry interpolates between two shapes:
   `lerp(TEXCOORD5.xyz, POSITION, t)` where `TEXCOORD5` is a second per-vertex position stream, i.e. a
   morph target, and `t = pow(factor, ScaleInFactor | ScaleOutFactor)` selected by the direction flag.
2. **World-space radial dissolve** (`srx_blend_ocean.ps`) - a wavefront at `factor.x * 8500` units from
   a fixed origin, sine-wobbled, smoothstepped for a soft edge, with `discard` killing old-or-new
   geometry either side of the front.
3. Colour lerp + displacement in `srx_dynamiceffect`.

Material-side switches in `base_srx.materials.bin`: 37 `env_transition` childTechniques and 21
`ENV_TRANSITION` switches, so it is a **minority of materials**, not the whole map.

## Grass tint crossfade

Separate from the geometry blend and simpler. `staticmesh/vertexdeform.ps.dx11` blob 19:

```
sample r2, t1   // GRASS_TINT_MAP_ALTERNATE  - state being ENTERED
sample r1, t0   // GRASS_TINT_MAP            - state being LEFT
add   r2, -r1, r2
mad   r1, cb1[16].yyyy, r2, r1     // lerp; cb1[16].y = PerFramePixelCB+260 = GRASS_INTERP
```

UV is `planarXZ * TERRAIN_XFORM.xy + .zw` with **V flipped** (`add r1.z, -r1.y, l(1.0)`, sampled at
`r1.xz`). Reaches exactly one material on SR - `.../LevelProp/Materials/VertexDeform_inst` - so it tints
foliage, not terrain.

Implemented: M394 (timing), M395 (D3D11), M397 (GL), M396/M400/M401 (driving).

## Transition VFX

27 `*_Transition_*` systems exist as `VfxSystemDefinitionData` in `base_srx.materials.bin` and are
PLACED as `MapParticle` inside `MapPlaceableContainer.items`, carrying `transform`, `mVisibilityFlags`
(a MASK: 2/4/8/16/32/64), a `VisibilityController` link, and a dedicated boolean **`Transitional = True`**
(63 occurrences in that bin, 0 in map11.bin). The six `SRS_*_Transition_DragonPit` have one placement
each; `SRS_Mountain_Transition_Geo` has 4, `SRX_Cloud_Transition_Swoosh` 7.

`Transitional` is almost certainly the "play this only during a transition" gate. NOT yet confirmed
against a consumer - no code reads it in ReyEngine, and its semantics are inferred from the name plus
co-occurrence with the transition systems.

## Not yet implemented

- Playing the transition VFX on a state change (`Transitional` placements).
- The geometry blend: needs `TransitionFactorAndDirection` fed, the `env_transition` technique selected,
  and - for the vertex morph - the TEXCOORD5 morph-target stream, which ReyEngine's mapgeo loader does
  not currently read.
- `mFowOverlayTextureName` and the per-state `AudioBankUnits`.
