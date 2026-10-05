# Chroma Studio plan (M812, M813, then C3-C7)

Recolour one champion skin or chroma, its body and its effects, in one place, and ship it as an ordinary
project mod. Agreed 2026-10-02. M812 and M813 are done. The later steps are named C3-C7 and get their milestone
number when they start (the LTK game-data work, docs/plans/ltk-game-data-mods.md, takes M814-M819 and one more milestone first).

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
| C3 | **Body recolour, live.** Sliders recolour the body textures and colour parameters on the character while dragging (wire the existing pixel-swap hooks). Save re-derives from originals into the project; the Character window gets the Copy To Project route it lacks. In place, with "this also changes ..." warnings on shared textures. | Device A/B render, round trip into the project, Build Package ships it. |
| C4 | **Effects recolour.** birthColor, colour over life, linger and fresnel colours (constants, keys) and the colour / multiplier / gradient / palette textures of the skin's systems, through the Particle Editor's own edit path. | A real champion's systems round-trip with every colour key transformed exactly, masks byte-identical, device render. |
| C5 | **"This skin only".** Shared textures copied to skin-specific paths and this skin's references repointed; shared effects cloned into the skin's own bin and its ResourceResolver repointed. Research first: does every effect use (clips, spells, idle) go through the skin's resolver? | Every other skin byte-identical in a built package; user's in-game check. |
| C6 | **Studio UX and recipes.** Skin picker with chromas grouped under their skin, Body / Effects groups with per-material and per-system switches, hue-range eyedropper on the preview, before/after, a shipped chroma as reference. The recipe is saved in the project and re-applied from Riot's new files after a patch. What's New entry, wiki tutorial. | UiProbe, recipe re-application test. |
| C7 | **Ship.** Three skins end to end through Send to LTK Manager, in-game check, full suite, release. | |

Out of scope: new selectable chromas, colours inside .scb/.sco meshes, normal and mask maps, Riot's
component (Shimmer) effects.

## Measured by M812 (2026-10-03, every shipped skin bin unless stated)

**Sharing is normal, not an edge case.** 85-90% of a skin's colour items are used by another skin of the
same character (Aatrox over 41 skins: 8,065 of 9,383). C5 ("this skin only") is what lets a chroma be recoloured on its own.

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
- C3 may let the user opt an excluded sampler in.

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
  `reflectionFresnelColor`. Values are Vector4.
- How many exceed 1.0 depends on what is counted:
  - vfx-support-report.md counts 808 components above 1.0.
  - M813's read-only scan of every champion WAD found 26 DISTINCT vectors above 1.0, over the five fields'
    constants and first/last keys. Kalista's (255,255,255,255) is the largest.
  - C4 should count over every key.
- Negative colours ship too: five distinct vectors.
  - Four are fresnel colours: Singed, Vel'Koz, Jarvan IV and Jayce (-1,-1,-1).
  - One is a plain colour: Miss Fortune `SmokeTrail_AB6`.
  - `ColorTransform.CanTransform` leaves them untouched.
- Typed as colours but MASKS, never hue-shifted: `paletteDefinition.palleteSrcMixColor`,
  `alphaErosionDefinition.erosionMapChannelMixer`.
- Colour textures: `texture`, `textureMult.textureMult`, `particleColorTexture`,
  `paletteDefinition.paletteTexture` (replaces rgb), `reflectionMapTexture` (cubemap). Masks/data:
  `erosionMapName`, distortion `normalMapTexture`, `falloffTexture`, `glossTexture`, `transitionTexture`.
- An absent field is the schema default: never write a `constantValue` that is not there.

**From the M813 review, to settle in C3/C4.**
- **A particle's colour is a product.** It is birthColor x colour-over-life (or the linger colour) x the
  colour texture, with keys lerped per component and a per-channel probability table on each key
  (`VfxParticleSimulator` ~914).
  - Brightness and saturation applied to EVERY factor compound: a white multiplier becomes (B,B,B), so the
    result is B squared. Apply them to one factor only.
  - Hue-shifting each key on its own changes the colours between keys: red to green passes through yellow,
    but after +180 cyan to magenta passes through grey.
  - Census the colours that carry probability tables or coloured multipliers before C4 writes anything.
  - An absent field must be skipped by structure, not because the transform happens to be a no-op on white
    (brightness is not).
- **BC1/BC3 decode greys off-grey.** RGB565 endpoints decode a neutral texel to something like
  (132,130,132), about 1.5% saturation, or about 20% near black. Exact-grey protection therefore rarely
  fires on real textures. C3's default grey threshold should be about 0.05-0.1 with a feather, and
  possibly value-aware.

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

## C3 as built (M824)

