# M730 — map bin recipes: the patch updater re-does a forced map instead of carrying it

**Date:** 2026-09-14 · **Report:** "when we update it, it removes that we forced a specific map … if a new map
comes it will not be changed to the new map. The default map can change." · **Projects read:** Map Forcer,
Map Forcer (2), Winter Rift 2025 (all `D:\ReyEngine`), the updater's CommunityDragon cache, the installed 16.18.

## What the files said

`YonkeyProbe mapskins` over every original and forced `map11.bin` on the machine:

| | 16.15 | 16.17 | 16.18 (installed) |
|---|---|---|---|
| registered slots | 36 | **37 — `Hall_Of_Legends` added** | **36 — removed again** |
| Map Forcer (forced) | all 36 → Default's environment (`Base_SRX`, `objectcfg_SRX.cfg`, `Particles_SRX.ini`, `GrassTint_SRX.tex`) | after the update: **only `Hall_Of_Legends` differs from Riot's bin** (hand-forced to `Base_SRX`); the other 32 routes are gone | — |
| Winter Rift 2025 (forced) | — | target `Hall_Of_Legends` ← `Milkshake_SRS`, 24 character skins carried (its own map-skin report) | 36 slots forced **plus a stale unregistered `Hall_Of_Legends` object** the 16.17→16.18 merge "restored" |

The 16.15→16.17 update itself worked as designed — "33 modified object(s) carried, 21 conflicts, mod value
kept" — and that is the problem: the switcher's edit is a **rule** ("every slot loads this environment") and the
updater carried its **result**. The slot 16.17 added was left as Riot shipped it, the game picks the seasonal
slot, so it loaded Hall of Legends. The 21 conflicts were noise: Riot flipping `mGrassTintTexture` to a
WadChunkLink on every slot the mod had also written.

## The design

**Store what was done, not what it produced.** A `BinRecipeRecord` in `project.json` per forced bin:
map id, the base ("target") slot, the source slot, its container link, the M649 carry flag, and a **snapshot** —
the source MapSkin object and its FeatureAudio object as a tiny base64 bin, exact wire forms kept.

**On a patch update the recipe is replayed on both originals** (`BinRecipeRebase`):

```
oldR = replay(recipe, old original)      newR = replay(recipe, new original)
mod == oldR  →  result = newR                              (no merge, no conflicts)
else         →  result = merge3(oldR, mod, newR)           (only the hand edits beyond the recipe)
```

So slots the patch **adds** are forced, slots it **removes** vanish instead of being "restored", Riot's changes to
the source slot flow through (as re-running the switcher by hand would), and a conflict reported is a real one.

**Replay resolves the source** by name → by container link (Riot renames) → the snapshot (Riot vaults seasonal
slots; the update then counts one item to review, because what it forces must still ship or be in the mod). A
snapshot replay is wire-form aligned with the new original (M590). A missing base slot falls back to `Default`
and says so.

**Legacy projects need nothing.** `MapSkinForceRecipe.Infer(mod, oldOriginal)` reads the recipe back out of a
forced bin — one untouched slot whose route values every MapSkin object agrees with, carry detected from the
character-skin fields, the base slot from the one audio profile that changed — and the updater records it.
Verified on the real data: Map Forcer's 16.15 bin **is** `Default` replayed exactly (0 objects differ), and its
replay on 16.17 forces `Hall_Of_Legends`; Winter Rift's bin infers `Milkshake_SRS` with carry, its only remainder
being the stale alias.

**The current Map Forcer bin cannot be inferred** — it is Riot's 16.17 bin with one slot hand-edited, so no slot
explains the others. Re-run the Map Skin Switcher once (Default → any base slot, source `Default`); it now records
the recipe as it saves.

## Not covered

- The **compatible materials container** the switcher also writes (`BuildCompatibleContainer`: server placeable
  keys remapped in the source's `materials.bin`) still rebases as a plain merge. Its `items` map is one property,
  so a patch that changes the source container's items conflicts with it wholesale. A second recipe kind.
- Hand edits to a map bin's other objects are still diffs; the recipe only owns the switch.
- Nothing re-applies recipes without a patch change. Re-running the switcher does the same job.
