# Champion preview and ability simulation: plan

Branch `claude/integ-m852-m863` (worktree `.codex_tmp/integ`). Game build 16.22.8348910. Tracker: `CHAMPION_SIMULATION_PROGRESS.md`.

## What exists (inspected 2026-11-06)

| System | Where | State |
| --- | --- | --- |
| Character window | `Views/MeshPreviewWindow.axaml`, `MeshPreviewViewModel*.cs` (13 partials) | Tabs: Material, Animate, Play, Scene, Chroma |
| Skin load | `MainWindowViewModel` (~12200): mesh, clips, D3D11 scene, idle effects, VFX closure, actions | Works on 16.22 multi-skin bins |
| Actions (Q/W/E/R, attack, move, recall) | `Formats/Characters/CharacterActions.cs` (M611) | Record-driven; clip lookup tolerant of `Q1`/`Spell4_Call` names |
| Spell data | `Formats/Characters/ChampionSpellData.cs` (M631/M638) | Root spell timing, missiles (speed/duration/width), targeting kind, range, cooldown, mana, clip name, tags, basic attacks |
| Aiming | `Formats/Characters/SpellAim.cs` (M637) | Plans aim, walk-in, hit point by targeting kind |
| Spell composites | `Formats/Vfx/ChampionEvents.cs` (M116) | Name-token grouping (`_q_`, `_mis`, `_tar`); the only bridge to impact VFX (scripts ship nowhere) |
| VFX playback | `VfxPlaybackItem` (StartDelay, TravelTo, AttachBone, TargetBone) | Caster, missile, target, idle effects |
| Clip events | particle and sound events at `mStartFrame / fps` | Works (M84-M91) |
| Arena | `MeshPreviewViewModel.Arena.cs`, `Services/ArenaLoader.cs` (M636) | Picker of shipped maps, navgrid movement, Map11 preselected but not loaded |
| Manual control | `MeshPreviewViewModel.Control.cs` (M613/M637/M638) | "Drive the character": right-click move/attack, QWER cast |
| Map backdrop | Dominion (Map8) / Twisted Treeline (Map10) NVR packs, `Settings.PreviewBackground*` | Shown behind the character when installed |
| Target dummy | `TargetDummyEnabled`, `DummyProps` | One enemy dummy; no ally |
| Riot client metadata | `Plugins/rcp-be-lol-game-data/default-assets2.wad` | `v1/champions/<id>.json` (spell names, icon paths, chromas with colours), champion icons, splash tiles, load screens, chroma images; `ClientNameCatalog` reads names only |

Facts that bound the scope (measured, see memory `arena-playground-m636`, `spell-clip-and-caster-bone-m663`):
- `Scripts.wad.client` luabin64 files are stubs of constants. Damage, dashes, buffs and most impact VFX are compiled into the client. LUABIN gives no ability behaviour.
- 602 of 816 `mHitEffectKey` sit on basic attacks; ability impacts are linked by scripts. The name-token convention is the only bridge.
- No green-screen or black-screen environment exists in the code. The "void" is the empty preview without a map; Map11 as default replaces it.

## Shared design

1. **Simulation definition (data, Formats).** One deterministic JSON per champion, generated from the champion WAD:
   game version, generator version, abilities (P, Q, W, E, R, plus sub-casts and extra spells), each with spell names, clip, cast time,
   targeting, range, missiles (effect system, motion, width, hit system), caster and target systems, clip events, scenario hints
   (enemy, ally, distance), and the evidence source of every field (`authored`, `convention`, `adapter`).
2. **Validator (Formats).** Per ability: clip resolves, effect keys resolve to visual systems, missiles move, targets exist.
   Status COMPLETE / INCOMPLETE / BLOCKED with the reason.
3. **Timeline (Formats, pure).** Definition + scenario positions -> timed events: clip start, VFX items with delays and travel,
   SFX, hits on the dummy, displacement.
4. **Player (App).** Runs a timeline in the Character window on Map11: places champion, enemy and ally dummies, frames the
   camera, cleans up. Controls: ability list from the definition, showcase, replay, reset, pause, speed.
