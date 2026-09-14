# M727 — the Character Viewer's Dominion / Twisted Treeline backdrop under Direct3D 11

**Date:** 2026-09-14 · **Report:** with Direct3D 11 on, the legacy map backdrop loaded but drew entirely
white. Two routes were proposed: port the map to mapgeo, or read the original shaders from the 4.20 client
(`K:\LeagueSandbox\League_Sandbox_Client\DATA\Shaders`).

**Outcome:** the white was a bug (fixed), neither proposed route was taken as stated, and D3D11 now has a
real NVR backdrop pass — material math read out of the legacy client's own shader source, lighting mirrored
from the GL viewport. Along the way the legacy source and the map data showed that **GL itself lights the
backdrop at about half the game's brightness** — recorded here, deliberately not "fixed" in one renderer.

---

## 1. Why it was white

The editor's own session log named it. Every backdrop material registered as

```
shown  prop:backdrop|?  idx 0+0  group -1  dynamic=False
```

The `?` is `BackgroundMapName ?? "?"` in `BackdropProp()` — the prop was built while the map's fields were
unset. Two bugs together:

1. **Assignment order.** `SetBackground` assigned `BackgroundMesh` *first*. It is an `[ObservableProperty]`,
   and its change hook (`OnBackgroundMeshChanged → RebuildSceneProps → BackdropProp()`) runs synchronously
   — before the next line assigns the textures or the name. Every submesh was built with `tex = null`;
   `D3D11MapProps` skips `SetTexture` for a null image; the carrier shader sampled an unbound slot: white.
2. **A mesh-only cache key.** `BackdropProp()` cached on the mesh reference alone, so when the textures
   landed a line later the cache never missed and the white prop stayed for the whole session.

Fix: the mesh is assigned **last**, and both backdrop caches key on the textures as well.

## 2. The two proposed routes

**Porting to mapgeo would not have fixed this window.** `LegacyMapPorter` exists, but the character window's
D3D11 host has no map-renderer channel — M665 established that even a *real* mapgeo arena floor draws
diffuse-only there. A port would also swap the legacy look for modern Riot terrain shaders.

**The legacy shaders are source, not bytecode.** In `DATA/Shaders/HLSL/**`, the `.ps_2_0` / `.vs_2_0` files
begin with the bytes `#inc` and `////` — plain HLSL with a misleading extension. The SM2 bytecode is the
`.ps_2_0_0.bin` twin (`00 02 FF FF`, then a `CTAB` block). So the entire environment set is readable:

| File | What it holds |
|---|---|
| `Environment/LIT_PS`, `LIT_VS` | the runtime NVR shader: `FOUR_BLEND`, `USE_HEIGHT_BLEND`, `MOD2X_COLORMAP`, `DECAL`, `DUAL_COLOR`, `VERTEX_ALPHA`, `WALL_OF_GRASS`, plus `DISABLE_SHADOWS` / `DISABLE_FOW` / `DISABLE_CLOUDS` |
| `Environment/CREATE_GROUND_MOSAIC_FOUR_BLEND_*` | the four-blend lerp chain |
| `HeightBlending/HeightBlending.hls` | `ApplyHeightBlend` |
| `Environment/BAKED_ENV_*`, `UNLIT_DECAL_*`, `NOLIGHTING_*`, `Water_Lake_*` | the rest of the environment family |

`VCOLORSWIZZLE` is an engine-injected macro with no definition on disk; ReyEngine's NVR reader already
decodes vertex colour to true RGBA (`AsBgraU8Array` names its fields), so it is the identity here.

**Decision:** a dedicated D3D11 pass. The *material* math comes from the legacy source (and is identical
to what GL already does); the *lighting* is GL's backdrop recipe, because the brief was D3D11 = GL.

## 3. What the legacy source and the data proved

### NVR material record — `type` @ +260, `flags` @ +264

The raw material block (v9.1, 2988 bytes per record) carries both, and ReyEngine's reader parsed neither.
Census of the real packs:

| type | Map8 Dominion | Map10 Twisted Treeline |
|---|---|---|
| 0 Default | 107, flags `0x0` | 60 × `0x10`, 4 × `0x11`, 6 × `0x14`, 9 × `0x18`, 2 × `0x19` |
| 1 Decal | 24, flags `0x0` | 2 × `0x10`, 1 × `0x18` |
| 2 WallOfGrass | 1 | 1 × `0x10` |
| 3 FourBlend | 8 × `0x1`, **real** blend masks | 2 × `0x11`, blend = `null_black` → height blend |

Flags, as they fall: `0x1` Ground · `0x4` exactly the `_alpha` vines and trees (vertex alpha) · **`0x8` on
every `LM_` material — LightMapped** · `0x10` on every Map10 material and no Map8 one (dual vertex colour).

### Vertex colour streams (probe `nvrcolors`)

