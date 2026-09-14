# S3Yonkey — the complete trace, from spawn source to rendered mob

**Date:** 2026-09-13 · **Patch:** the installed LIVE client · **Question:** what makes S3Yonkey exist
in-game, what is the full reference chain behind it, and what would ReyEngine need to inject an
equivalent custom mob?

Everything below was read out of the shipped WADs with a throwaway harness
(`.codex_tmp/YonkeyProbe`, gitignored) that opens the archive through `WadArchive`, resolves chunk
hashes against `data/hashes/communitydragon/lol/hashes.game.txt.*`, and dumps `.bin` trees with the
FNV-1a names resolved. **Nothing here is inferred; every claim names the file and property it came
from.** Two things are explicitly marked as unresolved.

---

## 0. The one-line answer

> S3Yonkey exists because **two `CharacterMeshEntityTemplate` items in
> `data/maps/mapgeometry/map453/jade_container.materials.bin`** name its record and skin as strings,
> and because **`Maps/Shipping/Map453`'s `MapCharacterList 0x2bf6fd19` lists `Characters/S3Yonkey`**
> so the client preloads it. There is no spawner, no script, no gameplay event, and no server-side
> unit. It is pure decorative scenery — the *simplest* complete character in the game.

It ships **only** in `Map453.wad.client`. Verified: a byte scan for the string `S3Yonkey` across
`Map11`, `Map12`, `Map22`, `Map30`, `DATA`, `Scripts`, `Bootstrap.windows` and `Global.wad.client`
(66,289 chunks) returns **zero** hits. Map453's `mapStringId` is `"JD"` (the jade map).

---

## 1. Entry point (the exact thing that spawns it)

`data/maps/mapgeometry/map453/jade_container.materials.bin`
→ object `[0x50a86c3c] = MapPlaceableContainer`
→ property `items` (a map)
→ **two entries**, keys `0x486ece82` and `0xe2dab4fe`, class **`0x9aa5b4bc` =
`CharacterMeshEntityTemplate`** (wire form: `BinTreeStruct`, i.e. a *pointer*):

```
0x9aa5b4bc <BinTreeStruct> {
  Transform  = <matrix, translation 951.374, 59.830, 2232.067>     BinTreeMatrix44
  name       = 0x059ee15f                                          BinTreeHash     (hash, never a string)
  Character  = SkinCharacterGeComponentDef <BinTreeStruct> {       (POINTER)
    CharacterRecord = "Characters/S3Yonkey/CharacterRecords/Root"   BinTreeString
    Skin            = "Characters/S3Yonkey/Skins/Skin0"             BinTreeString
  }
  CharacterMesh = CharacterMeshGeComponentDef <BinTreeEmbedded> {   (EMBED)
    PlayIdleAnimation = True                                        BinTreeBool
    IdleAnimationName = "Idle1"                                     BinTreeString
  }
}
```

The second entry is identical apart from `name = 0x069ee2f2` and translation
`13546.303, 45.341, 14836.649`. The map is 15000×15000, so that is **one Yonkey in each team's base
corner**.

**No `Team`, no `AttackableUnit`, no `NeutralCamp`, no `mVisibilityFlags`.** That is the whole
difference between scenery and a gameplay unit — see §4.

### The link is a *string*, not a hash

This is why a naive `refs`-style 32-bit-hash scan does **not** find the placement: the placement
stores `"Characters/S3Yonkey/CharacterRecords/Root"` as text. Only the *preload list* uses the
hashed object link `0x81ccb51d`. Any future tooling that maps placements → characters must match on
both forms.

---

## 2. Full reference chains

### 2.1 Existence / preload

```
data/maps/shipping/map453/map453.bin
  [Maps/Shipping/Map453] = Map
    characterLists[7] ──▶ [0x2bf6fd19] = MapCharacterList
                            Characters[15] ──▶ Characters/S3Yonkey (0x81ccb51d)
                                                 └─ [Characters/S3Yonkey] = Character { name = "S3Yonkey" }
```

