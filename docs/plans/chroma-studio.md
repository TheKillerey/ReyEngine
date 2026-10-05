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
| C4 | **Effects recolour.** birthColor, colour over life, linger and fresnel colours (constants, keys) and the colour / multiplier / gradient / palette textures of the skin's systems, through the Particle Editor's own edit path. | A real champion's systems round-trip with every colour key transformed exactly, masks byte-identical, device render. **Done: M826**, see "C4 effects recolour" below. |
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

**Remaining.** C4 effects (done in M826, below); C5 "this skin only" (the warning says the recolour is in place); a texture shared by two skins' recipes belongs to the one
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

## C4 effects recolour (M826)

**What it does.** The Character window's Chroma tab has an EFFECTS RECOLOUR group under the colour parameters. The same sliders recolour the colour VALUES and the colour TEXTURES of the effects the skin
plays (the systems M812's scan lists). It is a third part of the one pending state: Apply & Save, Ctrl+S, the auto-save, Export and Build Package flush it with the body textures and parameters
(`SavePendingEditorEdits` is unchanged), and a part is saved only when it differs from what the project holds. Nothing is switched on by default: see "Defaults".

**Which fields.** Per emitter, the five `VfxColorReader.ColorFields`: `birthColor`, `color` (colour over life), `Linger.SeparateLingerColor`, `reflectionDefinition.fresnelColor` and
`reflectionDefinition.reflectionFresnelColor` - the constant AND every key of the curve (`SkinEffectColors`). A field the emitter does not author is not read, not listed and never written; a struct that
authors no `constantValue` gets none (the value's keys are rewritten in place: the key count, times and probability tables are untouched); a `ValueColor` that holds only a probability table has no colour of its
own and is not a field. `ColorTransform.CanTransform` decides per value: a negative colour stays as authored (12 values in the 16-champion census), an HDR colour keeps its intensity (36), alpha is
never changed, a `Color`-typed value (bytes) is clamped to 0..1 and rounded to its byte. `paletteDefinition.palleteSrcMixColor` and `alphaErosionDefinition.erosionMapChannelMixer` are never read as
colours.

**Left alone, and said so** (the card's LEFT ALONE list, with the count and the reason): the two mixers; the data textures `erosionMapName`, the distortion `normalMapTexture`, `falloffTexture`,
`glossTexture`, `transitionTexture`; the `reflectionMapTexture` cubemap (DDS, six faces: the project writer handles TEX BC1/BC3 only). A texture file the body also draws is not switched on here (one
file never has two recipes): its row says so. DDS, BC5 and BGRA8 are listed with the reason, as the body's are.

**Census first** (opt-in `REYENGINE_CENSUS=1`, `EffectColourCensus`: Lillia, Ahri, Aatrox, Lux, Jinx, Kalista, Yone, Seraphine, Ezreal, Jhin, Syndra, Zed, Yasuo, Sona, Karma, Katarina; 1,129 skins with
effects, 23,581 distinct systems REACHED by them, 177,844 emitters with a colour value, 296,220 colour fields: birthColor 124,166, color 161,440, linger 1,381, fresnel 6,943 + 2,290; 117,579 constants
and 178,641 curves with 625,121 keys):

- **Probability tables**: none on 280,141 fields (94.6%), alpha only 12,344 (4.2%), ONE table on red, green and blue 757 (0.26%), tables that differ per colour channel or sit on some channels only 2,978
  (1.0%: birthColor 2,705, color 273). The sampler applies them to the birth colour only (`SampleBirth`).
- **Coloured multipliers**: 108,835 emitters (61%) author two or more colour values; 19,720 (11.1%) multiply two COLOURED ones (saturation above 0.1), 19,614 of them birth x colour over life;
  23,187 (13%) have only neutral (white / grey) colour values, and 645 have no colour value at all (their colour is a texture or the default).
- **Keys**: of 446,480 adjacent key pairs, a hue shift of +-60 or 180 degrees bends the midpoint colour by more than 0.1 in 14,634 (3.3%) and by more than 0.25 in 2,723 (0.6%); +120 turns every channel
  into the next one, which is linear, so it bends none.
- **Hue on every factor**: for an emitter with two coloured factors, the hue of the product of the two shifted factors against the hue of the shifted product: mean error 8.7 degrees at +-60 and 180,
  more than 15 degrees in 21% of the samples and more than 30 in 6%; exactly 0 at +120.

**The product rule (decided).** A particle's colour is birthColor x (colour over life, or the linger colour) x texture. Brightness and saturation applied to every factor compound (a white multiplier
becomes (B, B, B), the result B squared), so ONE factor carries them: per emitter, of the colour values being recoloured, the slot that holds the most colour (the highest HSV saturation of its values; the colour
over life and the linger colour are one slot, because the linger colour replaces the colour over life; a tie goes to the colour over life; a lone factor carries). Every other factor and EVERY colour
texture takes the hue-only form of the transform (`SkinEffectColors.HueOnly`: hue, colourise, hue range, grey guard and strength; saturation and brightness at 1), which is a no-op on white and grey, so a
white multiplier stays white and the product is the transform once - proved by a test (a (0.8, 0.2, 0.1) birth colour under a white colour over life comes out exactly 1.5 times as bright at brightness 1.5, not 2.25). The two fresnel colours are not factors of that
product (a separate shader stage), so each takes the full transform. Textures never carry brightness or saturation: they multiply the colour values (compounding), and an 8-bit texel cannot exceed 1. The
cost of this choice: an effect whose colour is ONLY a texture (645 of 177,844 emitters have no colour value at all) answers hue, colourise and the hue range, but not saturation or brightness.
Hue-on-every-factor is the approximation measured above: it keeps the product's hue to within 9 degrees on average where two coloured factors multiply.

**Keys (decided).** Each key is transformed on its own, in place. Between keys the sampler interpolates linearly in RGB, so after a hue shift of 60 or 180 degrees the path between two coloured keys is the
straight line between the new colours, not the shifted old path: 3.3% of key pairs bend by more than 0.1 (0.6% by more than 0.25). Inserting keys would change a curve's structure (and the acceptance is
"every key transformed exactly"), so the choice is to accept it and document it. A shift of +120 or -120 bends nothing.

**Randomised colours.** A per-channel probability table multiplies the key's channel by a per-particle roll. After a hue shift the same tables land on the same RGB channels, so a scatter that was in
red and green is now in the new red and green. 1.0% of fields have such tables: they are recoloured like any other and the row says "randomised per channel: the scatter stays on the same RGB
channels". One table shared by the three colour channels (a brightness scatter) commutes with a hue shift and is not flagged.

**Never compounding.** `SkinEffectColors.Apply` derives every value from RIOT's bin (the bin LTK makes of the package's modules for a bin the imported GameData changes: `ReadChromaBaseBytes`, M825's) and writes it
into the bin the project serves now, so the result is a function of Riot's value, the transform and the set of fields - however often it is run (tests: a second transform equals the first run directly;
a third save; a bin whose fields are already as wanted writes nothing).

