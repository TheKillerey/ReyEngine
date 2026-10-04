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
| 3 = M816 | **Import the layered format.** Layers (Name, DisplayName, Priority), `WAD_<layer>/` folders, `META/hashes/*.txt` (names for the packed chunks, kept with the project), License/Tags/Maps/Champions/Generator, and each layer's GameData stored VERBATIM in the project (number tokens and key order kept). The same WAD name may appear in several layers. Export writes imported GameData first, then ReyEngine's own declarations for the same layer. | Crauzer's file imports with its 3 layers and 347 named chunks; re-export reproduces each layer's GameData text exactly and the WADs chunk for chunk; a synthetic project with the same WAD in two layers round-trips. |
| 4 = M817 | **Apply engine (C#).** One `apply` call of ltk_game_data v1 in Formats, per `ltk-apply-engine-spec.md`: the PROP and PTCH paths, objects (class / clone / remove), links / -links / +links, signed paths with selectors, struct and type pins, refs through a supplied entry reader, override files, typing from LTK Manager's PatchSchema rules over meta.db.json, and LTK's diagnostics (skip, never abort). | Byte-equal to the Rust engine (ltk_game_data 0.8.0), bytes and diagnostics, with both sides fed the same entries: on Crauzer's 13 modules, M814/M815's corpus and switches, and a crafted document that reaches every diagnostic kind. |
| 5 = M818 | **Game object index and layered overlay.** The first-declaring-chunk index over the installed game (built lazily, cached per game fingerprint), and the overlay stage over it: several layers and modules on one chunk, entries fan-out, refs read once from the unmodified game, NoEffect / TargetSkipped / EntryUnresolved / EntryFanOut / ObjectShadowsGame / IndexUnavailable. | Crauzer's file over the installed game equals ltk_overlay's game-data output chunk for chunk, diagnostics included; a second run is served from the cache. |
| 6 = M819 | **Preview and edit.** A project's GameData is applied when the editor reads a declared bin, so the viewport, the material/particle editors and the Character window show the mod as LTK installs it (Crauzer's Winter Rift on the map). Editing such a bin materialises it in the project; export and Send to LTK then emit the imported modules followed by a literal module diffed against "game + imported GameData" (the M815 pattern), so the original refs and clones survive. Diagnostics are shown, not hidden. | The imported sample previews as the Snowdown map (render check) and its stats match LTK's apply; an edit on top round-trips through export and LTK's apply; the user's in-game check. |

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

**Milestone 3 = M816.** Import .fantome reads the layered format.
- **Storage.** Every layer's GameData, its override files, the hashtables and README/LICENSE are stored byte
  for byte under `<project>/.reyengine/ltk/` (`LtkProjectStore`).
- **WADs.** A non-base layer's WADs land in `layers/<layer>/<Wad>`, claimed whole by the layer.
- **Export and Send** write the imported modules first, verbatim, then ReyEngine's own.
- **Crauzer's file round-trips.**
  - The three GameData texts are byte-equal, and so are the 347 named chunks.
  - ltk_game_data applies the re-export exactly as it applies the original (11 targets).
- **Safety.**
  - A package's layer names are untrusted: an unsafe one is renamed at import.
  - Send proves every layer folder lies under the mod's content folder before writing.
- **Build Package** cannot carry layers or GameData. It says so and points to Export .fantome and Send.
- **Not done here:** GameData is not applied yet. That is M817-M819.

**Milestone 4 = M817.** The C# apply engine, `ReyEngine.Formats.LtkGameData`.
- **Equal to the Rust crate**, bytes and diagnostics, on every corpus: Crauzer's modules, M814-M816's exports,
  56k crafted cases, 14.6k random edits and 323k fuzzed documents.
- **Hardened for packages.** These are the only places it differs from the Rust crate, and no real bin reaches
  them:
  - the nesting cap after edits;
  - the zero-width budget and the reservation caps;
  - `GameDataLimits` (work, output and override size) and cancellation;
  - the writer refuses a u16 overflow.
- **Where M818/M819 must feed limits.**
  - The caller caps what it reads from a package (the document and the override files, about 16-32 MiB).
  - A tree costs about 8x its file size in memory.
- **MetaClassDatabase's `to` is now inclusive.** The latest build is unchanged.

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

## Where preview and edit hook in (mapped 2026-10-03, for M819)

**One read choke point.** Everything the editor displays reads through `AssetMountService.Read` (via
`ReadAssetFrom` / `ReadAsset` / `GetAssetBytes` / `ReadAssetByPath`). The thumbnail and colour-scan workers capture
the service itself. `BuildMounts` builds a fresh service on every trigger: project open, settings, the file
watcher (which also watches `.reyengine`). So the overlay is built in `BuildMounts` from `LtkProjectStore.ReadLayers`
(catching its `InvalidDataException`, or a broken document blocks project open), attached to the service, and
applied in `Read`.
- **Raw readers stay raw:** `ReadFallback`, `ReadRiotOriginalBytes`, PatchUpdate's `ReadProjectBytes`, and the
  Map Skin Switcher's recorded original. Refs resolve against the unmodified game.
- **Base bytes:** the project's copy when a project mount wins, else the game's. The overlay needs its own
  game reader and object index (M818): the mounted fallbacks cover only DATA, Common, Global, Shaders and the
  open map, and a champion WAD only while the Character window has it open.
- **Cache** applied results on (hash, base fingerprint, modules fingerprint), thread-safely: folder mounts
  re-read files on every read, and workers read concurrently.

**Editing.**
- `GuardEditable` refuses `RiotReference` entries, so a declared bin served from the game cannot be saved
  today.
- `SaveMapBinBytesAsync` falls back to the override store, which Send never sees. A declared bin must be
  materialised into a project folder (`TryPlaceInProjectFolder`, as `SaveGeneratedMapSkinBinAsync` does).
- Copy To Project copies raw Riot bytes. It must copy game + GameData.
- A materialised bin needs a record (with a fingerprint of the modules it includes), so that the overlay never
  re-applies them over it: `+list`, clones and `-list` are not idempotent.

**Export baseline.**
- `BinDeclarationPlanner.Plan` diffs against raw Riot bytes. It needs a baseline of "game + the imported
  GameData that applies before this layer's literal module".
- `readRiot` stays for the existence check and for `MapSkinDeclarations`. Its `ApplyReferences` reads values
  from the raw game but applies them over the baseline.
- With declarations OFF, a whole bin that imported GameData also targets is applied twice at install. Force those
  bins to declarations, or warn.

**Stale state after a GameData change.**
- `_mapState`: `InvalidateMapState` has no callers.
- `Documents[].Scene` snapshots, `_mapMaterialNames`, `_propLightGrid`, `_propClipTables`.
- Open editors' `BaseBytes`.
- The map thumbnail key: `AssetIdentity` sees file sizes and times, not GameData.

**Gaps.**
- Bins that exist only in the game are not listed in the Content Browser or in `AssetEntries`. Sibling controller
  bins and the switcher's list come from `AssetEntries`.
- Map open picks the materials.bin next to the mapgeo. It does not follow an overlaid `mMapContainerLink`.
- Two layers holding the same file resolve first-added-wins in ReyEngine's mounts, and the importer adds base
  first. LTK's rule is the reverse (spec §1.4): the higher-priority layer's copy replaces base's, RAW wins over
  all, and an inactive layer contributes nothing. The preview must mount layers in LTK's order and honour
  which layers are active.
