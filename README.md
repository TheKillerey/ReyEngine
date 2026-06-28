# ReyEngine

A modern, dark, futuristic **asset editor for League of Legends** — think Unreal/Unity
for LoL art assets, minus the gameplay runtime and the Play button. Browse and unpack
`.wad.client` archives, preview textures/meshes/maps, inspect `.bin` metadata, resolve
hashes, and export/repack assets.

> Status: **MVP foundation working.** WAD browse + extract, hash tools, texture (`.tex`/`.dds`)
> preview, and a live OpenGL viewport are functional and verified against a real game install.

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
│   ├─ Hashing/               # FNV-1a / ELF / SDBM / XxHash64 + HashDictionary
│   ├─ Assets/                # AssetType + magic sniffer, WadAssetEntry, AssetTree
│   ├─ Wad/                   # WadArchive (open / list / extract / repack-later)
│   ├─ Decoding/              # TextureDecoder (.tex/.dds → RGBA8); mesh/bin decoders later
│   └─ ReyProject.cs          # editor project model (game dir, hash dir, recents)
├─ src/ReyEngine.Rendering/    # No UI, no Avalonia. Pure Silk.NET GL + System.Numerics.
│   ├─ OrbitCamera.cs
│   └─ GridRenderer.cs        # grid + axes; the base the mesh renderer extends
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
- [ ] SKN/SKL mesh preview (M3) · MAPGEO (M3) · ANM playback (M6)
- [ ] `.bin` tree inspector (M3) · material/shader preview (M4) · WAD repack (M5)

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
| **M3** | SKN/SKL mesh rendering in viewport · `.bin` property tree in Inspector · MAPGEO load |
| **M4** | Material/shader preview (League `.bin` params → preview shaders) |
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
For readable paths, drop CDTB hash lists into `data/hashes/` (see that folder's README).
