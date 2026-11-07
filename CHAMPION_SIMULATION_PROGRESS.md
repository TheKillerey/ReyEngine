# Champion simulation progress

Game build: 16.22.8348910. Plan: `docs/plans/champion-simulation.md`.

## Roster

Detected: 173 champions (174 champion WADs, minus `TFTChampion`, a stub record with no spells).
Order: alphabetical by WAD name, Kalista and Talon first (special cases named in the brief).

| Status | Count |
| --- | --- |
| COMPLETE | 0 |
| AUTO-PASS (in-app check passed, visual sheet review and mechanics review pending) | 136 |
| INCOMPLETE | 37 |
| BLOCKED | 0 |
| NOT STARTED | 0 |

Status rules. COMPLETE needs all three: the in-app `--simcheck` passes on D3D11, the contact sheet was reviewed by eye, and every
special mechanic the brief names for the champion is simulated (adapter where the data cannot say it). AUTO-PASS is the first of
the three only: the check proves that every ability shows its own effect (pixels outside the caster's path or live particles)
and plays Riot's authored clip, not that the mechanic is right (Kalista auto-passes, but W has no sentinel yet).

## Shared framework

| Part | Milestone | State |
| --- | --- | --- |
| Definition model, generator, validator, `--simgen` | M867 | done (9fa6c1c): 173 definitions, 922 ability entries; asset checks 620 pass / 272 incomplete / 30 blocked |
| Remove obsolete preview features, Map11 default | M868 | done (86f0b6f): checked live on D3D11 with Kalista and Ezreal |
| Timeline, player, dummies, camera, controls | M869 | done: planner (dash/blink by tag, stop-at-first-hit, channel, ally rule), player (showcase, replay, pause, speed, cleanup); checked live on D3D11 with Ezreal Q/E |
| UI overhaul (icons, skins, chromas, ability bar) | M870 | done (2c7e002): portrait grid, skin cards, chroma swatches, banner, ability icons; checked live with Talon |
| In-app validation `--simcheck` (real load, real player, D3D11 frames) | M871 | done (6345dd6) |
| Stage memory (13 GB WS / 6.5 GB GPU with Map11) | M872 | done (ee37752): 3.6 GB WS on the D3D11 stage; champion swap 0.5 s |
| Authored clip wins over the composite's guess; composite graph clips; simcheck strictness | M873 | done: clip = authored name in the LOADED skin's graph, then the definition's file, then the composite; 120 authored names were composite graph entries (now resolved); a dash/blink may no longer pass on the caster's own movement; full roster 137 / 36 auto-check, abilities 881 / 41 |

## First validation snapshot (M871, provisional, before M873)

First 20 champions (Aatrox..Briar): 9 COMPLETE, 11 INCOMPLETE, 0 BLOCKED; abilities 91 COMPLETE, 17 INCOMPLETE.
- 13 clip mismatches: the player plays the composite's slot-name guess instead of Riot's authored `mAnimationName`
  (Amumu W/E Crit, Ashe Q Spell3, Anivia Q Spell4, Bard W/E swapped, Ahri E took a skin14 clip); AurelionSol Q/W and
  Akshan E suggest authored names that are graph composite clips (sequencer/selector), not atomic clips.
- 4 abilities with no visual of their own in the data (Anivia P, Ashe P, AurelionSol P, Aphelios extra).

## Roster run (M873.1, 2026-11-07)

`--simcheck all` (3418 s) plus a re-run of the 62 champions with a dash or blink after the review fixes (1260 s). Reasons for the
41 abilities not passing: 33x no visible change, 8x only the caster pose changed. 30 of them are passives (a passive with nothing to
show until attacks or other abilities trigger it), 4 extra spells (Aphelios E and Severum Q, Elise spider Q and W), 7 slots:
Alistar W, Gwen Q, Kaisa Q, Kayn Q2 (assassin, since the dash fix), Leona Q, Talon Q (root), Zilean W.
Memory over a long run: private bytes +37 MB per champion swap (native/GPU, not the checker's frames); see M874 notes.

## Champions

| Champion | Auto-check (M873.1) | Abilities ok / not | Not passing | Status | Open |
| --- | --- | --- | --- | --- | --- |
| Aatrox | COMPLETE | 8 / 0 | - | AUTO-PASS | sheet review |
| Ahri | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Akali | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Akshan | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Alistar | INCOMPLETE | 4 / 1 | W | INCOMPLETE |  |
| Ambessa | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Amumu | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Anivia | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Annie | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Aphelios | INCOMPLETE | 9 / 2 | X:ApheliosEAbility, X:ApheliosSeverumQAbility | INCOMPLETE |  |
| Ashe | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| AurelionSol | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Aurora | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Azir | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Bard | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Belveth | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Blitzcrank | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Brand | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Braum | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Briar | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Caitlyn | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Camille | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Cassiopeia | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Chogath | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Corki | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Darius | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Diana | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| DrMundo | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Draven | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Ekko | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Elise | INCOMPLETE | 5 / 3 | P, X:EliseSpiderQAbility, X:EliseSpiderWAbility | INCOMPLETE |  |
| Evelynn | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Ezreal | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| FiddleSticks | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Fiora | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Fizz | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Galio | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Gangplank | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Garen | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Gnar | COMPLETE | 8 / 0 | - | AUTO-PASS | sheet review |
| Gragas | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Graves | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Gwen | INCOMPLETE | 6 / 2 | P, Q | INCOMPLETE |  |
| Hecarim | COMPLETE | 7 / 0 | - | AUTO-PASS | sheet review |
| Heimerdinger | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Hwei | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Illaoi | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Irelia | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Ivern | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Janna | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| JarvanIV | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Jax | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Jayce | COMPLETE | 8 / 0 | - | AUTO-PASS | sheet review |
| Jhin | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Jinx | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| KSante | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Kaisa | INCOMPLETE | 4 / 1 | Q | INCOMPLETE |  |
| Kalista | COMPLETE | 5 / 0 | - | INCOMPLETE | W sentinel entity, E spear stacks + rend, R ally, passive hop |
| Karma | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Karthus | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Kassadin | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Katarina | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Kayle | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Kayn | INCOMPLETE | 18 / 1 | Q2 (Assassin) | INCOMPLETE |  |
| Kennen | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Khazix | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Kindred | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Kled | COMPLETE | 6 / 0 | - | AUTO-PASS | sheet review |
| KogMaw | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Leblanc | COMPLETE | 8 / 0 | - | AUTO-PASS | sheet review |
| LeeSin | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Leona | INCOMPLETE | 3 / 2 | P, Q | INCOMPLETE |  |
| Lillia | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Lissandra | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Locke | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Lucian | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Lulu | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Lux | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Malphite | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Malzahar | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Maokai | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| MasterYi | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Mel | COMPLETE | 6 / 0 | - | AUTO-PASS | sheet review |
| Milio | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| MissFortune | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| MonkeyKing | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Mordekaiser | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Morgana | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Naafiri | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Nami | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Nasus | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Nautilus | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Neeko | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Nidalee | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Nilah | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Nocturne | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Nunu | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Olaf | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Orianna | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Ornn | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Pantheon | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Poppy | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Pyke | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Qiyana | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Quinn | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Rakan | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Rammus | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| RekSai | COMPLETE | 8 / 0 | - | AUTO-PASS | sheet review |
| Rell | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Renata | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Renekton | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Rengar | COMPLETE | 7 / 0 | - | AUTO-PASS | sheet review |
| Riven | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Rumble | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Ryze | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Samira | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Sejuani | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Senna | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Seraphine | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Sett | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Shaco | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Shen | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Shyvana | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Singed | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Sion | COMPLETE | 8 / 0 | - | AUTO-PASS | sheet review |
| Sivir | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Skarner | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Smolder | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Sona | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Soraka | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Swain | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Sylas | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Syndra | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| TahmKench | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Taliyah | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Talon | INCOMPLETE | 6 / 1 | Q | INCOMPLETE | Q leap, E wall jump, passive stacks + bleed |
| Taric | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Teemo | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Thresh | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Tristana | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Trundle | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Tryndamere | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| TwistedFate | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Twitch | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Udyr | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Urgot | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Varus | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Vayne | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Veigar | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Velkoz | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Vex | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Vi | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Viego | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Viktor | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Vladimir | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Volibear | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Warwick | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Xayah | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Xerath | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| XinZhao | COMPLETE | 6 / 0 | - | AUTO-PASS | sheet review |
| Yasuo | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Yone | INCOMPLETE | 6 / 1 | P | INCOMPLETE |  |
| Yorick | COMPLETE | 7 / 0 | - | AUTO-PASS | sheet review |
| Yunara | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Yuumi | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Zaahen | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Zac | INCOMPLETE | 4 / 1 | P | INCOMPLETE |  |
| Zed | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Zeri | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Ziggs | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Zilean | INCOMPLETE | 4 / 1 | W | INCOMPLETE |  |
| Zoe | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |
| Zyra | COMPLETE | 5 / 0 | - | AUTO-PASS | sheet review |


## Next

M874: shared steps for summons, unit moves, effect markers, attacks and jumps; Kalista and Talon adapters; ally rule by
`mAffectsTypeFlags` (friendly 0x1 without enemy 0x2). Then the sheet review of the AUTO-PASS champions and the passive trigger family.