**Where the systems live and how a bin is saved.** The scan says which bin each system lives in (the skin bin, a Multi_Skins dependency bin, the champion's root bin...). Each touched bin goes through M825's write
(`WriteChromaSkinBinAsync`): the Material tab's and the Particle Editor's unsaved edits of that bin are saved first, the bin is read and re-derived again when it moved, `SaveEditorBinBytesAsync`
with the bytes it read (the M819 guards and M823's declarations), a Riot bin the project does not hold is first copied into the champion's WAD folder and remembered (`ChromaEffectRecord.PlacedBins`) so a
revert takes it out again while it is still Riot's data, and the project file is written bin by bin (a later bin that fails does not leave an earlier bin without an owner). A bin the GameData changes cannot
express a colour inside a list of embedded structs without LTK's class schema (the synthetic-game test is refused with that reason): those fields are listed recolourable but start off with the reason. The
Particle Editor's document of a bin is saved first when it holds unsaved edits and READ AGAIN from the recoloured bin afterwards (the selected system kept), so its next save starts from the recolour; a field
the Particle Editor changed since (an owned field that no longer holds what the recipe wrote) is flagged, kept as it is and carried until it is switched on again, which replaces the edit.
**A name no file can have**: Ahri's Multi_Skins bin is named by 430 characters (a file name holds 255); M825's copy-into-the-project threw, which would have failed the whole Apply. It is now placed as the loose
`<hash>.bin` at the WAD folder's root, which the packer and the mounts read as that chunk (the form an unnamed texture takes). An override would not ship in a folder project's build.

**Effect textures.** Through M824's pipeline unchanged: re-derived from Riot's pristine bytes (`CheckOutRecolorBase`), TEX BC1/BC3 by `TexWriter`, every WAD folder that holds the chunk, `TextureRecolors` records
told apart from the body's by a new optional `ChromaPart = "effects"` (null, and not written, for the body and for every older record). They take the hue-only transform.

**The record.** `Project.ChromaEffectRecolors` (null until a skin has one, null again after the last revert, no new key in an older file): per skin bin, the sliders' transform and the fields it owns - by
system hash, emitter ordinal (emitter containers walked in ascending field-hash order, so a re-serialised copy of the bin finds the same emitter) and field name, never by value - stored grouped by system
(`"3:birthColor"`), because a skin owns up to 3,905 fields (Lux skin 7) and a flat list of structs made project.json half a megabyte. Lillia 49 with everything on: 1,496 fields, 104 systems, project.json 171 KB
(the 142 texture records are most of it). The sliders restore from the first recipe found (textures, then parameters, then effects).

**Defaults (decided).** The effects start switched OFF (`MeshPreviewViewModel.EffectsStartOn`, one constant): over 85% of the systems a skin plays are shared with the champion's other skins (M812), the
recolour is in place until C5, and switching them on puts whole shared bins into the project. A saved recipe restores exactly its switches. All colours / No colours / All textures / No textures set them in
bulk; All textures covers this character's own folder only (a shared sprite outside it is switched on one by one, with its OUTSIDE badge). A system's switch sets all its fields and a field's switch
moves the system's, whether or not the system was expanded (the state lives in the card, rows are made when a system is opened).

**Preview.** The live preview is the save, not a second implementation: a working copy of each bin that holds a playing system (`EffectColorWorkingSet`) is recoloured with the same
`SkinEffectColors.Apply` and the systems it touched are read back through `VfxSystemResolver.ParseSystemObject`, so the definitions the viewport plays are bit-exact those the save writes (device test:
96 emitters compared). The card publishes them by rebuilding the playing items (idle effects, event composite, manual pick, children included) with the new definitions and recoloured COPIES of
the textures they draw with, so both viewports rebuild their simulators and the effects RESTART with the new colours on every slider position (a field switched off or a slider back at no change returns to what the
project holds; a save or a revert rebases the working copy). A D3D11 pool texture is also overwritten in place through `UpdatePooledTexture` + `RegeneratePooledMips` under the lower-cased
path the particle pipeline binds it with (the particle colour gradient is CPU-sampled and takes the copy). Throttle: the body's loop, newest request wins; measured in a Debug build, one slider position
of Lillia 49 with everything on (104 systems, 222 textures) is 78 ms from the slider to the republished playback. Not done: an in-place swap of the live simulators' definitions (the restart is the price of
keeping `AuthoredIndex`, child spawns and the D3D11 slices consistent, which all key on the definition instance); the OpenGL viewport republishes the same way and was not run (no GL context here).

**Cost.** Lux skin 7 (431 systems, 3,905 fields, 3 bins): scan and effect lists 0.7 s, Apply & Save 0.9 s, 210 MB more managed memory while the card holds the working copies; Ahri skin 0: 0.5 s, 1.7 s, 141 MB.

**Remaining.** C5 ("this skin only": the warning says the recolour is in place and names the other skins); a texture used by two skins' recipes belongs to the one saved last; beams'
`mAnimatedColorWithDistance` and `modulationFactor` are not recoloured (not listed by M812); Shimmer component emitters; the live preview restarts the effect; a baseline moved by an edit outside the card
(the Particle Editor saving the same bin) is read again only by the next scan; the working copy holds two parsed trees per bin.

**Review round (M826).** The preview remembers the originals of its recoloured textures weakly (a copy per slider position used to stay in a map until the next scan), and it only copies and pushes a texture an item that plays draws - an item built later asks for the rest once it is published; the D3D11 push uses the spelling the particle pipeline binds. A move of brightness or saturation alone leaves the effect TEXTURE part empty (they take the hue only), so textures saved under a hue are given back instead of orphaned and the card settles. A save spanning several bins judges every bin against the recipe as it stood before the save; the record's transform moves only after the last bin, a bin written at another transform in between is kept in `BinTransforms`, and the record keeps the emitter names beside the ordinals, so a field whose emitter Riot's bin now calls something else is listed as unchangeable and left alone. All colours and a system switch turn on only fields nobody changed (an edited or default-off field is switched on by hand; the share warning counts the edits it replaces). The live preview of a saved recipe judges "kept" by what the project holds, as saving does. A colour texture that is also read as a mask or data map by a system of the skin is listed as unchangeable: 25 of 232 files on Lillia 49, 5/71 Yone, 3/106 Ahri, 2/254 Lux 7, 10/94 Aatrox.
