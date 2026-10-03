# LTK game-data mods plan

A mod can ship its `.bin` changes as **game-data declarations** that LTK Manager applies over the INSTALLED
game's bins at install time, instead of shipping whole bins. A whole bin goes stale with every Riot patch; a
declaration (and above all a `ref` to Riot's own data) does not. Agreed 2026-10-03, prompted by
`winter-rift-2025_0.3.0.fantome` (by Crauzer, built with `ltk_mod_project 0.16.2`): a complete Snowdown
Summoner's Rift whose WAD carries 346 textures and one .mapgeo and NO .bin. All of its bin changes are 13
modules in `META/info.json`, across the layers `base`, `snowdown-baron` and `snowdown-minions`.

## Milestones (export first: it is what makes ReyEngine's own mods work this way)

| | Milestone | Done when |
|---|---|---|
| 1 | **Export the layered format.** Project > Export .fantome writes LTK's layout: `META/info.json` with `Layers{name:{Name, DisplayName, Priority, GameData}}`, the base layer's files in `WAD/`, other layers in `WAD_<layer>/`, a harvested hashtable, `Generator`. With the project's declarations setting on, every declarable game bin ships as GameData (M757's diff, in the JSON form); the rest ship whole with the reason logged. | LTK's own reader (`ltk_fantome`) reads the export, and `ltk_game_data` applied over Riot's bins gives the project's bins byte for byte (harness). |
| 2 | **Map Skin Switcher by reference.** A forced map skin becomes references to the source skin (`Default.mMapContainerLink = ref Milkshake_SRS:mMapContainerLink`, ...) plus the server-placeable shop fix as `+items`/`-items` with refs, exactly as the sample does, instead of a whole map11.bin and materials.bin. | Harness: applied over the current Riot bins it equals what the switcher writes today; the project ships no map11.bin. |
| 3 | **Import the layered format.** Layers, `WAD_<layer>/` folders and `META/hashes/*.txt` come in; GameData is kept verbatim (refs and clones intact) and re-exported unchanged. | Crauzer's file imports with 3 layers and named chunks; re-export is byte-equal in GameData. |
| 4 | **Apply engine (C#).** `ltk_game_data` apply in C# so the editor previews a declaration-only mod. | Byte-equal to the Rust engine on the sample and on generated documents. |

## Done

**Milestone 1 = M814 (b91ab81).** The layered export, GameData, harvested hashtable and atomic write.
- Module names are OFF, because LTK Manager 1.21 pairs ltk_fantome 0.14.2 with ltk_game_data 0.6.0, which
  refuses them.

**Milestone 2 = M815.** A forced map skin ships by reference (`MapSkinDeclarations`).
- **What becomes references:** every MapSkin object except the source gets, as a ref to the source slot:
  - each route field that both it and the source carry;
  - the opted-in character-skin fields;
  - the routed FeatureAudio properties.
- **The container's shop key moves** are `-items`/`+items` with refs. A ref is kept only where the project
  already holds the value it produces.
- **Hand edits** follow in a literal module, diffed against the game with those refs applied.
- **Results:**
  - 5 real switches (Map11 Milkshake/Sodapop, Map12 Trueshot/Odyssey) apply equal to the switcher's output
    over the installed game.
  - After a simulated source-slot patch the refs give the patched result byte for byte, while literals stay
    stale.
  - The Winter Rift 2025 copy ships no map11.bin and no container bin.
- **Where ReyEngine differs from the sample** (it follows its own switcher):
  - It routes every slot and alias, not only Default.
  - It does not route `mResourceResolvers`.
  - It refs every route field both carry, including ones equal today.
  - It routes audio unless the recipe skips it.
  - It does not set the shop `name`s (BuildCompatibleContainer moves keys only).
  - It writes `{class, set}` where the sample used `clone`.
- **A patch that removes or retypes a referenced field** makes LTK skip those edits, as a warning in LTK
  Manager's log only. ReyEngine re-reads the installed game on every export, and falls back to values (with
  a note) when the source is gone.
- **References are decided per group.** The groups are a slot's route fields, a slot's character-skin
  fields, the audio profile, and one container's key moves. A group stays by reference only if every field
  in it is held; otherwise the whole group ships as values. A slot is never left half-switched.