`0x2bf6fd19` holds 23 links; the Map object has 9 character lists in total. This is exactly the list
`MapCharacterListWriter` already documents and targets.

### 2.2 Placement / visual spawn

```
data/maps/shipping/map453/map453.bin
  [Maps/Shipping/Map453] = Map
    mapSkins[0] ──▶ [0x13719c13] = MapSkin { name = "Default",
                       mMapContainerLink = "Maps/MapGeometry/Map453/JADE_CONTAINER" }   ← a STRING
                        │
data/maps/mapgeometry/map453/jade_container.materials.bin
  [Maps/MapGeometry/Map453/JADE_CONTAINER] = MapContainer
    chunks{ 0xf80a5bef } ──▶ [0x50a86c3c] = MapPlaceableContainer
                               items{ 0x486ece82, 0xe2dab4fe } ──▶ CharacterMeshEntityTemplate (§1)
```

`chunks` has 11 keys; the resolvable ones are `Regions`, `jungle`, `Vfx`, `Audio`. **`0xf80a5bef` —
the key holding the Yonkeys — does not resolve in any hash list available here (unconfirmed name).**
It is simply "the props chunk" by content.

### 2.3 Record → skin → assets

```
Characters/S3Yonkey/CharacterRecords/Root          (data/characters/s3yonkey/s3yonkey.bin, 83 bytes)
  CharacterRecord { mCharacterName = "S3Yonkey", UseableData { flags = 0 } }
  + [Characters/S3Yonkey/Skins/Meta] = SkinCharacterMetaDataProperties {}   ← empty, but present

Characters/S3Yonkey/Skins/Skin0                    (data/characters/s3yonkey/skins/skin0.bin, 323 bytes)
  SkinCharacterDataProperties
    SkinAnimationProperties.AnimationGraphData ──▶ Characters/S3Yonkey/Animations/Skin0 (0x04709361)
    skinMeshProperties = SkinMeshDataProperties
      skeleton         = "ASSETS/Characters/S3Yonkey/Skins/Base/S3Yonkey.skl"   (string, Riot's mixed case)
      simpleSkin       = "ASSETS/Characters/S3Yonkey/Skins/Base/S3Yonkey.skn"   (string)
      texture          ──▶ 0xe45b07f84c6d6b1a = assets/characters/s3yonkey/skins/base/
                                                yonkey_base_tx_cm_v01.tex       (WadChunkLink)
      SkinScale        = 6
      selfIllumination = 1.5
    emoteBuffbone = ""   godrayFXbone = ""
    objectPath = Characters/S3Yonkey/Skins/Skin0 (0x236af730)
  linked: DATA/Characters/S3Yonkey/S3Yonkey.bin, DATA/Characters/S3Yonkey/Animations/Skin0.bin

Characters/S3Yonkey/Animations/Skin0               (data/characters/s3yonkey/animations/skin0.bin, 143 bytes)
  AnimationGraphData
    mClipDataMap{ Idle1 (0x9dd9dc06) } = AtomicClipData
        mFlags = 2                                  ← the loop flag
        mTrackDataName = Default (0x933b5bde)
        mAnimationResourceData.mAnimationFilePath ──▶ 0x96d4cbca84366617 =
            assets/characters/s3yonkey/skins/base/animations/s3yonkey_idle1.anm
    mTrackDataMap{ Default } = TrackData {}
    objectPath = Characters/S3Yonkey/Animations/Skin0 (0x04709361)
```

### 2.4 Mesh → material → texture

There is **no material bin and no `StaticMaterialDef`** anywhere in the chain. The `.skn`'s single
submesh is literally named `lambert1` (a Maya default), and the only texture binding is the
`texture` WadChunkLink on `skinMeshProperties`. A scenery character draws with the default skinned
character shader; `selfIllumination = 1.5` is the only shading knob.

