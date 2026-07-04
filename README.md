# ReyEngine

A modern, dark, futuristic **asset editor for League of Legends** — think Unreal/Unity
for LoL art assets, minus the gameplay runtime and the Play button. Browse and unpack
`.wad.client` archives, preview textures/meshes/maps, inspect `.bin` metadata, resolve
hashes, and export/repack assets.

> Status: **M39 complete.** **PROJECT Kalista UI redesign.** ReyEngine now looks like the modern editor it acts
> like. A proper theme system (`Themes/ReyColors.axaml` palette + `Themes/ReyTheme.axaml` control styles) drives
> the whole app: deep-navy surfaces, cyan primary / violet secondary accents, warning/amber + error/red +
> success/green signals, badge/status pills, cards, glass HUD overlays, glowing toggle pills, compact rows, slim
> scrollbars — with Fluent accent tinting so even built-in controls follow the brand. The window chrome was
> rebuilt: a **custom title bar** (native caption buttons overlaid, drag + double-click-maximize on the header)
> carries the REYENGINE · PROJECT KALISTA wordmark, menus and the live project title; the toolbar became grouped
> icon+text actions with **Build Package** as the cyan primary and `.fantome` export alongside; document tabs get
> an active glow + accent underline; the viewport wears a glass HUD (hint bar + Wire/Cull/Bones/Bounds/Particles
> pills + Frame); the Content Browser gained a breadcrumb chip, asset search and Unreal-style tiles with type
> badges, lock overlays and modified/conflict indicators; the Inspector is card-based (asset identity card with
> type/MODIFIED badges, then Mesh / Animation / Champion VFX / Map Geometry cards); the console shows a colored
> level legend; the status bar carries selection + branding. Zero backend changes — every control maps to an
> existing command, and the app was launched + screenshot-verified after the restyle. (Deferred, honestly:
> rotate/scale gizmo modes, Local/World + snap toggles and split console tabs need engine features that don't
> exist yet — no dead buttons were added.)
>
> Previously (**M38**): **cubemap probes + animated props.** The two remaining placed-object types on a map
> (alongside the M35 particles) are now browsable. `MapPlaceableExtractor` reads **cubemap reflection probes**
> (position + the baked cubemap `.dds` they reference) and **animated props** — the placed characters that carry a
> character record + skin: Baron, all six elemental dragons plus elder and hextech, every jungle camp
> (razorbeaks, wolves, gromp, krugs, blue/red buffs, scuttle), turrets, inhibitors and the Nexus, 89 in all across
> 29 characters. They show up as folders in the Map Content outliner — props grouped by character — and as
> colour-coded crosses in the viewport (props orange, probes green); click one to focus the camera on it and see
> its details (the cubemap file, or the character record + skin). Verified headless: all 89 prop markers and the
> lone probe land exactly on their map positions. (This is the browser/locator layer — rendering the actual prop
> meshes and applying probe reflections to materials are the natural next steps.)
>
> Previously (**M37**): **champion-skin VFX preview.** The live particle playback built in M36 now works for
> champions, not just maps. A champion skin's `.bin` carries a whole library of VFX systems (the same
> `VfxSystemDefinitionData` used on maps — Aatrox's skin01 has 52 of them), so loading a skin now populates a
> **Champion VFX** list in the inspector; pick an effect and it simulates and plays live at the model, with the
> real particle sprites resolved straight from the champion's WAD (and a soft-dot glow standing in for anything
> unshipped). It's pure reuse of the M36 parser + simulator + billboard renderer — no new rendering code — and
> was verified end-to-end headless: Aatrox's W plays its golden erosion ring and rising shard column from the
> real emitter data with all twelve sprites resolved. (Still a faithful billboard preview — bone attachment,
> animation-triggered timing, mesh/trail primitives and distortion remain out of scope.)
>
> Previously (**M36**): **live VFX particle playback.** Placed particles no longer just show as markers —
> ReyEngine now *plays* them. It parses every `VfxSystemDefinitionData` in the map's `.materials.bin` into a
> real, playable model (birth rate, particle/emitter lifetime, size and colour over life as sampled animation
> curves, initial velocity, world acceleration, sprite texture, flipbook, blend mode), runs a CPU particle
> simulator (spawn-by-rate, integrate velocity+acceleration, curve-driven size/colour — steady-state counts
> match rate×lifetime), and draws the particles as camera-facing, hardware-instanced, textured billboards
> (additive or alpha, depth-tested but not depth-writing) that read correctly under the engine's -X mirror.
> Select a placed particle in the **Particles** folder and hit **▶ Play VFX**: the system simulates and animates
> live at its placement, with the real sprites resolved from the mod (a soft-dot glow stands in when a texture
> isn't shipped), and it follows the particle if you move it. GLSL is ASCII-only and verified compiling on the
> live NVIDIA driver; the whole parse→simulate→render pipeline was proven headless on Old Summoner's Rift (its
> fire torches and cauldrons render on the map at exactly the right spot). (Full engine-accurate VFX — mesh
> primitives, trails/beams, distortion, sub-emitters — is out of scope; this is a faithful billboard preview.)
>
> Previously (**M35**): **placed particles — browse & move.** A map's `.materials.bin` carries every placed
> particle system (`MapParticle` items in `MapPlaceableContainer`); ReyEngine now reads them all, groups them by
> VFX system into a **Particles** folder in the Map Content outliner, and draws a cyan cross marker at each
> placement in the viewport (the selected one turns amber and always reads through geometry, with a **Show**
> toggle). Select a particle and you can **reposition** it: type an absolute X/Y/Z, hit **Apply** for a live
> marker preview (or **Reset**), then **Save to Mod** to persist. Saving byte-patches only that particle's
> transform translation in place — it locates the item by the exact 64 bytes of its original matrix and
> overwrites just the 12 translation bytes, so the rest of the `.bin` stays byte-for-byte identical and is
> re-parse-validated before it lands in the override + Build Package. Round-trip verified on Old Summoner's Rift
> v2 (1,194 particles across 69 systems): moved placements land exactly on target, every other particle is
> untouched, and the file size is unchanged. (Full VFX playback is out of scope — this is placement browse/move.)
>
> Previously (**M34**): **map material render-state fidelity** — real `blendEnable`/`cullEnable`/blend-factor
> fields + `AlphaTestValue` parsed into a per-material profile (Opaque/Cutout/Transparent), per-submesh backface
> culling (no global mode), two-sided lighting via `gl_FrontFacing`, mirrored-transform normal fix, a per-mesh
> Flip Normals tool, texture address-mode Clamp (decals stop tiling), and face/two-sided/mirrored debug views.
> **M33**: baked lightmap rendering (Map12 `BakedLight`+`Texcoord7`), material virtual-assets + thumbnails in the
> Content Browser, viewport/document tabs, and the Map Content panel folded into the Inspector.
>
> Previously (**M30**): **Multi-select + batch transform for MAPGEO meshes.** Hold **Ctrl** and click
> meshes in the viewport (or Ctrl+click rows in the Map Content tree) to build a selection; a plain click
> single-selects, an empty click clears. Every selected mesh gets its own amber highlight box, the group gets a
> cool-blue combined bounds box, and **one gizmo sits at the selection center** — dragging it moves the whole
> group rigidly. The inspector swaps to a **Batch Transform** panel (count, move Δ, rotate/scale about the
> selection center, **Reset Selected**, **Clear Selection**); the status bar shows *"N meshes selected"*.
> Batch ops compose through a world-space **GroupMatrix** applied *after* each mesh's own transform, so rotation
> and scale happen around the true group center without disturbing single-select fields. Every batch edit
> (drag or numeric) is **one Undo step** (`BatchTransformCommand`) that restores/reapplies every mesh exactly;
> persistence reuses the M25 byte-patcher so all changed transforms ship in the build. Hidden (visibility-
> filtered) meshes are pruned from the selection so they're never transformed. Verified with a 20-check suite on
> real SR Map11 geometry — rigid rotate, 2× scale doubling distance-to-center, undo/redo to exact vertices, and
> byte-patch round-trip all pass; single-select is unchanged.
>
> Previously (**M29**): **Undo/Redo** — every edit is a reversible command on a global stack (**Ctrl+Z / Ctrl+Y**,
> Edit-menu steps, ↶/↷ buttons) covering mesh move/rotate/scale/reset, .bin values, material texture paths &
> params, and sampler add/remove; title-bar `*` savepoint; stale-document purge.
>
> Status: **M28 complete.** **Viewport editing now actually works like Blender/UE.** Three fixes from live
> testing: (1) the mesh itself now moves when you drag — the vertex re-upload ran on the UI thread where no GL
> context is current, so only the highlight box moved; it now happens inside the render loop. (2) Dragging no
> longer fights you — the pick ray is derived purely from the render matrix (the old one mixed in the unmirrored
> camera eye → wrong direction), and the drag axis stays anchored at its press-time origin (re-anchoring at the
> live pivot created a feedback loop that snapped the mesh back every other frame). (3) **Click a mesh in the
> viewport to select it** — exact Möller–Trumbore triangle picking over all visible triangles (~9ms/click on the
> full SR map), respecting the dragon/baron visibility filters, syncing the Map Content tree selection; clicking
> empty space deselects. All three verified: the harness reproduces the old feedback bug (t: 98.98 → 0 snap-back)
> and confirms the fix, mirrored-matrix round-trips recover exact axis coordinates, and 12/12 test picks land
> inside the picked mesh's bounds.
>
> Status: **M27 complete.** **You can now see and drag the selected mesh in the viewport.** Selecting a mesh
> in the Map Content → Layer Groups tree draws a bright amber wireframe box around it (always on top, so it
> reads clearly even inside dense terrain), plus a 3-axis **translate gizmo** — red/green/blue lines from the
> mesh's pivot. **Click-drag an axis line to move the mesh along it**, live, no typing required; the numeric
> Position field updates as you drag, and Save to Mod persists it exactly like the M25 patcher. Dragging is
> axis-constrained via proper 3D ray-picking (project the mouse to a world ray, find its closest point on the
> axis line) — not a screen-space hack — verified with 9 independent round-trip checks (project→unproject
> recovers the exact original coordinate on X/Y/Z, including at an angled camera) before being wired into the
> UI, and confirmed visually: an isolated render shows the highlight box exactly enclosing the selected mesh
> with the gizmo axes anchored at its pivot. LMB still drives the camera (look/fly) everywhere else — the gizmo
> only intercepts clicks that land within ~10px of one of its axis lines.
>
> Status: **M26 complete.** **Rotate and scale now work and persist too**, alongside move. The Transform
> Mesh panel gained Rotation (X/Y/Z degrees) and Scale (X/Y/Z) fields plus a **Reset** button. Rotation/scale
> happen around the mesh's own pivot (its local bounding-box center) — not the map origin — so a mesh spins/grows
> in place. The patcher now writes the **full 4×4 transform matrix** (linear part = original-linear × scale ×
> rotation, translation = pivot + rotated(original-translation − pivot) + offset) instead of just the translation
> bytes, and also **recomputes the mesh's bounding box** from its transformed corners so in-game culling stays
> correct. Verified rigorously on two real maps: pure translation/rotation/scale each cross-checked against a
> hand-computed matrix, a combined move+rotate+scale+reset round-trip restores the exact original vertex, and the
> patched file re-decodes with all edited (and un-edited) vertices matching to sub-millimeter precision.
>
> Status: **M25 complete.** **Mesh move/reposition now works and persists.** Select a mesh in the Map Content
> tree, type an X/Y/Z position, **Apply Move** (it moves live in the viewport), then **Save to Mod**. Because
> LeagueToolkit's `EnvironmentAsset.Write` is lossy (it corrupts the mapgeo), ReyEngine instead **surgically
> patches** each moved mesh's transform translation in place — locating it by its unique
> `[BoundingBox][Transform]` byte signature (the AABB disambiguates the many identity transforms) and overwriting
> only the 12 translation bytes, leaving the rest of the file byte-exact. Verified on both the current SR Map11
> (v18) and the old OldSR map (which LeagueToolkit's writer couldn't even round-trip): the patched mapgeo reopens
> cleanly, moved meshes land at the right spot, and Build Package ships it. (M24 was the move foundation +
> discovering the writer was broken; M25 is the patcher that unblocks it.)
>
> Status: **M23 complete.** The **Baron pit visibility** combobox is now live. ReyEngine decodes the map's
> visibility controllers out of its `.materials.bin` files — Dragon Layer (`0xc406a533`), Baron Layer
> (`0xec733fe2`) and Child (`0xe21083b5`) controllers, recursing through `Parents`/`ParentMode` — and resolves
> each mesh's controller into its baron state bits (Base / Cup / Tunnel / Upgraded). Selecting a baron state in
> the Map Content panel hides the baron-pit meshes that don't belong to it, combined with the dragon-layer
> filter. Verified on SR Map11: 25 controllers (4 baron), 14 baron-constrained meshes (3 Base / 3 Cup / 5 Tunnel
> / 3 Upgraded), and the filter live-hides the non-matching ones.
>
> Status: **M22 complete.** Camera + map layer/visibility system. The viewport camera now uses **LMB**
> (mouse-look + WASD/QE fly · Alt+LMB orbit · MMB pan · wheel dolly · F focus) with **inverted/direct** look
> (cursor up→look up, left→look left). Adds the **dragon elemental-rift visibility system** (mirrors the
> MapgeoAddon): each mapgeo mesh carries its `VisibilityFlags` (8-bit dragon bitmask) + baron controller hash,
> the **Map Content** panel shows a *Meshes → Layer Groups → mesh names* tree, and two **Dragon / Baron**
> comboboxes filter the viewport — selecting *Ocean*, *Chemtech*, *Void*, … live-hides the meshes that don't
> belong to that elemental rift (verified on SR Map11: All = 769 groups, Ocean = 421). Baron-state precise
> filtering and per-mesh move/reposition are the next steps.
>
> Status: **M21 complete.** Editor polish + a shader-fidelity fix. The viewport now has an **Unreal-style
> camera**: RMB = mouse-look + WASD/QE fly, Alt+LMB = orbit, MMB = pan, wheel = dolly (RMB+wheel adjusts fly
> speed), **F** = focus selected. Adds the **ReyEngine logo** (titlebar icon + menu-bar wordmark) and **file/folder
> type icons** in the Content Browser (folder, mesh, texture, skeleton, animation, map, bin, shader, …). Shader
> fix: **normal maps are now classified and never shown as the base texture** — they're only used by shaders that
> declare them (a material whose only sampler is a normal map no longer renders the normal map as its diffuse).
> Note: full DX11 shader recreation isn't possible (compiled bytecode can't run in GL) — fidelity continues via
> the `.bin`-driven RiotApprox emulation (M18–M20 + this normal-map gating).
>
> Status: **M20 complete.** Adds **MatCap** support to the RiotApprox preview — the 2nd-most-common League
> champion sampler (`MatCap_Tex`), a view-space spheremap of fake studio lighting. ReyEngine resolves the
> per-submesh `MatCap_Tex` (+ optional `MatCap_Mask`), binds them to the renderer, and adds the matcap as an
> additive highlight sampled by the view-space normal (the standard, well-defined matcap technique — not
> guesswork). A **Debug · MatCap** view shows the raw spheremap shading. Verified the spheremap math with a
> directional test matcap (correct view-dependent shading across the model) and confirmed per-submesh resolution
> on Aatrox's Prestige skin (Arm/Diamond → DiamondColor matcap).
>
> Status: **M19 complete.** Adds **secondary-sampler blending** to the RiotApprox preview. The champion
> material's own **Gradient** sampler now colours the fresnel rim (Aatrox's rim glows its darkin red instead of
> flat white), the **Mask** sampler gates where the rim shows, and an **Emissive** sampler (where present) adds a
> self-illuminated glow — all driven by the real `StaticMaterialDef` sampler values, with a safe fallback so
> materials without these samplers look unchanged. New viewport debug views expose the raw **Mask** and
> **Emissive** channels for inspection. Verified on Aatrox (3 masks / 2 gradients resolved; rim picks up the
> gradient, no regression on diffuse).
>
> Status: **M18 complete.** Adds **Riot shader inspection + a close-preview emulation**. ReyEngine scans the
> game's `ShaderCache.dx11.wad.client` into a cached shader database (**Tools ▸ Scan Riot Shaders** → 828
> shaders, vertex/pixel, with variant + define counts, cached to `.reyengine/shader_cache.json`), mounts the
> shared `Shaders.wad` textures as read-only references, and surfaces a shader-binding panel in the Material
> Editor. The viewport gains a **preview-mode dropdown** — *Basic*, **Riot Approx** (a fresnel rim highlight +
> alpha cutout that approximates League's champion shaders), and **Debug** views (base / alpha / normals).
> DX11 bytecode is *not* executed in the GL viewport (it can't be) — the preview is a faithful approximation,
> not parity. Riot shader WADs are strictly read-only. Champions and maps both use the upgraded shading.
>
> Status: **M17 complete.** Adds **`.fantome` mod export** (Fantome / cslol-manager format) + a **Project
> Settings** dialog. Set the mod Name / Author / Version / Description / Thumbnail, then **Project ▸ Export
> .fantome…** builds the project, packs its folders into WADs, and writes a `.fantome` ZIP — `META/info.json`
> (+ `details.json`), `META/image.png` thumbnail, `WAD/*.wad.client` — named `<Name> by <Author>.fantome`.
> Verified the package byte-matches a real Fantome export's structure (`info.json` identical).
>
> Status (M16): Adds **folder → `.wad.client` packing** for distributable mods. Building a
> folder project now stages the content (with overrides applied) and packs it into a fresh, valid
> `.wad.client`: v3.4 header, hash-sorted TOC, Zstd-compressed chunks, no subchunks (so it sidesteps the
> v3.4 subchunk-relocation problem of *editing* an existing WAD — a fresh WAD is fully under our control).
> Verified: packed the Old-SR project (3,614 chunks, 1.48 GB → 0.72 GB), reopened it, and skn/materials.bin/
> mapgeo/dds all extract + decode. ReyEngine can now open a mod folder, edit it, and build a shippable WAD.
>
> Status (M15): fault-tolerant `.bin` reader so malformed/old-tooling mod bins load.
> Some mod bins have a struct/object with two properties sharing one name hash, which makes LeagueToolkit's
> strict reader throw — ReyEngine now replicates the container framing, reuses LeagueToolkit's own per-property
> reader, and de-duplicates (last value wins) before building real `BinTree` objects. All `.bin` consumers
> (materials, editor, document) route through `SafeBinTree.Parse` (strict first, tolerant on failure).
> Verified: identical to LeagueToolkit on good bins; the Old-SR `base_srx.materials.bin` (1.8 MB, strict throws)
> now parses to 765 objects → **200/201 map materials resolve, 1084/1096 groups textured** — the map renders.
>
> Status (M14): Project-mode bug fixes + Project Settings. The real cause of untextured
> champions / missing skeletons / empty animation lists in project mode was a set of `_archive is null`
> guards (compound conditions an earlier sweep missed) that short-circuited texture/skeleton/animation
> loading whenever there was no single WAD — now `ContentLoaded`, so they run against the mount layer.
> Also: companion `.materials.bin` linking tolerates renamed copies ("base_srx - Kopie.mapgeo"), map
> materials fall back to the original game `.materials.bin` when a mod's copy is broken/empty, animations
> fall back to the game WADs, and **Project ▸ Project Settings** sets the game folder (powers the
> fallback), build output, and references. *Known limit:* one specific Old-SR `.materials.bin` is malformed
> (LeagueToolkit can't parse it) and is an older map revision, so that map's textures still don't resolve.
>
> Status (M13): mounts the **original Riot game WADs as a read-only fallback** a mod folder only ships its
> *changed* files, so assets it doesn't include (skin bins, textures, meshes — e.g. `sru_dragon`) had nowhere
> to resolve. ReyEngine now mounts the **original Riot game WADs as a read-only fallback** (auto-discovered
> from the game folder: DATA / Common / Global + the matching map WAD) — consulted only on a miss, not added
> to the browser. Project files still win; missing base assets resolve from the install. Verified: `sru_dragon`
> (bin+texture only in the game WAD) now renders textured.
>
> Status (M12): Unreal-style **workspace layout**: the asset browser became a **Content Browser**
> (folder tree + file tiles + breadcrumb, Windows/Explorer-style) docked in the center **above the console**;
> the **left panel is now Map Content** (project maps + the loaded map's mesh-group outline); the right stays
> the Inspector. Plus **Open Recent** projects. On top of the M11 project editor. Builds 0 errors; launches clean.
>
> Status (M11): ReyEngine is a **project editor** (Unity/Unreal-style), not just a WAD viewer.
> **File ▸ Open Project Folder** scans a mod folder for `.wad.client` files and unpacked-WAD folders, writes
> `.reyengine/project.json`, and mounts a virtual file system: **project content is editable, Riot WADs are
> read-only references**, overrides win over project, project wins over Riot (with conflict indicators). Editing
> a Riot asset is blocked until you **Copy Asset To Project**; **Build Package** builds from the project root to
> `<root>/Build/` (never the game install). Single **Open WAD** still works as read-only inspection mode.
> Verified end-to-end on a real mod (Old Summoners Rift V2 + Riot Map11): open folder → mount 32k assets →
> copy a read-only Riot `.bin` → edit → build → reopen → edit parses back. M1–M10 preview/edit still work.
>
> *Scope notes:* (1) **Adding brand-new WAD chunks** (importing files at new paths) was investigated and
> deliberately **not shipped** — WAD v3.4 stores a separate subchunk table that can't be safely relocated
> when the chunk TOC grows, and a wrong write corrupts the archive; replacing existing chunks stays fully
> supported. (2) ReyEngine loads League's **material/sampler** system, not its HLSL shaders; secondary
> samplers (mask/gradient/emissive) are parsed + editable but not blended in the preview.

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
│   ├─ Wad/                   # WadArchive (open / list / extract / re-resolve)
│   ├─ Decoding/              # TextureDecoder (.tex/.dds → RGBA8)
│   ├─ Projects/              # ReyProject(.reyproject), ReyProjectService, ProjectWorkspace, AssetOverrideStore (M5)
│   ├─ Build/                 # WadRepackService, BuildPackageService, BuildReport, BuildSafety (M5)
│   └─ ReyPaths.cs
├─ src/ReyEngine.Formats/      # No UI. SKN/SKL/MAPGEO decoding → plain data. (refs Core + LeagueToolkit)
│   ├─ Meshes/                # MeshAsset (+blend data), SkinnedMeshDecoder
│   ├─ Skeletons/             # SkeletonAsset (+joints/influences), SkeletonDecoder
│   ├─ Animation/             # AnimationClip, AnimationDecoder, SkinnedMeshAnimator (CPU skinning, M6)
│   ├─ Materials/             # MaterialDocument (editable slots + params, M8); ChampionMaterialResolver
│   │                         #   (per-submesh diffuse via StaticMaterialDef samplers + materialOverride, M9)
│   ├─ Meta/                  # BinDocument (read-only tree); BinEditorDocument + BinValueEditor (editable
│   │                         #   primitives, re-serialize via LeagueToolkit, M7); BinTreeCloner (deep-copy for add/dup, M10)
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
- [x] **MAPGEO rendering** — `.mapgeo` decoded + **textured** from map materials `.bin` (Summoner's Rift verified)
- [x] **Project system** — `.reyproject` (new/open/save), asset override tracking, modified markers in the tree
- [x] **Build Package** — non-destructive WAD repack (copy + append + TOC patch) + build validation; never writes into the game install
- [x] **Animation playback** — `.anm` decoded + CPU-skinned onto the champion; play/pause/timeline/speed/loop, textures + bones preserved
- [x] **`.bin` editing** — edit primitive values (string/int/uint/float/bool/hash/vector) in the Inspector, dirty tracking + revert, save to override, edited `.bin` flows through Build Package
- [x] **Material Editor** — champion + map materials: texture slots, sampler names, params, submesh assignment; edit/replace texture paths, live Apply, save to override, unresolved warnings
- [x] **Multi-material rendering** — each submesh resolves to its own `StaticMaterialDef` diffuse (Aatrox body/wings/sword/banner all distinct); editor + viewport share one resolver
- [x] **Array/struct element editing** — add/remove texture sampler slots on a material (clones the schema); via `BinTreeCloner` + `BinTreeContainer.Add/Remove`
- [x] **Project editor** — open a mod folder (packed WADs + unpacked-WAD folders), mount Riot WADs read-only, edit project content, Copy-to-Project, build to `<root>/Build/`
- [ ] GPU skinning · HLSL shader/blend emulation · sound preview · pack folder→WAD · ~~add chunks to Riot WADs~~ (blocked: WAD v3.4 subchunk table)

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
| **M4 ✅** | `.bin` property tree · textured champion · textured MAPGEO (SR verified) · -X orientation |
| **M5 ✅** | project system (`.reyproject`) · replace WAD assets · Build Package (repack + reopen-validate) · install-folder guard |
| **M6 ✅** | ANM animation playback (CPU skinning) · animation panel (play/timeline/speed/loop) · textures + bones during playback |
| **M7 ✅** | structured `.bin` editing (primitive values) · dirty/revert per-field+file · save to override · edited `.bin` in Build Package · reopen-validate |
| **M8 ✅** | Material Editor (champion skin + map materials) · texture-slot/param editing · live Apply · Inspector tabs (Overview/Materials/Raw BIN) · unresolved warnings · raw texture replace |
| **M9 ✅** | proper material/sampler resolution · per-submesh multi-material diffuse (fixes single-texture bug) · unified editor+renderer resolver (`ChampionMaterialResolver`) |
| **M10 ✅** | array/struct element editing · add/remove material sampler slots (`BinTreeCloner`) · build-validated · (new-chunk import shelved: WAD v3.4 subchunk table) |
| **M11 ✅** | project-folder editor · asset mount layer (override>project>Riot + conflicts) · read-only Riot refs + Copy-to-Project · build from project root · inspection-mode for single WAD |
| **M12 ✅** | Unreal-style layout · Content Browser (folder tree + tiles + breadcrumb, center above console) · Map Content left panel · Open Recent projects |
| **M13 ✅** | game-WAD reference fallback (auto-discovered DATA/Common/Global + map WAD) · resolves assets a mod doesn't ship · fixes untextured project champions/maps |
| **M14 ✅** | fix project-mode texture/skeleton/animation guards · `.materials.bin` copy-name + game fallback · animation fallback · Project Settings (game folder / output / references) |
| **M15 ✅** | fault-tolerant `.bin` reader (`TolerantBinReader`/`SafeBinTree`) — dedupes duplicate property keys, reuses LeagueToolkit's per-property reader · malformed Old-SR map materials now load/render |
| **M16 ✅** | folder → `.wad.client` packing (`WadPackService`) — fresh v3.4 WAD, sorted TOC, Zstd chunks, reopen-validated · folder projects build a distributable WAD |
| **M17 ✅** | `.fantome` export (`FantomeExporter`) — META/info.json + thumbnail + WAD/ · Project Settings dialog (mod name/author/version/description/thumbnail + game/output folders) |
| **M18 ✅** | Riot shader inspection + close preview — scan `ShaderCache.dx11.wad` → cached `ShaderDatabase` (`.reyengine/shader_cache.json`) · mount `Shaders.wad` textures read-only · Material Editor shader-binding panel · viewport preview modes (Basic / **Riot Approx** rim+cutout / Debug base·alpha·normals) · export shader bytecode dump |
| **M19 ✅** | Secondary-sampler blending — per-submesh **Mask / Gradient / Emissive** samplers resolved from `StaticMaterialDef` and bound to the renderer (texture units 1–3); RiotApprox rim is now gradient-coloured + mask-gated, with emissive glow where present · Debug · Mask / Emissive views · safe fallback for materials without them |
| **M20 ✅** | **MatCap** preview — per-submesh `MatCap_Tex` (+ `MatCap_Mask`) bound to texture units 4–5; view-space spheremap fake-lighting highlight (additive, mask-gated) in RiotApprox · Debug · MatCap view · view matrix plumbed for the spheremap lookup |
| **M24–M25 ✅** | **Mesh move/reposition** — per-mesh vertex tracking + live viewport translate; persist by surgically patching the transform translation via its `[BoundingBox][Transform]` signature (LeagueToolkit's `EnvironmentAsset.Write` is lossy, so we never re-serialize) → saved to the override + Build Package |
| **M26 ✅** | **Mesh rotate + scale** (around the mesh's own pivot) — live viewport preview via a pristine-vertex snapshot; persist by patching the *full* transform matrix + recomputed bounding box; Reset button; verified against hand-computed matrices + round-trip re-decode on two real maps |
| **M27 ✅** | **Selection highlight + translate gizmo** — amber wireframe box around the selected mesh (always on top); click-drag 3-axis gizmo (X/Y/Z) to move it live via real 3D ray-picking (`ViewportPicking`: project/unproject/closest-point-on-axis, independently verified with 9 round-trip checks); numeric fields stay in sync; Save to Mod persists via the M25/M26 patcher |
| **M39 ✅** | **PROJECT Kalista UI redesign** — theme system (`ReyColors.axaml` palette incl. signal colors + Fluent accent tint, `ReyTheme.axaml` control styles: cards, badges/pills, glass overlays, tool/primary/icon buttons, vp toggle glow, compact trees/lists, slim scrollbars) · custom title bar (extended client area, drag + double-click maximize, native caption buttons, REYENGINE · PROJECT KALISTA wordmark + live project title) · grouped icon+text toolbar with cyan primary Build Package + .fantome export · doc tabs with active glow/underline · glass viewport HUD · Content Browser breadcrumb chip + typed asset tiles (badge, lock, modified/conflict) · card-based Inspector (identity card + Mesh/Animation/VFX/MapGeo cards) · console level legend · branded status bar · XAML/styles only, zero backend changes, launch-verified |
| **M38 ✅** | **Cubemap probes + animated props browser** — the last two `MapPlaceableContainer` types. `MapPlaceableExtractor` reads **cubemap reflection probes** (`MapCubemapProbe` — position + baked cubemap `.dds` path) and **animated props / placed characters** (identified structurally by an embedded `characterRecord`+`skin` — Baron, all six dragons + elder/hextech, every jungle camp, turrets, inhibitors, Nexus: 89 across 29 characters on SR). Both appear as folders in the Map Content outliner (props grouped by character, green/orange colour-coded), draw as colour-coded crosses in the viewport, and clicking one focuses the camera + shows its info (cubemap file / character record). Verified headless: all 89 prop + 1 probe markers land at the right map positions. Mesh rendering + real reflection application are the natural follow-ups. |
| **M37 ✅** | **Champion-skin VFX preview** — extends live particle playback (M36) from maps to champions. A loaded skin's `.bin` holds a whole VFX library (`VfxSystemDefinitionData`, same class as maps — Aatrox skin01 has 52 systems / 205 visual emitters); the champion inspector now lists them in a **Champion VFX** panel and selecting one plays it at the model origin using the exact M36 simulator/renderer. Sprite textures resolve straight from the champion's WAD via their full `ASSETS/...` paths (soft-dot fallback otherwise). Verified end-to-end headless: Aatrox's W renders its golden erosion ring + shard column from real emitter data with all 12 sprites resolved. Zero new rendering code — pure reuse of the M36 pipeline. |
| **M36 ✅** | **Live VFX particle playback** — real particle preview, not just markers. `VfxSystemResolver` parses every `VfxSystemDefinitionData` in the map's `.materials.bin` into a playable model (`rate`, particle/emitter lifetime, `birthScale0`×`scale0` and `birthColor`×`color` multiplier curves, `birthVelocity`, `worldAcceleration`, texture, flipbook, blend) — `ValueFloat/Vector3/Color` read as constant-or-`dynamics{times,values}` curves sampled over particle age · a GL-free `VfxParticleSimulator` spawns/integrates particles (steady-state = rate×lifetime, verified) · `VfxParticleRenderer` draws them as camera-facing, instanced, textured billboards (additive/alpha, depth-tested not depth-writing, ASCII shaders verified on the live driver, correct under the -X mirror) · select a placed particle and hit **▶ Play VFX** to simulate its system live at the placement (real sprites resolved from the mod, procedural soft-dot fallback), following the particle as you move it · verified end-to-end headless on Old SR (fire torches/cauldrons render on the map at the right spot) |
| **M35 ✅** | **Placed particles browser + move** — read `MapParticle` placements from the map's `.materials.bin` (`MapPlaceableContainer.items` → world transform + name + VFX-system link), grouped by system into a **Particles** folder in the Map Content outliner · cyan cross markers at every placement in the viewport (selected one amber, always-on-top), **Show** toggle · numeric X/Y/Z **move** with live marker preview + Reset · **Save to Mod** byte-patches each moved particle's transform translation in place (located by its original 64-byte matrix, translation-only overwrite, re-parse-validated → override + Build Package) · round-trip verified on Old-SR v2 (1,194 particles / 69 systems, size-exact) |
| **M34 ✅** | **Map material render-state fidelity** — parse real `StaticMaterialPassDef` fields (`blendEnable`/`cullEnable`/blend factors/`writeMask`) + `AlphaTestValue` param into `MaterialProfile` (Opaque/Cutout/Transparent, double-sided, alpha cutoff) · per-submesh `cullEnable` in the renderer (no global cull mode) · two-sided lighting via `gl_FrontFacing` (backface normals flipped) · negative/mirrored transform detection + inverse-transpose normal matrix · per-mesh **Flip Normals** tool · Face-Orientation / Two-Sided / Mirrored debug views · honor texture `addressU/V` (Clamp stops decals tiling over the mesh) · Inspector + Material Editor show render state · verified via headless MapVisRender on Old SR |
| **M33 ✅** | **Map lighting + content polish** — Dragon/Baron visibility resolver fix + diagnostics · vertex-color/lightmap attribute metadata · **baked lightmap rendering** (Map12 `BakedLight` + `Texcoord7` convention: `diffuse × lightmap`) · mesh-details inspector · material **virtual assets** + lazy thumbnails (textures + material diffuse) in the Content Browser · viewport/document tabs (the loaded map survives tab switches) · Map Content panel folded into the Inspector |
| **M32 ✅** | **RiotApprox UV + specular correctness** — data-driven per-material preview `MaterialProfile` (features + UV transform) classified from real `.bin` data: `switches` (`USE_RIM` …), `paramValues` (`UV_Scale`/`UV_Offset`/`Decal_UV_Tile`/…), specular `Specular_*`/`Spec_Color` params · per-submesh UV transform (`uv·scale+offset`, optional rotation) in the shader · **rim gated on real rim data**, **specular off by default** (only for specular-tagged materials — 49/5,600 on SR, not global) · profiles: Champion Skin · Map Static · MatCap · Specular · Material Editor shows profile + specular flag + UV scale/offset + source param · **UV-checker** & **specular-only** debug views · renderer extended (per-submesh `SubmeshMaterial` uniforms), not rewritten; Basic mode unchanged · verified on real Aatrox + Map11 |
| **M30 ✅** | **Multi-select + batch transform** — `Core/Selection` (`SelectionSet<T>`: primary anchor, toggle/re-anchor, Changed event) · Ctrl+click viewport + tree multi-select, per-mesh amber highlight boxes + cool-blue group bounds, one gizmo at the selection center · **Batch Transform** inspector (move Δ · rotate/scale about center · Reset Selected · Clear Selection) · world-space `GroupMatrix` applied after each mesh's own transform so batch ops compose rigidly · one `BatchTransformCommand` per batch edit (drag or numeric) → one Undo step restoring every mesh exactly · byte-patch persistence carries the GroupMatrix · visibility-filtered meshes pruned from the selection · 20-check suite on real Map11 |
| **M29 ✅** | **Undo/Redo** — `Core/Undo` (`IEditorCommand`, `UndoRedoService` with merge/purge/savepoint, `CompositeCommand` for M30 multi-select) · typed commands: `MeshTransformCommand` (drag = one step), `BinEditCommand`, `TexturePathEditCommand`, `MaterialParamEditCommand`, `SamplerAddRemoveCommand` (exact-element reinsert) · Ctrl+Z/Y + Edit menu + ↶/↷ buttons · title-bar dirty savepoint · stale-document purge |
| **M28 ✅** | **Viewport editing fixed + click-to-select** — vertex re-upload moved onto the GL thread (the mesh actually moves now); pick ray derived purely from the render matrix (two-point unprojection, correct under the -X mirror); drag axis frozen at its press-time origin (kills the snap-back feedback loop); **click a mesh to select it** (exact triangle picking, visibility-aware, ~9ms/click, syncs the tree; empty click deselects) |
| **M23 ✅** | **Baron pit visibility** — decode the map's visibility controllers (`MapVisibilityControllers`: Dragon `0xc406a533` / Baron `0xec733fe2` / Child `0xe21083b5`, recursing `Parents`/`ParentMode`) → resolve each mesh to Base/Cup/Tunnel/Upgraded bits; the Baron combobox now live-filters the baron pit, combined with the dragon filter |
| **M22 ✅** | Camera (LMB look + inverted, fly on LMB) · **dragon visibility system** — per-mesh `VisibilityFlags` carried through the decoder, per-submesh render visibility toggle, **Dragon/Baron comboboxes** + *Meshes→Layer Groups→names* tree filter the viewport live (Base/Inferno/Mountain/Ocean/Cloud/Hextech/Chemtech/Void) |
| **M21 ✅** | Editor polish — **Unreal-style camera** (RMB look + WASD/QE fly · Alt+LMB orbit · MMB pan · wheel dolly · RMB+wheel fly-speed · F focus) · **logo** (titlebar icon + menu wordmark, runtime-loaded) · **Content Browser type icons** · shader fix: **normal-map gating** (normal maps never used as the base texture) |
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
    **textured** (from its `.materials.bin`), framed to its bounds; Inspector shows version/mesh/vertex/
    material counts. Large maps load 200–300 textures, so expect a brief decode + a few hundred MB of VRAM.

### M5 build-package checklist (project editing)

1. Open `Champions/Aatrox.wad.client`, then `File ▸ New Project` (uses the open WAD as source).
2. `File ▸ Save Project As…` → pick a folder (this becomes the workspace; overrides live in `overrides/`).
3. Click a `.tex` (e.g. `aatrox_base_tx_cm.tex`), right-click → **Replace Selected Asset…** → choose any `.tex`/`.dds`.
   The tree gets a violet dot, the Inspector shows *Modified — Project Override*, and the preview updates.
4. `Project ▸ Build Package` → console logs *replaced/copied* counts + *Reopened OK — N chunks*. Output lands in
   `<workspace>/build/<project>/` (it refuses to write into a Riot/League folder).
5. `File ▸ Open WAD` → the built file → navigate to the replaced texture → it shows the new image; everything else intact.
6. Right-click the asset → **Revert Asset**, build again → the original texture returns.

### M6 animation checklist (champion playback)

1. Open `Champions/Aatrox.wad.client`, navigate to `assets/characters/aatrox/skins/base/aatrox.skn`, click it.
   The textured mesh appears and the Inspector's **ANIMATION** panel shows *Skeleton: 127 bones*.
2. Pick an animation from the dropdown (e.g. `*idle*` or `*run*`) → it loads and starts playing; the model moves,
   stays textured, and the timeline + `0.00 / N.NN s (frame …)` advance.
3. **Play / Pause**, drag the **timeline**, change **Speed** (0.1–2×), toggle **Loop**, **◀| / |▶** step frames.
4. Toggle **Bones** (top-right of the viewport) → the skeleton overlay animates with the mesh.
5. If a champion's animations don't auto-list, `Tools ▸ Assign Animation…` to load a `.anm` from disk.
6. Selecting a non-mesh asset (texture/mapgeo) stops playback cleanly; mapgeo still renders textured (no regression).

### M7 `.bin` editing checklist (Aatrox)

1. Open `Champions/Aatrox.wad.client`, click `data/characters/aatrox/skins/skin0.bin` → the **BIN EDITOR**
   appears. Primitive fields (string/number/bool/hash/vector) have an editable text box; complex
   objects/arrays show their value read-only.
2. Filter for `skinScale` (a float) — change it (e.g. `1.09` → `0.5`), press **Enter** or **✓ Apply**.
   The field shows a dirty dot; an invalid value (e.g. letters) turns red and is rejected (decimals are
   culture-invariant). **↺** reverts the field; **Revert File** reverts everything.
3. Filter for `image`/a `.tex` string and point it at another existing texture path.
4. **Save To Override** → the edited `.bin` is re-serialized, re-parse-validated, and written into the
   project override layer (a project is created/prompted if needed). The asset turns **Modified**.
5. **Build ▸ Build Package** → reopen the built `.wad.client`; the edited `.bin` parses back with your
   changes and unmodified chunks are intact. **Export BIN…** writes the edited `.bin` straight to disk.
6. Right-click any field → **Copy Field Path / Hash / Value**.

### M8 Material Editor checklist

**Champion (Aatrox):**
1. Open `Champions/Aatrox.wad.client`, click `…/skins/base/aatrox.skn`. The Inspector shows tabs
   **Overview / Materials / Raw BIN Tree**; open **Materials** → 5 materials (skin default texture, Sword,
   Banner, VFXBase, Wings) with their texture slots (diffuse marked •), sampler names, submesh assignment, and params.
2. On `(skin default texture)` change the path to another existing `.tex` (e.g. another skin's `…_TX_CM.tex`),
   press **Enter**/**✓**, then **Apply (preview)** → the champion re-textures live. A bad path shows a ⚠ and the
   "Only unresolved" filter isolates it.
3. **Save To Override** → the edited `skin0.bin` is re-parse-validated and written to the project override.
   **Build ▸ Build Package** → reopen the built WAD → the material edit parses back.
4. Per-slot **Preview** (thumbnail), **Open** (jump to the texture asset), **Copy**, and **Replace Texture…**
   (drop in a raw `.dds`/`.tex` — reuses M5 replace).

**Map (Map11):**
1. Open `Maps/Shipping/Map11.wad.client`, select a `…/map11/*.mapgeo` → **Materials** lists the companion
   `.materials.bin` materials (StaticMaterialDef) with their texture slots. **Search** narrows the list.
2. Edit one diffuse `texturePath` to another valid map texture → **Apply** (viewport re-textures) → **Save To
   Override** → **Build Package** → reopen → edit persists.

### M10 sampler add/remove checklist (Aatrox)

1. Open Aatrox, select `aatrox.skn` → **Materials** tab. On a `StaticMaterialDef` material (e.g. `Sword_inst`)
   click **+ Add Sampler** → a new slot appears (cloned from an existing sampler, so its address modes etc.
   stay valid). Rename it / set its path, **✓** to apply.
2. Click **✕** on any added sampler to remove it. (The base/inline texture slots have no **✕** — they aren't
   array elements.)
3. **Save To Override** → **Build Package** → reopen the built WAD → the added/removed sampler parses back.

### M11 project editor checklist

1. **File ▸ Open Project Folder** → pick a mod folder (e.g. an *Old Summoners Rift V2* export). ReyEngine scans
   for `.wad.client` files + unpacked-WAD folders, writes `.reyengine/project.json`, and the Content Browser
   shows two groups: **Project** (editable, `PRJ`/`OVR` tags) and **Riot References** (read-only, 🔒); conflicts
   (asset in both) show ⚠.
2. **Project ▸ Manage Riot References…** → add a Riot WAD (e.g. `…/Maps/Shipping/Map11.wad.client`). Its assets
   appear under *Riot References*, read-only.
3. Select a Riot asset → preview works but the Inspector shows *Read-only Riot asset*; saving an edit is blocked.
   Right-click ▸ **Copy Asset To Project** → it becomes a project override (editable); edit its `.bin`/material/
   texture path → **Save To Override**.
4. **Project ▸ Build Package** → builds to `<project>/Build/` (folder projects are staged with overrides applied;
   project WADs are repacked). Reopen the built folder via **Open Project Folder** to confirm the edit is present.
5. **File ▸ Open WAD (inspect)** still opens a single WAD in read-only inspection mode (yellow banner).
