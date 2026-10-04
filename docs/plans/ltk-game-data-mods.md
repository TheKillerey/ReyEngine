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
| 6 = M819 | **Preview (read-only).** A project's GameData is applied when the editor reads a declared bin, so the viewport, the material/particle editors and the Character window show the mod as LTK installs it (Crauzer's Winter Rift on the map). Layers follow LTK's file precedence. Diagnostics are shown, not hidden. Every path that would write a target bin back as a FILE is refused, because saving overlaid bytes makes LTK apply the modules twice (M823 keeps an edit as a declaration instead). | The imported sample previews as the Snowdown map (render check) and reads byte-equal to the overlay; legacy projects read byte-identical before and after. |
| 7 = M823 | **Edit and export.** An edit to a target bin is saved as a declaration, never as a whole bin: one ReyEngine literal module per bin, diffed against "game + imported GameData" (B) and recomputed on every save. It is placed after the imported modules in the last layer that touches that bin, so at install it runs after them. A whole copy would make LTK apply the imported modules again. An edit a declaration cannot express is refused with the reason. Export and Send emit the imported modules verbatim, then these edit modules. The planner diffs a project bin that GameData also targets against "game + the modules in front of its own", not the raw game. | An edit on top round-trips through export and LTK's own apply; it survives a simulated patch; legacy exports are unchanged; the user's in-game check (not done by the milestone). |

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

**Milestone 5 = M818.** The game object index and the layered overlay (`GameObjectIndex`, `GameDataOverlay`).
- **The index equals ltk_game_index** over the installed game: 393 archives, 808,646 chunk holders and
  440,618 declarations, with zero differences.
  - It builds in 4-13 s cold. The cache is 17 MB under `%LocalAppData%\ReyEngine\cache`, and a warm load takes
    0.1 s.
- **The overlay equals ltk_overlay's game-data stage**, chunk for chunk and diagnostic for diagnostic, on:
  - Crauzer's file (11 chunks; the routing too);
  - M815's s1-s7;
  - 24 synthetic layered scenarios.
- **The schema on this machine.** LTK Manager's cached schema
  (`%LocalAppData%\LeagueToolkit\meta\meta-schema.json`) is byte-identical to ReyEngine's
  `data/meta/meta.db.json`; both have latest 8217343.
  - The installed game is 8230722. The manager's `load` falls back to its shipped snapshot only when that one
    describes the build, and it does not (8175716). So today the manager types nothing from the schema.
  - At the installed build, Crauzer's file therefore yields 1,641 skipped edits. At a described build it yields
    none.
  - The preview uses the same data and rule, so it shows what LTK Manager installs today.
- **Two LTK indexes.** LTK Manager's browsing index numbers archives in natural order. The overlay, which
  installs mods, uses byte order. ReyEngine follows the overlay.

**Milestone 6 = M819.** The preview, read-only (`GameDataPreview`, `IAssetOverlay`).
- **Reads.** A project that stores GameData serves every target chunk through `AssetMountService` as LTK
  installs it. Raw readers stay raw. Game-only targets are listed under "LTK GameData".
- **Layers** mount in LTK's apply order.
- **Schema.** It comes from LTK Manager's cache when present. On this machine that cache is undescribed, so
  Crauzer's package skips 687 edits (summer ground). The note counts what the manager will add once it updates.
- **Guards.** Every write-back of a target is refused.
  - A save during a rebuild waits.
  - Unchanged documents inherit the known targets.
  - A failed preview keeps its targets refused.
- **Untrusted names** pass a path-safety proof before they become file names.
- **Results.**
  - Byte-equal to ltk_overlay on Crauzer's package.
  - Four legacy projects read byte-identical before and after.
  - Unchanged maps render pixel-identical.

**Milestone 7 = M823.** An edit on top of the GameData (`LtkEditStore`, `GameDataEditPlanner`, `MainWindowViewModel.GameDataEdit.cs`).
- **What is kept.** An edit of a target bin is ONE literal module per bin, `diff(B, E)`: `B` is the bin LTK makes from the
  game and the imported modules alone, `E` is what the editor saved.
  - It lives in `.reyengine/ltk/game_data/<layer key>/reyengine-edits.json`, beside the package's `declarations.json`. The
    package's document is never written after the import.
  - It is placed in the LAST layer (LTK apply order, `GameDataTarget.Layers`) that applies anything to the bin, after that
    layer's imported modules. Saving again replaces the module, recomputed against the current `B`.
  - A declaration states values, so the edit keeps applying after Riot's next patch.
- **Proof before keeping.** The planner applies the module to `B` with the C# engine and the preview's schema. LTK's skips
  (any diagnostic but the schema fallback) or a result that differs from `E` refuse the edit with the reason, and nothing is
  written. The editor keeps its unsaved state. There is no fallback to a whole copy.
- **Refused by construction.** A removed property, a changed object or embed class, a PTCH or lossy bin, a non-finite float.
  A property or object that needs the class schema is refused where the schema is silent (see below).
- **Preview.** The preview applies the edit exactly where the export puts it and refreshes in place after a save (no pending
  window); a reload structurally equals `E`. The bins are editable (`IAssetOverlay.AllowsEdits`); the tile keeps its LTK badge.
- **Export and Send.** The imported modules are written as they came, then the edits, then ReyEngine's own planner modules,
  numbered `origin.module` 0, 1, 2... in file order (M816 numbering carries on past the edits).