**No `ResourceResolver` either** — worth stating because that is the indirection the question asked
about. `SmallGolem`'s skin shows what one looks like when it exists:
`mResourceResolver ──▶ [Characters/SmallGolem/Skins/Skin0/Resources] = ResourceResolver` whose
`resourceMap` maps the key `0xc7a6c760` — the `mHitEffectKey` named by its two `SpellObject`s — to
`Items/Shared/Particles/Jade_GlobalHit_Physical`. That is how a spell's abstract effect key becomes a
concrete VFX system per skin. S3Yonkey has no spells, so it has no resolver and no keys to resolve.

| Asset | Chunk hash | Measured |
|---|---|---|
| `s3yonkey.skn` | `f4c5c6e2a42d6190` | 2,131 vertices, **1 submesh** `"lambert1"`, 7,569 indices |
| `s3yonkey.skl` | `a185deb7736ab1ca` | **47 joints**, 43 influences, root `Root` → `Hip`/`Spine`, tail, ears, eyes, `Mouth`, `Hair` |
| `s3yonkey_idle1.anm` | `96d4cbca84366617` | **21.167 s @ 30 fps** (~635 frames) |
| `yonkey_base_tx_cm_v01.tex` | `e45b07f84c6d6b1a` | **512×512** |

### 2.5 What is deliberately *absent*

A byte scan of all 17,411 Map453 chunks for `S3Yonkey` returns exactly 5 files (the 4 bins + the
placement bin). A TOC filter for `s3yonkey` returns exactly 8 files. Therefore, **confirmed absent**:

- no spells / attacks (`SpellObject`) — contrast `SmallGolem`, §4
- no buffs, no VFX / particle systems, no `.troybin`
- no audio: no bank, no VO, no SFX event, no `MapAudio` placement near it
- no stats, no collision/selection radius, no `unitTagsString`, no team
- no minimap icon, no tooltip/localisation strings
- no Lua/script reference anywhere in `Scripts.wad.client`
- no `.mapgeo` entry — character placements live **only** in the `.materials.bin`
- the `.project_jade.*` variants listed in CDTB's hash dump (`s3yonkey.project_jade.skn` etc.) are
  **not present in the shipped WAD** at this patch; only the plain names ship.

`data/characters/s3yonkey/skins/root.bin` (`Characters/S3Yonkey/Skins/Root`, hash `0x67c3c2dd`)
exists but **nothing references it** — a scan of every PROP chunk in Map453 finds `0x67c3c2dd` only
inside that file itself. It is a vestigial "Root skin" and is not needed for the placement.

---

## 3. Files involved

**Riot, in `C:\Riot Games\League of Legends\Game\DATA\FINAL\Maps\Shipping\Map453.wad.client`:**

| Path | Size | Role |
|---|---|---|
| `data/maps/shipping/map453/map453.bin` | — | `Map` object: character lists + map skins |
| `data/maps/mapgeometry/map453/jade_container.materials.bin` | — | `MapContainer` + 11 `MapPlaceableContainer`s; **holds the 2 placements** |
| `data/characters/s3yonkey/s3yonkey.bin` | 83 B | `CharacterRecord` + `Skins/Meta` |
| `data/characters/s3yonkey/skins/skin0.bin` | 323 B | `SkinCharacterDataProperties` |
| `data/characters/s3yonkey/skins/root.bin` | 149 B | unreferenced duplicate skin |
| `data/characters/s3yonkey/animations/skin0.bin` | 143 B | `AnimationGraphData`, 1 clip |
| `assets/characters/s3yonkey/skins/base/s3yonkey.skn` | 126,114 B | mesh |
| `assets/characters/s3yonkey/skins/base/s3yonkey.skl` | 5,656 B | rig |
| `assets/characters/s3yonkey/skins/base/animations/s3yonkey_idle1.anm` | 17,602 B | idle |
| `assets/characters/s3yonkey/skins/base/yonkey_base_tx_cm_v01.tex` | 174,788 B | diffuse |

**ReyEngine, already on disk:**

