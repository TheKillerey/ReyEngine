# LTK Manager vs the ReyEngine Character Viewer — the measured delta, and what M724 closed

**Date:** 2026-09-14 · **Reference:** [LeagueToolkit/ltk-manager](https://github.com/LeagueToolkit/ltk-manager)
(shallow clone under `.codex_tmp/ltk-manager`, read-only) · **Method:** nine paired reads — one agent per
axis over LTK's implementation, one over ours, then a skeptic pass that tried to refute every claimed
difference against our actual source.

**108 differences survived refutation, 1 was refuted.** This document is the map; M724 is the first
milestone against it. Every number below was measured against the shipped WADs with a throwaway harness,
not taken from the agents' claims.

> **Scope warning for whoever picks this up.** This is not one milestone's worth of work. Axis 7 alone
> (general VFX renderer parity) holds 27 findings, and the brief that produced this explicitly said not to
> touch the particle renderer except where a gap is *proven* to block character VFX. Land the axes in the
> order below; each one is independently verifiable.

---

## What M724 landed

| # | Was | Now | Proof |
|---|---|---|---|
| 1 | `SubmeshVisibilityEventData` frames never read; a clip's whole show/hide **union** applied from frame 0 to the end | Each event keeps its own `mStartFrame`/`mEndFrame`, folded per frame in start order | Kayn `Transform_Assassin` shows 2 / hides 1 at frame 114; Riven `Recall_Winddown` hides 2 at 0 then shows 3 at 9. **194 of Aatrox's clips and 78 of Gnar's carry >1 event**; 230 of Gnar's events are windowed |
| 2 | Show applied after hide, so SHOW won for the whole clip | SHOW then HIDE **inside each event**, later events beat earlier ones | matches LTK's remove-then-append |
| 3 | The manual tick *was* the computed bool, so the auto pass overwrote it on every clip change | `Override` is a separate layer applied last; `ApplyComputed` never records one | `SubmeshVisibilityTimelineTests` |
| 4 | Visibility never reached D3D11 at all — `Visible` was written once at commit from `initialSubmeshToHide` | `CharacterSlice.SubmeshIndex` → `PreviewMaterial.CharacterSubmeshIndex` → pushed every frame from the same list GL binds | both renderers now read one array |
| 5 | `mTickDuration` ignored; frames→seconds always divided by the .anm's fps | `FrameSeconds()` prefers the clip's tick | **223 of Aatrox's 1,353 clips and 132 of Gnar's 1,130 author one** (the review had this as *unverified*) |
| 6 | `mEndFrame`, `mIsLoop`, `mIsKillEvent` unread | parsed onto `AnimParticleEvent` | Viego 83 / Kayn 63 events author an end frame |
| 7 | Only the **first** `mParticleEventDataPairList` entry used; `mTargetBoneName` never read | every pair becomes an `AnimParticleSpawn` with its own bone + target bone | Riven 72 / Viego 15 events name a target bone |
| 8 | Name fallback read `mParticleName`, which no animation class declares | `mEffectName` first, `mParticleName` kept as a dead-but-harmless third | schema |
| 9 | Four readers of `initialSubmeshToHide`, two splitters — two accepted only spaces, so a comma list produced names with trailing commas that matched nothing | one `SplitSubmeshList`, four callers | `SubmeshVisibilityTimelineTests` |
| 10 | `idleParticlesEffects` collapsed to one record per effect key | `SkinIdleEffects.Read` keeps the list verbatim | **Lux's two wand glows share one key** and differ only by bone; Thresh reads **1,047** records where the key dictionary reached **490** |

### Two schema traps worth remembering

- **`mIsKillEvent` defaults to `true` in the schema.** Reading it literally makes every event that omits it a
  kill event. Census over Aatrox, Riven, Viego, Jhin and Thresh: **absent 3,746, explicitly false 726, true
  ZERO.** Honouring the default would suppress 84% of all champion particle events. We treat absent as
  false and say so in the code — and the practical consequence is that this "fix" is **inert on shipped
  data**; it exists for mod bins.
- **`Position` on `SkinCharacterDataProperties_CharacterIdleEffect` was not observed at all** — 0 of 1,855
  records across six champions. It is read defensively; do not build behaviour on it without a real case.

---

## Still open, in implementation order

The remaining axes, each with its own ordering already worked out by the verify pass. Highest value first.

### A. Idle particles — **mounted in M726**
`SkinIdleEffects.Read` → `MeshPreviewViewModel.SetIdleEffects` → `_idleItems`, published under whatever else
is playing (a clip's events, a spell composite, a hand-picked system) because in game they never stop. Read
from **the loaded skin's own bin** via `TryLoadIdleEffects`, never from the dependency walk. `VfxPlaybackItem`
gained `TargetBone`; a fixed seed stops the effect re-rolling when the character is moved.

The blocker this depended on is also fixed: GL re-anchored bone-attached systems **only inside the animated
branch**, so with no clip — precisely when idle effects play — they sat at the world origin.
`AnchorBoneSystemsToBindPose` handles the no-clip case, with the joint walk cached per skeleton rather than
rebuilt per frame (the habit the D3D11 side was flagged for).

`Position` is read but **unused**: 0 of 1,855 censused records author one, so there is nothing to calibrate
against. `RigExempt` was not added — an idle effect is never offered to the manual-pick rig anyway.

**Not visually verified.** The data path is tested and proven against real bins; nobody has yet rendered a
champion and *looked*. Per AGENTS.md that wants a headless render through the real `ViewportMeshRenderer`
to a PNG, on the user's Windows machine.

### B1. Clip particle events — **done in M726**
Now consumed from the M724 parse: kill events are skipped, **one item per spawn pair** instead of one per
event (an event lighting up both hands lit one), `StartDelay` uses the clip's own tick rather than the
.anm's fps, `mEndFrame` becomes `EndTime`, and `mTargetBoneName` becomes `TargetBone`.

`TargetBone` is consumed by **both** renderers: an explicit world `BeamTarget` wins, then the event's target
bone, then the practice dummy — written as one expression on each side so the order cannot drift.

### B2. The cue clock — **done in M726**
While a clip is selected the sims advance by the **animation's** delta, so pausing freezes the effects,
scrubbing moves them, and slowing the clip keeps each cue on the pose that throws it. Gated on a clip being
selected: with none — idle effects, and the whole of the particle editor — the clock stays real time, because
nothing is driving it and freezing there would mean idle effects that never move. `ParticleSpeed` is
deliberately *not* stacked on top of the clip's own speed.

A looping clip now **replays** rather than rebuilds: `ParticleReplayToken` resets the sims and re-arms their
start delays, where handing over a fresh `VfxPlayback` tore down and rebuilt every simulator, texture upload,
material and pipeline once per loop. The subtlety worth keeping: `VfxParticleSimulator.Reset()` clears the
particles but `Update()` *consumes* `_startDelay` by counting it down, so a replay that does not re-arm it
fires every cue at once on the second pass.

### B3. Clip identity — still open
Clip events are looked up by **.anm file name** (`clips[file]`, first clip wins), so when several clips share
one file only the first clip's events ever fire, and they fire for every clip using that file. The real fix
is larger than a lookup change: the animation list is built from .anm WAD entries, one row per file, so
there is currently no way to *select* the second clip that uses a file. `_allClips` (M663) already carries
the clip-level view this would bind to. Not attempted — a list-model change, not a bug fix.

### C. Bone attachment
GL re-anchors a bone-attached system only while a clip is selected **and** the clock is ticking; D3D11 does
it from bind pose every frame, so the two renderers disagree. The joint's animated scale leaks into emitter
offsets and birth velocities. An unresolved bone leaves the system at the **world** origin rather than on
the character. `isLocalOrientation` is parsed and consumed by nothing (162,164 emitters authored not to
inherit bone rotation). The skin's `skinScale` is never applied in this window at all.

### D. Composite clips
Every non-Atomic clip is **discarded at parse time** — `ParseClips` keeps an entry only when its class hash
is `atomicClipData`. So Sequencer/Parallel/Parametric/Selector clips have no children, no playlist, no
per-step time offsets, and their later steps' events do not exist. Note the trap: `ParallelClipData` shares
`mClipNameList` with Sequencer but resolves to exactly **one** .anm — only the literal Sequencer class
concatenates.

### E. Renderer parity beyond visibility
Three different rules answer "which material draws this submesh" (GL last-writer-wins, D3D11 first-match +
name pass, outliner a third). A submesh D3D11 cannot resolve is simply absent; GL draws a white stand-in.
Props/character draw order is inverted between them. Grid, NVR backdrop and skybox are GL-only in this
window while their toolbar toggles stay live.

### F. Character Viewer backdrop cleanup — **done in M725**
Scope confirmed with the user: **the NVR backdrop only** — Dominion (Map8) and Twisted Treeline (Map10) —
and the Arena (M636, any shipped map, navgrid + A* + dummy) is explicitly **out of scope and was not
touched**.

| Was | Now |
|---|---|
| `ArenaBackdropTests` + `ChampionRenderStateTests` looked for `ReyEngine.sln`; the repo holds `ReyEngine.slnx`, so their `Source()` walk ran off the drive and **16 assertions had never once executed** | marker fixed; all 16 run and pass, and a meta-test now fails the build if the typo returns |
| The same pack was "Dominion" in the wizard and "Crystal Scar (Map8)" in Settings | named once in `SetupService.BackdropDominion` / `BackdropTwistedTreeline` |
| `Activate` set `Installed = true` straight after extraction | re-checks `Scene\room.nvr`, and says so when the package is wrong |
| Backdrop cached by folder path, never invalidated | cached by folder **and** the room's write time, plus an explicit `InvalidatePreviewBackground()` |
| Map10 silently inherited Map8's hand-tuned hero-shot placement (6,400 units off, turned 180°) | `BackdropPlacement(mapName)`: Dominion keeps its tuned numbers, everything else uses the loader's own spawn anchor (zero) |
| Light.dat forced ON for every backdrop, double-counting Twisted Treeline's baked composite | on only when the map ships no composite lightmap, with the enable tick the card never had |
| Unloading an arena cleared the backdrop and never restored it | `ReapplyBackdrop` host hook, invoked from `UnloadArena` |
| `ConfigureArena` ran only for subjects with animation clips, and reset the user's map pick every load | hoisted above the early return, and idempotent |
| The whole backdrop channel was GL-only; ticking D3D11 made the map vanish while its card stayed live | M725 drew it as a diffuse-only prop, which then drew **white**. **M727 replaced it with a real D3D11 NVR pass** — four-blend and height-blend ground, the composite atlas, vertex light and Light.dat, lit by GL's own recipe; the prop survives only as a fallback. See [nvr-backdrop-d3d11.md](nvr-backdrop-d3d11.md) |
| The first-run wizard offered Map8 only | offers both |

Still open here: an in-window backdrop picker (choosing between the two still means a Settings round trip).
The sun is no longer dropped: M729 lights the backdrop with the level's authored sun and ambient on both renderers
(see [nvr-backdrop-d3d11.md](nvr-backdrop-d3d11.md) section 7).

### G. General VFX renderer parity — 27 findings, deliberately last
Only the ones that provably block character VFX should be pulled forward. The rest change every particle
preview in the app and need their own milestone and their own headless before/after. The largest are:
`worldAcceleration` integrated as a force when LTK treats it as a draw-time offset; probability tables
rolling an independent random per channel instead of one chance per particle; spawn volumes filling the
interior when LTK emits from the **surface** unless the shape's flag bit `0x1` says volume;
`childrenProbability` read as a count when it is an **index**; `bindWeight` parked so particles never follow
a moving emitter.

### H. Chromas opened as the skin they recolour — **done in M728**
Reported: Lillia skin 49 (a chroma) showed skin 46. Every read the character window made — textures and render
state, the D3D11 scene, the VFX library and resolver, idle effects, submesh rules, voice events, the Character
Editor's material bin — worked the skin bin out from the **mesh's folder** (`SkinPaths.BinPathForSkn`). A chroma
ships no mesh: `skin49.bin` names `Skins/Skin46/Lillia_Skin46.skn`, with its own skin49 textures,
`Skin49/Materials/*_inst` instances and `Skin49/Resources` resolver, so the folder rule landed on `skin46.bin`.

It was not an edge case. `YonkeyProbe skinmeshcensus` over all 174 champion WADs:

| Skin bins | Count |
|---|---|
| read | 14,749 |
| mesh in the bin's own folder | 3,434 |
| mesh in **another** skin's folder | 11,304 |
| of those, that folder's bin names a different look — previewed as another skin | **10,915** (7,020 chromas, 27 skins, 3,868 not in the client's skin list) |
| of those, that folder's bin names the same look | 383 |
| of those, that folder's bin is not shipped — previewed with no materials | 6 |

| Was | Now |
|---|---|
| The browser handed over the mesh only | `OpenSkin` and the prop route hand over the chosen skin bin with it; `LoadMeshPreviewAsync` resolves one `binPath` through `SkinPaths.PreviewBinPath` and every read takes it |
| Material edits on a chroma were compared against the base skin's bin and never previewed | `PreviewSkinBinHash` and the D3D11 rebuild use the bin the window was opened with |
| Chromas listed as a bare "Skin 49" — `skins.json` nests them in the base skin's `chromas` array | read as named skins with `ChromaOf`; the picker badges them "chroma" |
| The header said `Lillia_Skin46.skn` | `Lillia_Skin46.skn · skin49` |

Deliberately unchanged: audio banks are still found from the mesh's folder — skin 49's `bankUnits` name skin
46's banks, so that answer is right for the case measured (not censused). The main viewport's bare-`.skn` loader
keeps the folder rule, since nothing chose a skin there; `ChromaPreviewTests` fails if any other App code calls it.

---

## Evidence index

| Claim | How it was measured |
|---|---|
| visibility windows, tick durations, end frames, multi-pair spawns, target bones | `YonkeyProbe clips <champion>.wad.client` — parses every animation graph in a wad through the real `ChampionAnimationData` |
| `mIsKillEvent` absent/false/true | `YonkeyProbe killcensus` — walks every nested `ParticleEventData` struct |
| idle effect field set and frequency | `YonkeyProbe idlecensus` |
| the list reader vs the key dictionary | `YonkeyProbe idleread` — both readers over the same bytes |
| Lux's duplicate key | `YonkeyProbe dump Lux.wad.client data/characters/lux/skins/skin0.bin` |
| skins whose mesh lives in another skin's folder, and which of them are chromas | `YonkeyProbe skinmeshcensus <Champions dir> <skins.json> <champion-summary.json>` — both JSON files pulled from `default-assets2.wad` with `YonkeyProbe raw` |

The harness lives in `.codex_tmp/YonkeyProbe` (gitignored). It is a throwaway; rebuild it from this document
rather than trusting a stale copy.