- **Map8:** `Position, Normal, PrimaryColor, Texcoord0` (+ `Texcoord7` on the 647 ground meshes). No second stream.
- **Map10:** 2,177 meshes carry **`SecondaryColor`, averaging 0.474** — neutral grey, a Mod2X tint of ≈ ×1.
- **Every `LM_` material has primary colour exactly (0, 0, 0)** across 50,822 vertices: they are lit by the
  composite colour map, never by vertex colour.
- Decals: primary RGB 0, alpha 0.17–0.39 — the fade lives in vertex alpha.

### The divergence — GL versus the game

| Surface | Legacy `LIT_PS` | GL backdrop today |
|---|---|---|
| Map10 vertex-lit statics | `vColor × 2 × secondary × 4` ≈ **vColor × 3.8** | vColor × 2 |
| Map10 composite ground | colorMap × **4** (× (shadowMask·0.5+0.5)) | composite × 2 |
| `LM_` meshes and decals | lit by the **colour map** (`FOUR_BLEND \|\| DECAL`) | fall through to a hardcoded dim night sun (the M142.5/.6 workaround) |
| Map8 statics | `sun·N·2 + vColor × 4 + ambient` from the map's sun settings | flat 0.55 sky, **zero** sun vector, VertexLight slider default 0 |

So GL lights Map10's vertex-lit statics at roughly **half** the game's brightness, and the `LM_` black-mesh
problem M142.5 papered over has a direct answer in the source. **Not changed here**: it would alter GL's
established look, and the two renderers should move together.

## 4. The D3D11 pass

`ShaderPreviewRenderer.Backdrop.cs`, on the sky/overlay pattern (lazy compile remembered on failure, its own
everything):

- 56-byte vertex (pos · normal · UV · 2nd UV · colour); per-frame cbuffer (world, view-projection, sun, sky,
  model flags, up to 256 lights — Map10 ships 95); per-draw cbuffer (layer presence, alpha mode, clamp).
- HLSL is ASCII-only and **samples every texture before the first branch** — no gradient op inside flow
  control. GL's Map8 recipe passes a **zero-length sun vector** (`normalize(0)` is undefined), so the shader
  guards it rather than trusting a driver to return 0.
- Never culled (GL passes `cullBackfaces: false`; M356 is the standing reason not to guess a winding).
  Opaque → cutout → blended, GL's pass order; blended decals test depth without writing it.
- Drawn after `DrawSky`, before the scene pass, testing **and** writing depth so the character composites
  against the map.
- The lighting is `MeshPreviewViewModel.ResolveBackdropFrame` — a pure static mirror of ViewportControl's
  backdrop block, with every term cited, held to GL's numbers by tests.
- While an **arena** owns the backdrop the pass is hidden and no scene is uploaded: the arena's mapgeo floor
  already draws under D3D11 as its own prop.
- The M725 diffuse-only prop remains only as a **fallback** when the pass cannot be built (`Dx11BackdropFailed`),
  so the map is never drawn twice and never silently absent.

## 5. A gate the pixel test caught

`ShaderPreviewRenderer.IsReady` is `device && _materials.Count > 0`, and `RenderFrame` returned
`"no shader loaded"` without it. The backdrop pass owns no material — so a Character Viewer subject that
resolved **no D3D11 scene** (a prop, or a skin the resolver did not understand) would refuse every frame and
take the map with it, where GL still draws it. The old white prop only escaped this because it registered
materials. `RenderFrame`'s gate now counts `HasBackdrop`; `IsReady` itself is unchanged, because the map
window gates vertex and material rebuilds on it.

## 6. Verification

- **Headless D3D11 renders of both real packs** through the real renderer, zero scene materials so every
  pixel is backdrop: Map8 uploads 1,027,174 verts / 3,017 draws / 141 textures; Map10 933,004 / 2,351 / 76.
  The frames were inspected: Dominion shows textured four-blend ground blending into flagstone, cutout
  grass, ground decals and warm Light.dat pools; Twisted Treeline shows its dark-blue height-blended night
  ground with braziers and structures.
- **`NvrBackdropDx11Tests`:** the recipe's exact numbers for both models; source guards on the mesh-last
  ordering, both cache keys, pass order, disposal, the `RenderFrame` gate and the fallback wiring; an
  ASCII / no-sampling-in-branches shader guard; and a device- and data-gated render of each pack that fails on
  low coverage, a white frame, or a black one. Set `REYENGINE_DUMP_BACKDROP=1` to write the frames to `%TEMP%`.
- **Not measured:** a pixel-for-pixel GL-versus-D3D11 comparison. The D3D11 frames match GL's documented
  look qualitatively; exact parity is unverified.

The probes (`nvrcolors`, the material census) live in `.codex_tmp/YonkeyProbe`, gitignored.

---

## 7. M729 — the sun, the sky and every point light

**Report:** not all point lights show, and the sun and sky are not the map's own light settings. Both were
true, for four reasons — two per renderer, so the renderers disagreed with each other as well as with the game.