| Path | Role |
|---|---|
| `src/ReyEngine.Formats/Characters/CharacterPackage.cs` | builds the 3 bins — S3Yonkey *is* its documented template |
| `src/ReyEngine.Formats/Characters/CharacterFolderImport.cs` | scans a folder into a package |
| `src/ReyEngine.Formats/MapGeo/MapPlaceableWriter.cs` | writes the `0x9aa5b4bc` placement |
| `src/ReyEngine.Formats/MapGeo/MapPlaceableExtractor.cs` | reads placements back |
| `src/ReyEngine.Formats/MapGeo/MapCharacterListWriter.cs` | registers into `MapCharacterList` |
| `src/ReyEngine.App/ViewModels/MainWindowViewModel.CharacterCreator.cs` | the UI host binding all of it |
| `tests/ReyEngine.Formats.Tests/CharacterPackageTests.cs` | asserts the bin/placement shape |

---

## 4. How the character systems connect — and what a *gameplay* mob adds

S3Yonkey's `CharacterRecord` has **two properties**. `SmallGolem`'s, in the same WAD
(`data/characters/smallgolem/smallgolem.bin`, 1,982 B), has about forty. That contrast is the
cleanest available definition of "scenery vs. mob":

| Layer | S3Yonkey (scenery) | SmallGolem (real monster) |
|---|---|---|
| Placement class | `CharacterMeshEntityTemplate` (`0x9aa5b4bc`) | `AttackableEntityTemplate` (`0xad65d8c4`), grouped under `NeutralCampEntityTemplate` (`0xd178749c`) |
| Team | absent | `Team = TeamGeComponentDef { Team = 300 }` |
| Record | `mCharacterName`, `UseableData{flags=0}` | + `baseHPModifiable 450`, `baseDamageModifiable 30`, `baseArmorModifiable 12`, `BaseMR -10`, `baseMoveSpeedModifiable 285`, `attackRange 150`, `attackSpeed 0.613`, `acquisitionRange 300` |
| Economy | — | `expGivenOnDeath 40`, `goldGivenOnDeath 15`, `experienceRadius 400`, `significance 0.4` |
| Collision / selection | — | `selectionHeight 120`, `selectionRadius 155`, `pathfindingCollisionRadius 24.5747`, `overrideGameplayCollisionRadius 80` |
| Classification | — | `unitTagsString = "Monster"` (the JADE variant: `"Monster \| Monster_Camp \| Monster_Golem"`), `flags = 8398088` |
| Spells | none | `Characters/SmallGolem/Spells/SmallGolem_BasicAttack` + `…BasicAttack2` = `SpellObject` → `SpellDataResource` (`castFrame 23.22`, `mHitBoneName "C_Buffbone_Glb_Chest_Loc"`, `mHitEffectKey 0xc7a6c760`), referenced back from the record's `basicAttack`/`ExtraAttacks` by **name string** |
| Localisation | — | `name = "game_character_displayname_SmallGolem"`, `enemyTooltip = …` |
| Clips | 1 (`Idle1`) | 4 (`golem_idle1`, `golem_run`, `golem_attack1`, `golem_death`) |
| Record variants | one (`Root`) | two — `Root` and `JADE` (`0x5a02f2d6`), the map-specific tuning the placement actually names |

Note the last row: **a placement can name a per-map record variant** (`…/CharacterRecords/JADE`),
which is how Riot retunes the same monster per map without duplicating the skin or mesh.

Animation binding, end to end: `IdleAnimationName = "Idle1"` (a *string* on the placement) →
`mClipDataMap` key `Idle1` = FNV-1a `0x9dd9dc06` (a *hash* in the graph) → `AtomicClipData` →
`mAnimationFilePath` (a *WadChunkLink*) → the `.anm`. The name must resolve through all three forms
or the prop stands still.

---

## 5. Spawn process, step by step