**What the Character window's Chroma tab does now.** A BODY RECOLOUR section above the M812 inventory. After Scan colours it lists the body
textures with an include switch each, and one `ColorTransform` (hue, saturation, brightness, strength, colourise, a hue range with feather,
grey guard with feather; the guard defaults to 0.08 with a 0.06 feather, the M813 review's figure) acts on the included ones.

- **Defaults.** Included: the textures under the character's own folder (a shared one carries a SHARED badge). Off: textures outside the
  character's folder (matcaps, shared gradients), which other characters and maps also use. The inventory's excluded samplers (masks, normal
  and data maps) are listed only on request and off. DDS, BC5 and BGRA8 are listed with the reason and cannot be switched on.
- **Live preview.** The included textures are recoloured from their ORIGINAL decoded texels (never compounding) on a worker, one request at
  a time with only the newest kept, and pushed into the D3D11 texture pool at full size with the mips regenerated
  (`UpdatePooledTexture` + `RegeneratePooledMips`, the key being the scene's lower-cased path found by chunk hash), or - on the GL viewport -
  into the viewport's own decoded images through `QueueTextureUpdate`. Measured in a Debug build: 50-80 ms per slider position for a whole
  skin (Lillia 49: 13 textures up to 1024x1024). A 2048x2048 texture is ~130 ms in Release per M813, so a drag on the biggest textures
  updates a few times a second and always ends on the last position.
- **Save.** Apply & Save, Ctrl+S, the auto-save and Export / Build Package (all through `SavePendingEditorEdits`) re-derive each texture from
  Riot's pristine bytes (`CheckOutRecolorBase`), write TEX BC1/BC3 with `TexWriter` and record the transform in the same
  `TextureRecolorRecord` list (`Transform`, `ChromaSkin`, `WadFolders` are new, optional fields; old records and files load unchanged and
  write no new keys). A reload restores the sliders and the textures.
- **Where the files go.** In the WAD folder of every Riot WAD that holds the chunk, the Character window's champion first. Usually that is
  `<project>/<Champion>/...`; Aatrox's base diffuse is also in `Shaders.wad.client`, byte for byte, and the game reads whichever copy it
  mounts first, so both folders carry the recolour. A chunk the dictionary cannot name goes in as a loose `<hash>.tex` at the folder's root.
  The shared `RiotWadFolderNameForHash` now asks the read-only fallback mounts too: a second write of a texture used to find only the project
  copy and file it under `Overrides`.

**Guards (review round).** A saved texture the scan does not list, or whose original cannot be read, is carried unchanged: it stays in the
saved state, is never a target or a revert, and only an explicit switch-off, a saved Reset or Revert removes it. A project texture that differs
from Riot's with no record behind it (hand-edited) is left off, never drawn over by the preview, and warns when switched on; a texture only the
project holds is refused with a reason (an override beside it would be shadowed). The record's `WadFolders` are read back as one plain
folder name and the file is proven below the project root. A pending recolour is saved before the window loads another model (a failed save
keeps the model), and the auto-save does not retry a state that already failed.

**Remaining.** C4 effects; C5 "this skin only" (the warning says the recolour is in place); a texture shared by two skins' recipes belongs to the one
saved last. (The material colour parameters, left out of M824, are M825 below.)

## C3 colour parameters (M825)

**What it does.** The same sliders recolour the skin bin's colour PARAMETERS (`TintColor`, `OutlineColor`, `Bloom_TintColor`, the skin block's
`fresnelColor` / `reflectionFresnelColor`...) with the same `ColorTransform`; they are a COLOUR PARAMETERS list under the TEXTURES list of the card, each
with a switch, Riot's colour and the colour the sliders make of it (two swatches). One pending state: Apply & Save, Ctrl+S, the auto-save, Export,
Build Package and loading another model flush textures and parameters together (`SavePendingEditorEdits` is unchanged), and a part is saved only when it
differs from what the project holds.

**Which parameters.** The ones M812's inventory lists (`SkinColorScanner.IsColorName` + a Vector4 / Color / Vector3 value) in the skin bin's own
materials and default block - but that heuristic is a NAME test and measured over the 14,937 shipped skin bins it also lists numbers (`VColor_G_Mask_Discard_Size`
= (100,0,0,0) on 1,920 materials, `ColorFresnelSize`, `Fresnel_Color_Intensity`, `FresnelColor_Bias`, `Rim_Color_Strength`...), a hue shift of which would turn a size
into another channel. `SkinColorParameters.Classify` is the narrower test a write needs: no mask/size/intensity/strength/bias/range/... word in the name, not a lone
number in the first channel, not a negative colour (`CanTransform`), not a value the entry leaves out (an authored zero), not defined in a linked bin (shared with
other skins: C5). The word test applies to every type (Color, Vector3, Vector4); the lone-number shape only to a Vector4. Everything left alone is listed with the reason. Census (24 champions,
2,280 skin bins): 13,197 parameters recolourable, 3,221 left alone; over all 14,937 bins a recolourable Vector4 of shape (x, y, 0, 0) with both non-zero is 23 names and about 840
values, all orange or brown colours with alpha 0 (`Fresnel_Color (1, 0.5, 0, 0)`), so that shape is NOT excluded, and no recolourable name has params or mix in it (blend / scroll
names such as `Blend1_Color`, `AdditiveScroll_ColorTint_G` are saturated colours). A Color-typed value is clamped to 0..1 on write and in the preview (its bytes cannot hold more).

**Never compounding.** `SkinColorParameters.Rewrite(current, riot, transform, recolour, restore)` computes every value from RIOT's untouched bin
(`ReadRiotOriginalBytes`; for a bin the imported GameData changes, the bin LTK makes of the package's modules, `TryReadImportedOnly`) and writes it into the
bin the project serves NOW; alpha is the current bin's own, HDR keeps its intensity, negative colours stay as authored, and nothing else in the bin is touched.
The writer is `MaterialDocument.Serialize` - the Material tab's own - which writes properties in its own order: 85% of Riot's skin bins are not byte-identical
to their own re-serialisation (same length, same data; `BinTreeEquivalence` agrees), and a recolour that changes no value writes nothing.

**Through the bin save path.** `SaveEditorBinBytesAsync(entry, bytes, openedFrom: the bytes it read)`: the M819 guards, M823's declarations (a bin the GameData
changes is kept as a module on top of it and a revert takes it away; an edit the declarations cannot express - a material's `paramValues` needs LTK's class
schema - is refused with the reason), and M823's merge when a GameData bin moved meanwhile. Any other bin is NOT merged by that path, so the save reads the bin again
right before it writes and derives the values again when it moved (three tries). Because a refusal would otherwise make every Build and Export flush throw once a slider
moved, a material's parameters on a GameData-target bin start switched OFF with the reason on the row (the skin block's colours, which a declaration can express, stay on);
switching one on is the person's choice, and the auto-save does not retry a state that was refused. A Riot bin the project does not hold is first copied into
the champion's WAD folder (Copy To Project of that one asset, as the textures are placed), then edited in place; the record remembers that copy (`PlacedFile`) and,
when the last parameter is given back and the file is still Riot's data, removes it again - a file somebody edited since stays. The Material tab: unsaved edits of the same bin are
saved FIRST through its own save (`IsDirty` means "differs from what was opened", which stays true after a save, so unsaved is decided by comparing the document
with the served bin), the recolour is written on top, and the tab's document is read again - selection and search kept, and not at all when the tab changed meanwhile (its
next save merges) - so it shows the new colours and its next save starts from them; the preview is then built again from that document, so a scene the editor's own save
started cannot land after the recolour. The undo history of the tab's document does not survive that reload.