| | GL | D3D11 (M727) |
|---|---|---|
| Point lights | all of Dominion's 462 (GL uploads and loops to 1024) | the first **256** only (`MaxBackdropLights`): 206 torches never lit anything |
| Dominion's sun | `SetSunLighting(Vector3.Zero, …)`. A zero direction makes GL substitute its **own default** sun (0.75 from (0.4, 0.85, 0.45), sky 0.35) and drop the colours passed, so the Bright slider never reached the picture | read the zero literally: flat `base × brightness`, no directional term |
| Twisted Treeline's night sun | `SetSunLighting((-0.3, -0.85, -0.4))`: a travel direction handed to an API that wants the direction **toward** the sun, so LM_ meshes were lit from below | lit from above |
| The level's authored sun | never used: the packs ship without `terrain.inibin`, and `SetBackground` dropped `bg.Sun` | same |

M727's mirror of the recipe was typed from ViewportControl's call sites, and neither quirk of `SetSunLighting`
is visible there.

### What the game does

`LEVELS/Map8/terrain.inibin` (4.20 client, probe `inibindump`): SUN\*SunLightColor (155, 125, 83), SUN\*SunDir
(−0.5354, −0.8383, −0.1036), SUN\*AmbientLightColor (7, 31, 68).

`LIT_VS` / `LIT_PS` on a level with no colour map:

```
sun      = saturate(-dot(SUN_DIR, N)) * DIRECTIONAL_LIGHT_COLOR * 2     // LIT_VS
lighting = sun * shadowMask * clouds + vColor * 4 + AMBIENT_COLOR        // LIT_PS
final    = saturate(lighting * albedo)
```

There is **no point-light term**. The pools are baked into the vertex colours (probe `nvrlightcorr`, over the packs):

| | Dominion | Twisted Treeline |
|---|---|---|
| vertices / Light.dat lights | 1,027,174 / 462 | 933,004 / 95 |
| mean vertex luminance inside some light's radius | 0.063 | 0.212 |
| mean vertex luminance outside every radius | **0.000** (267,279 vertices) | 0.168 |
| correlation with summed light reach | **0.635** | 0.180 |

Dominion's vertex colours *are* its Light.dat pools. M148's "mask/AO data, NOT light" was wrong. Twisted Treeline's
carry baked night light in general.

### What changed

- **One resolver**, `Services.BackdropLighting.Resolve`, called by GL's backdrop block and by the D3D11 frame. It
  never emits a zero direction. With `k = Bright / 0.55`:
  - authored sun (Dominion): toward-sun = −SunDir, sun = SunLightColor × 2 × k, sky = Ambient × k, vertex light 4;
  - composite (Twisted Treeline): its existing numbers, sun from above;
  - no authored sun: GL's former default, now actually scaled by Bright.
- **`NvrSunSettings.BuiltIn("Map8")`**: the client's numbers for a pack without terrain.inibin, held to the file by a test.
- **Defaults per map, once per map**: runtime Light.dat starts OFF on both (the pools are already baked), and the
  defaults are applied when the map changes. They used to be re-applied with every skin load, so ticking Light.dat on
  lasted until the next skin.
- **D3D11 cap 256 → 1024**, the HLSL arrays sized from the constant; a final `saturate`, as in LIT_PS.
- **The card** names the sun's source and gets a "Map's sun / ambient" toggle.

### Verification

- The affected classes (`BackdropSunTests`, `NvrBackdropDx11Tests`, `BackdropEnvironmentTests`, `ChromaPreviewTests`,
  `ArenaBackdropTests`) pass: 44 tests. The device-gated ones upload 462 lights with 0 dropped, truncate 1,030 to
  1,024 with 6 reported, and render both real packs textured and lit.
- Frames inspected: Dominion is now warm, directional daylight with cool ambient in the shade; Twisted Treeline is
  unchanged.
- Full suite: 2,687 passed, 0 failed.
- **Not verified:** GL visually (there is no headless GL render), and the look in the running app.

### Still different from the game

- No shadow map or cloud layer: in game the sun term is multiplied by `shadowMask` (floor 0.4) and the cloud
  texture, so shaded ground is darker there than in this preview.
- Twisted Treeline keeps the M142 model: composite × 2 (game × 4), statics vColor × 2 (game vColor × 2 × vColor1
  × 4), LM_ meshes on the reference night sun (game: the colour map, inferred from the shader, not measured).
- The **subject** legacy viewer (Open Legacy Map) still hands NvrSunSettings' travel direction to `SetSunLighting`,
  so its sun is upside down on any level that ships terrain.inibin. Not changed here.
- Twisted Treeline's `envlighting.inibin`, `Light_Env.dat` (350 entries, 13 fields) and `LightGrid.dat` (256 × 256
  over the 15,398-unit level) are unexplored.
