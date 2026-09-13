# What a particle's blend mode means

M720. The seventh reading taken from `LeagueToolkit/ltk-manager`'s particle renderer plan: section 2.49
(ADD and SUBTRACT premultiply their colour on the CPU), and what it stands on - the blend enum of 3.2, the
nine modes of 2.19, NONE's depth write from 2.17 and the blend rank of the 2.11 draw order. It supersedes
`q1-blendmodes.md` (M260), whose census it vindicates, and closes Q1 of `renderdoc-capture-plan.md` except
for the factor mapping itself.

## The table

`blendMode` is `ParticleSystem::BLEND_MODE`. The reference renderer's state for each mode, what we drew
before, and the reachable population (1,523,119 reachable emitters of 1,581,956).

| mode | colour blend | before M720 | reachable |
| --- | --- | --- | ---: |
| 0 ADD (absent) | One, One, colour premultiplied on the CPU | SrcAlpha, One (read as 1) | 116,150 |
| 1 ALPHA | SrcAlpha, InvSrcAlpha | **SrcAlpha, One** | 604,446 |
| 2 SUBTRACT | Zero, InvSrcColor - a darken - premultiplied | sprite-decided | 8,962 |
| 3 NONE | no blend, **writes depth** | sprite-decided, no depth | 32,374 |
| 4 ALPHAADD | SrcAlpha, One | SrcAlpha, One - the one match | 755,207 |
| 5 PREMULTIPLIEDALPHA | One, InvSrcAlpha | SrcAlpha, One | 5,722 |
| 6 MIN | One, One, Min | SrcAlpha, InvSrcAlpha | 87 |
| 7 MAX | One, One, Max | SrcAlpha, InvSrcAlpha | 84 |
| 8 TARGETALPHA | InvDestAlpha, DestAlpha | SrcAlpha, InvSrcAlpha | 87 |

767,912 reachable emitters change state - half the particles in the game.

## Why the engine's table and not ours

Ours came from our own renderer's screen, every time: M117 made 3 additive because Kayn's scythe drew black
boxes here, M117c split it on the sprite because a ghost washed out here, M273 split 2 on the sprite because
a smoke drew black rectangles here. Nothing was ever compared with the client. Five independent things
point the other way:

1. **The engine names the enum** (plan 3.2), and it calls 1 ALPHA and 3 NONE.
2. **The schema's default is 0**, and Riot's writer omits defaults: 0 is written zero times in 1,581,956
   emitters while 1 through 8 all appear. The engine calls 0 ADD, the blend a particle defaults to.
3. **Our own M260 art census** classified the textures authored under each mode without knowing the
   enum: 1% additive art under 1, 75% opaque under 3, 58% premultiplied under 5, white-bordered masks under
   6 (the identity for a min), flat opaque colour holds under 8. Every clean result lines up with the
   engine's name. Modes 4 and 7 are mixed, as the names ALPHAADD and MAX allow.
4. **Riot's compiled particle shaders** - all 2,553 permutations of the eleven `particlesystem/*` pixel
   shaders, disassembled - carry no blend axis, and no permutation multiplies the colour by the alpha it
   outputs. So ONE,ONE really does ignore alpha in the client, and a fading additive particle can only fade
   on the CPU: 2.49.
5. **The reference renderer's screen** showed the ADD/ALPHAADD pair on
   `Riven_Base_Q_03_detonate_ult`'s `blastholeDark`.

What is still inference: the factor each mode indexes (2.19 says so), and six of the nine modes have no
screen subject anywhere. A client capture would confirm the factors; it would no longer be needed to
settle which integer is which.

## How it is built

One table in Formats, `VfxBlend`, read by both renderers.

- **The premultiply** lives in `VfxParticleSimulator.BuildInstances`, the one producer of every draw path's
  colour, and runs **before** the `particleColorTexture` gradient: `quad_ps` samples that texture per pixel,
  so its alpha has to reach the output alpha and the alpha test rather than scale the colour. A distorting
  emitter is excluded; its alpha is the warp's mask.
- **`alphaRef` reads its declared default, 5**, when absent. It barely mattered while every mode weighed the
  colour by alpha. Under ONE,ONE and under NONE the test is the only thing that drops a zero-alpha texel,
  so without it an ADD sprite's transparent border and a NONE mesh's cut-outs would draw.
- **NONE writes depth**, behind its own `VfxBlendOptions.NoneWritesDepth`, because that reading is the mesh
  path's and a depth-writing particle is the one change here that can hide a different system. The D3D11
  soft-particle depth snapshot is now taken before the first depth-writing particle as well as the first
  soft one, or a NONE mesh drawn earlier would land in it. Ribbons and heat haze go through the one depth
  chooser, which also gives them the M711 no-test flag they lacked.
