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
| UI overhaul (icons, skins, chromas, ability bar) | M870 | not started |
| In-app headless validation `--simcheck` | M871 | not started |

## Champions

None processed yet.

## Next

M867, then Kalista.
