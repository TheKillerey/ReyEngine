# ReyEngine

A modern, dark, futuristic **asset editor for League of Legends** — think Unreal/Unity
for LoL art assets, minus the gameplay runtime and the Play button. Browse and unpack
`.wad.client` archives, preview textures/meshes/maps, inspect `.bin` metadata, resolve
hashes, and export/repack assets.

> Status: **M4 in progress.** On top of M3 (mesh/skeleton/hash sync): **textured champion rendering**
> (skin `.bin` → per-submesh diffuse textures), a **`.bin` property-tree inspector**, and **MAPGEO
> rendering** (flat). Verified on a real install — Aatrox renders fully textured, Summoner's Rift
> (`Map11`, 2.6M verts) renders as geometry.

---

## 1. Tech stack (and why)

| Layer        | Choice                                   | Why |
|--------------|------------------------------------------|-----|
| Language     | **C# / .NET 10**                         | Single language across UI, core, and rendering. |
| Asset decode | **LeagueToolkit** (C#, NuGet)            | The most complete LoL format library — covers WAD, tex, skn/skl, anm, mapgeo, bin. No reimplementation. |
| UI           | **Avalonia 11**                          | Modern, cross-platform, fully themeable XAML desktop UI. |
| 3D viewport  | **Silk.NET (OpenGL)** in an Avalonia `OpenGlControlBase` | Low-level GL with a real engine-style render loop. |
| MVVM         | **CommunityToolkit.Mvvm**                | Source-generated observable properties + commands. |
| Hashing      | `System.IO.Hashing` (XxHash64) + in-house FNV-1a/ELF/SDBM | License-clean, self-contained. |

**Alternatives considered**

- *Rust core (`league-toolkit`) + C# UI* — best raw perf and an MIT/Apache license, but adds an
  FFI boundary and two toolchains. Overkill for an MVP. We can extract a Rust core later if profiling demands it.
- *Tauri (Rust + web UI)* — nice UI story, but streaming large binary assets across the JS/Rust IPC
  and doing 3D in WebGL is awkward; the Rust toolkit's mapgeo/version coverage is thinner.
- *Qt/C++* — would mean reimplementing every parser. No.

**License note:** `LeagueToolkit` is **GPL-3.0**, so linking it makes ReyEngine GPL-3.0
(fine for an open modding tool). If a closed-source build is ever required, the decode layer is
isolated behind `ReyEngine.Core` and can be swapped for the MIT/Apache Rust `league-toolkit` via FFI,
or hand-rolled parsers (WAD/tex/bin are easy; skn/mapgeo/anm are more work).

## 2. Project architecture

```
ReyEngine.sln
├─ src/ReyEngine.Core/         # No UI. Asset model + pipeline. (refs LeagueToolkit)
│   ├─ Diagnostics/            # Logger, ILogSink, LogEntry
│   ├─ Hashing/               # FNV-1a/ELF/SDBM/XxHash64, HashDatabase, HashSyncService,
│   │                         #   WadPathResolver, IHashResolver
│   ├─ Assets/                # AssetType + magic sniffer, WadAssetEntry, AssetTree
│   ├─ Wad/                   # WadArchive (open / list / extract / re-resolve / repack-later)
│   ├─ Decoding/              # TextureDecoder (.tex/.dds → RGBA8)
│   └─ ReyProject.cs, ReyPaths.cs
├─ src/ReyEngine.Formats/      # No UI. SKN/SKL/MAPGEO decoding → plain data. (refs Core + LeagueToolkit)
│   ├─ Meshes/                # MeshAsset, SkinnedMeshDecoder
│   ├─ Skeletons/             # SkeletonAsset, SkeletonDecoder
│   ├─ Meta/                  # BinDocument, SkinMaterialExtractor (M4)
│   └─ MapGeo/                # MapGeoAsset, MapGeoDecoder (M4) — reuses the mesh renderer
├─ src/ReyEngine.Rendering/    # No UI, no Avalonia. Pure Silk.NET GL + System.Numerics.
│   ├─ OrbitCamera.cs, ShaderUtil.cs (ES/desktop GLSL)
│   ├─ GridRenderer.cs        # grid + axes
│   └─ ViewportMeshRenderer.cs # solid/wireframe mesh + bounds + bone overlays; MapGeoRenderer (M4 stub)
└─ src/ReyEngine.App/          # Avalonia shell (the only project that knows about UI)
    ├─ Views/                  # MainWindow.axaml, ViewportControl (GL bridge)
    ├─ ViewModels/             # MVVM: MainWindow, AssetNode, Inspector, Console
    ├─ Services/               # DialogService (file pickers)
    ├─ Imaging/                # RGBA → Avalonia bitmap
    ├─ Converters/ Themes/     # dark theme + log colouring
    └─ App.axaml, Program.cs
```

Dependency direction is strictly one-way: `App → (Core, Rendering)`. Core and Rendering never
reference the UI, so the pipeline is unit-testable and reusable (e.g. a future CLI).

## 3. MVP feature list

- [x] Open `.wad.client`, list every chunk, build a folder tree (resolved or `0x…` paths)
- [x] Hash dictionary loading (CDTB format) → resolve obfuscated paths
- [x] Magic-byte type detection (DDS, TEX, SKN, SKL, ANM, MAPGEO, BIN, BNK, PNG/JPG)
- [x] Extract / export any chunk to disk (decompressed)
- [x] `.tex` / `.dds` texture preview in the Inspector
- [x] Hash Lookup tool (XxHash64 / FNV-1a / ELF for any string)
- [x] OpenGL viewport: grid + axes, orbit/pan/zoom camera
- [x] Console/import log, dark futuristic UI, full menu + toolbar
- [x] **Hash sync** from CommunityDragon (split files, 32/64-bit, conflicts) + binary cache + auto-load
- [x] **SKN mesh rendering** (solid/wireframe), bounds + **SKL bone overlay**, auto/manual skeleton pairing
- [x] Mesh inspector (verts/indices/tris/submeshes/materials/bounds/bones)
- [x] **Textured mesh** — skin `.bin` → per-submesh diffuse textures applied in the viewport
- [x] **`.bin` property-tree inspector** (resolved class/field names + values)
- [x] **MAPGEO rendering** — `.mapgeo` decoded + rendered flat (verified on Summoner's Rift: 2.6M verts, 293 materials)
- [ ] Textured maps (map materials `.bin`) (M4) · WAD repack (M5) · ANM playback (M6)

## 4. Data pipeline: WAD → decoded asset → preview

```
.wad.client
  └─ WadArchive.Open(path, hashDict)        LeagueToolkit.WadFile
       └─ for each chunk: key = XxHash64(lowercased path)
            └─ HashDictionary.Resolve(key)  → real path or 0x… placeholder
                 └─ AssetTypeDetector       → extension first, else magic bytes
  └─ on select: WadArchive.Extract(entry)   LoadChunkDecompressed (zstd/gzip/raw)
       └─ AssetTypeDetector.FromMagic(bytes)
            ├─ Texture/Dds → TextureDecoder.Decode → RGBA8 → Inspector bitmap
            ├─ SkinnedMesh → (M3) SknDecoder → RenderMesh → viewport
            └─ Bin         → (M3) BinTree    → Inspector property grid
```

## 5. Renderer plan

We don't run League's actual D3D shaders. Instead we map League material/`.bin` parameters
onto a small set of **preview shaders** (GLSL via Silk.NET):

1. **M2 (done):** grid + axes, orbit camera, depth/blend setup — the shared GL foundation.
2. **M3:** unlit/diffuse textured mesh shader for SKN; per-submesh material → diffuse texture.
3. **M4:** PBR-ish preview that reads League material params from `.bin`
   (diffuse, emissive, gloss, color tints) and approximates them; a "League shader param → preview
   uniform" mapping table is the bridge.
4. Skeleton overlay + ANM bone animation (M6).

## 6. Hash system plan

League obfuscates paths/names with several hashes; all operate on the **lowercased** string:

| Use                      | Algorithm     | Where |
|--------------------------|---------------|-------|
| WAD chunk path           | **XxHash64**  | `HashAlgorithms.WadPath` |
| `.bin` field/class/entry | **FNV-1a 32** | `HashAlgorithms.Fnv1a` |
| legacy lookups           | ELF / SDBM    | `HashAlgorithms.Elf/Sdbm` |

- `HashDictionary` loads CDTB lists (`<hex> <path>` lines) — drop them in `data/hashes/`.
- Unknown hashes display as `0x{hash:x16}.unknown` and still extract/preview by hash.
- The **Hash Lookup** toolbar computes all three hashes for any string (great for
  guessing/registering new paths). Discovered paths can be `Register`-ed back into the dictionary.

## 7. Import / export plan

- **Import / Open:** pick a `.wad.client` (or later: drag a folder of loose assets).
- **Export selected:** decompress a chunk and write it out (done).
- **Bulk export (M5):** export a whole subtree, recreating the folder layout from resolved paths.
- **Build Package / Repack (M5):** edit chunks → write a new `.wad.client` via `LeagueToolkit`'s
  WAD builder, preserving compression and rebuilding the hash table.

## 8. UI wireframe

```
┌─ File  Edit  View  Tools  Project  Help ─────────────────────────────────────┐
├─[Import][Export][Reload WAD][Build Package][Shader Preview] | (hash box)[Hash Lookup]┤
│┌───────────────┬───────────────────────────────────────┬────────────────────┐│
││ ASSET BROWSER │              VIEWPORT (GL)            │     INSPECTOR      ││
││  WAD EXPLORER │   grid + axes, orbit/pan/zoom         │  name / type chip  ││
││  ▸ folders    │   (meshes & maps render here, M3+)    │  texture preview   ││
││  ▸ [TEX] file │                                       │  path / hash /     ││
││  ▸ [MSH] file │                                       │  size / compression││
│├───────────────┴───────────────────────────────────────┴────────────────────┤│
││ CONSOLE · IMPORT LOG   (colour-coded: info/success/warn/error)      [Clear] ││
│└─────────────────────────────────────────────────────────────────────────────┘│
├─ status bar: archive name · entry count · resolved count ────────────────────┤
```

Theme: near-black `#0A0D13` canvas, panel `#10151F`, cyan accent `#36E2C2`, violet `#6C5CE7`.
No Play button — this is an editor, not a runtime.

## 9. Implementation roadmap

| Milestone | Scope |
|-----------|-------|
| **M1 ✅** | Solution, Core pipeline (WAD/hash/types), validated on real game data |
| **M2 ✅** | Avalonia shell, dark theme, browser/inspector/console, GL grid viewport, texture preview |
| **M3 ✅** | CommunityDragon hash sync + cache + path resolution · SKN mesh + SKL bone rendering · mesh inspector |
| **M4 ◐** | `.bin` tree ✅ · textured champion ✅ · MAPGEO flat render ✅ (SR verified) · textured maps (next) |
| **M5** | Bulk export + WAD repack / Build Package |
| **M6** | ANM animation playback · skeleton overlay · soundbank (BNK/WPK) extraction |
| **M7** | Project files, tabbed multi-WAD, search/filter, thumbnails, settings |

## Build & run

```powershell
dotnet build                                  # builds all three projects
dotnet run --project src/ReyEngine.App        # launch the editor
```

Requires the **.NET 10 SDK** (LeagueToolkit 4.1 targets net10.0). Then `File ▸ Open WAD…`
and point it at e.g. `C:\Riot Games\League of Legends\Game\DATA\FINAL\DATA.wad.client`.

### Hash resolution (M3)

`Tools ▸ Sync Hashes` downloads the CommunityDragon hash lists
(`hashes/lol`: split `hashes.game.txt.N`, `hashes.lcu.txt`, `hashes.bin*.txt`) into
`data/hashes/communitydragon/lol/`, merges them (32-bit FNV for bin, 64-bit XxHash64 for WAD,
keeping conflict candidates), and writes a fast binary cache `data/hashes/merged_hashes.cache`.
On the next launch the cache auto-loads — **no network needed**. `Tools ▸ Reload Local Hashes`
re-reads it; loose `.txt` files dropped in `data/hashes/` are still merged too. After a sync the open
WAD's tree refreshes `0x…` → readable paths in place. The full sync is ~250 MB; it is git-ignored.

### M3 test checklist (using `DATA.wad.client`)

1. **Launch** — `dotnet run --project src/ReyEngine.App`. Console shows the hash cache loading (if present).
2. **Sync** — `Tools ▸ Sync Hashes`. Watch the console: *Downloading… / parsed … / Loaded X / Saving cache*.
3. **Open** — `Import` → `DATA.wad.client`. Console: `resolved N / 4,895 paths`; tree shows `data/…` paths.
4. **Texture** — click any `IMG` (`.dds`/`.tex`) → preview appears in the Inspector.
5. **Mesh** — click a `MSH` (`.skn`) → it renders centered in the viewport; Inspector shows verts/tris/submeshes.
6. **Toggles** — `Wireframe`, `Bounds`, `Bones` (top-right of viewport) change the render; LMB orbit, wheel zoom.
7. **Skeleton** — for a champion WAD (e.g. `Champions/Aatrox.wad.client`) a matching `.skl` auto-pairs and
   draws an orange bone overlay; otherwise `Tools ▸ Assign Skeleton…` to load one manually.
8. **Hash Lookup** — type a path in the toolbar box → `Hash Lookup` prints xxhash64/fnv1a/elf and any resolved
   candidates (conflicts listed if a hash maps to several strings).
9. **`.bin`** — click a `.bin` → its property tree appears in the Inspector (resolved names + values).
10. **MAPGEO** — open a map WAD (`Maps/Shipping/Map11.wad.client`), click a `.mapgeo` → the map renders
    flat in the viewport, framed to its bounds; Inspector shows version/mesh/vertex/material counts.
    (Map textures are the next M4 step; maps render flat grey for now.)