1. Client loads `data/maps/shipping/map453/map453.bin` and finds `[Maps/Shipping/Map453] = Map`.
2. It walks `characterLists` → `0x2bf6fd19` → `Characters/S3Yonkey` and **preloads** that character.
   *This step is not optional:* per the measurement recorded in `MapCharacterListWriter`, a character
   that is placed but not listed is drawn through the **static** path, on a skinned material asking
   for a vertex shader without `NUM_BLEND_WEIGHTS` → `Missing shader constant WORLD_MATRIX` → the
   prop never appears.
3. It walks `mapSkins` → `0x13719c13` → the `mMapContainerLink` **string**
   `"Maps/MapGeometry/Map453/JADE_CONTAINER"` and loads that `MapContainer` out of
   `jade_container.materials.bin` (along with the map's navgrid, sun properties and lightgrid).
4. For each key in `MapContainer.chunks`, it instantiates the `MapPlaceableContainer` and iterates
   `items`.
5. Hitting a `CharacterMeshEntityTemplate`, it builds a game entity at `Transform`, and resolves
   `Character.CharacterRecord` / `Character.Skin` **by string path** to
   `Characters/S3Yonkey/CharacterRecords/Root` and `…/Skins/Skin0` (both already resident from
   step 2).
6. From the skin it loads `simpleSkin` + `skeleton` (string asset paths, resolved as WAD chunks),
   binds `texture` (a direct chunk link), applies `SkinScale = 6` and `selfIllumination = 1.5`.
7. `SkinAnimationProperties.AnimationGraphData` yields the graph; because
   `CharacterMesh.PlayIdleAnimation = True`, the entity plays the clip named by
   `IdleAnimationName` — `Idle1`, whose `mFlags = 2` loops it — from
   `s3yonkey_idle1.anm` (21.2 s, 30 fps).
8. No team, no attackable component, no navgrid footprint ⇒ nothing can target it, click it, or
   collide with it. It animates and nothing else.

---

## 6. ReyEngine comparison

### Already supported — and matching the shipped bytes

| Capability | Where | Verdict vs. S3Yonkey |
|---|---|---|
| Build record/skin/graph bins | `CharacterPackageBuilder.Build` | **Exact.** Same object paths, same `UseableData{flags=0}`, same `Skins/Meta` object, same `skeleton`/`simpleSkin` as strings, `texture` as WadChunkLink, `mFlags = 2` loop, `AtomicClipData` on the `Default` track. |
| Wad path layout | `AssetFolder` / `RecordBinPath` / `SkinBinPath` / `GraphBinPath` | **Exact.** `assets/characters/<n>/skins/base/…`, `data/characters/<n>/…`. |
| Placement item | `MapPlaceableWriter` `CreateCharacter` | **Class `0x9aa5b4bc` correct; `name` as hash correct; `Character` as pointer correct; `CharacterMesh` as embed correct.** Confirmed against the meta DB: `CharacterMeshEntityTemplate.Character` became `Pointer` at revision 7794239 (it was `Embed` before), so the current choice is right for this patch. |
| Preload registration | `MapCharacterListWriter.Register` | **Exact** — it targets list `0x2bf6fd19` by overlap, which is the list S3Yonkey is actually in. |
| Folder import, format upgrade, rig validation | `CharacterFolderImporter` | Beyond what Riot needs (checks blend indices against influences, drops clips that animate no joint of this rig). |
| Reading placements back | `MapPlaceableExtractor` | Finds them structurally via a nested struct carrying `characterRecord`. |

### Gaps

1. **`PlayIdleAnimation` is never written — this is the one that matters.**
   `MapPlaceableWriter` emits only `IdleAnimationName`; the string `PlayIdleAnimation` does not
   occur anywhere in `src/` or `tests/`. The meta DB gives
   `CharacterMeshGeComponentDef.PlayIdleAnimation` **default `false`**, so a prop placed by ReyEngine
   today loads and draws but **stands in bind pose in-game**, while ReyEngine's own viewport animates
   it (`D3D11MapProps` poses every prop with `CanAnimate`). Measured frequency:
   - Map453 `jade_container`: 2 of 26 `CharacterMeshEntityTemplate` items set it — and those two are
     *exactly* the S3Yonkey pair.
   - Map11 `bloom.materials.bin`: **11 of 12** set it (all the Gromp props).
   - It also appears in `milkshake_srs`, `boba_srs`, `boba_srs_act2a/b`, `a22` (Map11) and `crepe`,
     `bilgewater`, `bloom` (Map12).

   So: `IdleAnimationName` alone is the *static* prop form; `PlayIdleAnimation = True` alongside it
   is the *animated* prop form, and S3Yonkey is the reference for the animated one.

2. **`MapAnimatedProp` carries no idle information.** The extractor reads `characterRecord` and
   `skin` but never touches `CharacterMesh`, so a round-trip through the editor cannot show — let
   alone preserve — which clip a shipped prop plays or whether it plays at all.

3. **No per-map record variants.** The builder always writes `CharacterRecords/Root`. Riot's own
   monsters use `CharacterRecords/JADE` etc. to retune per map. Not needed for a Yonkey-class prop;
   needed the moment a mob should behave differently on two maps.

4. **Scenery only.** There is no support for `AttackableEntityTemplate` (`0xad65d8c4`),
   `NeutralCampEntityTemplate` (`0xd178749c`), `TeamGeComponentDef`, stats, spells, collision or
   tags — i.e. nothing that makes a *fightable* mob. `ChampionStats.cs` / `ChampionSpellData.cs` can
   **read** those, but nothing writes them.

5. **`Skins/Root` is not written.** Harmless — it is unreferenced on S3Yonkey too — but worth
   knowing before comparing a generated package to a Riot one file-for-file.

6. ~~`championSkinName` is written and Riot does not write it.~~ **Withdrawn.** S3Yonkey omits it, but
   `data/characters/smallgolem/skins/skin0.bin` carries `championSkinName = "SmallGolem"`, so the
   field is normal on scenery skins and S3Yonkey is the outlier. `CharacterSkinReader` also reads it
   for display. Writing it is correct; nothing to change.

### Fixed — M723

Gaps 1 and 2 are closed; 3 and 4 are deliberately left alone (they are features, not defects, and
implementing a fightable mob would mean designing stats/spells/camp systems this investigation did
not scope).

| Change | File |
|---|---|
| A created placement writes `PlayIdleAnimation = True` whenever a clip is named; naming none keeps the old still form (`IdleAnimationName = "Idle1"`, no flag) | `MapPlaceableWriter.cs` |
| `MapAnimatedProp` gained `IdleAnimation` + `PlaysIdle`, read from the `CharacterMesh` component by field | `MapPlaceableExtractor.cs` |
| With no clip chosen, the viewport now plays the clip the **placement** names instead of guessing the skin's idle (still falling back to the guess when the name resolves to nothing) | `MainWindowViewModel.cs` |
| Both forms asserted, plus the extractor round-trip | `CharacterPackageTests.cs` |

Proven against real data, not a fixture: `MapPlaceableExtractor` over the shipped
`jade_container.materials.bin` returns **94 props, 26 naming a clip, exactly 2 playing it — the two
S3Yonkeys**, matching the raw dump. A placement written by `MapPlaceableWriter` into that same real
1.4 MB bin passes the writer's `UnintendedChange` verifier and comes out field-for-field identical to
a shipped Yonkey item, `PlayIdleAnimation` first:

```
0x9aa5b4bc <BinTreeStruct> {
  Transform = …
  name = 0xfff9dceb <BinTreeHash>
  Character = SkinCharacterGeComponentDef <BinTreeStruct> { CharacterRecord = …  Skin = … }
  CharacterMesh = CharacterMeshGeComponentDef <BinTreeEmbedded> {
    PlayIdleAnimation = True
    IdleAnimationName = "Idle1" } }
```

The viewport still animates shipped props that lack the flag (turrets, inhibitors, the nexus). That
is deliberate: in-game those *do* move, driven by the server the editor does not have, so freezing
them would be less faithful, not more.

---

## 7. Minimum custom mob (the S3Yonkey recipe)

**Ten files, three object paths, two edits to existing map bins.** For a character named `MyMob`:

*Staged assets (4):*
```
assets/characters/mymob/skins/base/mymob.skn
assets/characters/mymob/skins/base/mymob.skl
assets/characters/mymob/skins/base/mymob_tx_cm.tex
assets/characters/mymob/skins/base/animations/mymob_idle1.anm
```

*Bins (3):*
```
data/characters/mymob/mymob.bin           CharacterRecord{mCharacterName, UseableData{flags=0}} + Skins/Meta
data/characters/mymob/skins/skin0.bin     SkinCharacterDataProperties, linked to the other two
data/characters/mymob/animations/skin0.bin AnimationGraphData, clip "Idle1", mFlags=2, Default track
```

*Edits to the map (2):*
```
<map>.materials.bin   + one CharacterMeshEntityTemplate (0x9aa5b4bc) in a MapPlaceableContainer.items
mapNNN.bin            + Characters/MyMob link in a MapCharacterList, + [Characters/MyMob] = Character{name}
```

Non-negotiable wire forms (the client silently drops a property whose form disagrees with its
schema, and a dropped `Character` is an invisible prop):

| Field | Form |
|---|---|
| placement item | `BinTreeStruct`, class `0x9aa5b4bc` |
| `name` (placement) | `BinTreeHash` — **never** a string |
| `Character` | `BinTreeStruct` (pointer), class `SkinCharacterGeComponentDef` |
| `CharacterRecord` / `Skin` | `BinTreeString`, full object paths |
| `CharacterMesh` | `BinTreeEmbedded`, class `CharacterMeshGeComponentDef` |
| `PlayIdleAnimation` | `BinTreeBool` = `True` — **required for animation; default is false** |
| `IdleAnimationName` | `BinTreeString`, must equal a `mClipDataMap` key name |
| `skeleton` / `simpleSkin` | `BinTreeString`, `ASSETS/…` mixed case |
| `texture` / `mAnimationFilePath` | `BinTreeWadChunkLink` (XxHash64 of the lowercased path) |
| clip loop | `mFlags = 2` |

**ReyEngine writes all ten correctly as of M723** — `PlayIdleAnimation` was the one that was missing.

To go beyond scenery to a **fightable** mob, add on top: `TeamGeComponentDef`, placement class
`0xad65d8c4` (`AttackableEntityTemplate`) with `AttackableUnitGeComponentDef`, record stats
(`baseHPModifiable`, `baseDamageModifiable`, `baseMoveSpeedModifiable`, `attackRange/Speed`,
`acquisitionRange`), `selectionRadius` / `selectionHeight` / `pathfindingCollisionRadius`,
`unitTagsString`, `flags`, `SpellObject`s named from `basicAttack.mAttackName`, and — for a jungle
camp — a `NeutralCampEntityTemplate` (`0xd178749c`) with `MinimapIcon`, `CampLevel` and a link to a
camp-config object (`0x3f04641e`). None of that is written by ReyEngine today.

---

## 8. Evidence index

| Claim | Proof |
|---|---|
| Entry point is the jade container | byte scan for `S3Yonkey` over all 17,411 Map453 chunks → 5 files; `data/maps/mapgeometry/map453/jade_container.materials.bin` has 4 occurrences (2 placements × 2 strings) |
| Placement class is `CharacterMeshEntityTemplate` | `data/meta/meta.db.json:19466` — `"0x9aa5b4bc": {"name": "CharacterMeshEntityTemplate", … bases:["0x40a9c74a" = GameEntityTemplate]}` |
| Placements live in `[0x50a86c3c] = MapPlaceableContainer`, `items` | dump of `jade_container.materials.bin`, enclosing object of the two Yonkey entries |
| Container reached via `MapContainer.chunks[0xf80a5bef]` | `[Maps/MapGeometry/Map453/JADE_CONTAINER] = MapContainer` → `chunks` map |
| `MapContainer` reached via `MapSkin` | `map453.bin` `[0x13719c13] = MapSkin { mMapContainerLink = "Maps/MapGeometry/Map453/JADE_CONTAINER" }`, linked from `[Maps/Shipping/Map453] = Map` `mapSkins[0]` |
| Preload list | `map453.bin` `[Maps/Shipping/Map453] = Map` `characterLists[7]` → `[0x2bf6fd19] = MapCharacterList` → `Characters/S3Yonkey (0x81ccb51d)` |
| `Characters/S3Yonkey` object exists | `map453.bin` `[Characters/S3Yonkey] = Character { name = "S3Yonkey" }` |
| Record contents | `data/characters/s3yonkey/s3yonkey.bin`, 83 bytes, 2 objects |
| Skin contents / scale / illumination | `data/characters/s3yonkey/skins/skin0.bin` — `SkinScale = 6`, `selfIllumination = 1.5` |
| Graph / clip / loop flag | `data/characters/s3yonkey/animations/skin0.bin` — `mClipDataMap{Idle1 = 0x9dd9dc06}`, `mFlags = 2` |
| Mesh / rig / clip / texture facts | decoded via `SkinnedMesh.ReadFromSimpleSkin`, `SkeletonDecoder`, `AnimationDecoder`, `TextureDecoder` |
| `PlayIdleAnimation` default is false | `meta.db.json:14871` — `"0x8e058751": {"name":"PlayIdleAnimation", … "default":false}` |
| Only the 2 Yonkeys animate on Map453 | 26 `CharacterMeshEntityTemplate` items, 26 `IdleAnimationName`, **2** `PlayIdleAnimation = True` |
| 11 of 12 animate on Map11 bloom | dump of `data/maps/mapgeometry/map11/bloom.materials.bin`; all 11 are `Sru_Gromp_Prop` |
| `Character` is a Pointer on the current patch | `meta.db.json` `"0x8b3aa710"` — `{"from":7794239,"type":["Pointer",…,"0xaef1db01"]}` |
| ReyEngine never wrote `PlayIdleAnimation` (before M723) | `grep -rn PlayIdleAnimation src/ tests/` → no hits |
| `championSkinName` is normal on shipped props | `data/characters/smallgolem/skins/skin0.bin` — `championSkinName = "SmallGolem"` |
| `ResourceResolver` maps a spell's effect key to a VFX system | same file — `mResourceResolver ──▶ Characters/SmallGolem/Skins/Skin0/Resources`, `resourceMap{0xc7a6c760 → Items/Shared/Particles/Jade_GlobalHit_Physical}` |
| The M723 fix reproduces the shipped shape | `MapPlaceableWriter.WriteEdits` into the real `jade_container.materials.bin` passes `UnintendedChange` and emits `PlayIdleAnimation` + `IdleAnimationName`; the extractor reads 94 props / 26 named / 2 playing |
| ReyEngine's placement shape is otherwise correct | `MapPlaceableWriter.cs:226,295–320`; asserted in `CharacterPackageTests.cs:247` |
| `Skins/Root` is unreferenced | scan of every PROP chunk in Map453 for `0x67c3c2dd` → only `skins/root.bin` itself |
| No spells/VFX/audio/scripts | TOC filter `s3yonkey` → 8 files; `S3Yonkey` string scan of `DATA`, `Scripts`, `Bootstrap`, `Global` (66,289 chunks) → 0 hits |
| SmallGolem contrast | `data/characters/smallgolem/smallgolem.bin`, 1,982 bytes, records `Root` + `JADE (0x5a02f2d6)` + 2 `SpellObject`s |

**Unconfirmed / marked:**

- The name behind `MapContainer.chunks` key **`0xf80a5bef`** is not in any hash list on this machine.
- `NeutralCampEntityTemplate`'s camp-config link field `0x5a4ef4e7` and the boolean `0x7fd43d60` have
  no names in the hash DB; only their types are known (`Link → 0x3f04641e`, `Bool`).
- The two placements' `name` hashes `0x059ee15f` / `0x069ee2f2` do not reverse to any known string.
