# What order particle emitters draw in

M709, the second reading taken back from `LeagueToolkit/ltk-manager`'s particle renderer. The first was the
transparent texel of `vfx-texture-slots.md`.

## The rule

The engine holds seven display lists. An emitter carrying `isGroundLayer` goes to `Render_Ground_Layer`,
which draws **before** the default one, and `pass` orders emitters **inside a list only**. The screen case
that settled it is `AurelionSol_Skin11_E_ExecuteZone_ChildParticle`, where `BG_BrighterInterior5` at pass
599 drew over the rocks of `REFLECTION_SPHERE2` and `REFLECTION_SPHERE4` at passes 102 and 103.

Their implementation is one comparator, not two lists, and it is **five keys deep**:

| key | what it compares |
| --- | --- |
| 1 | the ground layer, true first |
| 2 | `pass`, ascending |
| 3 | a rank derived from the blend mode |
| 4 | the whole `miscRenderFlags` byte |
| 5 | the emitter's own authored index |

Two keys precedence-emulate "two lists with pass inside each" only because their model has two categories.
Their own notes record a sixth key the engine has and they do not: a comparison of the system's position.

**Scope is one system.** Their renderer ranks a child system's emitters after the whole of its parent's and
does not order two systems against each other at all. Ours does the same, so the scope agrees rather than
approximating. "The ground layer draws first" is true inside one system instance and is not a claim about
the frame.

**It is ordering alone.** `groundLayer` reaches no material, no depth state and no blend state in their
renderer. Their depth test comes from `miscRenderFlags & DISABLE_ZBUFFER` and nothing else, which matters
here: `BG_BrighterInterior5` carries that flag, so the artefact that motivated the reading had an
independent sufficient cause. We read no part of `miscRenderFlags` today. The `ground_layer` technique that
projects these emitters onto the terrain is not built by either of us.

## What we ported, and what we did not

Keys 1 and 2. Key 5 we already had: every call site uses a stable `OrderBy` over a list still in authored
order, which is the same tiebreak.

Keys 3 and 4 are deliberately not ported. Key 3 indexes a rank table by the blend mode, and our blend table
disagrees with the engine's on the most common value: `VfxShaderFlags.IsAdditive` treats mode 1 as additive
where the engine's enum calls it ALPHA, and our own doc comment calls our table a guess. Porting a key that
indexes a table we know to be wrong is worse than not porting it. The corpus favours the engine's enum, for
what it is worth: blend mode 0 is authored **zero** times in 1,581,956 emitters while 1 through 8 all
appear, and under Riot's omit-the-default writer rule a value nobody writes is the default - which the
engine's enum calls `add`, the blend a particle system should default to. Settling that is its own reading.

## The population

Measured over 180 archives, 15,581 bins carrying VFX, 214,201 system occurrences, 1,581,956 emitters.

| | |
| --- | --- |
| emitters carrying `isGroundLayer` | 340,834 |
| of those, reachable (survive `IsVisual`) | 333,296 |
| system occurrences that change order | 52,223 of 198,195 |
| emitters that change position | 500,497 |

Unlike the previous reading, the reachable population is essentially the authored one: the visibility filter
removes 2.2% of it, not 99%. Ground-layer emitters skew **low** pass, mean -325 against +175, so most were
already drawing early; the change is carried by the minority authored high.

## This is not a visually inert change

An earlier reading of the numbers said 99.5% of the movers are additive and additive blending commutes, so
the reorder could not change the image. That number reproduces exactly - and only under our own blend table.
Under the engine's enum, 45.4% of the movers are order-dependent, because the largest "additive" bucket in
our table, mode 1 at 43.3% of movers, is the engine's alpha.

Separately, and whatever the blend: **58,114 movers (11.6%) carry distortion, soft particles or a stencil
mode**, and all three are order-dependent by construction. The blend argument never reached them.

So this is a visible change to roughly half a million emitter draws. `VfxDrawOrder.GroundLayerFirst` turns
it off, so an ordering artefact can be identified by bisect rather than argued about.

## The stencil exclusion

An emitter carrying a stencil mode is **never promoted**. Our GL renderer emulates Riot's stencil masking by
drawing a mode-1 writer before the mode-2/3 testers that read its mask, and `ApplyStencil` says in its own
remarks that this works only because emitters draw in pass order. Promoting a tester past its writer makes a
mode-2 emitter test against a mask nobody has written, which draws **nothing**.

| | |
| --- | --- |
| systems holding both a writer and a tester | 1,908 |
| systems where promotion separates a pair | 19 |
| tester emitters that would lose their visuals | 49, of which 44 are mode 2 |
| cost of the exclusion | 20,054 of 333,296 ground emitters keep their authored position |

Named: `Aatrox_Skin30_Q_cas3`, `Kaisa_Skin69_E_active`, `LeeSin_Skin31_W_shield_self`,
`Mordekaiser_Skin58_R_Arena03`, `Nunu_Skin16_R_Cas_Child_stage` and fourteen more. The reason it is only 19
of 1,908 is that stencil work is overwhelmingly ground-layer already - 72% to 82% of stencil emitters carry
the flag against a 21% baseline - so writer and tester are usually promoted together.

The exclusion protects our own emulation's precondition, which is an inference. The alternative compounds it
with a second inference and loses 44 effects. It is a divergence, and it is written down beside the key.

## Which hosts it reaches

Three particle hosts, and only two of them had a draw order at all.

- **OpenGL**, which serves the map viewport, the particle editor, the model preview and the workshop preview
  through one control: one sorted loop dispatching quads, meshes, beams and trails alike. The key goes here.
- **The Direct3D 11 preview window**: registered one material per emitter in authored order, so it had no
  draw order before this - not even `pass`. Now sorted, by the same key.
- **The Direct3D 11 map viewport**: draws in build order, placement-major then authored emitter. Its
  `OrderBy(s => s.Def.Pass)` runs *after* every material has been handed to the renderer, and the renderer
  keeps a non-pipeline-sortable material in submission order - so that sort reaches the quad budget's
  packing and never the picture. `pass` has never ordered this host. Giving it a key it cannot honour would
  move which emitters starve under the budget and change nothing on screen, so it is left alone and the
  comment that claimed otherwise is corrected. Making that host order-bearing is its own change with its own
  measurement.

## Loose ends

- `renderPhaseOverride` is what chooses the display list, and the reference renderer justifies ignoring it
  with "no shipped emitter writes it". Ours do: 1,046 carriers, 814 of them reachable, 110 also ground
  layer, and 18 of those are movers. So 18 emitters - 0.0036% of the movers - may be routed to a list this
  key does not know about. Recorded, not handled.
- `isUniformScale` and `useNavmeshMask` are consumed by the renderer today and still sit in the parked-field
  table, so the editor badges them as having no effect. Nothing in the suite catches that; the guard that
  would is worth building.