- **Adds need the class schema.** Fields added to slots that lack them (35 of 36 Map11 slots with the
  character-skin carry) are typed from LTK Manager's class schema. The databases bundled with 1.20 and 1.21
  type them; a game build newer than the database leaves them `Untypable` until the manager refreshes it.
  The export note says so.
- **Container re-keying.** If a Riot patch re-keys the source container's shop placeables, `-items` fails
  (RemovalUnmatched), LTK skips the whole move, and the server's shop keys may be missing (a StartSpawn
  crash). Export again after a patch.
- **The floor is LTK Manager 1.21.** Measured against the crates each manager pins:
  - 1.20.0 pins ltk_game_data 0.4.0, ltk_overlay 0.12.0 and ltk_fantome 0.14.0. It reads refs and
    `-items`/`+items`, but refuses the `objects` binding outright (the whole layer is dropped) and resolves
    a hash-form field name (`0x2d3285eb`) as text (`Untypable`).
  - 1.21.0 (ltk_game_data 0.6.0) and 0.8.0 pass on all five switches and on the Winter Rift copy.
  - The setting's label says 1.21+.

## The format (league-mod @219d84a: ltk_mod_project 0.16.2, ltk_game_data 0.8.0, ltk_fantome 0.15.1)

Clone: `.codex_tmp/league-mod` (`git pull` before re-verifying). Abbreviations: `gd/` = crates/ltk_game_data/src,
`ov/` = crates/ltk_overlay/src, `fa/` = crates/ltk_fantome/src, `mp/` = crates/ltk_mod_project/src,
`ht/` = crates/ltk_hashtable/src.

**.fantome layout** (`fa/reader.rs:795-878`, `crates/ltk_fantome/DESIGN.md:25-42`).
- WAD placement:
  - base layer: `WAD/<x>.wad.client`, packed or as a loose folder `WAD/<x>.wad.client/<path>`;
  - other layers: `WAD_<layer>/` (names `[A-Za-z0-9_-]+`, never `base`);
  - `RAW/`: base only.
  - Prefixes match case-insensitively.
- A GameData-only layer needs no WAD directory.
- `META/info.json` is matched case-insensitively, and a BOM is stripped.

**info.json fields** (case-sensitive names; `fa/lib.rs:70-133,227-267`).
- Required: `Name`, `Author`, `Description`.
- Optional:
  - `Version` (sanitised to semver);
  - `License` (a string, or `{Name, Url?}`);
  - `Tags`, `Champions`, `Maps`;
  - `Hashtables[{Path, Category:"game", Algorithm:"xxh64", Bits:64}]`;
  - `Generator`.
- Each layer needs `Name` and `Priority` (i32); `GameData`, `DisplayName` and `StringOverrides` are optional.
  Keep the key and `Name` identical.
- Any parse failure makes the whole mod unreadable.
- Key order is preserved (serde_json `preserve_order`): write keys in a stable insertion order.
- ltk_mod_project writes pretty-printed JSON and includes the base layer in `Layers`
  (`mp/fantome/pack.rs:508-527`).

**GameData document** (`gd/lib.rs:51-83`, `gd/document.rs:147-190`).
- The document is `{"version":1,"modules":[...]}`; unknown keys are refused.
- A module has:
  - an optional non-empty `name` (0.7+);
  - EITHER `target` plus a non-empty `edits`, OR `entries`;
  - a REQUIRED `origin` `{"manifest":"game_data.yaml","source":null,"module":<index>}`, used for
    diagnostics only.
- `target`: a path (xxh64 of the ASCII-lowercased path, seed 0) or exactly 16 hex characters with no `0x`.
  It is looked up across the whole game index.
- Never emit an empty `entries`: at this commit it can panic the overlay build.

**Edits** (`gd/apply/mod.rs:488-555`).
- Phases run in this order: overrides, object creations, entry edits, object removals, `-links`, `links`.
- Edit keys:
  - `overrides`, `objects`, `links` or `+links` (never both), `-links`;
  - every other key is an entry name containing `/`, or `0x` plus 8 hex digits (otherwise the whole layer
    is refused).
- `objects` values: `{"clone":"<src>","set":{}}`, `{"class":"<Name|0x...>","set":{}}` or `{"remove":true}`.
  - A clone's source must already be in the TARGET bin.
  - A clone rewrites the top-level hash/string self-references to the new name.

