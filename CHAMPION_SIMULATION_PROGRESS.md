# Champion simulation progress

Game build: 16.22.8348910. Plan: `docs/plans/champion-simulation.md`.

## Roster

Detected: 173 champions (174 champion WADs, minus `TFTChampion`, a stub record with no spells).
Order: alphabetical by WAD name, Kalista and Talon first (special cases named in the brief).

| Status | Count |
| --- | --- |
| COMPLETE | 0 |
| INCOMPLETE | 0 |
| BLOCKED | 0 |
| NOT STARTED | 173 |

## Shared framework

| Part | Milestone | State |
| --- | --- | --- |
| Definition model, generator, validator, `--simgen` | M867 | done (9fa6c1c): 173 definitions, 922 ability entries; asset checks 620 pass / 272 incomplete / 30 blocked |
| Remove obsolete preview features, Map11 default | M868 | done (86f0b6f): checked live on D3D11 with Kalista and Ezreal |
| Timeline, player, dummies, camera, controls | M869 | done: planner (dash/blink by tag, stop-at-first-hit, channel, ally rule), player (showcase, replay, pause, speed, cleanup); checked live on D3D11 with Ezreal Q/E |
| UI overhaul (icons, skins, chromas, ability bar) | M870 | done (2c7e002): portrait grid, skin cards, chroma swatches, banner, ability icons; checked live with Talon |
| In-app validation `--simcheck` (real load, real player, D3D11 frames) | M871 | done (6345dd6) |
| Stage memory (13 GB WS / 6.5 GB GPU with Map11) | M872 | in progress: map rebuilt per champion, GL decode + GL upload under D3D11 |
| Authored clip wins over the composite's guess; composite graph clips; simcheck strictness | M873 | next |

## First validation snapshot (M871, provisional, before M873)

First 20 champions (Aatrox..Briar): 9 COMPLETE, 11 INCOMPLETE, 0 BLOCKED; abilities 91 COMPLETE, 17 INCOMPLETE.
- 13 clip mismatches: the player plays the composite's slot-name guess instead of Riot's authored `mAnimationName`
  (Amumu W/E Crit, Ashe Q Spell3, Anivia Q Spell4, Bard W/E swapped, Ahri E took a skin14 clip); AurelionSol Q/W and
  Akshan E suggest authored names that are graph composite clips (sequencer/selector), not atomic clips.
- 4 abilities with no visual of their own in the data (Anivia P, Ashe P, AurelionSol P, Aphelios extra).
Statuses below stay NOT STARTED until the roster is re-run after M872/M873.

## Champions

None processed yet.

## Next

M867, then Kalista.