**The record.** `Project.ChromaParameterRecolors` (null until a skin has one, null again after the last revert, so an older file has no new key): per skin bin, the
transform and the parameters the recipe owns (material path hash - 0 for the skin block -, name, occurrence); never the values. A save keeps the refs it neither recoloured nor gave
back (a parameter the scan does not list stays owned and keeps its Revert); the card's Settled is only what the save recoloured. A parameter the recipe owns that the project no longer
holds as the recipe wrote it (edited in the Material tab since) is flagged on the row, starts off and is kept as it is - a give-back (untick, Reset) releases it from the record without
overwriting it; only the explicit Revert restores it, and switching it on again recolours it from Riot's value. The texture records and
`ChromaSavedRecipe` are unchanged for an M824 recipe: it loads, shows no pending state, and its parameters start switched off.

**Preview (D3D11).** The scene reads a material's parameters from `PreviewMaterial.Params` on every draw, so a recolour is four floats written into the material that
holds the parameter (`ChromaDx11Parameters.Apply`; the skin block's `fresnelColor` goes into the arrays `PreparedCharacterScene.SkinBoundParameters` lists): no rebuild, no
texture work, one frame later. Not drawn, and said so on the row: a parameter a material DRIVER sets (the game overrides the authored value the same way - Lillia's gear
tints), the second copy of a repeated name (the scene reads the first, M790), and `reflectionFresnelColor` (no shader of the scene reads it). They are written all the same.
The OpenGL viewport shows a champion's diffuse textures (which M824 pushes) and none of its material parameters, so the parameters have no live preview there; they save the same.

**Remaining.** A recolour of a driver's own literal outputs (`dynamicMaterial`); linked-bin materials (C5); the GL viewport; an owned parameter edited in the Material tab
afterwards is overwritten by the next recolour save; the bin's property order after the first parameter write.
