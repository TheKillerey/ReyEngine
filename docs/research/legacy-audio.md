# Legacy map audio (M574)

Porting a 2016-client map's Wwise audio into something current League will load.
Everything below is measured; the file or bank it came from is named at each claim.

## The short version

| question | answer |
|---|---|
| Does the legacy client store sound *placements*? | **No.** Nothing to port positionally. |
| Can the legacy banks be copied across? | **No** — BKHD 88 vs the client's 145. |
| Does the audio itself need converting? | **No** — Wwise Vorbis 44.1 kHz either way, 16/16 decode unchanged. |
| How much is already in modern League? | **57 of 88** reachable clips, original media ids intact. |
| What is missing? | **31 clips**, including the 240-second stereo ambient bed. |

## There is no placement data

`LEVELS/Map2` carries `Particles.dat` for VFX but has no audio equivalent.

- `soundmap.ini` is two lines (`200` / `Barrel 200`).
- Nothing in the tree names a bank, a Wwise event, or an ambience.
- `Constants.var` *does* have an `; Audio variables` block, but it is pre-Wwise FMOD config and dead:

  ```
  aud_FMODAmbientEvent  = map_treeline   ; in LEVELS/Map2 — Twisted Treeline's name, copy-pasted
  aud_FMODAmbientEvent  = AmbientEvent   ; in LEVELS/Map1 — a literal placeholder
  aud_FMODReverbPreset  = ReverbPreset
  ```

So a port recovers *what* the sounds were, never *where* they were. Placement has to be authored.

## Bank versions

`BKHD` version, read at offset 8:

| client | version |
|---|---|
| 2016 (`League_Sandbox_Client`) | **88** — all 970 banks |
| current League | **145**, with some **134** |

Wwise refuses a bank whose generator version does not match its reader, so the legacy files cannot be
shipped as-is. The *media* is fine: every reachable ENV_Map1 wem is Wwise Vorbis 44.1 kHz and all 16
decode with vgmstream untouched.

## Classic Summoner's Rift is Map1, not Map2

`LEVELS/Map2` is the classic SR the porter reads, but there is no `ENV_Map2` bank — the legacy client
only has Map1 / Map8 / Map10 / Map11 / Map12 families. Map1 is the right one, and not by elimination:

- Current League's **Map453** (the classic-SR map) declares a `MUS_Map1` bank unit whose events are
  `Play_mus_map01_phase1_early` and friends — Riot equates Map1 with classic SR.
- **53 of the 63** media ids reachable from the legacy `NPC_Map1_SFX` banks appear *verbatim* in
  Map453's shipped `mode_jade_sfx_audio.bnk`.

`LegacyAudioPorter.ResolveBankMapId` uses the level's own number when that family exists and falls back
to Map1 with a note when it does not.

## What Riot kept, and what they dropped

Measured against every bank and pack in `Map453.wad.client` (2,569 media ids):

| legacy family | events | playable clips | already in modern Map453 |
|---|---|---|---|
| `ENV_Map1_SFX` | 8 | 16 | **4** |
| `MISC_Map1_SFX` | 6 | 9 | **0** |
| `NPC_Map1_SFX` | 22 | 63 | **53** |

The four surviving ENV clips are the water lapping that Map453 still places. The ambient bed is gone:
Riot's replacement `Play_sfx_Env_Map453_Ambience_base` is a different 94-second recording, where the
legacy bed is 239.7 seconds of 44.1 kHz stereo.

Note also that `Play_sfx_Env_Map453_Ambience_base` is **defined and playable but never placed** in the
shipped map bin — the only sound placements Map453 ships are eight `waterlappingsmall` emitters.

## The v88 event bug

An Event's action-count field is version-dependent, and reading it at the wrong width fails silently:
the count comes out as 1, the action id is assembled from the wrong four bytes, and the event resolves to
nothing at all. That is why the legacy banks first looked like they held no sounds.

| version | action count |
|---|---|
| 88 (measured, `ENV_Map1_SFX_events.bnk`) | **u32** |
| 134 / 145 (measured, current League) | **u8** |

`BnkFile` switches at wwiser's documented `<= 122`. Nothing here exercises the boundary itself, so the
exact changeover point between 89 and 133 is **unverified**.

## v145 structures used by the writer

Offsets are into the HIRC object *payload* (after the `u8 type, u32 size, u32 id` record header).

### Sound (type 2)

| offset | field |
|---|---|
| 0–3 | plugin id (`0x00040001`) |
| 4 | stream type — `0` in-memory, `2` streamed |
| 5–8 | media (wem) id |
| 9–12 | in-memory media size |
| 13 | source bits |
| **23–26** | **DirectParentID** |

The parent offset was found by scoring every candidate offset by how often the `u32` there names another
object in the same bank, across **11,833** v145 Sound objects from Map453/Map11/Map12: offset 23 hits
**98.9%**, the runner-up 1.0%. At v88 the layout differs — the stream type is a `u32` there, and the
Sound also carries the id of the bank holding its media.

### ActorMixer (type 7)

| offset | field |
|---|---|
| 5–8 | output bus |
| 9–12 | parent (0 = root of its hierarchy) |
| tail | `[u32 childCount][u32 children…]` |

### Action (type 3) and Event (type 4)

Action: `u16` type at 0 (`0x0403` = play, game-object scope), target at 2–5, owning bank id at 10–13.
Event: `u8` count then the action ids (see the v88 note above).

### BKHD

`bankId == FNV-1(file stem)` held for **529 of 529** banks in `Map453.wad.client`. The 16 trailing bytes
shipped v145 banks carry are left zero by the writer: v134 banks ship a 28/32-byte BKHD with that area
absent or zeroed and the client loads them, so it is not validated content.

## Audio routing

Map453's own ambience routes `Play_sfx_Env_Map453_Ambience_base` → Sound `0x135e6e5f` → ActorMixer
`0x3d812b61` → bus **`0x1961115d`**, which no map bank defines, so it comes from the always-loaded
`Init.bnk`. The writer reuses that bus, so ported ambience is mixed exactly like the map's own.

## How a map declares its banks

`MapAudioDataProperties` in `data/maps/shipping/map<N>/map<N>.bin`:

```
MapAudioDataProperties {
  BaseData: link = "Maps/Shipping/Common/Audio"
  bankUnits: list2[embed] = {
    BankUnit {
      name: string = "MODE_Jade_SFX"
      bankPath: list[string] = {
        "ASSETS/Sounds/Wwise2016/SFX/Shared/MODE_Jade_SFX_audio.bnk"
        "ASSETS/Sounds/Wwise2016/SFX/Shared/MODE_Jade_SFX_events.bnk"
      }
      events: list[string] = { … }
    }
  }
}
```

`list2` must be written as an UnorderedContainer and its elements as Embedded — see
`docs/research/*` on bin wire forms; both mistakes load fine and then misbehave in game.

## Status

`WwiseBankWriter` + `LegacyAudioPorter` produce a v145 pair from the legacy Map2 set:
31 clips → `ENV_LegacyPort_Map2_SFX_events.bnk` (3,521 B) + `_audio.bnk` (4,453,525 B).
Read back with this repo's own reader: 31/31 events resolve, 31/31 media byte-identical, 31/31 decode
through vgmstream, every DIDX offset 16-aligned.

**Not yet verified in game.** The structures are cloned from shipped banks and every id rule is measured,
but no generated bank has been loaded by the client yet.