**Property keys.**
- An optional sign `+` / `-`, then `seg(.seg)*`, where `seg` is `name`, `name[idx]` (decimal, never a leading
  zero) or `name{key}`.
- A name is any characters except `.[]{}()`. `0x` plus 8 hex digits is a raw field hash; anything else is
  FNV-1a of the lowercased name.
- `{key}` in a path:
  - hash keys: `{"plaintext"}` or the decimal value `{505318251}` (never `{0x...}`);
  - string keys: `{"text"}`;
  - file keys: `{"path"}` or a decimal u64.
- `+list:[..]` appends; `+map:{k:v}` adds or replaces; `-list:[v..]` removes equal elements; `-map:[k..]`
  removes keys.

**Values** (`gd/apply/coerce.rs:118-261`; the writer is the exact inverse, `gd/render.rs:99-231`).
- bool: `true` / `false`. flag: a bool, or 0 / 1.
- Integers: JSON integers, range-checked; never `1.0`.
- f32: a number, with no NaN/Inf and nothing beyond the f32 range.
- vec2/3/4: arrays.
- mtx44: 16 numbers in on-disk row order.
- rgba: `[r,g,b,a]`, each 0-255.
- hash and link: strings, either a name or `"0x"` plus 8 lowercase hex digits.
- file: a path, or `"0x"` plus 16 hex digits.
- list: an array.
- option: `null`, `[]`, `[x]` or `x`.
- map: a JSON object whose KEY rules differ from path `{key}`: hash keys are `"0x%08x"` or the name (a
  decimal string is wrong here); file keys are `"0x%016x"` or the path.
- pointer: `{"pointer":{"class":C,"set":{..}}}`, or `null`.
- embed: `{"embed":{"class":C,"set":{..}}}`. A struct pin replaces the whole struct.
- `{"ref":"<entry>:<path>"}` resolves against the UNMODIFIED game: the first game chunk that declares the
  entry (`ov/builder/game_data.rs:688-721`).

**Order and failures** (`ov/builder/game_data.rs:410-669`).
- Mods run from lowest to highest precedence. Within a mod: base first, then ascending Priority, then
  names; then module order, then edit order.
- Failures are diagnostics (TargetSkipped, NoEffect, DeclarationsRejected for a whole layer); nothing
  aborts.

**Harvested hashtable** (`ht/table.rs:43-85`, `mp/fantome/pack.rs:381-445`).
- One name per line, LF, no BOM, printable ASCII, no backslash.
- It holds the WAD-relative paths of the packed files, sorted, that aren't bare hex.
- A table listed in `Hashtables` that is missing is an error.

**Compatibility of M757's YAML.** 0.6 to 0.8 only added things (module `name`, schema fallback, empty
`entries`, PTCH targets), so M757's output stays valid. Emitting `name` breaks consumers still on 0.6.

## ReyEngine before M814 (mapped 2026-10-03)

This is the starting point the milestones were planned from. The export, the declaration model and the Map
Skin Switcher have since changed (see **Done** above). The import is still as described here, except that
it now warns about the layered content it skips.

**Import** (`FantomeImporter.Import`).
- Reads Name/Author/Version/Description/Heart/Home only.
- `WAD_<layer>/`, GameData and META/hashes are silently dropped.

**Export** (`MainWindowViewModel.ExportFantome` -> `BuildProjectCore` -> `FantomeExporter.Export`).
- A flat `WAD/<folder>.wad.client`, whole bins.
- A legacy `META/details.json`.
- No layers.
- `ShipBinEditsAsDeclarations` is ignored here.

**Declarations (M757).**
- `BinDeclarations.Convert/Manifest` write YAML, used only by Send to LTK Manager
  (`DeclareGameBins` -> `content/<layer>/game_data.yaml`).
- Never emitted: `ref`, `overrides`, `+links`, property removal.
- No applier or parser exists.

**Map Skin Switcher** (`ApplyMapSkinSwapAsync` -> `MapSkinSwitcher.Switch`).
- Writes whole map11.bin and the container's materials.bin; a `BinRecipeRecord` "ForceMapSkin" replays it
  per patch.
- Its field lists are public: `EnvironmentRouteFieldHashes`, `CharacterSkinFieldHashes`,
  `FeatureAudioClassHash`.

**Hashes.** `HashDatabase.AddWad(HashAlgorithms.WadPath(n), n)` matches LTK's `game` table (xxh64 of the
lowercased path).
