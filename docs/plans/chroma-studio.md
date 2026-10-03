# Chroma Studio plan (M812-M818)

Recolour one champion skin or chroma, its body and its effects, in one place, and ship it as an ordinary
project mod. Agreed 2026-10-02.

**Ground rule.** A mod cannot add a new chroma to the client's skin list (the list is the client's), so the
studio repaints an EXISTING skin or chroma; the user picks which one the mod replaces. Riot's files stay
read-only: everything is re-derived from the untouched originals and written into the project through the
established override / Copy To Project paths, so Build Package, .fantome and Send to LTK Manager ship it.

**Where it lives.** The Character window (it already loads any skin or chroma, previews it on Riot's
shaders and plays its effects). The studio grows there as a card, starting with M812's read-only inventory.

## Milestones

| | Milestone | Done when |
|---|---|---|
| M812 | **Inventory (read-only).** Everything colour-bearing a skin or chroma draws: Body (material samplers, the skin's default texture fields, materialOverride textures, colour parameters) and Effects (the systems the skin uses, with colour fields and colour textures; masks excluded). Each item marked shared/unshared, naming the other skins and chromas of the champion that use it. Texture-format census. | Real-data tests on a base skin, a paid skin and a chroma (Lillia 49 over 46): structural assertions and known items, masks never listed, a known shared item detected. UiProbe card binds. |
| M813 | **One colour transform.** Hue rotate, saturation, brightness, colourise, a hue-range selection and grey protection, in one tested function for 8-bit texels and float colours. Colours above 1.0 keep their intensity; alpha is never touched; white stays white. | Unit tests, and Recolor Textures' existing output byte-identical. |
| M814 | **Body recolour, live.** Sliders recolour the body textures and colour parameters on the character while dragging (wire the existing pixel-swap hooks). Save re-derives from originals into the project; the Character window gets the Copy To Project route it lacks. In place, with "this also changes ..." warnings on shared textures. | Device A/B render, round trip into the project, Build Package ships it. |
| M815 | **Effects recolour.** birthColor, colour over life, linger and fresnel colours (constants, keys) and the colour / multiplier / gradient / palette textures of the skin's systems, through the Particle Editor's own edit path. | A real champion's systems round-trip with every colour key transformed exactly, masks byte-identical, device render. |
| M816 | **"This skin only".** Shared textures copied to skin-specific paths and this skin's references repointed; shared effects cloned into the skin's own bin and its ResourceResolver repointed. Research first: does every effect use (clips, spells, idle) go through the skin's resolver? | Every other skin byte-identical in a built package; user's in-game check. |
| M817 | **Studio UX and recipes.** Skin picker with chromas grouped under their skin, Body / Effects groups with per-material and per-system switches, hue-range eyedropper on the preview, before/after, a shipped chroma as reference. The recipe is saved in the project and re-applied from Riot's new files after a patch. What's New entry, wiki tutorial. | UiProbe, recipe re-application test. |
| M818 | **Ship.** Three skins end to end through Send to LTK Manager, in-game check, full suite, release. | |

Out of scope: new selectable chromas, colours inside .scb/.sco meshes, normal and mask maps, Riot's
component (Shimmer) effects.

## Measured by M812 (2026-10-03, every shipped skin bin unless stated)

**Sharing is normal, not an edge case.** 85-90% of a skin's colour items are used by another skin of the
same character (Aatrox over 41 skins: 8,065 of 9,383). M816 is what lets a chroma be recoloured on its own.

**How a skin reaches its effects.** It reaches them through:
- its own ResourceResolver's targets;
- links named directly in the skin object;
- children, by link or by key;
- the resolvers of GearSkinUpgrade objects (`mGearData.mVFXResourceResolver`).

Gear-only systems are marked "(inferred)": 136 skins reach 5,069 systems that way. No system in the
install is reached only through ANOTHER skin's resolver (0 of 14,937 skins), so the closure's other
resolvers are not merged.

**Where the resolver lives.**
- In the skin bin: 14,750 skins.
- In a dependency bin: 37.
- Nowhere: 4.
- None named: 146 companion skins.
- About 35% of skins reference keys or links that Riot's own data leaves dangling.

**Material links (25,022).**
- In the skin bin: 23,049.
- In a direct dependency: 1,899.
- Only in a transitive dependency: 0.
- In no linked bin: 74 (62 skins, e.g. Nunu, Quinn, Nasus; mostly the parent character's bin). These
  are warned about.

**Texture formats** (opt-in census `REYENGINE_CENSUS=1`, 10 champions, 648 skins, 21,759 colour
textures): TEX BC1 6,374, TEX BC3 15,354, DDS 31 (all reflection cubemaps). TexWriter already writes
every 2D colour texture; only cubemaps need more.

**Body classification by sampler name** covers 68% of sampler entries (203 names).
- The rest (iridescent, scroll overlays, RMA) are listed as excluded, with the reason.
- Channel-packed data maps (`EmissionR_DistortionG_Texture`) are excluded.
- M814 may let the user opt an excluded sampler in.

**Mount fix (whole Character window).**
- BuildMounts used to drop the champion WADs, so a champion's files went unreadable after any project
  change.
- It now re-mounts the champion the window has open, only from the current game folder.
- The mount index is published whole, and the fallback list is copy-on-write.
- Scans read through reader leases.

## What the code already has (mapped 2026-10-02)

**Skin loading (Character window).**
- `CharacterBrowserViewModel` lists `data/characters/<c>/skins/skinN.bin` from Riot's WAD (project skins are
  not listed). A chroma is an ordinary skin bin that names its base skin's .skn but has its own textures,
  material instances and ResourceResolver (Lillia 49 -> 46; never derive a bin from its mesh, M728).
  skins.json nests chromas under their skin (`ClientNameCatalog.ReadSkins`, `ClientSkin.ChromaOf`).
- `MainWindowViewModel.Characters.cs` `OpenSkin` -> `MakeCharacterWadReadable` (read-only fallback mount in
  project mode) -> `LoadMeshPreviewAsync(entry, skin.BinPath)`.
- Body textures: `MaterialDocument.Parse(bin, ...).Materials[].Slots` is the most complete list (local and
  linked StaticMaterialDef samplers, `(skin default texture)` = every texture field of skinMeshProperties,
  `(inline override: X)` per materialOverride). `TextureSlot` carries Path, ChunkHash, IsWadChunkLink and
  IsDiffuse/IsNormal/IsMask/IsGradient/IsEmissive/IsMatCap. Hash references with
  `BinTexturePath.HashOfReference` - `HashAlgorithms.WadPath` is wrong for `0x...` references.
- Effects: `TryLoadChampionVfxWithResources` (MainWindowViewModel) walks the dependency closure (shared
  `_multi_skins_` bins included, so it holds OTHER skins' systems) and returns systems by hash plus the
  resourceMap (effect key -> system). Nothing computes "the systems this skin uses" yet.

**Colour in effects** (`VfxSystemResolver.ParseEmitter`).
- Colour values (`ValueColor`: Embed { constantValue Vec4, dynamics Pointer -> times/values/probabilityTables }):
  `birthColor`, `color`, `Linger.SeparateLingerColor`, `reflectionDefinition.fresnelColor` /
  `reflectionFresnelColor`. Values are Vector4 and 808 shipped components exceed 1.0.
- Typed as colours but MASKS, never hue-shifted: `paletteDefinition.palleteSrcMixColor`,
  `alphaErosionDefinition.erosionMapChannelMixer`.
- Colour textures: `texture`, `textureMult.textureMult`, `particleColorTexture`,
  `paletteDefinition.paletteTexture` (replaces rgb), `reflectionMapTexture` (cubemap). Masks/data:
  `erosionMapName`, distortion `normalMapTexture`, `falloffTexture`, `glossTexture`, `transitionTexture`.
- An absent field is the schema default: never write a `constantValue` that is not there.

**Recolour pipeline (M171/M311).** `TextureRecolor.Apply` is a pure bytes -> bytes function over
`TextureAdjustment` (levels, contrast, HSV in sRGB-encoded space, tint, LUT, strength; alpha untouched; no
masking). Re-derives from originals (`ReadRecolorBase`), records in `Project.TextureRecolors`. Writes TEX
BC1/BC3 only, same path only; `.dds`, BC5 and BGRA8 are skipped.

**Live preview hooks (unused by the Character window).** D3D11 `ShaderPreviewRenderer.UpdatePooledTexture`
+ `RegeneratePooledMips` (same size, not on immutable textures; key = lowercased path shared by body and
particle textures); GL `ViewportControl.QueueTextureUpdate`. Pattern: `MainWindow.axaml.cs` ~776-808.

**Persistence.** Character material edits: `SaveMaterialOverrideFor` -> `GuardEditable` -> `RebaseOntoCurrent`
-> `SaveMapBinBytesAsync` (`TryWriteToProjectFile`, else `StoreOverrideBytes`). Particle edits:
`SaveParticleOverride` -> `SaveMapBinBytesAsync`. Copy To Project exists only from the asset tree.

**Risks to verify when a milestone touches them.** `OverrideMount` snapshots its folder (a first override
may not show until `BuildMounts`); `BuildMounts` drops the champion fallback mount while `_characterWads`
still marks it mounted; `ReplaceTextureForSlot` and `WriteRecoloredAsset` hash `0x...` references with
`WadPath`.
