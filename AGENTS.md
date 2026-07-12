# ReyEngine — Agent Guide

ReyEngine is a **dark Unreal/Unity-style desktop editor for League of Legends map & asset mods**
(C#/.NET 10, Avalonia 11, Silk.NET OpenGL, LeagueToolkit, CommunityToolkit.Mvvm). It loads a
League project (WADs + a project override folder), previews maps/champions/particles/materials/
textures in a live GL viewport, lets the user edit them (transforms, materials, particles, sounds),
and exports a mod via **Build Package** / **.fantome**.

Built incrementally as milestones (M1…M57). Read `git log --oneline` — each commit message is a
detailed changelog of *what* and *why* (this is the primary design record; there is no wiki).

## Solution layout (no .sln — build the csproj directly)

- **ReyEngine.Core** — hashing (`HashAlgorithms` FNV1a/WadPath/xxhash, `HashSyncService`),
  WAD access (`WadArchive`), texture decode (`TextureDecoder`→`TextureImage`), projects &
  overrides (`ProjectWorkspace.StoreOverrideBytes`), settings. Pure, cross-platform.
- **ReyEngine.Formats** — all file formats: `MapGeo/` (mapgeo decode + placeables + sun props +
  bucket grid), `Materials/` (MaterialDocument, StaticMaterialDef), `Meshes/` (.skn/.scb/.sco),
  `Animation/`, `Skeletons/`, `Vfx/` (VfxSystemDefinition + resolver), `Particles/`
  (ParticleDocument), `Audio/` (Wwise bnk/wpk), `Meta/` (BinTree read/write, tolerant reader).
  **Pure C#, cross-platform, headlessly testable — most work happens here.**
- **ReyEngine.Rendering** — `ViewportMeshRenderer` (the GL scene renderer), VFX particle
  sim + billboard/mesh renderer, `ShaderUtil`. **OpenGL; GLES/ANGLE-compatible calls only.**
- **ReyEngine.App** — Avalonia MVVM UI: `MainWindow` + tool windows (`ParticleEditorWindow`,
  `MeshPreviewWindow`), viewmodels (`MainWindowViewModel` is the hub), `Services/`, `Themes/`
  (Kalista palette: `ReyColors.axaml` tokens + `ReyTheme.axaml` control styles).

## Build / run / verify

```bash
# build (there is no .sln; build the app, which pulls the other 3 projects)
dotnet build src/ReyEngine.App/ReyEngine.App.csproj -c Debug -v q -nologo
```

- **Windows-only in practice**: the app is `WinExe`, its GL backend is ANGLE (OpenGL **ES**),
  and audio playback shells out to an external `vgmstream-cli.exe`. It cannot run on a headless
  Linux box. **Codex/cloud agents: you can build, analyze, and edit everything, and you can
  headlessly test all of Formats/Core (pure C#). You cannot run the GL app, the GL harnesses, or
  audio playback — those must be verified on the user's Windows machine.**
- **Verify format work with a throwaway console harness**, not by launching the app: a `net10.0`
  Exe that `ProjectReference`s Formats/Core, parses a *real* file, and asserts (round-trip
  byte-equality, counts, decoded output). Prove parsers against real data, never assume.
- **Verify GL/renderer changes** by rendering headless through the *real* `ViewportMeshRenderer`
  (Silk.NET offscreen window) to a PNG, then reading the PNG — do not eyeball the live app.
- **Smoke-test the app** non-intrusively (the user may be at their machine): launch the built exe
  minimized, wait a few seconds, confirm it didn't crash, then kill it. Never steal focus /
  screenshot the whole screen.

## Hard constraints (violating these breaks things silently)

1. **GLSL shader strings must be ASCII-only.** A non-ASCII byte (em dash, `·`, `°`) inside a
   shader string builds fine in C# but breaks the GL driver's lexer → blank/garbled viewport.
   Comments *outside* the shader string are fine.
2. **Renderer: GLES-compatible GL calls only.** ANGLE lacks desktop-GL entry points —
   `glPolygonMode`, `glPolygonOffsetLine`, etc. crash at draw time. Use line-primitive geometry
   instead of wireframe fill modes.
3. **Do not rewrite the engine backend or break existing features.** These milestones are
   additive. Keep the map/champion/material/particle/build pipelines working.
4. **Riot reference assets are read-only.** Editing requires *Copy To Project* first; saved edits
   go to the project override (`StoreOverrideBytes` + `_overrides.Set`), never into a game WAD.
5. **Commit per milestone.** End commit messages with:
   `Co-Authored-By: <your-agent> <noreply@…>`

## Where verified knowledge lives

The commit messages contain the verified byte-level specs discovered per milestone (mapgeo
placeables incl. **MapAudio** `class 0xa783cfd5` = transform+name+eventName; **Wwise** event =
FNV-1 32-bit lowercase, bnk BKHD/DIDX/DATA/HIRC with the **v145 RanSeq tail-parse** trick;
**flowmap water** Flow_Map channel layout; **MapSunProperties** lightMapColorScale; **bucket grid**
in `EnvironmentAsset.SceneGraphs`; etc.). When touching a format, `git log -p -- <file>` first.

Test data (present only on the user's machine, not in cloud): game install under
`C:\Riot Games\League of Legends\Game\DATA\FINAL\…`; a CSLolGo profile (note the profile dir name
uses a non-ASCII `‗` separator); mod folders under `D:\Mods_Github`.

## Working style

Prefer the dedicated tools over shell `grep`/`cat`. Make independent edits in parallel. When the
user is thinking out loud or asking a question, answer first — don't apply a fix until asked. State
outcomes plainly (tests that failed, steps skipped). Keep changes at the same altitude/idiom as the
surrounding code.