- **Planner baseline.** `IDeclarationBaselines` (`GameDataDeclarationBaselines`): a project bin that the GameData also targets
  is declared between the game's bin and the project's copy, BOTH with the modules in front of the planner's applied (the
  imported modules and the edits of the layers up to the bin's own). The map skin switcher's references still read the game's
  bin but apply over the baseline (`MapSkinDeclarations.ApplyReferences`). Without declarations the project's file ships
  as ever, and the modules apply over it.
- **Revert.** "Revert Edits On Top Of GameData" in the Content Browser menu takes a bin's module away.
- **Writers.** The two choke points every editor saves a bin through (`SaveMapBinBytesAsync`, `TryWriteToProjectFile`) route a
  target to the declaration save. The editors whose save ends there use `GuardBinEdit(Async)`; `GuardEditable` stays strict for
  everything that writes whole files. Flows that stage other files before the bin prove the declaration first (Workshop imports,
  `GameDataEditPreflightAsync`). Copy To Project of a target, Add Mesh, Create Character from a folder, the lightmap and
  bake flows, `PortLegacyMap`, the Map Skin Switcher and the patch update keep their refusal.
- **Never a file.** A ReyEngine save never writes a target's bytes into a project folder or the override store (tests).
- **An editor that holds a parsed document is merged, or refused.** `diff(B, E)` is only the person's edit when `E` was made
  from `B`. A document parsed while the preview was still working, or before another editor saved the same bin, lacks what
  the package put into the bin: saved as it is, its diff would state the game's values again and remove the objects the
  package created, and the proof would pass. So the Particle, Map Bin, Material, raw Bin and Ritobin editors and the Bin Issues
  repairs hand over the bytes they parsed their document from, and the save merges their edits onto the bin as it is served
  (`BinThreeWayMerge`) after the preview has settled. When that cannot be done - the editor cannot say what it was opened from,
  or the merge fails - the save is refused. A property that both the package (or another editor's save) and the stale editor
  changed to DIFFERENT values is a conflict, and the save is refused too ("the mod's GameData changed X since this editor opened
  it; reopen the editor and make the edit again"): neither value is known to be the wrong one (`BinMergeReport.RealConflicts`;
  the patch update still resolves its conflicts mod-wins, and a property both sides changed to the same value is no conflict).
  After a save that kept the editor's document as it was, the Particle and Materials editors move their base to the bin as
  served, so the edits they have kept are not taken for the mod's changes since; a save that had to merge something in leaves
  the base where it was (the document lacks what was merged, and moving the base forward would make the next save delete it).
  A Map Bin Editor patch update of such a bin is refused (its result is a whole file). Clean Particle and Map Bin editors are
  loaded again when the preview settles (only the editor the reload started from, and only if it is still clean) or an edit is
  taken away; a Particle or Materials editor that holds the edits of a bin whose edits were reverted is marked stale, and its
  saves - the auto-save's included - are refused until it is opened again.
- **Ready is not applied.** The preview is ready on its worker before the editor (the UI thread) has applied it; in between a
  READ still answers the bytes the declarations were not applied to, and an edit built from them would put the package's
  values back with the proof passing. The preview's task completes after the editor has applied it: the writers that can
  wait (`SettleGameDataForEditAsync`) wait for it, the ones that cannot (`GameDataEditBlocker`, so `TryWriteToProjectFile`, the
  raw Bin and Ritobin editors) refuse with "try again in a moment". Flows that read a bin and hold it across a pause (the
  cleanup's dialog, a repoint, the Workshop imports, the lighting and fog saves) hand the bytes they read to the save, so a
  bin that changed meanwhile has their edit merged onto it.
- **A save is made against ONE preview.** The mounts are rebuilt a moment after a file changes, and the plan is made off the UI
  thread: when the preview it was made against is not the editor's any more (or another save changed the bin it was merged onto),
  nothing is written and the save plans again. Bins saved together (the two a placement ends in) are proven before any is kept
  and put back if one fails - in the project they were kept in, whichever one the editor shows by then, with the mounts
  rebuilt (once) when the preview cannot follow in place, and what could not be put back said. A bin that is no target never
  waits for a pending preview. The LTK store is not a mount: a change under `.reyengine/ltk/` does not wake the browser's
  refresh.
- **The store is the person's file**: it is read with a limit (16 MiB), counted with the layer's document against the totals, off
  the UI thread (the preview's worker; the editor takes the set of edited bins when it settles). A file that cannot be read, is
  damaged or does not fit refuses the EDITS of that layer, with the reason in the preview's warnings, and the package's own
  modules still apply; a write puts the files back if one fails.
- **Cleanup** counts an asset only an edit names as referenced, and backs up the bin WITHOUT the GameData applied. **Patch
  update** leaves such a bin out of its rebase and says whether the project holds the mod's own copy of it.
- **The schema on this machine.** LTK Manager's cached schema stops at build 8217343 and the game is 8230722. Edits that
  construct a struct or add a property the bin does not hold (material parameter lists, new particle fields) are typed
  from the schema and are skipped by LTK today, so ReyEngine refuses them with the schema named as the cause. Edits of
  values the bin already holds (counts, strings, flags, existing list restatements) keep. With ReyEngine's own schema
  (`data/meta/meta.db.json`, which describes 8230722) the material edit keeps and installs.
- **Results.**
  - Crauzer's Winter Rift, through the real view model: a map edit and a material edit round-trip; `ovstage` (the code LTK
    Manager runs) over the export gives the editor's bins on all 11 chunks; `fantomecheck` and league-mod's loader read the
    export and the Send manifest; the imported modules are verbatim; a simulated patch of the game's bins gives the same
    result in the preview and in LTK's apply.
  - Legacy exports are byte-identical (M814-M816 golden suites).

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