- **The soft fade lands per mode.** For ADD and SUBTRACT the rgb lane is forced - after the premultiply
  their alpha is one. ALPHA and ALPHAADD fade alpha, PREMULTIPLIEDALPHA both, and NONE, MIN, MAX and
  TARGETALPHA rgb, which the reference renderer calls a pick. The shared constant-buffer key splits on the
  three vectors.
- **Draw order keys 3 and 4** (`VfxBlend.DrawRank`, the `miscRenderFlags` byte) join inside one system:
  34,997 of 198,195 reachable system occurrences reorder. They reach stencil emitters as the engine's
  comparator does, which parts 3 reachable testers from their writers in our GL stencil emulation. The
  Direct3D 11 map host sorts across the whole map and keeps the first two keys only: the engine separates
  systems by position between keys 2 and 3, a key neither renderer has.
- **One heat-haze predicate**, `VfxBlend.IsDistortion`: GL tested the block, D3D11 the block and its map.
- **The legacy table is one switch away**, `VfxBlendOptions.EngineModes = false`, read when a system is
  built. It maps an absent mode back to 1 so the old picture really comes back.

## Deliberate divergences

- **No particle writes destination alpha.** The engine gives SUBTRACT and TARGETALPHA alpha lanes of their
  own, but both of our presentations composite the target's alpha - the D3D11 readback lands in a
  premultiplied bitmap, the GL framebuffer is blitted into the window - so a lowered alpha shows the window
  through (the reference renderer's 2.48). Both renderers mask alpha out of every particle write; a
  factor pin alone would not hold under MIN and MAX, whose equations ignore the factors.
- **TARGETALPHA draws nothing**, explicitly. Against a destination alpha of one its factors give
  `(1 - 1) * src + 1 * dst`. Drawn as that answer so a GL target whose alpha has drifted cannot show a faint
  quad the D3D11 one does not.
- **`WriteAlphaOnly` draws nothing**: the engine writes no colour for it (249 emitters, all Vex).

Those last two, with MIN, are one technique: Vex's shadow writes a mask into the target's alpha and reads it
back. The pinned alpha makes it unreachable, and it is recorded as one gap rather than 87 inert emitters.

## Checked on a real device

`AatroxTrace blend <champ> <outDir> <skinN> <system> [age]` renders one exactly-named system through the
real D3D11 particle driver under the legacy table, the engine's, and the legacy table again, each on a fresh
device.

| subject | pixels changed | covered, legacy to engine | minimum alpha | repeat of legacy |
| --- | ---: | --- | ---: | --- |
| Riven_Base_Q_03_detonate_ult at 0.25 s | 314 | 442 to 418 | 255 | identical |
| Ahri_Skin77_E_mis at 0.3 s | 9,034 | 14,635 to 14,435 | 255 | identical |
| Aatrox_Base_R_Fear at 0.5 s | 9,109 | 5,202 to 2,677 | 255 | identical |
| Ahri_Base_P_Heal_champion at 0.35 s | 0 | unchanged | 255 | identical |

`R_Fear`'s mode-1 `DarkBackground` stops glowing faint red and draws the dark field its name says. The
OpenGL viewports - the particle editor, the model preview, the workshop - have no headless harness and were
not rendered; they read the same table through `ApplyBlend`.

## Recorded, not built

- **Back-to-front sorting.** 581,518 emitters became order-dependent straight alpha, and we draw a
  system's particles in birth order. The reference renderer sorts ALPHA, PREMULTIPLIEDALPHA, TARGETALPHA
  and SUBTRACT quads by eye distance, which is its own choice; the engine lead is the unread
  `kSortEmittersByPos` flag (1,337 emitters).
- **The system-position key** that would let the map host take keys 3 and 4.
- **NONE across systems.** A NONE mesh's depth can hide particles of a system drawn after it and the
  depth-tested editor overlays; within one system that is the engine's behaviour.
- **Eroding emitters and the colour texture.** `quad_ps` compiled with ALPHA_EROSION binds no
  `PARTICLE_COLOR_TEXTURE`, so the engine applies no gradient to an eroding emitter; ours still does.
- **Labels.** The particle editor still shows `blendMode` as a bare number.
- **The factors** for SUBTRACT, PREMULTIPLIEDALPHA, MIN, MAX and TARGETALPHA, which no screen has shown.

## Game-facing

The troybin converter's fallback for a legacy sprite whose blend could not be decoded wrote `blendMode 1`,
meaning additive under M117's guess. The engine reads 1 as ALPHA. It now writes nothing, which the game
reads as ADD.