5. **Adapters.** Per-champion code only where the data cannot say it (Kalista W sentinel path, R ally; Talon E wall), kept apart
   from extracted data and labelled as such.
6. **In-app validation.** `ReyEngine.App.exe --simcheck <champ|all>` runs the real preview pipeline headless on D3D11, renders
   sampled frames per ability, measures particle coverage and hit timing, and updates the tracker.

## Adapter evidence (measured 2026-11-06, build 16.22.8348910)

Tag census over the 173 definitions (root abilities): `Trait_PlayerSelectedDashDirection` 59 (dash), `PositiveEffect_Teleport` 16
(blink), `Trait_ChannelSpell` 42, `Trait_RecastOrReplaceSpell` 91, `Trait_Transformation` 8, `Trait_Pet` 7, `Trait_CreateClone` 4,
`Trait_CreateTerrain` 6, `Trait_Toggle` 7, `Trait_Trap` 7, `PositiveEffect_Boon` 66, `Trait_Shield` 54, `Trait_ActiveHeal` 66.
Targeting kinds: Self 171, Location 162, direction 81, SelfAoe 71, Area 69, LocationClamped 37, TargetOrLocation 18, Cone 13,
AreaClamped 11, Target 4, DragDirection 3, WallDetection 2, TerrainLocation 1, TerrainType 1, none 244.

Kalista (`Kalista.wad.client`):
- W Sentinel is a character of its own: `Characters/KalistaSpawn` (record `kalistaspawn.bin`: move speed 560, unit tags
  `Minion_Summon | Minion`), base skin `kalistaspawn/skins/skin0.bin` -> `KalistaSpawn.skn/.skl`, graph `Characters/KalistaSpawn/Animations/Skin0`;
  skins 24/29/30 have their own. KalistaW: Area targeting, castRange 5000, clip Spell2, mCastTime 0.5. W systems: W_Avatar, W_Alerted, W_Glow.
- E: target systems numbered `Kalista_Base_E_Spear_tar`, `E_SpearTar01..09` (stack counts), caster `E_Spear_out*`.
- R: `KalistaRx` (Self, clip Spell4_Call), children `KalistaRAlly` (speed 700), `KalistaRAllyDash`, `KalistaRMis` (Kalista_Base_R_aestheticmis 1500),
  `KalistaRAllyStun*`; systems R_AOE, R_Ally_FadeOut, R_Ally_Vanish_Cone, R_tar. Needs an ally.
- Passive: `KalistaPassiveDashSpell` / `KalistaPassiveDashSpellActual` (hop after an attack); systems P_BladeFall, P_Blade_Hero_Left/Right.
- `KalistaAltar` character (skin30 only) holds the altar spear.

Talon (`Talon.wad.client`): E = `TalonE` (Location, castRange 6000, display 725, clip Spell2, spellCastTime 0.25, total 1.0) with
DataValues `WallJumpDistance` 625 and `WallLockoutWidth` 1250, child `TalonEHop` (Self) and `TalonE2`. W = `TalonW` with
`TalonWMissileOne` / `TalonWMissileTwo` (out and back). R = `TalonR` with `TalonRMisOne` / `TalonRMisTwo`, `TalonRStealth`.
Q = `TalonQ` with `TalonQAttack` / `TalonQDashAttack`. Passive = `TalonPassive*` (Stack, Bleed, Attack).
The M868 stage point has a wall 559 units south (navgrid), inside E's 725 display range.

## Milestones

| M | Scope | State |
| --- | --- | --- |
| M867 | Definition model, generator, validator, `--simgen` | next |
| M868 | Remove manual control, map-backdrop picker, arena picker; Map11 auto-loads as the default environment | |
| M869 | Timeline + player: dummies (enemy, ally), placement, camera, controls, cleanup | |
| M870 | UI: champion grid with icons, skins with tiles/load screens, chromas with swatches, ability bar with names/icons | |
| M871 | `--simcheck` headless validation, tracker writer | |
| M872+ | Adapters: Kalista, Talon, then the roster in alphabetical order | |
