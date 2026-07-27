# League VFX Data vs. ReyEngine — Support Census and Audit

**Date:** 2026-07-26 · **Scope:** champion + champion-skin + map VFX (`VfxSystemDefinitionData` graphs), plus the map placement/environment layer and the shipped particle shaders.

## Coverage — what was actually measured

| Corpus | Figure |
|---|---|
| WADs opened | **211** non-locale: 203 `Champions\*.wad.client` + 8 content-bearing `Maps\Shipping\*.wad.client` |
| Locale WADs excluded | 203 `*.en_US.wad.client` (Champions) + 8 (Maps) — verified to contain **0** `.bin` chunks |
| Chunks enumerated | 559,494 |
| Chunk paths unresolved | 25 (0.004%) against `data\hashes\merged_hashes.cache` + CDTB shards. None of them is a usable `.bin` (2 champion-side chunks magic-sniffed as non-PROP; 1 map-side PROP chunk contains 0 objects) |
| `.bin` chunks parsed | **30,015**, LeagueToolkit `BinTree`, **0 parse failures** |
| Bins containing VFX | **13,398** |
| `VfxSystemDefinitionData` | **189,274** (162,720 of them champion-side) |
| `VfxEmitterDefinitionData` | **1,398,802** (1,221,115 champion-side) |
| Emitter property occurrences | **28,569,369** |
| Distinct emitter fields | **134** |
| Curve blocks (`VfxAnimated*VariableData`) | 5,578,945 |
| `VfxProbabilityTableData` | 6,590,560 (3,364,443 with keys; 3,226,117 are empty structs) |
| Map `MapParticle` placements | 29,811 |
| Shipped shaders inspected | `data/shaders/shaders.bin` (347 `CustomShaderDef`, 7 under `Shaders/Particles/`), all 833 `.dx11` permutation TOCs, DXBC RDEF of `particlesystem/quad_vs` and `quad_ps` |
| Textures decoded | 30,644 distinct emitter-referenced `.tex` (blend/authoring correlation), 33,490 header-inspected |

**Where this is a sample, not a census.** The emitter/system/field inventory, all frequency counts, and the ReyEngine coverage diff are **exhaustive over the 211-WAD corpus** — no extrapolation. Three sub-studies are samples and are labelled as such at point of use:

- Texture-authoring-vs-`blendMode` correlation: 20,101 single-mode textures from Map11 + 34 champion WADs (two independent runs on disjoint champion sets agreed to within 2 percentage points).
- Serialization round-trip: 400 VFX bins (every 17th), 393 parsed, 0 exceptions.
- Hash brute-forcing: ~1.3 × 10⁹ candidate names. At that volume ~9 false positives are expected by chance, so only hashes with a semantically coherent hit are reported as recovered.

**Not covered:** `Global.wad.client`/`DATA.wad.client`/`Scripts.wad.client` bins beyond `shaders.bin`, TFT set WADs, Companions, UI. `DATA.wad.client` holds 1,189 legacy `.troybin` particle files (pre-PROP format) and **zero** `.bin` — nothing here covers them. No live-client frame capture was taken, so every claim about Riot's *runtime* behaviour (blend states, sRGB, interpolation scheme) is marked UNKNOWN rather than asserted.

**Headline.** Emitter *discovery* is complete and correct: `VfxSystemResolver.ExtractAll` returns exactly 189,274 systems and 1,398,802 emitters — byte-identical to an independent exhaustive graph walk. Nothing is lost at the container level. Everything below is a *property-level* and *renderer-level* deficit. ReyEngine reads **40 of 134** emitter fields (**72.5%** of property occurrences, 30% of distinct fields) and **7 of 28** system fields. Roughly one emitter in six (215,373, 15.4%) has a primitive class the renderer silently degrades to a billboard, and 216,819 (15.5%) spawn from a point where League fills a volume. The gap is large; this report quantifies it rather than softening it.

---

## Fully Ported

Parsed **and** editable **and** rendered/simulated correctly **and** round-tripped on save. Anything that fails one of the four is in *Partially Ported* below, including several things it would be tempting to list here.

| Property / mechanism | Type | Freq | Evidence |
|---|---|---|---|
| Emitter discovery — both `complexEmitterDefinitionData` (0x868eb76a, 178,423 containers) and `simpleEmitterDefinitionData` (0x6781e762, 703) via the generic container scan | Container\<Struct\> | 1,398,802 emitters | `VfxSystemResolver.cs:171-176`; resolver output == exhaustive walk, 0 lost |
| `Value*` / `IntegratedValue*` container schema — `constantValue` (0xb4b427aa) + `dynamics` (0xbc037de7), nothing else on 12.8M instances | Embedded struct | ValueVector3 5,349,107 · ValueFloat 4,307,185 · ValueColor 2,346,812 · ValueVector2 844,427 | `data/characters/sru_atakhan/sru_atakhan_multi_skins_skin0_skins_skin1_skins_skin2_skins_skin3.bin` @ Map11 |
| Curve keyframe block — `times`/`values`/`probabilityTables`, linear interpolation with clamped ends | Struct | 5,578,945 | `VfxSystemDefinition.cs:175-194`. The data contains **no** tangent, ease or interpolation-mode field anywhere, so linear-over-(times,values) is the only scheme the format can parameterise |
| Probability tables as **multipliers** on the sampled curve value | Container\<VfxProbabilityTableData\> | 3,364,443 with keys | Proven: Riot authors `birthRotation0` both as curve=1 × prob[0..360] (156,700 emitters) and curve=360 × prob[0..1] (50,422); both idioms co-occur in 5,914 bins and are equivalent only under multiplication. `data/characters/turret/turret_multi_skins_skin1_..._skin35.bin` @ Map35 contains both. ReyEngine: `VfxSystemDefinition.cs:125,146-148,167-170` |
| `emitterName` / `particleName` / `particlePath` | String | 1,220,735 / 162,720 / 162,720 | Metadata only; displayed, editable, serialized |
| `texture` | String | 1,378,346 | Uploaded and sampled; `VfxParticleRenderer.cs:94-118` |
| `disabled` | Bool (only `true` written) | 34,647 | Honoured by the resolver, editable, serialized |
| `isSingleParticle` | BitBool | 907,926 | `VfxParticleSimulator` one-shot path |
| `timeBeforeFirstEmission` | F32 | 271,162 (champion) | Start delay, editable |
| `isRandomStartFrame` | BitBool | 254,728 (champion) | Flipbook start randomisation |
| `distortionDefinition.distortion` + `.normalMapTexture` | F32 + String | 20,148 / 20,185 | Rendered (`VfxParticleRenderer.cs:589-597`). Note the sibling `distortionMode` is *not* — see Partially Ported |
| `ResourceResolver.resourceMap` (0xd2f58721) — animation effect-key → system | Map\<Hash, ObjectLink\> | 12,416 of 12,639 resolvers | `VfxSystemResolver.cs:104-131`. Hash re-confirmed; the alternate `mResourceMap` (0x64b8a242) never occurs in champion data |
| VFX colour is straight (non-premultiplied) LDR `Vector4` | Vector4, never `BinTreeColor` | 5,968,487 colour leaves | 23,873,948 components: 808 exceed 1.0, 7 negative, max 255. `PREMULTIPLIED_ALPHA` appears on 10 of 833 shader TOCs, all staticmesh, **0** particle TOCs; 63% of mode-1 pixels have max(RGB) > A, impossible under premultiplication. ReyEngine's `fragColor = t * vColor` (`VfxParticleRenderer.cs:599`) is the right shape |
| Round-trip serialization is structurally lossless | — | 393 bins sampled | `ParticleDocument.Serialize` writes the whole `BinTree`, so all 94 unread emitter fields survive an override save. 0 exceptions, 0 semantic diffs. **Caveat:** not byte-exact — LeagueToolkit reorders properties in 281 of 393 bins (`data/characters/sightward/skins/skin30.bin` @ Map11: 2,355 of 5,919 bytes differ, canonical dumps identical) |

---

## Partially Ported

### Curve sampling

**Birth-time curves are frozen at key 0.** `SampleBirth` calls `Sample(0f)` unconditionally (`VfxSystemDefinition.cs:123,143,164`), and `Interp` clamps `t ≤ times[0]` to `values[0]` (`:181`). Roughly **172,200 multi-key birth curves** are therefore reduced to their first keyframe.

| Field | Multi-key count | Max keys |
|---|---|---|
| `birthColor` | 70,770 | 20 |
| `birthScale0` | 35,408 | 17 |
| `birthRotationalVelocity0` | 16,682 | 11 |
| `birthVelocity` | 15,707 | 22 |
| `particleLifetime` | 13,493 | 6 |
| `birthOrbitalVelocity` | 9,253 | 7 |
| `birthRotation0` | 3,885 | 21 |
| `birthUvScrollRate` | 3,780 | 6 |
| `birthDrag` / `birthFrameRate` / `birthAcceleration` | 2,217 / 519 / 477 | 6 / 4 / 4 |

Live proof: `VfxCurve4` with keys [0,1] → white,red returns `<1,1,1,1>` from `SampleBirth` and `<1,0,0,1>` from `Sample(1)` — the red end is unreachable. Real case: `data/characters/sru_atakhan/sru_atakhan_multi_skins_skin0_..._skin3.bin` @ Map11, emitter `trailBlend`, `birthColor` times [0.035, 0.255, 0.771, 1] with alpha 0.24 → 1 → 1 → 0; every particle spawns at alpha 0.24. **Which axis a birth curve is keyed on is UNKNOWN** — it cannot be particle age (the value is consumed once). Normalised emitter lifetime is the only other axis the format offers, and ReyEngine already uses that axis for `rate` (`VfxParticleSimulator.cs:167-170`), so it is internally inconsistent — but this remains inference, not measurement.

**`constantValue` is silently discarded when `dynamics` is present.** `VfxCurveF.Sample` returns `Constant` only when there is no curve (`VfxSystemDefinition.cs:117-119`). Measured: 72,182 `rate` values carry both, and in **21,270** of those `constantValue != values[0]` (e.g. `constantValue=5`, `values[0]=0`). Whether Riot multiplies constant × curve or uses the curve alone is UNKNOWN. Same shape on `worldAcceleration`, where 146,634 of 179,935 (81.5%) carry a `constantValue` that is dropped.

**Rate curve frozen on infinite emitters.** `emitterT = 0` when `EmitterLifetime` is null (`VfxParticleSimulator.cs:167-169`). 267,152 emitters have a rate and no `lifetime`; 2,527 of those have a multi-key rate curve, and 135 have `values[0] == 0` — those emit nothing at all.

**Curve time domain validated (positive).** 4,813,364 emitter curves: 2,194,566 end exactly at 1.0, only 8,308 (0.17%) exceed 1.0, 1,613 are non-monotonic, 159,477 (3.3%) do not start at 0. The clamped linear scan handles all of them safely. `len(times) == len(values)` in 100% of cases.

### Spawn shapes

`ReadSpawnShape` (`VfxSystemResolver.cs:282-290`) reads only `emitOffset` / `emitRotationAxes` / `emitRotationAngles`.

| Shape class | Instances | Status |
|---|---|---|
| `0xee39916f` (name unresolved) | 308,070 | **Works by accident** — single field `emitOffset` as a *plain* `Vector3`, caught by the `AsVec3` fallback at `:353`. Loses per-particle randomisation (a bare Vector3 carries no probability table) |
| `VfxShapeLegacy` (0x4f4e2ed7) | 185,017 | Fully handled |
| `VfxShapeCylinder` (0x12ab94a6) | 83,984 | **Collapsed to a point** — carries `radius` (73,744), `height` (42,848), `flags` (41,636) |
| `VfxShapeBox` (0xba945ee1) | 83,530 | **Collapsed to a point** — `flags` (79,422), `Size` (67,895) |
| `VfxShapeSphere` (0x3dbe415d) | 49,305 | **Collapsed to a point** — `radius` (38,256), `flags` (32,537) |

**Not one** box/cylinder/sphere in the corpus has an `emitOffset` field, so **216,819 emitters (15.50%)** spawn every particle at a single point. Value ranges are far wider than a naive implementation would assume: sphere radius up to 302,000,000 (`data/characters/syndra/syndra_multi_skins_skin44_..._skin53.bin` @ Syndra), cylinder radius 20,100 / height 250,100 (`data/characters/twistedfate/skins/skin3.bin`), box |Size| component 25,000 (`data/maps/shipping/map33/map33.bin`). `flags` (U8) is present on 95.2% of boxes — more often than `Size` — and its domain is unmeasured. Evidence bin with all shapes: `data/characters/aatrox/skins/skin37.bin` @ Aatrox (Box ×22, Cylinder ×18, Sphere ×1, 0xee39916f ×8, Legacy ×1).

`VfxSystemResolver.cs:75,284` also defines a fallback lookup for a field named `shape` (0x9dc3d926) — **dead code**, 0 occurrences in 1,398,802 emitters.

### Primitives

`IsMeshPrimitive` / `IsArbitraryQuad` match only two class hashes (`VfxSystemResolver.cs:203-204`); everything else falls through to a camera-facing billboard (`VfxParticleRenderer.cs:182`).

| Primitive class | Emitters | What ReyEngine does |
|---|---|---|
| `VfxPrimitiveMesh` | 349,009 | Recognised (mesh path) |
| `VfxPrimitiveArbitraryQuad` | 286,125 | Recognised (placement-oriented quad). Zero properties in 100% of instances — a pure type marker |
| `VfxPrimitiveAttachedMesh` | 62,409 | Billboard. Carries `mMesh` on 57,409, but only **1,678** of those name a mesh file; the other 54,690 hold `mLockMeshToAttachment` / `mSubmeshesToDraw` / `mSubmeshesToDrawAlways` — a submesh selection on the *host character model*, a separate and equally unsupported feature |
| `VfxPrimitiveRay` | 58,434 | Billboard. **Zero properties in 100% of instances** — nothing in the data says what a ray looks like. 7,361 of them do set `isDirectionOriented`, so for those the billboard is at least velocity-aligned (`VfxParticleRenderer.cs:542-546`) |
| `VfxPrimitiveCameraTrail` | 48,185 | Billboard; `mTrail` (48,098) unread |
| `VfxPrimitiveArbitraryTrail` | 30,836 | Billboard; `mTrail` (30,754) unread |
| `VfxPrimitiveBeam` | 13,829 | Billboard; `mBeam` (10,088) and `mMesh` (4,704, all named) unread |
| `VfxPrimitivePlanarProjection` | 1,025 | Billboard; `mProjection` (953) unread |
| `0x8df5fcf7` (unresolved) | 431 | **Not drawn at all** — 431 of 431 have no `texture`, and `VfxParticleRenderer.cs:183` skips texture-less emitters |
| `VfxPrimitiveCameraUnitQuad` | 197 | Billboard |
| `VfxPrimitiveCameraSegmentBeam` | 27 | Billboard |

Total unrecognised: **215,373 (15.4%)**. About 2,100 of those are not drawn at all (no texture). Only ~6,382 lose a loadable mesh file. `AlignYawToCamera` / `AlignPitchToCamera` occur on **both** `VfxPrimitiveMesh` (5,020 / 4,669) and `VfxPrimitiveAttachedMesh` (327 / 386) and are unread on both; `VfxPrimitiveAttachedMesh.UseAvatarSpecificSubmeshMask` (1,817) is also unread.

*Inference marker:* "trails and beams are ribbon geometry" is a reading of the class names plus the `mTrail`/`mBeam` payloads; it is **not** measured, and it does not extend to `VfxPrimitiveRay`, which has no data at all.

### Blend state

`blendMode` (U8, 0xfa784eab) — present on **1,288,262** emitters (92.10%), absent on 110,540 (7.90%).

| Value | Count |
|---|---|
| 4 | 687,615 |
| 1 | 557,058 |
| 3 | 29,770 |
| 2 | 8,189 |
| 5 | 5,372 |
| 8 | 103 |
| 6 | 89 |
| 7 | 66 |
| **0** | **never written, in 1,288,262 explicit values** |

**The integer → blend-state table is not in shipped data.** `data/shaders/shaders.bin` contains 347 `CustomShaderDef` objects, only 7 under `Shaders/Particles/`, and a full field-hash census of the file yields 17 distinct field names — none blend-related. The real default particle pixel shader (`assets/shaders/hlsl/particlesystem/quad_ps.ps.dx11` @ ShaderCache.dx11.wad.client) likewise declares no blend state. The table lives in `League of Legends.exe`.

What ReyEngine does: `IsAdditive(m) => m is 1 or 3 or 4 or 5` (`VfxParticleRenderer.cs:268`), with `IsAdditiveFor` overriding mode 3 at runtime based on whether >1% of the sprite's texels have alpha < 250 (`:275-278`, input computed at `:98-100`). Consequences: identical authored data renders two different ways depending on the texture (on 391 exclusive mode-3 textures the heuristic splits 69.8% additive / 30.2% alpha); modes 6/7/8 (258 emitters) fall off the end of the list rather than being decided; and an absent `blendMode` defaults to 1 (`VfxSystemResolver.cs:243`) for 110,540 emitters. The same guess governs the mesh path at `:433`.

Indirect evidence from texture authoring (sample: 20,101 single-mode textures; additive can only brighten, so an additive sprite must be black-bordered and alpha-free):

| Mode | n | uses alpha | black border | transparent border |
|---|---|---|---|---|
| absent | 1,731 | 22.8% | **91.1%** | 18.0% |
| 1 | 7,277 | **89.7%** | 18.2% | 71.5% |
| 4 | 10,547 | 68.1% | 44.2% | 52.2% |
| 3 | 391 | 30.2% | 5.9% | — |
| 2 | 96 | — | **93.8%** | — |
| 5 | 58 | 93.1% | — | 69.0% |

Mode 0/absent is the clean additive signature; mode 1 the clean straight-alpha signature; mode 4 — half the corpus — is genuinely bimodal and this test does not separate it from mode 1. **The claim "the default is 0" is refuted:** BIN does *not* omit default-valued properties (`alphaRef` is explicitly written as `0` on 391,078 emitters; `pass` as `-1` on 37,697), so a never-written value proves nothing about the default. ReyEngine's `?? 1` is a guess, but so is `?? 0`, and the absent-mode texture authoring above actually favours additive.

Evidence bins: `data/characters/vex/skins/skin22.bin` @ Vex (modes 6 and 8), `data/characters/braum/skins/skin54.bin` @ Braum (mode 7).

### `texDiv` and the flipbook

`texDiv` (Vector2, 0x86a84509) — 319,777 occurrences. 27,625 fractional, 7,492 negative, 95 containing a zero. `numFrames == round(texDiv.x * texDiv.y)` for 244,567 of 256,672 emitters carrying both (**95.3%**), so the grid reading is sound.

ReyEngine clamps twice on the quad path: CPU-side `X <= 0 ? 1f` (`VfxParticleRenderer.cs:207-208`) and `max(uTexDiv, 1.0)` in the vertex shader (`:559-560`). Broken down by primitive, sub-1 `texDiv` is overwhelmingly a **mesh** phenomenon (16,202 of 20,504 sub-1 values sit on `VfxPrimitiveMesh`), and the mesh path already honours it (`:444`, `:491`). The quad-path damage is therefore smaller than it first appears: **2,293 quad emitters lose a negative component and 390 lose a sub-1 component**. What negative/fractional `texDiv` *means* in League is UNKNOWN — "mirror" and "zoom" are guesses; the separate `TextureFlipU` (6,747) / `TextureFlipV` (7,443) fields show flipping is a first-class concept elsewhere in the schema.

The two shader paths disagree on the field's meaning regardless: `:559-560` treats it as an atlas divisor, `:491` as a tiling multiplier.

**Frame selection is a guess.** When `numFrames > 1` and neither `frameRate` nor `birthFrameRate` is authored — **243,492 emitters (87% of all flipbooks)** — the simulator invents `frame = floor((startFrame + t*numFrames) % numFrames)`, one full pass per particle lifetime (`VfxParticleSimulator.cs:284-288`). `Shaders/Particles/DefaultParticleLit` declares `FrameIndex` (default 0), `FlipbookSpeed` (default 1) and `FlipbookSize` (default (2,2)) behind a `FLIPBOOK` static switch, which is consistent but not proof. Separately, 30,115 emitters have a multi-cell `texDiv` and **no** `numFrames`; the resolver defaults `NumFrames` to 1 (`:254`) so the frame stays at 0 while the UV divisor still shrinks the sprite to cell 0.

`texDivMult` on the multiply stage has the same clamp plus a second bug: `vUvMult` never adds the flipbook frame offset (`:568-569`), so a 2×2 multiplier atlas (4,010 emitters) is frozen on its top-left cell.

### `textureMult` — 3 of 18 sub-fields read

`VfxTextureMultDefinitionData` (0xb097c1bd), 319,003 emitters (22.8%). All 18 field names hash-confirmed, including Riot's own typos `TextureMultFilpU` / `TextureMultFilpV`.

Read (`VfxSystemResolver.cs:217-222`): `textureMult` (305,572), `birthUvScrollRateMult` (144,122), `texDivMult` (16,367).
Unread: `uvScaleMult` 111,864 · `birthUVOffsetMult` 90,627 · `ParticleIntegratedUvScrollMult` 44,392 · `texAddressModeMult` 38,372 · `UvRotationMult` 21,458 · `birthUvRotateRateMult` 18,745 · `uvScrollClampMult` 10,436 · `ParticleIntegratedUvRotateMult` 9,541 · `uvScrollAlphaMult` 5,077 · `TextureMultFilpU/V` 4,338 · `uvTransformCenterMult` 1,953 · `emitterUvScrollRateMult` 1,235 · `flexBirthUVScrollRateMult` 260 · `isRandomStartFrameMult` 7. Also 13,431 emitters have a `textureMult` struct with no inner path string, so the stage is skipped entirely.

### `emitterPosition`

652,761 emitters (46.7%). Parsed then reduced to `.Constant` (`VfxSystemResolver.cs:251`). 146,346 carry a curve that is discarded; **130,259 have no `constantValue` at all** and collapse to (0,0,0) — verified end-to-end, `ExtractAll` returns `EmitterPosition != Zero` for only 522,502 of them. *Correction to an earlier reading:* this row **is editable** in the Particle Editor for the 522,502 with a constant, because `BinTreeEmbedded` subclasses `BinTreeStruct` and matches the `Value*` case at `ParticleDocument.cs:139-142` (measured: module=Position, TypeName=Vector3, IsReadOnly=False).

### Parsed but never used

| Field | Type | Freq | Where it dies |
|---|---|---|---|
| `particleLinger` | Optional\<F32\> | 678,442 (48.5%) | Stored at `VfxSystemDefinition.cs:25`, referenced nowhere else. `VfxParticleSimulator.cs:186` comments that it does nothing. What Riot does with it is UNKNOWN — that comment is ReyEngine's own guess, not a Riot source |
| `distortionDefinition.distortionMode` | U8 | 8,168 explicit (2:5,436 3:2,716 0:16) | Parsed at `VfxSystemResolver.cs:228-231`; renderer uses only `Strength` (`VfxParticleRenderer.cs:217`). Three authored modes collapse to one refraction path |
| `visibilityRadius` | F32 | 25,044 systems | Parsed, but consumed **only** by the map audio path (`MapPlaceableExtractor.cs:157`), never to cull particle rendering |
| `soundOnCreateDefault` / `soundPersistentDefault` | String | 18,138 / 11,217 systems | Same — audio placement only |
| `acceleration` | Embedded ValueVector3 | 3,509 | Used only as a fallback: `birthAcceleration ?? acceleration` (`:262`), so emitters carrying both lose the curve |

### Editable in the editor but ignored by the renderer

This is worse than a dead label — a user can change the value, watch it serialize, and see nothing happen. `U8`, `Bool`, `BitBool`, `F32`, `Vector2/3/4` and `String` are all in the editable list (`ParticleDocument.cs:145-149`), so: `alphaRef` (425,866), `miscRenderFlags` (547,010), `depthBiasFactors` (201,926), `disableBackfaceCull` (297,513), `stencilMode` (26,393) / `stencilRef` (23,743), `renderPhaseOverride` (897), `colorRenderFlags` (8,129), `meshRenderFlags` (133,900), `isGroundLayer` (301,919), `useNavmeshMask` (159,165), `importance` (397,261). Verified live on real bins: `alphaRef` → IsReadOnly=False (value 15), `miscRenderFlags` → False (1), `depthBiasFactors` → False ("-1, -60").

### Editor gaps

- **26.9%** of emitter rows are read-only (7,698,677 of 28,569,369). The genuine causes are `BinTreeOptional` (`lifetime` 1,128,460, `particleLinger` 678,442, `emitterLinger` 95,954, `period`, `timeActiveDuringPeriod`, `MaximumRateByVelocity`), `BinTreeI16` (`pass` 1,114,110 — missing from the editable list even though `BinValueEditor.KindOf` handles Int), `BinTreeHash` (`StencilReferenceId`), `Matrix44`, and every non-`Value*` nested struct, which collapses to one opaque read-only row so none of its inner fields is visible at all.
- **14.5%** of rows show a raw `0x…` hash (4,146,580 occurrences; 88 of 134 fields unnamed). The name table is a hand-maintained 57-entry list (`ParticleDocument.cs:217-236`) rather than a lookup into `ReyEngine.Core.Hashing.HashDatabase`, which already resolves nearly all of them. Worst by volume: `SpawnShape` 709,906, `alphaErosionDefinition` 307,050, `isGroundLayer` 301,919, `disableBackfaceCull` 297,513, `particleIsLocalOrientation` 244,868, `FlexShapeDefinition` 159,880, `useNavmeshMask` 159,165, `birthUVOffset` 146,833, `uvScale` 135,073, `birthOrbitalVelocity` 105,840, `startFrame` 32,881, `distortionDefinition` 20,251 — several of which the resolver *does* read.
- **8 names in the table hash to nothing that exists** in 1,398,802 emitters: `shape` (0x9dc3d926), `uvScroll` (0x189cbd4b), `uvRotation0` (0xbf65c270), `uvScale0` (0xeda11de8), `birthUvRotation0` (0xc3839d1d), `lingerColor` (0xe9ca82a1), `censorModifiers` (0x89e6331b), `emitterDefinitionDataFlags` (0xa751b4e3). The real fields are `uvRotation`, `uvScale`, `birthUvRotateRate`, `SeparateLingerColor`, `censorModulateValue`/`modulationFactor`, and system-level `flags`. Two further entries (`scaleBirthScaleByBoundObjectSize`, `scaleEmitOffsetByBoundObjectSize`) are listed as emitter fields but only ever occur nested inside `FlexShapeDefinition`, so the editor never shows them.
- **10,367 systems (5.5%) with zero emitters** never appear in the editor tree (`ParticleDocument.cs:51-68` requires `emitters.Count > 0`). Example: `data/characters/sightward/skins/skin253.bin` @ Map11.
- **No system-level surface at all.** `particleName`, `particlePath`, `flags` (86,225), `transform` (4,420), `visibilityRadius` (25,044), `overrideScaleCap` (6,810), `buildUpTime` (1,985), the sound events — none is exposed.
- Curve keys are display-only: `CurveTimes` / `CurveChannels` feed a preview with no editing path (`ParticleEditorView.axaml:174-179` is a single TextBox + Apply).

### Map layer

- `MapParticle` has **15** fields; `MapParticleExtractor.cs:44-57` reads 5 (`transform`, `name`, `groupName`, `system`, `mVisibilityFlags`).
- `MapParticleWriter.cs:27-54` can persist **1** field — it locates a placement by the raw 64 bytes of its original matrix and overwrites those bytes. No path exists to add, remove, re-link or re-tint a placement. Two of 29,811 placements have no transform at all and are not even locatable; the byte scan is shared with map audio placeables and fails silently if two placements share a matrix.
- **Positive:** all 29,811 `MapParticle.system` links resolve to a `VfxSystemDefinitionData` in the *same* bin (0 cross-bin, 0 dead), so no cross-bin resolver is needed. Verified with a set-valued index after an initial naive index produced a false positive.
- **Positive:** `MapParticle.groupName` does **not** link to `MapGroup` — 549 placements carry a groupName, 1,280 `MapGroup` objects exist, 0 name matches. ReyEngine's display-only use is correct and there is no missing parent transform.
- `MapSunProperties`: 9 of 20 fields read (`MapSunProperties.cs:59-67`). `fogEnabled` occurs 152 times and its only observed value is **False**, yet fog is gated purely on the user's `ShowFog` toggle (`ViewportControl.cs:838`); mitigated because `ShowFog` defaults false. Also unread: `fogAlternateColor` 192, `SunRadiusForShadows` 173, `useBloom` 169 (always True), `fogEmissiveRemap` 153, `ScaleSunShadowIntensity` 30, `SunIntensityScale` 18.
- **Two entire map VFX sources are never loaded.** `MainWindowViewModel.cs:5111` calls `ExtractAll` only on the mapgeo companion `.materials.bin`. Not loaded: `data/maps/shipping/mapXX/mapXX.bin` (8 bins, **5,229 systems / 47,118 emitters**) and `maps/modespecificdata/*.bin` (5 VFX-bearing paths, 766 systems / 6,819 emitters as shipped; `ultbook.bin` ships in both Map11 and Map12). Zero `MapParticle` placements reference them — they are script/effect-key driven — so they are not "missing placements" but they are invisible in the map view. They can still be opened by hand in the Particle Editor (`ParticleEditorViewModel.cs:59`).
- Map VFX is **not a different schema**: 133 distinct map emitter fields vs 134 champion, symmetric difference = `doesLifetimeScale` (champion-only, 7 occurrences); the 23 map system fields are a strict subset of the 28 champion ones; the only map-only Vfx class is `VfxPrimitiveCameraSegmentSeriesBeam` (3 instances, 1 bin). No shared emitter field has a different dominant type between the two corpora. Fixing champion VFX fixes map VFX.

---

## Missing

Grouped by parent structure, ordered within each group by frequency. Every row names a real evidence bin. "Purpose" is marked **[inferred]** wherever it is read off the field name rather than measured.

### `VfxEmitterDefinitionData` — render state and ordering

| Field | Hash | Type | Freq | Purpose | Evidence bin | Implement in |
|---|---|---|---|---|---|---|
| `pass` | 0x7b7a7318 | I16, 2,913 distinct values, full −32768…32767 range | **1,114,110 (79.7%)** | Intra-system draw order. **The highest-frequency unread field in the schema.** ReyEngine iterates emitters in container order with no sort and `DepthMask(false)` (`VfxParticleRenderer.cs:173,178`), so every alpha-blended composite is order-dependent and arbitrary | `data/characters/sru_atakhan/sru_atakhan_multi_skins_skin0_..._skin3.bin` @ Map11 | `VfxSystemResolver.cs` + sort in `VfxParticleRenderer.cs:178`; add `BinTreeI16` to `ParticleDocument.cs:145` |
| `isUniformScale` | 0x3559e15b | BitBool, **true in 100%** of 792,459 | 792,459 (56.7%) | **[inferred]** drive both axes from one component. `VfxParticleSimulator.cs:243` already collapses `Y == 0 → X` for 22,708 of them, so at most **634,368 emitters (45.4%)** could be drawn with the wrong aspect ratio. Semantics UNVERIFIED — do not implement on a guess | `data/characters/aatrox/skins/skin37.bin` @ Aatrox | `VfxParticleSimulator.cs:243` |
| `bindWeight` | 0xca406316 | Embedded ValueFloat (constant + curve; 26,147 multi-key) | 801,177 (57.3%) | **[inferred]** how strongly a live particle stays bound to its emitter/bone. ReyEngine world-locks at spawn (`VfxParticleSimulator.cs:230-232`); `SetWorldTransform` moves only the origin | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleSimulator.cs:103-115,226-233` |
| `miscRenderFlags` | 0x6563bee8 | U8 {1:~542k, 5:1,956, 3:1,019, 4:921, 2:388} | 547,010 (39.1%) | **UNKNOWN** bitfield; only bits 0-2 ever set. Distribution is flat across blend modes, so not a blend qualifier | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | — (decode first) |
| `alphaRef` | 0xb99310f4 | U8, 34,788 non-zero (max 255) | 425,866 (30.5%) | Alpha-test cutoff. **Engine-confirmed:** `quad_ps.ps.dx11` declares `ALPHA_TEST` and `AlphaTestReferenceValue`. ReyEngine's fragment shader has no `discard` at all (`:599`) | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleRenderer.cs` Frag + uniform |
| `importance` | 0xb9516a6f | U8 {3:351,908 1:38,468 5:6,456 4:239 0:190} | 397,261 (28.4%) | VFX-quality tier at which the emitter is culled | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleSimulator.cs` (LOD filter) |
| `isGroundLayer` | 0x27d40903 | BitBool, true in 100% | 301,919 (21.6%) | Ground-projected decal layer. Shader side declares `FEATURE_NAVMESH_MASK`, `NAVMESH_MASK_TEXTURE`, `NAV_GRID_TYPE_MASK` | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleRenderer.cs` |
| `disableBackfaceCull` | 0x3c91cebd | Bool, true in 100% | 297,513 (21.3%) | ReyEngine disables `CullFace` unconditionally (`:174`), which is *correct* for these 297,513 and wrong for the 1,101,289 that omit the flag (whose default is UNKNOWN) | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleRenderer.cs:174` |
| `particleIsLocalOrientation` | 0x37ddb774 | BitBool, true in 100% | 244,868 (17.5%) | **[inferred]** particles keep emitter orientation instead of billboarding | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleRenderer.cs` |
| `depthBiasFactors` | 0x67b5d729 | Vector2. X: −1 79.0%, 0 10.7%, +1 6.9%, tail of −3/−2/−59/−0.1/−50/−5/−15/+3/−20 | 201,926 (14.4%) | **UNKNOWN.** Both components are continuous, so the "mode flag + magnitude" reading is refuted. `glPolygonOffset` mapping is a name-based guess. ReyEngine never enables `GL_POLYGON_OFFSET_FILL`; because depth writes are off, the visible effect is a depth-*test* difference against opaque geometry, not particle z-fighting | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleRenderer.cs` |
| `useNavmeshMask` | 0xdddde180 | BitBool, true in 100% | 159,165 (11.4%) | Clip against the navmesh; engine shader has the mask texture | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleRenderer.cs` |
| `meshRenderFlags` | 0x19bdf4df | U8, **always 0** | 133,900 (9.6%) | Carries no information in shipped data. Why it is serialised 133,900 times at its own default is unexplained | `data/characters/janna/skins/skin17.bin` @ Janna | — |
| `texAddressModeBase` | 0x324f9f10 | U8 {2:75,648 3:3,285 1:1,409} | 80,342 (5.7%) | Sampler wrap mode. Enum→GL ordering **UNKNOWN**; `erosionMapAddressMode` uses {0,1,3} and `PaletteTextureAddressMode` {0,2}, ruling out a naive 0=wrap/1=clamp/2=mirror guess. ReyEngine hardcodes `GL_REPEAT` on both axes (`VfxParticleRenderer.cs:111-112`) | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleRenderer.cs:111-112` |
| `depthPushPull` | 0xcb13aff1 | F32, range −1980…1000 | 61,588 (4.4%) | **Name recovered by brute force** — FNV-1a("depthPushPull") == 0xcb13aff1, and the hash is absent from all four CDTB lists. **Engine-confirmed:** `EMITTER_DEPTH_PUSH_PULL` is a member of `VFXDynamicPerParticleInstanceCBVS` (offset 40, size 4) in `defaultparticlequadunlit.vs.dx11_0`, and `PARTICLE_DEPTH_PUSH_PULL` (offset 16) is in `quad_vs.vs.dx11_0`'s `$Globals` — so it is a **vertex-stage** depth offset, not a rasteriser bias. Exact units/direction still **[inferred]** | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleRenderer.cs` vertex shader |
| `stencilMode` / `stencilRef` / `StencilReferenceId` | 0x1aef807c / 0x7b43e2ec / 0x166c54c5 | U8 {2:15,741 3:7,387 1:3,206 4:59} / U8 / Hash | 26,393 / 23,743 / 6,217 | Stencil masking. ReyEngine never enables `GL_STENCIL_TEST`, so masked effects draw everywhere | `data/characters/hwei/skins/skin14.bin` @ Hwei | `VfxParticleRenderer.cs` |
| `colorRenderFlags` | 0x155f244b | U8, always 1 | 8,129 | **UNKNOWN** | `data/characters/janna/janna_multi_skins_skin27_..._skin35.bin` @ Janna | — |
| `isTexturePixelated` | 0xc66ee598 | Bool, always true | 1,298 | Point/nearest filtering; ReyEngine hardcodes linear, so pixel-art sprites render blurred | `data/characters/hecarim/skins/skin4.bin` @ Hecarim | `VfxParticleRenderer.cs:108-109` |
| `SortEmittersByPos` | 0xbd9b83c7 | BitBool | 1,337 | Per-emitter depth sort request | `data/characters/xerath/skins/skin32.bin` @ Xerath | `VfxParticleRenderer.cs` |
| `renderPhaseOverride` | 0x76fb6570 | U8 {0:835 5:45 4:14 3:2 6:1} | 897 | Render-phase routing | `maps/modespecificdata/ultbook.bin` @ Map11 | `VfxParticleRenderer.cs` |
| `WriteAlphaOnly` | 0x51af37d2 | BitBool, always true | 249 | **[inferred]** alpha-channel-only colour write | `data/characters/vex/skins/skin22.bin` @ Vex | `VfxParticleRenderer.cs` |

### `VfxEmitterDefinitionData` — UV transform stack

ReyEngine implements exactly one term: `uUvScrollRate * age` from `birthUvScrollRate` (`VfxParticleRenderer.cs:566-569`) — and even that is constant-only (`ReadValueVec2`, `:447-451`), so the 38,870 emitters that animate the scroll rate lose the curve and the 3,272 curve-only ones collapse to (0,0).

| Field | Hash | Type | Freq | Evidence bin | Implement in |
|---|---|---|---|---|---|
| `birthUVOffset` | 0xb9198a2a | Embedded ValueVector2 | 146,833 (10.5%) | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxSystemResolver.cs` + vertex shader |
| `uvScale` | 0xeddebb48 | Embedded ValueVector2 | 135,073 (9.7%) | same | same |
| `particleUVScrollRate` | 0x645e1b8b | Embedded **IntegratedValueVector2** | 86,904 (6.2%) | same | Accumulates over time — must **not** be implemented as an ordinary curve |
| `uvRotation` | 0x968279e0 | Embedded ValueFloat | 50,439 (3.6%) | same | vertex shader |
| `uvMode` | 0x264afd39 | U8 {2:30,461 1:9,244 3:226 4:29 5:5} | 44,585 (3.2%) | `maps/modespecificdata/ultbook.bin` @ Map11 (1,2); `data/maps/shipping/map33/map33.bin` (4) | **UNKNOWN** enum |
| `uvScrollClamp` | 0xa6630e0a | BitBool | 29,820 | same | vertex shader |
| `emitterUvScrollRate` | 0x52010b69 | Vector2 | 23,890 | `data/characters/janna/skins/skin2.bin` @ Janna | vertex shader |
| `TextureFlipV` / `TextureFlipU` | 0x2bf854fb / 0x2cf8568e | BitBool | 7,443 / 6,747 | `data/characters/janna/skins/skin62.bin` @ Janna | vertex shader |
| `particleUVRotateRate` | 0xc0390b2d | Embedded **IntegratedValueFloat** | 8,676 | `data/characters/rengar/skins/skin46.bin` @ Rengar | integrated |
| `birthUvRotateRate` | 0x32a3ce18 | Embedded ValueFloat | 8,203 | `data/characters/janna/skins/skin2.bin` @ Janna | vertex shader |
| `uvTransformCenter` | 0x769be1af | Vector2 | 3,977 | `data/characters/janna/skins/skin55.bin` @ Janna | pivot for rotate/scale |
| `uvParallaxScale` | 0xf6989fe1 | F32 | 1,578 | `data/characters/janna/skins/skin3.bin` @ Janna | vertex shader |

### `VfxEmitterDefinitionData` — whole render stages with no ReyEngine equivalent

| Struct | Hash | Freq | Contents | Evidence bin | Implement in |
|---|---|---|---|---|---|
| `alphaErosionDefinition` → `VfxAlphaErosionDefinitionData` (0x5e842b9b) | 0xc4663005 | **307,050 (22.0%)** | `erosionDriveCurve` 302,390 (ValueFloat) · `erosionMapName` 301,311 (String) · `erosionMapChannelMixer` 210,972 (ValueColor) · `erosionFeatherOut` 153,237 · `erosionFeatherIn` 90,477 · `erosionSliceWidth` 86,441 · `erosionMapAddressMode` 62,848 {0,1,3} · `LingerErosionDriveCurve` 5,867 · `UseLingerErosionDriveCurve` 4,408 · `erosionDriveSource` 477 {1}. **Engine-confirmed:** `quad_ps` declares `ALPHA_EROSION`, `sAlphaErosionTexture`, `cAlphaErosionParams`. The largest single unimplemented visual feature — these emitters fade by uniform alpha instead of dissolving | `data/characters/sru_atakhan/...skin3.bin` @ Map11; erosion map e.g. `ASSETS/Maps/Particles/SR/Dragon_Elder_Tar_Dust_Erosion.tex` | `VfxSystemResolver.cs` + new shader stage in `VfxParticleRenderer.cs` |
| `softParticleParams` → `VfxSoftParticleDefinitionData` (0x1daa3fb0) | 0xbfb0efdd | 95,671 (6.8%) | `deltaIn` 90,175 · `beginIn` 12,235 · `deltaOut` 3,751 · `beginOut` 422 · `0x3bf176bc` U8 241 (unresolved). **Engine-confirmed:** `quad_ps` declares `SOFT_PARTICLES`, `cSoftParticleParams`, `cSoftParticleControl`, `cDepthConversionParams`, `sDepthTexture_SharedTexture`; `Shaders/Particles/DefaultParticleQuadUnlit` has `SoftParticle_FadeDistance` default 25. ReyEngine binds no depth texture (`CaptureScene` copies colour only, `:121-144`), so sprites hard-clip against terrain. Per-component meaning **[inferred]** | `data/characters/sru_atakhan/...skin3.bin` @ Map11; `data/characters/turret/skins/skin27.bin` @ Map11 (`beginIn`) | `VfxParticleRenderer.cs` (depth bind + Frag) |
| `reflectionDefinition` → `VfxReflectionDefinitionData` | 0x3f7567cd | 59,149 (4.2%) | `fresnelColor` 53,083 · `fresnel` 49,349 · `reflectionFresnelColor` 21,159 · `reflectionFresnel` 11,106 · `reflectionOpacityDirect` 9,267 · `reflectionOpacityGlancing` 9,200 · `reflectionMapTexture` 8,645 (real cubemaps, e.g. `ASSETS/Shared/Particles/Samira_CubeMap.dds`) | `data/characters/nexus/nexus_multi_skins_skin37_skins_skin39.bin` @ Map11 | `VfxParticleRenderer.cs` |
| `paletteDefinition` → `VfxPaletteDefinitionData` | 0x8aa1ffc1 | 43,621 (3.1%) | `paletteTexture` 36,144 · `paletteCount` 34,463 (16/10/32/8…) · `paletteSelector` 28,767 · `palleteSrcMixColor` 19,296 *(Riot's typo, hash-confirmed)* · `PaletteTextureAddressMode` 2,334 · `PaletteUAnimationCurve` 2,237 · `PaletteVAnimationCurve` 480. **Engine-confirmed:** `quad_ps` declares `PALETTIZE_TEXTURES`, `cPaletteSelectMain`, `cPaletteSrcMixerMain`, `COLORPALETTE_COLORBLIND`. Distinct from `particleColorTexture`, which ReyEngine does implement; 13,823 emitters have a palette and **no** colour texture, so they fall back to `birthColor` (defaulted to white when absent) | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxSystemResolver.cs` + `VfxParticleSimulator.cs` colour path |
| `fieldCollectionDefinition` → `VfxFieldCollectionDefinitionData` | 0xae4f2c3a | 39,904 (2.9%) | `fieldNoiseDefinitions` 26,464 (`axisFraction`, `radius`, `frequency`, `velocityDelta`, `Position`) · `fieldDragDefinitions` 7,716 · `fieldAccelerationDefinitions` 5,694 · `fieldAttractionDefinitions` 4,589 · `fieldOrbitalDefinitions` 2,752. The simulator integrates only birth velocity + birth/world acceleration + drag + one orbital term (`VfxParticleSimulator.cs:182-205`), so noise/attractor motion is absent — particles fly straight where League swirls them | `data/characters/sru_atakhan/...skin3.bin` @ Map11; `data/maps/mapgeometry/map11/ruby_sr_trialofdoom_overload.materials.bin` (orbital) | `VfxParticleSimulator.cs:182-205` |
| `childParticleSetDefinition` → `VfxChildParticleSetDefinitionData` (0xb520045a) | 0xa03664c8 | 35,510 (2.5%) | `childrenIdentifiers` 35,399 → `VfxChildIdentifier{effectKey:Hash 36,161, effect:ObjectLink 3,472}` · `boneToSpawnAt` 2,945 · `childrenProbability` 2,270 · `childEmitOnDeath` 1,271 · `ParentInheritanceDefinition` 1,169 → `VfxParentInheritanceParams{Mode:U8 786, RelativeOffset 148}`. `effectKey` is in the same namespace as `ResourceResolver.resourceMap`; 4,392 of 6,334 sampled keys resolve in the same bin, the rest need the skin's dependency bins. **Caution:** `childrenProbability` is **not** a probability — 478 of 2,262 constants exceed 1.0 | `data/characters/sru_atakhan/...skin3.bin` @ Map11; `maps/modespecificdata/ultbook.bin` (`childEmitOnDeath`) | new; reuse `VfxSystemResolver.ExtractResourceMap` (`:109`) |
| `Linger` → `VfxLingerDefinitionData` (0x9b19f2b5) | 0xb929b43e | 22,274 | `SeparateLingerColor` 12,242 · `UseSeparateLingerColor` 7,552 · `UseLingerScale` 6,177 · `LingerScale` 3,796 · `KeyedLingerVelocity`/`Drag`/`Acceleration` + their `Use*` flags · `LingerRotation`. A second complete colour/scale/velocity curve set for the shutdown phase | `data/characters/janna/skins/skin2.bin` @ Janna | `VfxParticleSimulator.cs` |
| `Filtering` → `VfxEmitterFiltering` | 0xf50b1a41 | 25,861 | `keywordsExcluded` 59% · `spectatorPolicy` 35.6% {1,2} · `keywordsRequired` 2.1% · `censorPolicy` 1.4%. ReyEngine shows emitters League would suppress for a given skin/keyword | `data/characters/turret/skins/skin27.bin` @ Map11 | `VfxSystemResolver.cs` + filter at build time |
| `distortionDefinition.distortionMode` | 0x40c58b71 | 8,168 | See *Parsed but never used* | | |
| `CustomMaterial` → `VfxMaterialDefinitionData` (0x2820c167) | 0x4b901db3 | 1,266 | `Material` ObjectLink 100% · `materialDrivers` Map 19.5% → `VfxFloatOverLifeMaterialDriver` (347) / `VfxColorOverLifeMaterialDriver` (35) / `0xfbef6376` (5). Replaces the particle shader with an authored `StaticMaterialDef`. Note `overrideBlendMode` (U32 {1:166 2:810 3:4 4:122}) on the sibling override struct is a **second, distinct** blend enum | `maps/modespecificdata/ultbook.bin` @ Map11; `data/characters/ekko/skins/skin56.bin` @ Ekko | `VfxParticleRenderer.cs` (large) |
| `materialOverrideDefinitions` → `VfxMaterialOverrideDefinitionData` (0xd9b82e87) | 0x332b2c86 | 3,978 elements on emitters + 346 on systems | `baseTexture` 89.3% · `subMeshName` 51.9% · `priority` 49.2% · `overrideBlendMode` 26.9% · `transitionTexture` · `transitionSample` · `Material` · `glossTexture` · `transitionSource` | `data/characters/janna/skins/skin5.bin` @ Janna | `VfxParticleRenderer.cs` mesh path |

### `VfxEmitterDefinitionData` — motion, timing and emission

| Field | Hash | Type | Freq | Purpose | Evidence bin | Implement in |
|---|---|---|---|---|---|---|
| `isLocalOrientation` | 0x2ae335b2 | BitBool, **false in 100%** | 301,827 | Explicit opt-**out**. These 301,827 already get what ReyEngine does; the risk is the 1,096,975 that omit it, whose default is **UNKNOWN** | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleSimulator.cs` |
| `rotation0` | 0x712b01bd | Embedded **IntegratedValueVector3**, dynamics on 100%, up to 57 keys | 52,647 (3.8%) | Rotation over life. ReyEngine reads only `birthRotation0` + a constant `birthRotationalVelocity0` | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleSimulator.cs:246-247,300` |
| `birthRotation0` Y/Z and `birthRotationalVelocity0` Y/Z | — | Vector3 components | 574,216 / 129,461 emitters have a non-zero Y or Z | The simulator keeps only X as a 2-D sprite spin; the shader applies Y/Z only when `uArbitraryQuad != 0` (`VfxParticleRenderer.cs:550-555`) | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleSimulator.cs:246` |
| `directionVelocityScale` / `directionVelocityMinScale` | 0x7ad984a5 / 0xebd44083 | F32 | 62,435 / 12,486 | Velocity-driven stretch on direction-oriented quads. ReyEngine rotates but never stretches (`:542-546`) | `data/characters/janna/skins/skin2.bin` @ Janna | `VfxParticleRenderer.cs:542-546` |
| `emitterLinger` | 0x3bc59eb6 | Optional\<F32\> (8,018 empty) | 95,954 (6.9%) | How long the emitter survives after it stops emitting | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleSimulator.cs` |
| `velocity` | 0x32741c32 | Embedded ValueVector3, 18,006 multi-key, up to 61 keys | 23,112 | Velocity over life (distinct from `birthVelocity`). Named in `ParticleDocument.cs:206,225` for the editor but read by nothing | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxSystemResolver.cs` + `VfxParticleSimulator.cs` |
| `rateByVelocityFunction` / `MaximumRateByVelocity` | 0x50b7397d / 0xc6fa1b5d | Embedded ValueVector2 / Optional\<F32\> | 16,593 / 3,320 | Emission rate scaled by emitter movement speed. ReyEngine samples rate against emitter age only | `data/characters/janna/skins/skin2.bin` @ Janna | `VfxParticleSimulator.cs:157-178` |
| `particleLingerType` | 0x38897864 | U8 {1:12,773 2:1,547} | 14,320 | Selects linger behaviour | `data/characters/janna/skins/skin2.bin` @ Janna | `VfxParticleSimulator.cs` |
| `isFollowingTerrain` | 0xbaa6e0c9 | BitBool | 12,880 | Snap particles to terrain height | `data/characters/janna/skins/skin55.bin` @ Janna | `VfxParticleSimulator.cs` |
| `isRotationEnabled` | — | BitBool, true only | 108,379 (champion) | Master rotation enable ReyEngine ignores while still applying rotation | `data/characters/janna/skins/skin17.bin` @ Janna | `VfxParticleSimulator.cs` |
| `period` / `timeActiveDuringPeriod` | 0x99c94704 / 0x17d9c5ce | Optional\<F32\> | 8,132 / 8,240 | Duty cycle. Pulsed emitters emit continuously in ReyEngine — very visible on map beacons and braziers | `data/characters/janna/skins/skin17.bin` @ Janna | `VfxParticleSimulator.cs:157-178` |
| `ParticlesShareRandomValue` | 0x676949a1 | BitBool | 8,173 | All particles of a burst draw the same random roll; ReyEngine rolls per particle and per property | `data/characters/janna/skins/skin2.bin` @ Janna | `VfxSystemDefinition.cs:141-149` |
| `HasVariableStartTime` | 0x538effa4 | BitBool | 3,973 | Randomised emitter start offset — repeats look over-synchronised without it | `data/characters/janna/janna_multi_skins_skin56_..._skin65.bin` @ Janna | `VfxParticleSimulator.cs` |
| `ChanceToNotExist` | 0xcef2ba70 | F32 (0.05, 0.01, 0.75, 0.5, **1.0**…) | 1,164 | Per-spawn skip probability. The 14 emitters with 1.0 should emit **nothing** and currently render at full rate | `data/maps/shipping/map22/map22.bin` @ Map22 | `VfxParticleSimulator.cs` |
| `birthRotationalAcceleration` | — | Embedded ValueVector3 | 1,053 | Angular acceleration | `data/characters/irelia/skins/skin60.bin` @ Irelia | `VfxParticleSimulator.cs` |
| `IsEmitterSpace` | 0xa786282d | BitBool, true in 100% | 3,248 | Simulate in emitter space | `data/characters/velkoz/skins/skin33.bin` @ Velkoz | `VfxParticleSimulator.cs` |

### `VfxEmitterDefinitionData` — mesh, surface and flex

| Field | Hash | Type | Freq | Purpose | Evidence bin | Implement in |
|---|---|---|---|---|---|---|
| `FlexShapeDefinition` → `VfxFlexShapeDefinitionData` (0xb13097f0) | 0x4ffce322 | Struct, 9 F32 fields | 159,880 (11.4%) | Scales birth size and emit offset by the bound object's size/height/radius — this is why one VFX fits both Teemo and Cho'Gath. `scaleBirthScaleByBoundObjectSize` 113,599 · `scaleEmitOffsetByBoundObjectSize` 84,870 · 7 more | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxSystemResolver.cs` + `VfxParticleSimulator.cs` |
| `mMesh.mSubmeshesToDraw` / `mSubmeshesToDrawAlways` | 0xdb63db58 / 0xb1a2e185 | Container\<Hash\> (resolved names: BODY, Helmet, Cloak, ULT_FORM…) | 46,187 / 23,994 | Submesh masking — ReyEngine draws the whole mesh where League draws a subset | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleRenderer.cs:447-462` |
| `mMesh.mLockMeshToAttachment` | 0xe79da182 | Bool | 59,295 | Rigid attachment | same | `VfxParticleRenderer.cs` |
| `emissionMeshName` / `emissionMeshScale` / `useEmissionMeshNormalForBirth` / `emissionSurfaceDefinition` | 0x2135c4d4 / 0xbda98597 / 0x7ba817e9 / 0x5452d898 | String / F32 / BitBool / Struct | 6,234 / 6,659 / 3,366 / 1,239 | Emit across a mesh surface; these emitters collapse to a point source. `VfxEmissionSurfaceData`'s own fields are unresolved (see Unknown) | `data/characters/locke/skins/skin5.bin` @ Locke (`meshName=ASSETS/Characters/Locke/Skins/Base/Locke_Base.Locke.skn`) | `VfxParticleSimulator.cs` spawn |
| `mMesh.mAnimationVariants` | 0x147f071c | Container\<String\> | 62 | Random pick among several `.anm`; ReyEngine plays only `mAnimationName` | `data/characters/cherry_goh_fiddlesticks/skins/skin0.bin` @ Map30 | `VfxParticleRenderer.cs` |
| `flex*` value family (`flexScaleBirthScale`, `FlexInstanceScale`, `flexBirthUVOffset`, `flexRate`, `flexBirthUVScrollRate`, `flexParticleLifetime`, `flexBirthVelocity`, `flexBirthRotationalVelocity0`) | — | `FlexValueFloat/Vector2/Vector3` / `FlexTypeFloat` = `{mValue, mFlexID:U32}` | ~4,100 combined | Runtime-scriptable override slots keyed by `mFlexID` (only values 1 and 2 observed). The ID → runtime-quantity mapping lives in game code, not in the bins | `data/characters/sru_riftherald/sru_riftherald_multi_skins_skin0_skins_skin1.bin` @ Map11 | low priority |

### `VfxEmitterDefinitionData` — colour and texture

| Field | Hash | Type | Freq | Purpose | Evidence bin | Implement in |
|---|---|---|---|---|---|---|
| `colorLookUpScales` / `colorLookUpOffsets` | 0xf02bd44d / 0xcd5a323e | Vector2 | 36,423 / 23,920 | Affine remap of the `particleColorTexture` lookup coordinate. ReyEngine reads `colorLookUpTypeX/Y` but applies neither scale nor offset (`VfxParticleSimulator.cs:305-314`), so these emitters sample the gradient at the wrong U/V. Modal scales (1,0.5)×2,362, (1,0.1)×1,763 — i.e. League reads the first 10% of the V axis where ReyEngine reads a fixed 0.5 | `data/characters/sru_atakhan/...skin3.bin` @ Map11 | `VfxParticleSimulator.cs:305-314` |
| `modulationFactor` / `censorModulateValue` | 0x6592b854 / 0x08b52819 | Vector4 | 6,306 / 6,048 | Per-emitter RGBA modulation; the censor value applies only under the low-violence filter | `data/characters/janna/skins/skin4.bin` @ Janna | `VfxParticleRenderer.cs` |
| `falloffTexture` | 0xa5b8cdf4 | String (1,763 are empty) | 8,647 | A third texture slot with no ReyEngine equivalent | `data/characters/janna/janna_multi_skins_skin45_..._skin55.bin` @ Janna | `VfxParticleRenderer.cs` |
| `sliceTechniqueRange` | — | F32 | 1,145 | Range for the alpha-slice technique (relates to erosion) | `data/characters/janna/skins/skin2.bin` @ Janna | with erosion |
| `Audio` → `VfxEmitterAudio` | — | Struct {`SoundOnCreate` 67.4%, `SoundPersistent` 42.3%} | 3,869 | Per-emitter sound events, distinct from the system-level ones | `data/characters/janna/skins/skin17.bin` @ Janna | `VfxSystemResolver.cs` |
| `colorblindVisibility` | 0x468da513 | U8 {1:86, 2:68} | 154 | Correct to ignore in default mode | `data/characters/viego/viego_multi_skins_skin10_..._skin18.bin` @ Viego | — |

### `VfxSystemDefinitionData` — 21 of 28 fields unread

| Field | Hash | Type | Freq | Purpose | Evidence bin | Implement in |
|---|---|---|---|---|---|---|
| `flags` | 0x9c677a2c | U16, **81 distinct values** | 86,225 systems (45.6%) | **UNKNOWN** bitfield. Champion bit frequencies: b7 99.9%, b2 98.0%, b6 90.1%, b0 56.5%, b1 41.2%, b4 5.9%, b5 3.3%, b10 2.9%, b3 2.3%, b11 0.5%. Top values 197, 132, 198, 2260, 213, 199, 164. Values ≥2000 are concentrated in shipping bins (1,074 of 1,090) but not exclusive to them. Decoding this should be the first follow-up — several behaviours ReyEngine currently guesses may live here | `data/characters/janna/skins/skin17.bin` @ Janna (=197); `data/maps/shipping/map11/map11.bin` (high band) | `VfxSystemResolver.cs:163-182` |
| `visibilityRadius` | — | F32 | 25,044 | Parsed but used only for audio falloff — never culls particle rendering | `maps/modespecificdata/ultbook.bin` @ Map11 | `ViewportControl.cs` cull |
| `overrideScaleCap` | 0x3aae4707 | Optional\<F32\>, never empty | 13,620 | **Semantics UNVERIFIED.** −1 in 38.9% of cases (sentinel), 0 in 4.1%, remainder discrete 250-500 — those magnitudes look like world units, not scale multipliers | `data/characters/janna/skins/skin2.bin` @ Janna (value −1) | `VfxSystemResolver.cs` |
| `transform` | 0xe1ad931b | Matrix44, **never identity** | 4,420 | Decomposed: 67.7% are **pure scale** (no translation, no rotation), 21.7% carry translation, 14.2% rotation/shear. So ignoring it mis-**sizes** two thirds of affected systems rather than mispositioning them. That it composes onto every emitter is **[inferred]** | `data/characters/strawberry_aurora/skins/skin0.bin` @ Strawberry_Aurora (diag 0.8/1.2); `data/characters/aatrox/skins/skin37.bin` (180° Z + translate 0,−100,0) | `VfxSystemDefinition.cs:10-17`, `ViewportControl.cs:1193` |
| `assetRemappingTable` | 0xd01a2020 | Container\<`VfxAssetRemap`{`oldAsset`:Hash 1,703, `newAsset`:String 1,755, `type`:U32 11}\> | 3,044 systems, 1,760+ entries | Per-skin/chroma asset substitution. `oldAsset` is FNV-1a over the lowercased path — **confirmed** on 334 cases where the target string also occurs in the same bin. 173 of 967 champion entries are identity remaps, so ~82% genuinely change something | `data/characters/janna/janna_multi_skins_skin45_..._skin55.bin` @ Janna | `VfxSystemResolver.cs` + texture resolution |
| `buildUpTime` | 0xe994800f | F32: 5(178) 1(118) 0.5(79) 2(51) 10(41) 3(24) 4(15) … up to 40, one −0.1 | 1,985 | **UNKNOWN.** The "paired with `MapActionBuildUpMapParticle`" reading is refuted — that action occurs twice in the entire map corpus, while `buildUpTime` occurs 1,147 times in champion bins where no `MapAction` class exists at all | `data/characters/janna/skins/skin17.bin` @ Janna | `VfxParticleSimulator.cs` |
| `scaleDynamicallyWithAttachedBone` | 0xf55e1472 | Bool, true in 100% | 1,139 | **[inferred]** system scales with its bone | `data/characters/irelia/irelia_multi_skins_skin17_skins_skin36.bin` @ Irelia | `VfxParticleSimulator.cs` |
| `mIsPoseAfterimage` | 0xcf08f8e6 | Bool, true in 100% | 303 | **[inferred]** frozen-pose afterimage. Corroborated: all 11 champions using it (Aatrox, Akali, Ezreal, Gwen, Jinx, Mordekaiser, Samira, Sylas, Tristana, Viego, Zeri) are dash/blink champions | `data/characters/gwen/gwen_multi_skins_skin20_..._skin29.bin` @ Gwen | low priority |
| `mEyeCandy` | — | Bool, true only | 176 | Non-gameplay decoration, culled first at low VFX quality | `data/characters/akali/skins/skin67.bin` @ Akali | quality filter |
| `drawingLayer` | 0xc803c9f6 | U8, always 1 | 143 | Draw layer; carries no information in shipped data | `data/characters/viego/skins/skin43.bin` @ Viego | — |
| `voiceOverOnCreateDefault` / `voiceOverPersistentDefault` | — | String | 410 / 104 | VO events | `data/characters/janna/skins/skin5.bin` @ Janna | audio |
| `hudAnchorPositionFromWorldProjection` / `hudLayerDimension` | — | Bool / F32 | 74 / 10 | HUD-space anchoring | `data/characters/viego/skins/skin43.bin` @ Viego; `data/characters/karthus/skins/skin18.bin` @ Karthus | low priority |
| `audioParameterFlexID` / `audioParameterTimeScaledDuration` | 0x596b6a3e / — | I32 (0, −2) / F32 | 42 / 41 | Wwise RTPC link | `data/characters/ivern/ivern_multi_skins_skin11_..._skin19.bin` @ Ivern | audio |
| `ClockToUse` | 0xa301c4a7 | U8 {1:6, 2:1} | 7 | **[inferred]** game-time vs real-time clock | `data/characters/ahri/skins/skin86.bin` @ Ahri | low priority |
| `selfIllumination` | — | F32 (0) | 1 | **UNKNOWN**, effectively unused | `data/characters/leesin/skins/skin52.bin` @ LeeSin | — |

### `MapParticle` — 10 of 15 placement fields unread

`MapParticlePlacement` (`MapParticleExtractor.cs:11-17`) has no member for any of these.

| Field | Hash | Type | Freq (of 29,811) | Purpose | Evidence bin | Implement in |
|---|---|---|---|---|---|---|
| `eyeCandy` | 0x558aca8f | Bool, true only | **13,165 (44.2%)** | **[inferred]** decoration dropped at low graphics settings (mirrors system-level `mEyeCandy`). The most frequent unread placement field | `data/maps/mapgeometry/map11/ruby_sr_trialofdoom_overload.materials.bin` @ Map11 | `MapParticleExtractor.cs` |
| `AllDimensions` | 0x321f0c11 | Bool, true only | 4,990 (16.7%) | **UNKNOWN** — plausibly overrides the dragon/dimension filter ReyEngine already implements via `mVisibilityFlags`, which would make it cheap and high-value, but unverified | `data/maps/mapgeometry/map22/lux.materials.bin` @ Map22 | `MapParticleExtractor.cs` + `MainWindowViewModel.cs:194` |
| `VisibilityController` | 0x5150a6a1 | ObjectLink | 4,237 (14.2%) | Controller-driven visibility. ReyEngine already models `ChildMapVisibilityController` (174) / `MutatorMapVisibilityController` (40) for **meshes** (`MapVisibilityControllers.cs`) but not for particles | `data/maps/mapgeometry/map11/ruby_sr_trialofdoom_overload.materials.bin` @ Map11 | `MapParticleExtractor.cs` |
| `startDisabled` | 0x3edc338f | Bool, true only | 1,370 (4.6%) | Starts hidden, switched on later. ReyEngine renders all 1,370 immediately. The switching mechanism (`MapActionToggleMapParticle`, 1,366 occurrences in 69 bins) is a strong correlation, not a traced binding | `data/maps/mapgeometry/map11/bloom.materials.bin` @ Map11 | `MapParticleExtractor.cs` |
| `Transitional` | — | Bool, true only | 1,159 (3.9%) | **UNKNOWN** | `data/maps/mapgeometry/map11/base.materials.bin` @ Map11 | `MapParticleExtractor.cs` |
| `colorModulate` | 0xc246d7e7 | Vector4 | 172 (0.6%) | Per-placement tint/opacity. Measured impact: 16 of 29 (bin, system) pairs use **more than one** tint on the same system — e.g. `bloom.materials.bin` system 0x0f87e47b has 6 distinct alphas (0.8/0.71/0.7/0.58/0.5/0.4) all rendered identically today | `data/maps/mapgeometry/map12/bloom.materials.bin` @ Map12 | `MapParticleExtractor.cs` + `ViewportControl.cs:1193` |
| `visibilityMode` | — | U32 {1:91, 2:52, 3:20} | 163 | **UNKNOWN** enum | `data/maps/mapgeometry/map11/ruby_sr_trialofdoom_overload.materials.bin` @ Map11 | `MapParticleExtractor.cs` |
| `TextureOverride` | — | Struct\<0x115b5460\>{`TextureToOverride`:Hash, `TacticianIndex`:U32} | 16 | TFT per-tactician texture swap; container class unresolved | `data/maps/mapgeometry/map22/battleacademia.materials.bin` @ Map22 | low priority |
| `AttachToCamera` | 0x992b4e86 | Bool, true only | 7 | **[inferred]** screen-space ambience (snow, dust) following the view | `data/maps/mapgeometry/map22/lux.materials.bin` @ Map22 | `ViewportControl.cs` |
| `quality` | — | I32 (23, 28) | 3 | **UNKNOWN** graphics-quality gate | `data/maps/mapgeometry/map12/bloom.materials.bin` @ Map12 | — |

Also: `mVisibilityFlags` **is** read and used (`MapParticleExtractor.cs:48-55`, `MainWindowViewModel.cs:194`), but when absent (16,966 of 29,811 placements) ReyEngine substitutes 255 — a guess, not read from the data. If Riot's real default is narrower, ReyEngine over-shows.

### Map environment classes with no ReyEngine support

| Class | Freq | Purpose | Evidence bin | Implement in |
|---|---|---|---|---|
| `MapPointLightType` {`lightColor`, `radius`, `Impact`, `castStaticShadows`, `HdrScale`} | 788 objects in 139 bins | The modern per-map point light. Repo-wide grep finds no reference — ReyEngine's point lights come exclusively from legacy `Light.dat`. 788 authored lights are absent from every modern map, which changes how lit VFX and geometry read | `data/maps/mapgeometry/map11/base_srx.materials.bin` @ Map11 | new, alongside `MapSunProperties.cs` |
| `MapBehavior` + `MapActionToggleMapParticle` / `CycleMapParticle` / `AttachMapParticle` / `BuildUpMapParticle` | 1,356 behaviours in 78 bins; 1,366 / 31 / 5 / 2 actions | The VFX scripting layer — how a map turns particles on and off over time, keyed by name to `MapParticle.name`. Nothing in the repo references `MapBehavior` or any `MapAction*`. Combined with `startDisabled`, ReyEngine shows a static always-on snapshot where the game shows a sequence | `data/maps/mapgeometry/map11/base_srx.materials.bin` @ Map11 | new |
| `MapLightingVolume` (transform + full `MapSunProperties` set) | 172 instances in 150 bins | Region-local atmosphere override positioned by its own transform. ReyEngine reads only the single global `MapSunProperties`, so baron pit / base / brush all render with map-wide values. Implementing needs the falloff rule, which is unknown | `data/maps/mapgeometry/map11/base_srx.materials.bin` @ Map11 | `MapSunProperties.cs:48-69` |
| `MapLightingV2` {`MinimumEnvironmentColorContribution`, `BounceLightFalloffDistance`} | 177 bins — nearly every map | Ambient floor | same | `MapSunProperties.cs` |
| `MapSkinColorizationPostEffect` {`mMultipliersRGB`} / `MapActionPlayPostEffect` | 3 / 1 | Full-screen colour grades that change how every map VFX reads | `data/maps/mapgeometry/map22/battleacademia.materials.bin` @ Map22 | `ViewportControl.cs` |
| `MapClouds` / `MapCloudsLayer` | 1 / 3, one bin | Scrolling cloud-shadow weather | one occurrence in the 236-bin map census | low priority |

### Renderer features with no data field to parse

These are pure renderer gaps — no amount of better bin reading fixes them.

| Feature | Evidence | Affected |
|---|---|---|
| **No emitter or particle sorting at all.** One draw call per emitter in container order, `DepthMask(false)` | `VfxParticleRenderer.cs:173,178`; 0 `OrderBy`/`Sort` hits in `src/ReyEngine.Rendering/Vfx/` | Every alpha emitter; `pass` on 79.7% |
| **No bloom pass.** 0 bloom code in `src/` | `Shaders/Particles/DefaultParticleQuadUnlit` declares `BloomThreshold` default (3,0,0,0) and `FOW_Intensity_Bloom`; `FEATURE_BLOOM` is an axis on 157 of 833 TOCs. **Correction:** that shader is an opt-in `CustomShaderDef` reached only via `CustomMaterial` (36 of 94,979 emitters sampled, 0.038%, none of which actually links a particle shader), and the default `quad_ps` declares no bloom — so this is a frame-level post-process gap, not that parameter | 22.2% of mode-4 and 24.1% of blend-absent emitters are "glow"-named |
| **No depth texture bound to the particle pass.** `CaptureScene` copies colour only | `VfxParticleRenderer.cs:121-144` | Blocks soft particles (95,671) |
| **No fog-of-war / navmesh mask.** Engine declares `FEATURE_FOW`, `FOW_MAP`, `FEATURE_NAVMESH_MASK`, `NAVMESH_MASK_TEXTURE`, `NAV_GRID_TYPE_MASK` | `assets/shaders/shareddata.bin` @ Shaders.wad.client | Ground decals spill onto walls at full brightness |
| **No team-colour correction or colour-remap ramp.** `quad_ps` declares `APPLY_TEAM_COLOR_CORRECTION`, `PIXEL_COLOR_REMAP_RAMP_SharedTexture` | `assets/shaders/hlsl/particlesystem/quad_ps.ps.dx11` | Unquantified |
| **No separate alpha texture.** `DefaultParticleQuadUnlit` declares `Alpha_Texture`, `Alpha_ChannelToUse`, `Alpha_Texture_USE`, `Color_ChannelToUse` | `data/shaders/shaders.bin` @ Global | Custom-material emitters only |
| **Mesh particles are drawn one `DrawElements` per particle**, not instanced | `VfxParticleRenderer.cs:447-462` | 349,009 mesh emitters — performance, not correctness |
| **16,797 enabled emitters are never simulated.** `IsVisual` requires a texture, textureMult, mesh path or distortion normal map; anything drawing purely through another stage is filtered out silently | `VfxSystemDefinition.cs:71-73`, `VfxParticleSimulator.cs:85` | 16,797 (1.2%) |
| **Texture-less mesh emitters are forced invisible**, and unresolvable textures fall back to a procedural white dot | `ViewportControl.cs:1202,1255-1260` | 1,106 mesh emitters with no resolvable mesh path |

### Hardcoded constants standing in for data

`VfxParticleSimulator.cs`: `MaxParticlesPerEmitter = 4000` (silent truncation, `:68`); `dt` clamped to 0.1 s (`:140`); particle life floor 0.05 s (`:218`); `birthScale0.Y == 0 → X` (`:243`, hits 49,869 emitters); colour-gradient U axis for `colorLookUpType 1` uses an invented 400 units/s normalisation and types 2/3 are both treated as per-particle random (`:305-314`, with the code's own comment admitting it); V axis defaults to 0.5.
`VfxSystemResolver.cs`: `rate = 10/s` when absent (6,499 emitters), `particleLifetime = 1 s` (38,239), `blendMode = 1` (110,540), `numFrames = 1` (30,115 with a multi-cell `texDiv`).

The rate/lifetime defaults were checked specifically and are **not** a significant error source — `rate` is genuinely absent on 0.31% of emitters and `particleLifetime` on 3.05%, and Riot authors those exact values explicitly often enough that they look like the real defaults.

---

## Unknown

Honest gaps. Nothing here should be implemented on a guess.

### Unresolved hashes

| Hash | Kind | Type / shape | Freq | Parent | Evidence bin |
|---|---|---|---|---|---|
| `0xee39916f` | **CLASS** | Struct, single field `emitOffset`:Vector3 (plain) | **308,070** — 43% of all spawn shapes, the most common shape class in the game | `VfxEmitterDefinitionData.SpawnShape` | `data/characters/sru_atakhan/sru_atakhan_multi_skins_skin0_..._skin3.bin` @ Map11 |
| `0x6aec9e7a` | FIELD | Bool, always true | 3,668 on `VfxPrimitiveMesh` + 3 on `VfxPrimitiveAttachedMesh` | Sits beside `AlignPitchToCamera`/`AlignYawToCamera`, so probably a third alignment switch — unconfirmed; ~20 candidate names rejected by hash | `data/characters/sightward/skins/skin256.bin` @ Map11 |
| `0xd1ee8634` | FIELD | BitBool, always true | 795 | `VfxEmitterDefinitionData` | `data/characters/sightward/skins/skin259.bin` @ Map11 |
| `0x2808fffd` | FIELD | Struct → `0x526478f0` (385) / `0xcd5a34f5` (83) / `0x3df230bf` (10) | 478 | `VfxEmissionSurfaceData` (its only field). One brute-force run offered "EmissionSurface" but at 1.3e9 candidates that is inside the false-positive rate — treat as unconfirmed | `data/characters/sru_atakhan/...skin3.bin` @ Map11 |
| `0x8df5fcf7` | **CLASS** | Zero properties in 100% of instances | 431 | `VfxEmitterDefinitionData.primitive` — an 11th primitive type. 431 of 431 have no texture and are not drawn | `data/characters/ashe/skins/skin78.bin` @ Ashe |
| `0x526478f0` | **CLASS** | Zero fields | 385 | under `VfxEmissionSurfaceData.0x2808fffd` | `data/maps/mapgeometry/map11/ruby_sr_trialofdoom_overload.materials.bin` @ Map11 |
| `0x3bf176bc` | FIELD | U8 {2:239, 1:2} | 241 | `VfxSoftParticleDefinitionData` — likely the soft-particle fade mode; render-relevant | `data/maps/shipping/map11/map11.bin` @ Map11 |
| `0x9836cd87` | FIELD | U8 {7:136, 6:16, 4:3, 3:1, 2:1} | 157 | `VfxSystemDefinitionData` | `data/characters/turret/skins/skin1.bin` @ Map11 |
| `0xcd5a34f5` | **CLASS** | 7 fields: `meshName`, `skeletonName`, `Submeshes`, `useSurfaceNormalForBirthPhysics`, `AnimationName`, `maxJointWeights`, `meshScale` | 83 | under `VfxEmissionSurfaceData.0x2808fffd`. Brute force offered "VfxEmissionMeshData" — plausible but inside the false-positive rate | `data/characters/locke/skins/skin5.bin` @ Locke |
| `0x115b5460` | **CLASS** | `TextureToOverride`:Hash, `TacticianIndex`:U32 | 16 | `MapParticle.TextureOverride` | `data/maps/mapgeometry/map22/battleacademia.materials.bin` @ Map22 |
| `0x3df230bf` | **CLASS** | `skeletonName` + `0xb8314653`:UnorderedContainer\<Hash\> | 10 | under `VfxEmissionSurfaceData.0x2808fffd`; ShyvanaDragon only | `data/characters/shyvanadragon/shyvanadragon_multi_skins_skin17_...bin` @ Shyvana |
| `0xf97b1289` → `0x7fb92f53` {`0x3c475337`:F32 900/1000, `0xc865acd9`:F32 1500/2000, `0x28de30d6`:F32 0.3} | FIELD + CLASS | Struct | 18 | `VfxSystemDefinitionData`. Value pattern *suggests* a near/far distance fade pair — a guess, not measured | `data/characters/aurora/aurora_multi_skins_skin0_..._skin9.bin` @ Aurora |
| `0x8b301739` → `0x75e34c40` → `0x1dcc5270` → `0x0d5c9eb1` {`EventName`:Hash, `0x1004c9c8`} → `0x056bb851` → `0xe6d60f41` → `0xc76c1b9a` → `0x51445de9`/`0x557bb273` {`value`:Vector4} | FIELD + 7 CLASSES | Nested event-keyed modifier tree | 16 systems, Viktor only | `VfxSystemDefinitionData` | `data/characters/viktor/viktor_multi_skins_skin10_..._skin9.bin` @ Viktor |
| `0xfbef6376` | **CLASS** | single field `graph`:Embedded\<ValueFloat\> | 5 | value type in `VfxMaterialDefinitionData.materialDrivers`, beside `VfxFloatOverLifeMaterialDriver` / `VfxColorOverLifeMaterialDriver` | `data/maps/shipping/map22/map22.bin` @ Map22 |
| `0xf8b81c77` → `0x671b7351` {`VfxGroupName`:String = "Void"} | FIELD + CLASS | Struct | 1 | `VfxEmissionSurfaceData` | `data/maps/shipping/map11/map11.bin` @ Map11 |
| `0x1c45cf5c` | FIELD | Color | 2 | `VfxPrimitiveCameraSegmentSeriesBeam` | one map bin |
| `0x52a5da10` / `0xe58451c3` / `0xc6d048fc` / `0xfccc2584` | FIELDS | Bool / F32 / F32 / String | 3 / 4 / 3 / 1 | `MapSunProperties`, `MapLightingVolume` | `data/maps/mapgeometry/map11/base_srx.materials.bin` @ Map11 |
| `0x82b49579` / `0x630a0360` / `0x2f798a22` / `0x0ebe7428` | **CLASSES** with ObjectLinks into `VfxSystemDefinitionData` | — | 1,408 / 228 / 14 / 1 links | Unfollowed link routes into VFX systems (`0x2f798a22` has 8 named link fields: `IdleVfxSystem`, `HoverVfxSystem`, `RefreshVfxSystem`, `RefreshOverlayVfxSystem`, `PickedVfxSystem`, `NotPickedVfxSystem`, +2 unnamed) | `data/maps/mapgeometry/map11/ruby_sr_trialofdoom_overload.materials.bin` @ Map11; `data/maps/shipping/map30/map30.bin` @ Map30 |
| `0x670b6ae3` / `0x2cf1dfdd` / `0xa4404f0c` / `0x7dde758d` / `0x7df5733d`+`0xc15041c4` | FIELDS/CLASS | U8 / Bool / Bool / U8 / Struct | 2,809 / 192 / 532 / 2 / 5 | `MapAnimatedProp`, `MapActionPlayAnimation`, `MapActionAttachMapParticle` — not VFX proper but in the same bins | `data/maps/mapgeometry/map11/base_srx.materials.bin` @ Map11 |

Also unresolved but reachable only from non-VFX roots: `0x9be57ed9` (15 bins), `0x48f3fe52` (2), `0x6fb748e3` (2) — top-level objects in VFX-bearing champion bins, not reachable from any VFX system, so their fields were not enumerated.

### Unconfirmed semantics

1. **The `blendMode` integer → blend-state table.** Not in shipped data at all. A single RenderDoc/PIX capture of the live client showing the D3D11 blend desc for one emitter of each mode resolves all eight at once. Until then every mapping in `IsAdditive` is inference, and mode 4 — half the corpus — could not be separated from mode 1 by texture authoring. Modes 6/7/8 (258 emitters) have no assignment at all. Riot's default for an absent `blendMode` is equally unknown.
2. **Does League decode particle diffuse through an `_SRGB` SRV?** The `.tex` container has no sRGB flag (33,490 headers, 12-byte header, format byte 12=BC3 or 10=BC1), and neither particle pixel shader family declares GAMMA/SRGB/LINEAR_TO or binds `SAMPLER_GAMMA_LOOK_UP`. ReyEngine uploads `Rgba8` (`VfxParticleRenderer.cs:105,137`) and blends in gamma space — self-consistent, but if League is sRGB every particle is subtly wrong in exactly the way the user reports. A frame capture showing the SRV format settles it.
3. **Linear vs spline interpolation between keyframes.** The data stores only (times, values) — no tangents, no ease, no mode selector across 5,578,945 curve blocks — so nothing richer than a fixed scheme is *parameterisable*, but whether the runtime lerps or fits a spline through those points cannot be read out of the bins. 979,340 three-key and 695,335 four-key curves with irregular hand-authored times (0.0105 / 0.1963 / 0.7391 / 0.971) are consistent with either.
4. **What axis a birth-time curve is keyed on.** ~172,200 multi-key birth curves depend on this. Normalised emitter lifetime is the only candidate the format offers, but that is inference.
5. **Whether `constantValue` multiplies the curve or is superseded by it.** 21,270 `rate` values and 146,634 `worldAcceleration` values disagree between the two.
6. **`isUniformScale`** (792,459 emitters, always true). If it means "drive both axes from X", up to 634,368 emitters are drawn with the wrong aspect ratio. Testable by A/B-ing one emitter against the live game.
7. **`VfxSystemDefinitionData.flags` bit meanings** (86,225 systems, 81 distinct values).
8. **Enum semantics**, domains measured but meanings not: `miscRenderFlags` {1,2,3,4,5}, `uvMode` {1..5}, `importance` {0,1,3,4,5}, `texAddressModeBase` {1,2,3}, `erosionMapAddressMode` {0,1,3}, `PaletteTextureAddressMode` {0,2}, `stencilMode` {1,2,3,4}, `distortionMode` {0,2,3}, `colorLookUpTypeX` {0,2,3} / `TypeY` {1,2,3}, `VfxParentInheritanceParams.Mode` {2,8,10,24}, `VfxShapeBox/Sphere/Cylinder.flags`.
9. **`colorLookUpTypeY = 3`** covers 14,388 of 15,532 authored Y axes and is currently bucketed as "per-particle random" with an in-code admission that it is approximate. Whatever 3 means drives nearly every colour-texture emitter in the game.
10. **Frame rate when `numFrames > 1` and no rate is authored** (243,492 emitters, 87% of flipbooks).
11. **Whether `numFrames` should be derived from `texDiv.x * texDiv.y`** when absent (30,115 emitters). The relation holds 95.3% of the time when both are present.
12. **`depthBiasFactors` component meanings**, and how it composes with `depthPushPull`.
13. **`meshRenderFlags`** is serialised 133,900 times and is **always 0**. Why a tool writes a field at its own default is unexplained and may indicate the default is non-zero.
14. **Riot's defaults for one-valued BitBools.** `isUniformScale`, `isGroundLayer`, `disableBackfaceCull`, `particleIsLocalOrientation`, `useNavmeshMask` are only ever written `true`; `isLocalOrientation` and `useEmissionMeshNormalForBirth` only ever `false`. The tempting inference — "the default is the opposite" — is **refuted** by `alphaRef`, which is explicitly written as 0 on 391,078 emitters, and `pass` as −1 on 37,697. Property absence means "the author never touched this", not "this equals the default". The real defaults live in the executable.
15. **Whether one random roll is shared across a vector's components.** ReyEngine rolls independently per component and per property; the existence of `ParticlesShareRandomValue` (8,173) implies independence is the default, but the flag's scope is not derivable.
16. **Emitter ordering when a system has both containers.** ReyEngine iterates the property dictionary, i.e. field-hash order, not a semantic order. 261 bins affected.
17. **Probability-table outliers.** 1,083,670 of 2,893,818 tables have `keyValues` outside [-1,1], 657,849 contain negatives, range −20,000…+10,000, and 149,728 pair a large constant with a large table value. Multiplication is proven for the `birthRotation0` idiom pair, but whether it holds for those outliers is untested.
18. **`MapParticle.mVisibilityFlags` default** when absent (16,966 placements; ReyEngine substitutes 255).
19. **`MapLightingVolume` blending** — hard switch or falloff at the volume boundary?
20. Riot ships two typo'd field names that any writer must reproduce byte-for-byte: **`palleteSrcMixColor`** (19,296) and **`TextureMultFilpU`/`TextureMultFilpV`** (4,338). Worth a regression test around `ParticleDocument.Serialize`.
21. The prior memory note **"Jade does NOT render particles"** could not be re-verified this session — no Jade source exists on this machine. ReyEngine itself demonstrably does render particles. The only consequence is that Jade cannot serve as a visual reference for validation.

---

## Implementation Plan

### 1. Properties required for visual accuracy

Ordered by emitters affected × confidence that the fix is correct.

| # | Work | Files | Affected | Risk / difficulty |
|---|---|---|---|---|
| 1.1 | **Spawn-shape volumes.** Extend `ReadSpawnShape` to dispatch on class hash and read `VfxShapeSphere.radius`, `VfxShapeBox.Size`, `VfxShapeCylinder.radius+height`; sample a point in the volume at spawn | `VfxSystemResolver.cs:282-290`, `VfxSystemDefinition.cs`, `VfxParticleSimulator.cs:234-253` | **216,819 (15.5%)** | Low risk, well-defined data. Watch the extreme ranges (sphere radius up to 3.02e8) — clamp or the particle buffer explodes. Distribution within the volume (uniform vs surface) is unverified; start uniform and mark it |
| 1.2 | **Birth curves at the right time.** Thread emitter-normalised time into `SampleBirth` instead of the hardcoded `Sample(0f)` | `VfxSystemDefinition.cs:123,143,164`, `VfxParticleSimulator.cs:217-225` | ~172,200 curves | **Medium risk — the axis is inferred.** Gate behind a flag and A/B one emitter (`sru_atakhan` `trailBlend`) against the game before making it the default |
| 1.3 | **Emitter draw order.** Sort emitters by `pass`, then by container index as a tiebreak | `VfxSystemResolver.cs` (parse I16), `VfxParticleRenderer.cs:178` | 1,114,110 (79.7%) | Low difficulty. 2,913 distinct values spanning the full I16 range, so use a stable sort. Whether Riot sorts globally or per-system is unknown — start per-system |
| 1.4 | **Alpha test.** Read `alphaRef`, add `if (a < uAlphaRef) discard;` | `VfxSystemResolver.cs`, `VfxParticleRenderer.cs:573-600` | 34,788 with a non-zero cutoff | Low risk — engine-confirmed by `ALPHA_TEST` / `AlphaTestReferenceValue` in `quad_ps` |
| 1.5 | **`AttachedMesh` mesh path.** Widen the `isMesh` gate to include `VfxPrimitiveAttachedMesh` and `VfxPrimitiveBeam` where `mMesh` names a file | `VfxSystemResolver.cs:203,208`, `MainWindowViewModel.cs:810` | 6,382 emitters gain a mesh | Low. The other 54,690 AttachedMesh cases need host-model submesh masking — a separate, larger job |
| 1.6 | **`texDiv` clamp on the quad path.** Honour negative (flip) and sub-1 values; the mesh path already does | `VfxParticleRenderer.cs:207-208,559-560` | 2,293 + 390 quad emitters | Small, but the *meaning* of negative/fractional is unverified — implement flip/zoom behind a note, or align the quad path with the mesh path's existing tiling reading |
| 1.7 | **Colour-lookup scale/offset.** Apply `colorLookUpScales`/`Offsets` to `LookupCoord` | `VfxParticleSimulator.cs:305-314` | 36,423 / 23,920 | Low risk, straightforward affine. Does not fix the unknown meaning of type 3 |
| 1.8 | ~~**`emitterPosition` curve-only collapse.**~~ **REFUTED during implementation — see note below.** The real defect is that the field was stored as a bare `Vector3`, discarding its probability tables | `VfxSystemResolver.cs`, `VfxSystemDefinition.cs`, `VfxParticleSimulator.cs:240` | 3,350 emitters actually scatter | Done in M174 |
| 1.9 | **`velocity` and `rotation0` over-life curves.** Add the two field hashes and drive them in the integrator | `VfxSystemResolver.cs`, `VfxParticleSimulator.cs` | 23,112 / 52,647 | Low, except `rotation0` is `IntegratedValueVector3` — integrate, do not sample |
| 1.10 | **`isUniformScale`** | `VfxParticleSimulator.cs:243` | ≤634,368 | **Do not implement yet.** Highest-value unresolved semantic; needs one live A/B |


**Correction to 1.8, found while implementing it (2026-07-26).** The claim was that 130,259 emitters lack a
`constantValue` and so "collapse to (0,0,0)", implying the curve's first key should be used instead. Measured
over 60 champion WADs: 37,327 emitters do lack `constantValue`, and **every one of their curve keys is exactly
(0,0,0)** — there is nothing to recover, and the proposed fix is a no-op. What those structs actually carry is
`probabilityTables` (37,220 of 37,327). The authored intent is a per-particle SCATTER around the origin, and the
real defect was that `VfxEmitterDefinition.EmitterPosition` was a bare `Vector3`, which threw the tables away.
Fixed by making it a `VfxCurve3` sampled per particle at birth. Behavioural check: 3,350 emitters now produce a
non-zero spread (max 1,500 world units, e.g. `data/characters/jinx/skins/skin60.bin`); the other ~34,000 carry
degenerate all-zero tables and correctly stay fixed. Evidence: `data/characters/aatrox/skins/skin26.bin`.

### 2. Missing renderer behaviour

| # | Work | Files | Affected | Risk / difficulty |
|---|---|---|---|---|
| 2.1 | **Alpha erosion / dissolve.** Second texture + drive curve + feather/slice in the fragment shader | `VfxParticleRenderer.cs` (Frag + a third sampler), `VfxSystemResolver.cs` | **307,050 (22.0%)** | Largest single visual win. Medium difficulty; engine-confirmed feature (`ALPHA_EROSION`, `sAlphaErosionTexture`, `cAlphaErosionParams`). The exact channel-mixer and feather math is inferred — start from the field names and iterate visually |
| 2.2 | ~~**Soft particles**~~ **DONE (M175)** | `VfxParticleRenderer.CaptureDepth` + Frag | 95,671 (6.8%) | Formula decoded, then validated twice: against Riot's own `quad_ps` blob#128 on D3D11 (worst 0.0021), and against `smoothstep` at five distances through the real GL path (worst 0.002). Depth arrives via a depth-only `BlitFramebuffer` into a texture, because the viewport's depth attachment is a renderbuffer. `cSoftParticleControl` is still **UNKNOWN** and hardcoded to (1,0,0,1) |
| 2.3 | **UV transform stack.** Rotation, scale, offset, clamp, flip, integrated scroll | `VfxParticleRenderer.cs:558-569`, `VfxSystemResolver.cs` | ~150k emitters across 12 fields | Medium. Note `particleUVScrollRate`/`particleUVRotateRate` are `IntegratedValue*` and accumulate — do not sample them like ordinary curves |
| 2.4 | ~~**Force fields**~~ **DONE (M176)** - all five kinds | `VfxParticleSimulator.ApplyForceFields`, `VfxSystemResolver.ReadForceFields` | 39,904 (2.9%) | Data shapes measured off 6,134 live collections, not inferred from names. The MATHS is still inferred and cannot be validated the way M175's shader stages were - these integrate on the CPU, so there is no bytecode to decode. See 2.4b below for exactly which parts |
| 2.5 | ~~**Trails and beams**~~ **DONE** (trails M177, beams M183) | `VfxParticleSimulator` history + `VfxParticleRenderer.RenderTrailEmitter` | 78,852 trail emitters done; 10,088 beam emitters remain | The ribbon reading is now MEASURED, not inferred from class names - see 2.5b. Beams are deferred for a stated reason, not overlooked |
| 2.6 | ~~**Palette recolour**~~ **DONE (M175)** | `VfxSystemResolver.ReadPalette`, `VfxParticleRenderer.cs` Frag | 43,621, of which 13,823 have no colour texture at all | Decoded and validated against Riot's `quad_ps` blob#12 (worst 0.0096). `paletteSelector` measured as a ValueVector3 row index against `paletteCount` (median 16). The U/V animation curves are deliberately **NOT** applied - see 'What was deliberately left out' below |
| 2.7 | ~~**Child particle systems**~~ **DONE (M180)** in the model preview | `VfxSystemResolver.ReadChildren`, `VfxParticleSimulator.QueueChildSpawns`, `ViewportControl.SpawnQueuedChildren` | 35,510 (2.5%) | Spawn scheduling is in the simulator, instantiation in the viewport, resolution in the preview VM (which owns the resource map). Depth-capped because cycles are NOT ruled out — see 2.7b |
| 2.8 | ~~**Depth offset**~~ **DONE (M175)**; `depthBiasFactors` still deferred | `VfxParticleRenderer.cs` vertex shader | 61,588 / 201,926 | No longer inferred. **DECODED** from `quad_vs` instructions 12-16: `world += normalize(world - vCamera) * depthPushPull`, per vertex, before projection - so positive pushes AWAY and negative pulls toward. Cross-checked against `defaultparticlequadunlit.vs` (same form for `EMITTER_DEPTH_PUSH_PULL`) and confirmed by rendering against a depth-writing wall. 74.6% of authored values are negative, which the decoded sign explains |
| 2.9 | ~~**Stencil masking**~~ **DONE (M182)** - modes 1/2/3; mode 4 (0.2%) unresolved | `VfxParticleRenderer.ApplyStencil` | 26,393 | Mode meanings are a project decision, recorded as such. Masking verified end to end: equal + not-equal tile the tester's area exactly once - see 2.9b |
| 2.10 | **Sampler state per emitter.** `texAddressModeBase` and `isTexturePixelated` instead of hardcoded Repeat/Linear | `VfxParticleRenderer.cs:108-112` | 80,342 / 1,298 | Trivial code change, but the enum ordering is UNKNOWN — needs one visual A/B to pin |
| 2.11 | **Backface culling per emitter** | `VfxParticleRenderer.cs:174` | The 1,101,289 emitters that omit `disableBackfaceCull` | Blocked on the unknown default; only matters for mesh and arbitrary-quad primitives |
| 2.12 | ~~**Reflection / fresnel**~~ **DONE (M181)** - rim and cubemap both | `VfxParticleRenderer.cs` mesh path | 59,149 (4.2%), of which ~87% are fresnel-only | Fully DECODED from `mesh_vs`/`mesh_ps` REFLECTIVE, and the field mapping is pinned by the maths rather than inferred - see 2.12b. Cubemap sampling still needs cubemap loading on the particle path |
| 2.13 | **Bloom pass** | new post-process in `ViewportControl.cs` | 22% of emitters are "glow"-named | High effort, high perceptual payoff. Frame-level, not per-emitter |
| 2.14 | **Duty-cycle and rate-by-velocity emission** (`period`, `timeActiveDuringPeriod`, `rateByVelocityFunction`, `ChanceToNotExist`, `HasVariableStartTime`) | `VfxParticleSimulator.cs:157-178` | ~30k combined | Low individually, visible on map beacons and dash trails |
| 2.15 | ~~**`Linger` shutdown stage**~~ **DONE (M185)** | `VfxParticleSimulator.Stop`, ParticleEditor Stop button | 22,274 | Trigger measured to be an EXTERNAL stop, not lifetime expiry. The preview gained a Stop action so the stage is observable |
| 2.16 | **Flex bound-object scaling — RETIRED (M179), not implemented** | — | 159,880 (11.4%) | Measured to be a **no-op at the reference size**, so today's behaviour is already the reference case. Implementing it against a guessed bound-object metric would move effects away from that, not toward it. See 2.16b |

### 2b. M175 findings worth recording

**`depthPushPull` is fully decoded, not inferred.** `quad_vs` blob#0:

```
12: add r0.xyz, v0.xyz, -cb2[4].xyz      // vCamera -> vector camera->vertex
13-15: dp3 / rsq / mul                   // normalize
16: mad r0.xyz, r0.xyz, cb1[1].xxxx, v0.xyz   // PARTICLE_DEPTH_PUSH_PULL
```

Because every quad corner slides along its own camera ray, screen position and size are **exactly**
preserved and only depth changes - so this can be applied unconditionally with no risk of moving artwork.
The near-identical `defaultparticlequadunlit.vs` applies the same form to `EMITTER_DEPTH_PUSH_PULL` at
cb3[2].z. That shader's *other* offset, at cb1[0].x, uses the opposite direction vector - but it is
`CameraOffset`, a different global, so there is no contradiction between the two shaders.

**The same disassembly independently confirms M174's flipbook fix.** Instructions 23-28 decompose the
frame index exactly as ReyEngine does - `round_ni` (floor) on the frame, `col = frame - floor(frame/cols)
* cols`, then scale by `TEXTURE_INFO.yz` = (1/cols, 1/rows). That was implemented from reasoning in M174
and is now evidenced.

**A depth-linearisation bug that no amount of code review would have found.** ReyEngine builds its
projection with `Matrix4x4.CreatePerspectiveFieldOfView`, and System.Numerics follows the **Direct3D**
convention (clip z maps near->0, far->+1). GL then applies `d = (z_ndc + 1) / 2`, so window depth only
ever occupies **[0.5, 1.0]**. The textbook GL linearisation constants `(1/near, 1/far - 1/near)` are
therefore exactly half the correct slope; using them made every measured distance ~1.9x too large and
silently halved the width of every authored fade band. It still *looked* like a plausible soft particle.
Caught only by comparing measured alpha against `smoothstep` at five distances in an offscreen probe.

> Side note, not acted on: the same convention mismatch means the app discards half of its depth-buffer
> precision everywhere, not just for particles. Fixing it would change existing depth behaviour across
> the whole renderer, so it is recorded here rather than changed as a side effect of a VFX milestone.

**`glClearDepth` does not exist in GLES 3.0.** ANGLE raises `SymbolLoadingException` for it. Nothing
else in the codebase called it; the GL default clear-depth is already 1.0, so `glClear` alone is right.

**What was deliberately left out.** `PaletteUAnimationCurve` / `PaletteVAnimationCurve` would naturally
feed the two offsets the decoded shader adds (`U + select.z`, `V + select.w`). They are **not** applied,
because the data refutes the naive reading: their authored median is 1.0, and a constant +1 on U drives
every lookup off the right-hand end of the gradient. Whatever the CPU does with those curves, it is not
a plain add. 372 of 8,866 palette structs author a U curve and 99 a V curve.

**Erosion (M174) was only ever reaching one code path.** Of the seven places that build a
`VfxPlaybackItem`, exactly one - the champion-VFX list - passed erosion maps. The Particle Editor, the
model preview and both map-particle paths did not, so the dissolve stage shipped in M174 was inert on
the surfaces built for looking at VFX. M175 wires erosion and palette through all seven.

### 2.4b M176 force-field findings

**Measured, not inferred** - class hashes, inner property names and BinTree types read off 6,134 live
collections across 28 WADs:

| kind | class | fields |
|---|---|---|
| noise | `0x634db850` | `axisFraction` (raw Vector3), `radius`, `frequency`, `velocityDelta` (ValueFloat), `Position` (ValueVector3) |
| drag | `0xe750fae2` | `radius`, `strength`, `Position` |
| acceleration | `0x0a94f3d4` | `acceleration` (ValueVector3), `isLocalSpace` |
| attraction | `0x1a7617fd` | `radius`, `acceleration`, `Position` |
| orbital | `0xb67aee6f` | `direction` (ValueVector3), `isLocalSpace` |

Two measurements close questions the census left open:

- **`isLocalSpace` is false on every one of the 690** acceleration and orbital fields that resolve. World
  space is not an assumption, it is the only case that ships.
- **Attraction's `acceleration` is signed** (min −10000). 20 repulsors appear in 25 WADs. Clamping it
  positive — the obvious defensive move — would have silently deleted every one of them.

**Still inferred, and unvalidatable by the M175 method.** These fields are integrated on the CPU, so
unlike alpha erosion, soft particles and palette there is no shader bytecode to decode and nothing of
Riot's to compare against:

- that `frequency` is a spatial **wavelength** (noise sampled at `pos / frequency`) rather than a
  multiplier. Measured median 25, range 0.005–5000. As a wavelength that is a 25-unit swirl next to a
  ~100-unit champion; as a multiplier it is 25 cycles per world unit, which is white noise at any
  distance a particle travels — so the wavelength reading is the only one that produces motion at all.
  Neither reading is tidy at the extremes.
- that `velocityDelta` is an acceleration in units/second rather than an absolute velocity offset.
- the **radial falloff shape** for the three positioned field types. Linear-to-zero is used; a hard cut
  makes particles jerk visibly as they cross the boundary.

**A bug the obvious test would have missed.** The orbital field was first written to contribute
`omega x r` to velocity, so that it would compose with drag and attraction. But velocity persists across
frames, so the term accumulates every step and the particle spirals away under runaway acceleration. It
still *bent the trajectory*, so the natural test — "does an orbital field curve an otherwise straight
path?" — passed while the behaviour was badly wrong. Replaced with a rotation of position and heading
together (matching the existing `birthOrbitalVelocity` path), and the test replaced with one that
asserts a **constant orbit radius**: measured 100.00 at t = 0.25, 0.5, 1 and 2 s, with the swept angle
matching `|omega|*t`.

**Fields authored purely as curves.** Some values carry `dynamics` with no `constantValue` — 65 of 191
orbital `direction` values, against only 56 with a constant. Reading the constant alone resolved those to
zero and switched the field off. Reading the first curve key instead lifted active orbital fields from
51/184 to 86/184. What a field curve is parameterised over (particle age, emitter age, something else)
is **UNKNOWN**, which is why one key is read rather than sampling over life.

### 2.5b M177 trail findings

**The ribbon reading is no longer an inference.** The census flagged "trails and beams are ribbon
geometry" as a reading of the class names. Reading the payload settles it - `mTrail` is
`VfxTrailDefinitionData` (class `0x00c2a390`), measured on 14,755 live instances:

| field | n | what it is |
|---|---|---|
| `mBirthTilingSize` | 14,596 | ValueVector3, but X-only in practice — (500,0,0) is the commonest at 4,375, then (300,0,0), (600,0,0), (400,0,0). A texture repeat LENGTH along the ribbon |
| `mMode` | 14,133 | U8, **always 1**. No mode branch exists to get wrong |
| `mCutoff` | 12,264 | F32, median 1,000. Maximum ribbon length |
| `mSmoothingMode` | 12,183 | U8 {1,2}. Meaning **UNKNOWN** — parsed and surfaced, but the geometry builder does not branch on it |
| `mMaxAddedPerFrame` | 9,505 | I32, median 50. Points appendable per frame |

A texture repeat length, a maximum length, a smoothing mode and a per-frame point budget is the parameter
set of a ribbon generator and of nothing else. The geometry itself is not in the bin at all — it is built
from where the particle has been, and these values only say how.

**`mCutoff` carries junk that must not reach the geometry builder.** −1 occurs, and the maximum observed
is 68,719,476,736 (2^36). 2,499 of 14,755 resolved trails fall outside a usable range and fall back to
the corpus median; without that guard a single authored value would produce a ribbon spanning the map.

**A speed-dependence bug the natural implementation has.** The obvious way to sample a ribbon is "append
the particle's position once it has moved far enough". That overshoots by however far the particle
travelled during the frame that crossed the threshold — so the ribbon's length depends on its SPEED.
Measured on a 1,000-unit cutoff: 1,018 units at 50 u/s but **1,567 units at 2,000 u/s**, and every
authored cutoff overshot (a 250-unit cutoff produced 313). Walking the segment and inserting points at
the exact interval removes both the frame rate and the speed: now 1,000.0 and 999.9 across that same 40x
speed range, and 250.0 for the 250 cutoff.

That fix is also what gives `mMaxAddedPerFrame` a job — the field only makes sense if the engine can
append more than one point per frame, which is a small independent confirmation of the shape.

**Two orientation cases, INFERRED.** `VfxPrimitiveCameraTrail` twists the ribbon to face the viewer;
`VfxPrimitiveArbitraryTrail` holds the placement's up axis. The payloads are identical, so this comes
from the class names alone — but it is the only distinction those names draw, and it is the reason Riot
ships two classes. Measured split: 5,718 camera, 9,037 arbitrary.

**Not implemented, and why.** Colour is applied uniformly along the ribbon. Real trails usually taper or
fade toward the tail, but nothing in the payload says so, and the tiling field means the texture REPEATS
along the length rather than providing an inherent fade. Inventing a taper would be exactly the kind of
silent guess this report exists to prevent.

**Beams landed in M183**, once the project decided they bind to the M114 practice dummy. See 2.5c.

### 2.12b M178 reflection findings

**Reflection belongs to the MESH path only, confirmed twice independently.** `REFLECTIVE` is a define on
`mesh_vs` and `mesh_ps`; it does not appear in `quad_ps`'s define pool at all, so Riot never compiles
reflection into the billboard path. The authored data agrees without being asked to: of 8,058 emitters
carrying a `reflectionDefinition`, **95.9% use a mesh-capable primitive** (52.9% `VfxPrimitiveAttachedMesh`,
43.0% `VfxPrimitiveMesh`), against 1.1% quads and 0.1% rays. Putting fresnel on a camera-facing quad would
have been inventing behaviour, not restoring it.

**Decoded, from `mesh_vs` perm #7 and `mesh_ps` REFLECTIVE:**

```
VS:  N = normalize(mul(normal, mWorld));  R = V - 2*dot(V,N)*N;  f = saturate(dot(-V,N))
     fresnelOut  = (1 - pow(f, vFresnel.w)) * vFresnel.rgb
     reflOpacity = lerp(vReflection.y, vReflection.z, 1 - pow(f, vReflection.x))
PS:  rgb += cubemap.Sample(R).rgb * reflOpacity * lerp(1, vReflectionFColor.rgb, reflOpacity)
     rgb += fresnelOut * alpha            // alpha = texel.a * colorTex.a * vertexColor.a
```

**The field mapping is pinned by the maths, not guessed** — the opposite of alpha erosion, where which
authored value landed in which cbuffer slot was undecidable. Here the lerp endpoints determine it: at a
direct view `NdotV = 1`, so the term is 0 and the opacity is exactly `vReflection.y`, which the authored
name calls `reflectionOpacityDirect`. `.z` is the glancing end, named `reflectionOpacityGlancing`. Names
and maths agree independently.

**A correction to an M175 finding.** M175 recorded `PIXEL_COLOR_REMAP_RAMP` as replacing RGB
*unconditionally*. In `mesh_ps` it is **gated**: `lt r0.w, (0), r0.w` then `movc` selects the remapped RGB
only when the ramp texel's own alpha is greater than zero. The M175 measurement was taken with a white
stub whose alpha was 1, which is why it looked unconditional. A ramp with alpha 0 therefore makes the
stage a no-op — which resolves the open worry from M175 in the reassuring direction.

**A test that encoded a wrong intuition.** The first exponent check asserted that a *higher* `fresnel`
value tightens the rim. It failed, and the implementation was right: `term = 1 - pow(f, n)` with
`f` in [0,1] means a larger exponent makes `pow` smaller and the rim **wider**. Measured at mid-radius:
0.449 at n=0.3, 0.636 at n=2, 0.909 at n=8. That also explains the authored distribution — the median
`fresnel` is 0.1, i.e. artists overwhelmingly want the tight, subtle rim.

**Normals had to be generated.** `StaticMeshData` (.scb/.sco) carries positions, UVs and indices and no
normals at all, while Riot's `mesh_vs` declares a `NORMAL0` input — so the fresnel term had nothing to
work from. They are now accumulated from face cross-products (area-weighted) and normalised, with a +Y
fallback for degenerate vertices. Re-skinned frames (M48's butterflies) deliberately keep their bind-pose
normals rather than regenerating per frame.

**The cubemap half landed in M181**, reusing the M122 `CubemapDecoder` so face ordering is shared rather
than reinvented. `reflectionFresnel` and the two opacity fields now do their job — they only ever scale the
cubemap sample, so until there was a map they were parsed and inert. Measured against a blue cubemap:
opacity 1/1 gives a full reflection, 0/0 gives none, and direct 0 / glancing 1 leaves the centre black
(0.000) while the silhouette reads 0.735. That is the `lerp(direct, glancing, ...)` endpoints behaving
exactly as the decode says, and so a second independent confirmation of the field mapping.

**Two GLES traps, neither visible without actually running the renderer:**

- *`Precisions of uniform 'uHasRefl' differ between VERTEX and FRAGMENT shaders.`* GLSL ES defaults `int`
  to `highp` in the vertex stage and `mediump` in the fragment stage, so a uniform of the same name
  declared in both fails to LINK. Both declarations now state `highp` explicitly.
- *A samplerCube left on texture unit 0.* With no reflection map the shader branch is skipped, so leaving
  `uReflCube` unbound looked safe — but an unset sampler uniform defaults to unit 0, where `uTex`'s
  `sampler2D` already lives, and a cube and a 2-D sampler sharing a unit is a type conflict that
  invalidates the whole draw. Every mesh particle **without** a reflection map silently stopped rendering,
  which is most of them. A 1x1 white cubemap is now bound to the reflection unit unconditionally: the
  shader branch is not enough, the BINDING has to be valid whether the branch runs or not.

While chasing the first, the mesh-shader failure log moved from `Debug.WriteLine` to `Console.Error` — it
is invisible in a release run and in the offscreen probes, which is exactly how M174 shipped a blank
viewport twice. It surfaced the link error on the first run after the change.

Negative `fresnel` values occur (min −1) and are dropped: `pow(f, -1)` is `1/f`, which diverges as the
surface turns edge-on and would subtract unbounded colour rather than add a rim.

### 2.16b M179: why flex scaling is retired rather than implemented

This was the largest single unimplemented item left in tier 2 (159,880 emitters, 11.4%). Measuring it
before writing code turned it from a missing feature into a non-issue, and the measurement is worth
recording because the naive implementation would have been a visible regression.

**The coefficients are not blend weights.** `scaleBirthScaleByBoundObjectSize` has a median of **0.005**
across 25,447 authored values — not 1.0, and not a 0..1 weight. 0.005 is 1/200, which reads as a
per-unit coefficient: `multiplier = boundObjectSize * factor`, calibrated so a character of roughly 200
world units produces a multiplier of exactly 1.

**The authored sizes confirm that calibration independently.** If the multiplier were anything other
than ~1 by default, emitters opting into flex would have to author compensating birth sizes. They do not
— the distributions are the same to within noise:

| | p10 | p25 | median | p75 | p90 |
|---|---|---|---|---|---|
| with `FlexShapeDefinition` (n=31,134) | 3.05 | 17 | **50** | 110 | 200 |
| without (n=202,910) | 1.2 | 10 | **50** | 120 | 300 |

**So the consequence is the useful part: not applying flex is equivalent to previewing on a
reference-sized character.** That is a correct-looking default, not a bug. Applying it with a
bound-object metric guessed wrong — bounding-box diagonal vs height vs gameplay radius, all of which the
struct has *separate* fields for — would move 11.4% of emitters away from the reference in an unknown
direction and by an unknown factor. There is no shader to decode here (the scaling is CPU-side) and no
safe failure mode: the symptom of getting it wrong is "everything is the wrong size".

Two further practical points. Most editor contexts have **no bound object at all** — map particles and
the Particle Editor are not attached to a character — so there would be nothing to scale by in the
majority of previews. And the struct's own field set shows Riot distinguishes at least three different
size metrics (`...ByBoundObjectSize` 25,447, `...ByBoundObjectHeight` 308, `...ByBoundObjectRadius` 65),
so "the bound object's size" is not one quantity that could simply be measured off a loaded model.

**What would unblock it:** a definition of the plain `Size` metric, and a decision about what the editor
binds to when previewing a champion whose size differs from the reference. Both are choices, not
findings — which is why this is recorded here rather than guessed at in code.

### 2.7b M180 child-system findings

**`childrenProbability` is an expected COUNT, not a probability.** The census already flagged that values
exceed 1.0; measuring the whole set settles what it is instead. Across 136 authoring sets the values are
{0.005, 0.1, 0.2, 0.35, 0.4, 0.5, 0.55, 1, 2, 3, 8, 10}, **108 of 131 are whole numbers**, and the median
is exactly 1. Read as an expected count — `floor(p)` children plus one more with probability `frac(p)` —
every observed value makes sense with no special cases: 1 means "always one" (the overwhelming default,
since only 136 of 11,308 sets author the field at all), 0.5 means "half the time", 8 means "eight".

**`childEmitOnDeath` is Bool and `true` in all 178 instances that carry it**, so its ABSENCE is what
means "spawn at birth". That is a measured default rather than an assumed one.

**A probe of mine that measured nothing, and the correction.** An early run appeared to show zero cycles,
zero self-references and a maximum child-chain depth of 1 — which would have made recursion a non-issue.
It was wrong: it compared `effectKey` values against system OBJECT path-hashes, and those are different
keyspaces (`effectKey` indexes `ResourceResolver.resourceMap`). Every key looked unresolvable, so the
graph looked empty and acyclic for the same reason. **Nothing in the data establishes that child chains
terminate**, so the implementation caps depth at 2 rather than trusting them to.

**Where each piece lives, and why.** The simulator only SCHEDULES spawns — it is GL-free by design and a
child system needs textures — so it queues `ChildSpawn` records that the viewport drains and turns into
real simulators. Child systems are resolved ahead of playback, not on demand, because a spawn happens on
the GL thread mid-frame and resolving there would mean WAD reads and texture decodes inside the render
loop. Resolution lives in the model-preview view-model because that is what already holds both the system
table and the resource map.

Two ceilings exist that are frame-rate guards rather than Riot behaviour, and are marked as such in code:
16 queued spawns per emitter per frame, and 48 live child systems. A child set turns *every parent
particle* into a whole VFX system, so an emitter at a few hundred particles per second would otherwise
accumulate simulators until the frame rate collapses. Child systems retire on a 6-second timer rather
than when they run dry, because a looping child would otherwise live forever, one per parent particle.

**INFERRED:** when a set names more than one identifier (255 of 10,608 measured sets), one is picked at
random per spawn. The data gives a list and a count and says nothing about how they pair up; a set of
alternatives with a roll count is what the two fields together suggest. 97.6% of sets name exactly one
child, so it rarely bites.

**Not wired:** `boneToSpawnAt` (931 sets) is parsed and carried but not acted on — child spawns are not
bone-bound. Map-particle and Particle-Editor playback paths do not resolve children either, because
neither holds a resource map; only the model preview does.

### 2.9b M182 stencil findings

**Mode distribution, measured over 3,891 authored modes in 25 WADs:**

| mode | count | share |
|---|---|---|
| 2 | 2,292 | 58.9% |
| 3 | 1,009 | 25.9% |
| **1** | **584** | **15.0%** |
| 4 | 6 | 0.2% |

`stencilMode` and `stencilRef` are both U8. `stencilRef` runs 1-7 for the bulk with a tail to 48.
`StencilReferenceId` (1,140 emitters, 86 distinct hashes) is an alternative symbolic reference and is
not read.

**Mode 1 = normal write** (a project decision, recorded here as such rather than as a measurement): draw
as usual and replace the stencil value with `stencilRef` where the fragment passes. Implemented as
`StencilFunc(Always, ref, 0xFF)` + `StencilOp(Keep, Keep, Replace)`.

**The pairing evidence supports that reading.** Of 1,016 objects that use stencil at all, **254 contain
both a mode-1 emitter and a non-1 one** - the shape you would expect from "one writes, another tests".
125 contain only mode 1.

**Mode 2 = test equal, mode 3 = test not-equal** (also project decisions). Implemented as
`StencilFunc(Equal|Notequal, ref, 0xFF)` with `StencilMask(0)` - the tests do NOT write, since a mask that
rewrote the buffer would change what later emitters in the same frame see. Mode 4 (6 instances, 0.2%) is
still unresolved and draws with the stencil untouched.

Verified end to end with a two-emitter system - a small mode-1 writer at pass 0, a large tester at
pass 10:

| tester | pixels drawn |
|---|---|
| mode 2 (`== ref`) | 3,136 - **exactly** the writer's footprint |
| mode 3 (`!= ref`) | 14,288 |
| no stencil | 17,424 |

3,136 + 14,288 = 17,424, so equal and not-equal tile the tester's area precisely once with no overlap and
no gap.

**An absent `stencilRef` is NOT zero, and conflating them would have deleted emitters.** 726 of 3,891
emitters with a `stencilMode` author no numeric ref - most name a symbolic `StencilReferenceId` instead
(86 distinct hashes, not resolved here). Defaulting those to 0 is harmless for mode 2 but actively
destructive for mode 3: "draw where the stencil is not 0" fails everywhere on a freshly cleared buffer,
so the emitter vanishes outright. `StencilRef` therefore uses -1 for absent, and a test mode with an
unresolved reference draws unmasked. The corpus has real examples - `Flash_Around` is `mode=3 ref=-
refId=0x422f0a3f`.

**ORDERING is a live dependency.** A tester only sees a mask if its writer already drew, and draw order
is authored `pass` (M174 1.3). The mechanism is verified, but that Riot's authored passes always place
writers first is NOT verified - the sampled mode-3 emitters sit at passes from -5,000 to +15, so a system
whose writer sits later than its tester would mask against an empty buffer. If a stencil effect looks
wrong in practice, relative `pass` is the first thing to check.

**The FBO clear needed fixing too.** The viewport cleared colour and depth but not stencil, so last
frame's mask would persist - which with masking live reads as particles flickering rather than as an
obviously wrong buffer. `ClearStencil` also restores the write mask to 0xFF rather than 0, because a zero
write mask silently prevents the NEXT frame's `glClear` from clearing the plane at all.

**The FBO change rippled.** The viewport depth attachment moved from `DEPTH_COMPONENT24` to
`DEPTH24_STENCIL8`, and `glBlitFramebuffer` requires matching depth formats - so M175's soft-particle
capture texture had to move to the same combined format. Sampling a `DEPTH24_STENCIL8` texture returns
the depth component, which is all that path reads, and the soft-particle checks still match `smoothstep`
to within 0.002 afterwards.

### 2.5c M183 beam findings

Corpus: 239 WADs, 45,931 bins, **15,416** `VfxPrimitiveBeam` emitters (the M177 figures came from a
25-WAD sample and are superseded). Payload split: `mBeam` only 9,195 · `mMesh` only 2,859 · both 2,257 ·
neither 1,105. `mBeam` total **11,452**, `mMesh` total **5,116**.

**There is no beam shader, and that is measured.** A substring search for `beam` — and for `trail` — over
every path in `ShaderCache.dx11.wad.client` returns **zero** hits, and the union of all 29 `#define` names
across the 140 shipped shaders contains nothing beam, ribbon or segment related. `quad_vs` consumes an
already-expanded **world-space** POSITION0 and performs no billboard expansion, so a CPU-tessellated
ribbon is the same vertex stream a billboard quad produces. Riot ships no beam shader precisely because
the beam is CPU geometry.

**The tiling slot is Y for a beam, X for a trail.** Of 9,834 authored `mBirthTilingSize` vectors, Y is
non-zero on **96.6%** and X on **7.3%** — the reverse of `VfxTrailDefinition`. Reusing the trail reader,
which is the obvious move given the shared field name, would have zeroed 96.6% of beams and substituted a
500-unit fallback. X is kept as a secondary (290 beams have X as their only non-zero component); Z is
ignored, being only ever a small integer {1,2,3,30,100} and never a world length.

**INFERRED — the tiling sign split.** Positive = world units per repeat, negative = a repeat COUNT over
the whole beam. The negative cluster is {−0.5 ×21, −1 ×1275, −2 ×18, −3 ×9}; −0.5 is only meaningful as a
count.

**INFERRED — one ribbon per emitter, not per particle.** The offsets are per-emitter constants, so N live
particles would draw N coincident ribbons; with `DepthMask(false)` and the additive blend most beams
author, that is an N× brightness multiplier.

**No target bound follows Riot's own pattern.** `Augment_LaserEyes_Beam` (target-bound, offset `(0,0,20)`)
has a sibling literally named `_Long_FakeDir` carrying `(0,0,-1250)` — with no target, the authored offset
alone defines the far end. Only with neither target nor offset does a fixed 500-unit fallback apply, and
that is flagged in code as an editor substitute. The map viewport and Particle Editor bind no dummy and
take this path automatically.

**Mesh-carrying beams keep the mesh path — a DECISION, not a measurement.** All 609 beam emitters with a
`reflectionDefinition` also carry `mMesh` (100%, zero exceptions), and REFLECTIVE is a mesh-only define,
so mesh-before-beam preserves the M174 (1.5) behaviour those depend on.

**Parsed but not acted on**, each with the reason recorded in source: `mSegments` (a straight beam with N
segments is geometrically identical to one, and nothing in the payload bends or perturbs it);
`mAnimatedColorWithDistance` (time axis in **world units**, not normalised life — 248 of 767 curves exceed
1, clustering at 200/400/1400/1600/2400 — and per-vertex vs per-beam sampling is unresolved); `mMode` and
`mTrailMode` (both 1 in 100% of instances, so there is no second value to branch to).

**Two pre-existing bugs fixed in passing.** `EnsureTrailProgram` ended with `BindVertexArray(0)`, and
since setup runs inside the emitter loop, every early return in a ribbon path left VAO 0 bound so the
NEXT emitter drew with no attribute arrays. And neither the trail nor the mesh path called
`ApplyStencil`, so a mode-2/3 emitter earlier in pass order silently masked them.

### 2.x M184: the four remaining blockers, attacked with measurement

**The cross-cutting result: Riot's bin writer omits default-valued properties.** Corpus-wide there are
**970 distinct (class, bool-field) pairs and not one ships both polarities**, while six Vfx bools ship
*only* `false`. Both polarities are representable, so a field written only as `true` must default to
`false`. This retroactively fixes the reading of every "true in 100%" row in this document.

**2.10 sampler state - RESOLVED.** The enum came off Riot's own NAMED shared samplers in
`assets/shaders/shareddata.bin`: `Wrap_No_Mip` / `CharacterWrap` / `EnvironmentWrap` write `0`, and the
sampler called `Mirror` writes `2`. So **0 = Wrap, 2 = Mirror** (measured), **1 = Clamp** (elimination
plus usage). It is Unity's `TextureWrapMode` ordering, **not** `D3D11_TEXTURE_ADDRESS_MODE` - `0` is not
legal in the D3D enum and is authored 75,451 times, which falsifies that hypothesis outright. Line 237's
"ordering UNKNOWN" is now stale. Shipped: the palette clamp (7,266 of 9,969 palette structs author no
mode), via a **sampler object** because `ViewportControl` shares one GL texture per decoded image across
five slots and 268 textures are used under more than one mode - per-texture state cannot express it.
Remaining: full per-emitter sampler binding across all units.

**2.11 backface culling - data settled, winding NOT; culling stays off.** `disableBackfaceCull` is true in
all 358,113 occurrences, so by the omit-defaults rule absent means culling ENABLED (171,153 mesh
emitters). Parsed and carried, but **not applied**: enabling it with `FrontFace=CW` made the M178 fresnel
sphere read a full rim at every radius - the signature of keeping faces whose normals point away - and
regressed four passing checks, while a second probe using an occluding wall disagreed. Two contradictory
measurements mean the question is open, and getting it backwards inverts 171k emitters. Needs a real Riot
`.scb` checked in the app rather than a probe-generated sphere.

**2.13 bloom - the VFX half is RETIRED (measured non-issue).** All 11 pixel shaders under
`particlesystem/` declare exactly **one** `SV_Target`, and `quad_ps`'s define pool has no BLOOM. Riot
extracts bloom at material-write time into MRT slot 1; the default particle shader never writes it, and
no `Vfx*DefinitionData` field carries bloom data. The "22% of emitters are glow-named" rationale is
measured false - League VFX brightness comes from additive/screen blending, not a post-process. The
map-side bloom is real but is an HDR + MRT + blur-chain architecture milestone with four unsourced
quantities (`BLOOM_INTENSITY_SCALE`, the screen-vs-additive composite choice, buffer resolution/`UVStep`,
mip depth), so it is re-scoped out of tier 2 rather than left open. Banked for whoever does it: the 7-tap
sigma~1.0 Gaussian (0.383103 / 0.241843 / 0.060626 / 0.00598), the 15-tap sigma~1.63, the CoD-AW 13-tap
downsample, the 3x3 tent upsample, the screen composite `out = 1 - (1-scene)*(1-bloom)`, and
`MapSunProperties.useBloom` (169 occurrences, always true, therefore default false).

**2.15 Linger - reduces to one product question.** The finite-lifetime hypothesis is **refuted**: across
1,490,205 emitters the Linger group shows zero enrichment for a finite `lifetime`, and 19.9% have no
`lifetime` at all. The trigger is an **external stop**, proved by construction -
`AurelionSol_Skin*_W_Buff` emitter `Body` has `particleLifetime = -1`, no `lifetime`, `isSingleParticle`
and a constant opaque colour, so nothing in the data can ever end it; its only fade is the Linger curve,
because Astral Flight is a gameplay-toggled buff. **The question:** should the Particle Editor preview
gain a Stop action (and should a looping preview auto-stop each cycle so the linger phase is visible)?
Until a stop event exists the stage is unobservable in the editor, so everything else about it is moot.

### 2.15b M185: the Linger stage ships

The preview gained a **Stop** action, which is what the stage was waiting for. Riot's shutdown curves are
triggered by an external stop - a buff dropping, an ult ending - and a looping preview never produces
one, so the stage was unobservable and the data had nothing to hang off.

**The trigger is measured.** The lifetime-expiry hypothesis is refuted: across 1,490,205 emitters the
Linger group shows no enrichment for a finite `lifetime` (80.1% vs 82.0% corpus-wide), and 4,665 of
23,491 Linger emitters carry no `lifetime` at all. Proof by construction: `AurelionSol_Skin*_W_Buff`
emitter `Body` authors `particleLifetime = -1`, no `lifetime`, `isSingleParticle` and a constant opaque
colour - nothing in the data can ever end it, and its only fade is `SeparateLingerColor` alpha 1 -> 0,
because Astral Flight is a gameplay-toggled buff.

**`particleLinger` is the window, not a tail.** The "seconds added to a particle's life" reading is
arithmetically impossible for most of the corpus: median `particleLinger`/`particleLifetime` is 1.5, and
among single-particle emitters carrying both, `particleLinger` exceeds `particleLifetime` 62.3% of the
time and exceeds `lifetime + particleLifetime` 42.0% of the time.

**Inferred and marked:** the `Use*` flags are not read - not one ever ships `false` anywhere in the corpus
so their polarity is unreadable, and 6,851 `SeparateLingerColor` curves ship with no flag beside them
(if absence meant off, those authored fades would be dead data). A curve's presence is the enable. For the
46.4% of Linger structs with no duration, the particle's remaining natural life is the window.
`particleLingerType` is parsed and unused - it is largely independent of the Linger struct.

**Auto-stop (M186).** The preview now loops as run -> stop -> linger -> restart, on by default with a
toggle. The restart waits on the LAST PARTICLE having gone rather than on a timer, so a long linger window
is never cut off mid-fade; manual Stop takes precedence so auto-stop cannot restart underneath the user.
The cycle length (`VfxParticleSimulator.NaturalDuration`) is start + emit + the last particle's life, and
is an EDITOR measure — Riot systems are stopped by gameplay, so an endless emitter gets a 3-second
stand-in purely so it has a visible cycle. Nothing in the renderer or the data path reads it.

### 3. Missing editor controls

| # | Work | Files | Affected | Risk |
|---|---|---|---|---|
| 3.1 | ~~**Replace the hand-maintained name table with `HashDatabase.TryGetBinName`**~~ | — | — | **DONE in M187** — see 3.1b |
| 3.2 | ~~**Add `BinTreeI16` and `Optional<T>` unwrapping to the editable-type list**~~ | — | — | **DONE in M187** — see 3.2b |
| 3.3 | ~~**Expand nested structs into editable sub-rows**~~ | — | — | **DONE in M189** — see 3.3b |
| 3.4 | ~~**Show systems with zero emitters**~~ | — | — | **DONE in M188** — see 3.4b |
| 3.5 | ~~**Add a system-level property panel**~~ | — | — | **DONE in M188** — see 3.4b |
| 3.6 | ~~**Curve key editing**~~ | — | — | **DONE in M190** — see 3.6b |
| 3.7 | ~~**Flag fields that are editable but ignored**~~ | — | — | **DONE in M191** — see 3.7b; the row's own list was stale |
| 3.8 | **Map placement inspector** for the 10 unread `MapParticle` fields | `MapParticleExtractor.cs`, map inspector view | 29,811 placements | Depends on 5.2 for saving |

#### 3.1b / 3.2b — M187 result, and three corrections to the rows above

Measured by running `ParticleDocument.Parse` itself over 40 champion WADs (2,604 particle bins,
34,643 systems, 266,343 emitters, 5,380,294 property rows) — not by modelling the code against the
census, which is how the original 3.1/3.2 estimates were produced and how each of them went wrong.

| | built-in table | + `HashDatabase` |
|---|---|---|
| rows shown as a raw hex hash | 764,680 (14.2%) | **10,732 (0.2%)** |
| rows landing on the catch-all "Other" card | 1,030,960 (19.2%) | **277,651 (5.2%)** |

Exactly two field hashes are unknown to CDTB as well and still show as hex: `0xcb13aff1` (10,569 rows,
F32) and `0xd1ee8634` (163 rows, BitBool).

**Correction 1 — the dead-entry count was 11, not 8**, and the two entries the original count flagged
as dead were not. `Fnv1a` lowercases, so the table's `color` and `emitterPosition` hash identically to
CDTB's `Color` and `EmitterPosition`; comparing display spellings rather than hashes marked two live
entries dead and missed five genuinely dead ones. The 11 removed (`birthUvRotation0`, `emitOffset`,
`shape`, `uvScroll`, `uvRotation0`, `uvScale0`, `lingerColor`, `scaleBirthScaleByBoundObjectSize`,
`scaleEmitOffsetByBoundObjectSize`, `censorModifiers`, `emitterDefinitionDataFlags`) hash to nothing
that occurs on any emitter in the corpus. The remaining 46 stay as an offline fallback for when no
hash dictionary is loaded.

**Correction 2 — the module map had to become case-insensitive**, which item 3.1 did not anticipate.
Riot's own spelling varies (`Color`, `EmitterPosition`, `SpawnShape`, `TextureFlipV`, `Filtering`),
so an ordinal `switch` would have named those fields correctly and then dropped every one of them
into "Other". Naming them without also placing them would have moved rows from one useless state to
another.

**Correction 3 — 3.2's "+2.6M rows editable" was too high.** The real figure on this slice is 566,965
rows (10.5%): 358,327 unwrapped `Optional<T>` and 208,638 `I8`/`I16`/`I64`/`U64`/`Hash`. The estimate
counted census *occurrences* of those types, which includes types that were already editable.

**Found while measuring, and fixed in the same milestone:** a `Value*` struct that carries a curve but
no `constantValue` was falling through to the read-only default case as one opaque `Embedded` row,
which hid **the curve as well as the constant**. This is common rather than exotic, because Riot's
writer omits default-valued properties — `ValueColor` ships `constantValue` in only 63.0% of its
instances and `IntegratedValueFloat` in 34.1%. 365,806 rows were affected and 365,805 of them have
curve keys that the editor was not drawing. `Color`, the single most-used emitter field, was one of
them. The curve is now shown; the constant is left unwritten and unguessed, since what Riot's reader
substitutes for an absent `constantValue` is not established here.

VERIFIED: 5 round trips through `Serialize` → re-`Parse` on real champion bins — `lifetime`
(`Optional<F32>` → 7.25), `particleLinger` (`Optional<F32>` → 3.5), `pass` (`I16` → -17, negative on
purpose), `stencilRef` (`U8` → 5) and `EmitterPosition` (`Vector3`, editable before M187, as the
control that shows the harness can observe a success). The `Optional` cases are the load-bearing ones:
the row edits a property one level below the one the emitter struct holds, so a round trip is the only
thing that proves the write reaches the tree. Also asserted: 0 of 5,380,294 rows report dirty before
any edit.

#### 3.4b / 3.5b — M188 result

Same slice as 3.1b (40 champion WADs), measured through `ParticleDocument` itself.

**Emitterless systems are no longer dropped.** 2,124 of 36,767 systems (5.8%) carry no emitter, and the
editor hid every one of them; 70 bins consisted *only* of such systems and the editor refused to open
them at all. That is worse than an empty list, because the systems are named and referenced elsewhere —
a user searching for `Aatrox_Skin21_Recall_Beam` saw nothing and concluded the tool had failed to parse
it. 55.4% of them carry nothing but `particleName` and `particlePath`; they are stubs, and the tree now
says "no emitters (stub system)" rather than showing a count that reads like a failure.

**Systems have their own panel.** 99,591 rows where there were previously none — 99.3% editable —
shown as a `SYSTEM` card alongside the emitter cards, reusing the same rows and the same inspector.
Grouped as System (89,448 rows), Transform (5,077) and Audio (5,052).

| field | rows | |
|---|---|---|
| `particleName`, `particlePath` | 36,767 each | editable |
| `flags` | 15,516 | editable |
| `visibilityRadius` | 3,478 | editable |
| `soundOnCreateDefault` / `soundPersistentDefault` | 3,418 / 1,541 | editable |
| `overrideScaleCap` | 779 | editable (`Optional<F32>`, via M187) |
| `transform` | 437 | read-only — `Matrix44` has no editor kind |
| `assetRemappingTable`, `materialOverrideDefinitions` | 125, 114 | read-only containers (3.3 territory) |

14 rows still show as hex: `0xf97b1289`, which CDTB cannot name either.

VERIFIED: 3 system-level round trips through `Serialize` → re-`Parse` — `visibilityRadius` (500 →
1234.5), `particleName` (→ `ReyEngineProbe`) and `flags` (197 → 7). Also asserted: the two emitter
containers never leak into the system panel (they are the emitters, which the tree already shows), and
0 of 99,591 rows report dirty before any edit. The UI wiring is checked by Avalonia's compiled bindings
at build time — `x:DataType` is set on the card template, so a missing property fails the build — but
the new card has not been exercised in a running window.

#### 3.3b — M189 result

Same 40-WAD slice. Nested rows are **flattened with a depth** rather than made into a real tree: the
module card already renders a flat list, so depth-indenting it keeps selection, the inspector, dirty
tracking and the curve panel working unchanged, and the struct's own row stays as a read-only header.

| | rows |
|---|---|
| top-level (all there was before) | 5,380,294 |
| nested sub-rows | **1,141,174** (+21.2%) |
| ...editable | 872,533 (76.5%) |
| ...carrying a curve that was previously invisible | 135,572 |

Depths observed: 830,268 at depth 1, 256,686 at depth 2, 54,220 at depth 3. The expansion limit is 4,
and **nothing in the corpus reaches it** — the limit is a guard against pathological data, not something
the real data exercises, and a row that did hit it would say so rather than being silently truncated.

Sub-rows by definition struct (top of the list): `SpawnShape` 294,608 (77% editable), `primitive`
269,793 (60%), `alphaErosionDefinition` 223,367 (77%), `textureMult` 152,335 (98%),
`fieldCollectionDefinition` 43,905 (59%), `FlexShapeDefinition` 42,219 (100%), `reflectionDefinition`
30,431 (100%), `childParticleSetDefinition` 27,505 (37%), `softParticleParams` 17,344 (100%),
`paletteDefinition` 15,632 (100%), `distortionDefinition` 10,121 (100%), `Filtering` 7,115 (71%),
`Linger` 3,813 (81%), `materialOverrideDefinitions` 2,309 (66%). `CustomMaterial` expands to 132
sub-rows of which **none** are editable — its contents are all types the row model does not handle.

A `Value*`/`Integrated*` struct is deliberately treated as a **leaf, not a container**. Expanding one
would replace its single editable constant plus curve display with `constantValue`/`dynamics`/`times`/
`values` rows and lose the curve entirely — a regression dressed as more detail.

The same expansion runs on the system panel, where `assetRemappingTable` and `materialOverrideDefinitions`
were read-only containers for the same reason; system rows went 99,591 → 100,375.

VERIFIED: 11 checks. Nine round trips through `Serialize` → re-`Parse` on fields **inside** definition
structs — `alphaErosionDefinition.erosionMapName` (a texture path), `.erosionFeatherOut`,
`.erosionDriveCurve` (a *curve* row one level down), `softParticleParams.deltaIn`,
`reflectionDefinition.reflectionFresnel`, `paletteDefinition.paletteCount` and `.paletteTexture`,
`Linger.UseSeparateLingerColor`, `textureMult.texAddressModeMult` — plus two assertions that a container
header (`childParticleSetDefinition.childrenIdentifiers`) and a struct header (`primitive.mMesh`) are
**not** editable. Also asserted: 0 of 6,521,468 rows dirty before any edit. Four of the round-trip
targets initially reported "not found in the sampled WADs" because the inner field names were guessed
rather than read off the census; a not-found target proves nothing, so they were corrected to the real
names rather than left in the output looking like passes.

#### 3.6b — M190 result

The curve panel drew a picture of the keys and nothing more: `ReadDynamics` copied them into float
arrays, so anything the user did to them could never have reached the bin. Keys are now edited in a
list beside the plot — time and comma-separated components per key, plus add and delete — and every
mutation edits the live `dynamics` block.

Over 8 champion WADs: 206,762 rows carry a curve (464,769 keys), **100% of them now editable**.
By component count: 105,171 vector3, 53,460 colour/vector4, 37,700 float, 10,431 vector2.

**86,212 of those rows have a read-only *value* but editable *keys*.** Those are M187's curve-only
rows — Riot omitted `constantValue` because it was the default — and the two facts are genuinely
different: there is no constant to edit, but the curve is right there and is the whole content of the
field. Conflating them would have left the largest single group of curves unauthorable.

**`BinTreeContainer.Elements` is read-only through LeagueToolkit's public API**, so a key edit rebuilds
both containers and reassigns them on the `dynamics` struct, whose property dictionary *is* mutable.
That keeps the write path on supported API rather than casting the library's backing list.

Two deliberate limits: an inserted key is placed so times stay ascending (Riot's curves are read in
order, so an out-of-order key would evaluate as a discontinuity rather than as the point the user
placed), and the last remaining key cannot be deleted — an empty `dynamics` block is not the same
thing as no curve, and what Riot's reader does with one is not established here.

VERIFIED: 19 checks. Setting a key on a 1-component and on a 4-component curve, each asserting that
nothing is dirty beforehand, that the edit marks the document dirty, that the key count is unchanged,
that the new time and value survive `Serialize` → re-`Parse`, and that the component count and field
identity are preserved; then add (key count up, times still ascending after an out-of-order insert,
survives a round trip), delete (count down, survives a round trip), and the two guards — deleting the
last key and an out-of-range index are both refused. M187's 5, M188's 3 and M189's 11 round trips all
still pass.

The float curve the harness happened to land on was `erosionDriveCurve` — a depth-1 row inside
`alphaErosionDefinition` — and the colour curve was `Color`, a curve-only row. So M189's expansion and
M187's curve-only rescue both compose with key editing rather than merely coexisting with it.

#### 3.7b — M191 result, and why the row's own list was wrong

**The list in 3.7 was stale.** It named `alphaRef` as ignored; M174 implemented it. M175–M186 likewise
implemented erosion, soft particles, palette, force fields, trails, beams, children, reflection, stencil
and Linger. Re-implementing the row as written would have badged working fields as broken.

The set is therefore **derived, not written down**: `VfxPreviewCoverage` reflects over
`VfxSystemResolver`'s own hash constants. A second hand-maintained list would rot the first time a
milestone taught the resolver a new field, and it would rot *silently, in the direction of telling the
user something works when it does not*.

Measured against the census: the resolver reads **84.0%** of emitter field occurrences (74 fields);
**60 fields / 3,989,530 occurrences (16.0%)** are never read at all. In the editor that is 451,469 of
3,082,575 rows (14.6%), of which **432,649 are editable** — those are precisely the rows that let a user
change something and see nothing happen. The badge is inherited by a struct's sub-rows: a struct the
preview never reads makes every field inside it equally invisible.

Largest badged fields: `bindWeight`, `isUniformScale`, `miscRenderFlags`, `importance`,
`disableBackfaceCull`, `isGroundLayer`, `particleIsLocalOrientation`, `isLocalOrientation`,
`depthBiasFactors`, `useNavmeshMask`, `FlexShapeDefinition`, `meshRenderFlags`.

A second, **hand-maintained** category covers fields the resolver *does* read but the renderer then
does nothing with: `particleLingerType` (M185 — measured as largely independent of the Linger struct)
and `disableBackfaceCull` (M182/M183 — parsed, not applied, because two probes disagreed on the winding
of Riot's mesh primitives). Proving "parsed but unused" mechanically needs real dataflow analysis; an
attempt at it here produced obvious false positives (`TexturePath`, `Disabled`), so the list is limited
to cases verified directly in the source and deliberately under-claims.

The failure direction is chosen on purpose. If reflection returns nothing the class shows no badges at
all, and the set includes class hashes and other non-field constants, which can only ever cause a field
to be treated as read. Both mean a *missing* badge, never a false one.

VERIFIED: 41 checks. 28 fields the renderer demonstrably consumes assert as read (`rate`, `Color`,
`SpawnShape`, `alphaErosionDefinition`, `Linger`, `stencilMode`, `primitive`, …), 8 measured-unread
fields assert as unread, both parsed-but-unused fields assert as read-yet-badged, sub-row inheritance
holds across 3,082,575 rows, and the badge is neither empty nor universal. M187's 5, M188's 3, M189's 11
and M190's 19 checks all still pass.

The first attempt at the coverage set filtered on `IsInitOnly` and so dropped every `const uint`,
including `F_spawnShape` — it would have badged `SpawnShape` (632,283 rows, 51.8% of emitters) as
ignored when the resolver reads it. Caught by spot-checking a suspicious entry against the source
before shipping, not by the harness.

#### 3.7c — M192: three defects in the above, found by adversarially reviewing the tier-4 plan

An adversarial review of item 4.1's design refuted it, and in doing so found that **M191's badge was
already asserting the opposite of the truth on 698,251 measured occurrences** — before tier 4 added
anything. All three defects share one root cause: *"the resolver reads it" is not "the preview renders
it."*

**A. Four fields are parsed and then consumed only by the map path — or by nothing at all.**
`VfxSystemResolver` is shared with the map placement pipeline. `soundOnCreateDefault`,
`soundPersistentDefault` and `visibilityRadius` are read into the model and used solely by
`MapPlaceableExtractor.cs:149,150,157`; the particle preview has no audio and no distance culling and
never touches them. `emitterLinger` (82,304 occurrences) is parsed at `VfxSystemResolver.cs:437` into
`VfxSystemDefinition.cs:135` and read by **nothing anywhere in `src/`**. All four rendered unbadged,
i.e. the editor claimed the edit was visible. They are now in the explicit `NotRendered` map.

**B. The lookup never ran for nested rows.** `IgnoredNote` was called only at depth 0 and sub-rows
inherited the parent's answer verbatim, so a field inside a struct the resolver *does* read was never
asked about. Measured: **657,785 occurrences across 46 nested (owner, field) pairs** falsely claimed to
be visible — including all six Linger `Use*` flags, which `VfxSystemResolver.cs:553-562` states *in
capitals* that it deliberately does not read. Now every row with a field identity of its own gets its
own answer. Inheritance survives but is **one-directional**: a badged parent still badges its children,
because a struct the preview ignores makes everything inside it equally invisible; a container element
(`[0]`) has no field identity and can only inherit. Both rules fail towards showing the badge.

**C. It failed open.** `Collect()` caught reflection failures and returned an empty set, commented "no
set means no badges, never a wrong badge". That had the safe direction exactly backwards: under this
badge's semantics *the absence of a badge is the claim that the edit is visible*, so one reflection
failure asserted that all 3,989,530 unread occurrences render. It now fails **closed** — unknown means
badged, with a note saying so.

Effect on the editor: badged emitter rows 451,469 → **536,697** (17.4%), of which nested 22,626 →
**97,021**; badged system rows 0 → **12,604** on those three fields.

VERIFIED: 52 checks. The four map-path/no-consumer fields assert as parsed-yet-badged; a nonsense hash
is badged (fail-closed); 22 fields the renderer demonstrably consumes stay unbadged, including nested
ones (`erosionMapName`, `erosionDriveCurve`, `deltaIn`, `reflectionFresnel`, `paletteCount`); the ten
previously-unbadged nested fields are now badged on every one of their rows (`erosionMapAddressMode`
5,621/5,621, `uvScaleMult` 9,237/9,237, `birthUVOffsetMult` 7,536/7,536, `mSubmeshesToDraw` 4,165/4,165,
and each Linger `Use*` flag); one-directional inheritance holds across 3,082,575 rows; and
`particleName` stays unbadged on all 17,307 of its rows. Tier 3's 38 round-trip checks still pass.

**One M191 check had to be deleted rather than kept green.** It asserted "a struct's badge is inherited
by every one of its sub-rows" — which is the bug, written down as a test. It failed immediately on the
fix, naming the case: `textureMult` is read, its sub-row `texAddressModeMult` is not. Replaced with the
one-directional rule.

**Not fixed, and recorded as a known limit:** the badge is per *field*, but the resolver drops some
structs per *instance* — `ReadAlphaErosion` returns null unless both `erosionMapName` and
`erosionDriveCurve` are present (`VfxSystemResolver.cs:726`), and the same shape appears at `:492`,
`:749`, `:772`, `:512`, `:574`, `:634`. On such an instance every sub-row still says it is visible.
Field-coverage percentages suggest roughly 1.2% of erosion structs, but the census is per-field, not
per-instance, so the true rate is **not measured** and no fix is attempted on a guess.

### 4. Missing parsing support

| # | Work | File | Note |
|---|---|---|---|
| 4.1 | ~~Add the unread emitter field hashes to `VfxSystemResolver`~~ | — | — | **DONE in M193** — see 4.1b. The row said ~94; it is **60** |
| 4.2 | ~~Delete the dead `F_shape` lookup~~ | — | **ALREADY DONE in M174** (commit 2d4c9da); `VfxSystemResolver.cs:75` is now the comment recording it. Row retired, no work remained |
| 4.3 | ~~Parse the 21 unread system fields~~ | — | — | **DONE in M194** — see 4.3b. "67.7% pure scale, so it is a sizing fix" is **wrong twice** |
| 4.4 | ~~Parse the 10 unread `MapParticle` fields~~ | — | — | **DONE in M195** — see 4.4b. The high-value field is `VisibilityController`, not `eyeCandy` |
| 4.5 | ~~Load the two never-loaded map VFX sources~~ | — | — | **DONE in M197** — see 4.5b. There are **three** sources, not two, and **18,569** systems, not 5,995 |
| 4.6 | ~~Parse `MapPointLightType` and `MapLightingVolume`~~ | — | — | **DONE in M196** — see 4.6b. "Affects how all lit VFX read" is **unsupported**, and the point lights cannot be positioned |

#### 4.1b / 4.2b — M193 result

**4.2 needed no work at all.** M174 deleted `F_shape` in commit 2d4c9da and left the reason in place at
`VfxSystemResolver.cs:75`. Re-verified: `Fnv1a("shape")` = `0x9dc3d926` occurs 0 times on emitters *and*
0 times on any other class in the census. The removal was behaviour-neutral — it was a `??` fallback that
could never fire. Note for anyone acting on the old row: do **not** grep for `F_shape` and delete
`F_shapeRadius` / `F_shapeHeight` / `F_shapeSize`; those are live and parse the sphere/cylinder/box spawn
volumes.

**4.1's "~94 unread fields" is stale — the real figure is 60.** The 94 was correct when the report was
written (the resolver knew 77 hash constants then); M174–M186 taught it 104 more, and at HEAD it knows 181
and leaves 60 unread. The headline at the top of this report is stale the same way: **74 of 134** emitter
fields are read, **84.0%** of property occurrences, not 40/134 and 72.5%.

**All three magic hashes were correct** and are now named, each verified by recomputing FNV-1a-lowercase
and cross-checked against the census's independent CDTB naming: `0x3bf0b4ed` = `SpawnShape`, `0x0d89732d`
= `mMesh`, `0x90595a15` = `mMeshSkeletonName`. `F_spawnShape` also stopped being a `const`, which removes
the `IsInitOnly` reflection trap that M191 fell into.

**43 of the 60 are now parked** in `VfxEmitterExtras` — raw values, every member nullable, no
interpretation applied to anything whose meaning is not established (`miscRenderFlags` 484,286,
`depthBiasFactors` 183,823 and `uvMode` 35,694 are all still UNKNOWN and are stored as-is). Measured on
20 champion WADs: 119,161 of 125,345 emitters (95.1%) carry at least one, led by `bindWeight` 71,563,
`isUniformScale` 70,451, `miscRenderFlags` 46,273, `importance` 34,506, `depthBiasFactors` 20,215.

17 were deliberately **not** parked: `meshRenderFlags` (0 in 100% of 112,727) and `colorRenderFlags`
(1 in 100% of 7,240) carry no information; `0xcb13aff1` and `0xd1ee8634` cannot be named by CDTB or by us;
the eight `flex*` value structs were retired in M179; and `emissionSurfaceDefinition`'s sub-field and both
payload classes are unresolved, so there is nothing inside it to model.

**The badge design that made this safe.** The parked hashes are declared in `VfxParkedEmitterFields`, *not*
as constants on `VfxSystemResolver`. `VfxPreviewCoverage` reflects over the resolver's constants to decide
what the preview reads, so declaring them there would have silently un-badged all 43 fields — the exact
defect M192 had to fix. Keeping them in their own table means parking a field badges it automatically:
there is no naming convention to remember, and no way to park a value while telling the user it renders.
When a renderer stage starts consuming one, deleting its entry is the single required edit, and forgetting
it produces a spurious badge — the harmless direction.

VERIFIED: 23 checks. The three named hashes still equal their old literals; all 43 parked fields are
badged and none is a resolver constant; a parked field's note is textually distinct from a never-read
field's ("no renderer stage consumes it" vs "does not read this field"), because "we read it and do
nothing" and "we never look at it" are different promises; 10 rendered controls stay unbadged; the parked
values populate from real bins with counts matching the census exactly; and an emitter authoring none of
them gets `null` rather than a record full of nulls. M192's 52 and tier 3's 38 checks still pass.

#### 4.3b — M194 result, and why `transform` is not a sizing fix

The count is right: exactly **21** system fields are unread. (A hash-only method says 23, but two of those
are the emitter containers, which `ParseSystem` consumes structurally by scanning for the emitter class
rather than by field hash.) All 18 nameable ones are now parked in `VfxSystemExtras`, led by `flags`
(74,589), `overrideScaleCap` (6,361), `transform` (2,400) and `buildUpTime` (1,147). The two containers
record only a COUNT — `assetRemappingTable` (983) and `materialOverrideDefinitions` (336) — because a
count is honest where an empty list would imply we had read their contents.

**The rationale on the row is wrong twice, and `transform` is parsed but deliberately NOT applied.**

*First*, pure scale is not 67.7%. Independently re-derived here over the 145 authored transforms in a
20-WAD slice: **identity 35.9%, pure scale 33.8%**, with translation 21.4% and rotation 17.2%. The
adversarial pass measured the whole champion corpus and got the same shape — identity 38.0%, pure scale
29.2–29.5%. Note that identity + pure scale ≈ **69.7%** here and ≈ 67.5% corpus-wide: *that* is where the
report's number came from. It was the complement of (rotation OR translation), which silently counts every
identity matrix as a scale.

*Second, and decisively: the engine cannot express a size change through this channel at all.* Verified in
source:
- Sprite size is `p.BirthSize.X * scaleMul.X` / `.Y * scaleMul.Y` (`VfxParticleSimulator.cs:787-790`) with
  **no world-transform term**, and the placement basis is re-normalised by `SafeNormal`, which strips
  scale. A pure-scale matrix would spread the spawn cloud and speed up velocities while leaving every
  sprite exactly the same size.
- `VfxParticleSimulator.SetWorldTransform` **replaces** `_worldTransform` (`:322`) and is re-issued every
  frame from `ViewportControl.cs:1598` (bone-attached) and `:1008` (travelling missiles), carrying nothing
  forward. Anything composed in at build time survives until the first animated frame — and missiles are
  exactly the cohort the report's own worked example, `Aatrox_Skin37_W_mis`, belongs to.

So applying it would be a no-op that looks like a feature on the very cases the row cites. It is parsed
and kept for a later milestone that fixes the re-anchor path first.

**The matrix convention is proven, not assumed.** For 520/520 translation-bearing systems the 16 raw
floats on disk match `M11..M44` in sequential order and 0/520 match the transposed order, with floats
3/7/11 zero and float 15 one — so floats 12/13/14 are the translation, which is what
`Matrix4x4.Translation` returns. Independently, 29,805 of 29,809 map particle placements have a 4th-row
magnitude > 1 and **0 of 29,809** have a 4th-column one. Transposing does not manufacture the 67.7%
either: it yields 0 translations at all, which is absurd for map placements. There is also no
double-apply risk — `VfxSystemDefinition` had no `Transform` member at all until now, and 0 of the 424 map
placements that link a transform-carrying system duplicate it.

VERIFIED: 5 further checks on top of M193's 23. All 18 parked system fields are badged and none is a
resolver constant; transforms parse; pure scale measures 33.8%, well under the claimed 67.7%; and the
identity matrices that figure absorbed are real (52 of 145). One harness bug was caught and fixed rather
than shipped: an exact `== Matrix4x4.Identity` test reported **zero** identity matrices while 40 of 145
fell through every bucket — they were identity with float noise, so all the buckets had to agree on one
epsilon.

#### 4.4b / 4.6b — M195 and M196 results

**4.4 (M195).** All ten dropped `MapParticle` fields are read; every count reproduces over the 8 shipping
map WADs — 29,811 placements, `eyeCandy` 13,165, `AllDimensions` 4,990, `startDisabled` 1,370,
`Transitional` 1,159, `colorModulate` 172, `visibilityMode` 163, `AttachToCamera` 7, `quality` 3.

*The row named the wrong field first.* The one that changes the viewport is `VisibilityController` (4,237
Map11 placements), not `eyeCandy`. ReyEngine already owns `MapVisibilityResolver` and **meshes already use
it**; particles did not, so those placements stayed visible under every baron/dragon selection while the
geometry around them switched. `IsParticleVisible` now takes the same path.

Everything else is parsed for display and gates nothing. `eyeCandy` is believed to be a quality cull but
nothing measured predicts which placements carry it — ~3,600 Map11 placements without it are ordinary
decoration, so consuming it would hide 13,163 placements (61% of the base SR bins) on a guess.
`AllDimensions` is TFT/Arena-only with no meaning in a single-instance viewport. `quality` stays a raw
integer (a bitmask reading fits 80 of 81 samples — not enough). All five write-only-true bools are
nullable: absent is the executable's default, not `false`. New `HasVisibilityFlags` separates authored data
from ReyEngine's permissive 255 substitute, which is **never** authored (0 of 12,845) — measured 12,845
authored, 16,966 substituted, and the 255 count matches the substituted count exactly.

**4.6 (M196) — the item bundled two things, and only one can ever render.**

`MapPointLightType` is real (788 objects in 139 bins) but has **no position field**: its whole schema is
`lightColor`, `radius`, `Impact`, `castStaticShadows`, `HdrScale`. The placeable records that would carry
its transform ship as **null pointers** (one bin measured 205/205 null; 85,335 of 151,457 placeable values
game-wide, 56.3%, are stripped this way), and nothing references the 401 distinct entry hashes. It is
therefore parsed for inspection only, and any UI showing it must say why it cannot be placed — otherwise
it reads as a ReyEngine bug rather than a Riot build-stripping fact. Measured: 182 of 788 omit `radius`,
10 omit `lightColor`, `Impact` appears 328 times and is only ever 1, `HdrScale` 8 times. None of those
three meanings is established and none is interpreted.

`MapLightingVolume` is implementable: 172 across 150 bins, real placeables with their own `Matrix44`. The
valuable part is not "region override" — **146 of 172 carry a `lightMapColorScale` (usually 2.0) higher
than their bin's global sun**, which ReyEngine renders at 1.0, i.e. up to half as bright as authored. A
field a volume omits inherits the bin's global sun rather than a neutral default; 8 volumes rely on that,
and defaulting to `Vector4.One` instead would render them blown-out white.

Two corrections to the row: "Affects how all lit VFX read on modern maps" is **unsupported** —
`VfxParticleRenderer` declares no lighting uniform and the only lighting-adjacent emitter field in the
census is `doesCastShadow`, which nothing consumes; these affect map *geometry* lighting. And **Map11 has
zero lighting volumes**, so the row's baron-pit/base/brush example cannot be about them — the affected maps
are Map22 (159), Map30 (9) and Map33 (4).

**M207 applied the volumes, by removing the question rather than answering it.** The extent convention is
**not resolvable from the bins** — two attempts failed and both are recorded here so nobody repeats them.
A containment test (does the box hold the bin's placements?) cannot discriminate, because the larger
reading contains more points *by construction*. And the struct carries **no bound at all**: its 22 fields
are `transform`, `name`, `mVisibilityFlags` and 19 lighting parameters, so M196's "centre (2000,2000) vs
boundsMax (4000,4000)" cannot have come from it.

So `MapLighting.EffectiveSun` uses a volume's lighting for the whole scene when a bin has **exactly one**,
and leaves multi-volume bins on the global sun. With one volume there is nothing to choose *between*, so
the box could only decide whether to fall back to a global sun that is dimmer than the volume — which is
the error already shipping. Measured: **128 of 150** volume-bearing bins have exactly one, **110** of them
now render brighter (largest **3.00×**), and all **22** multi-volume bins are verifiably untouched.

The residual assumption is stated in the source: a lone volume covers wherever the camera goes. That is
not proven — it is preferred only because the status quo is *known* wrong for those scenes.

VERIFIED: 7 checks for 4.4 and 6 for 4.6, all against the real shipping map WADs. For 4.6: both classes
parse; 146 of 172 volumes are brighter than their global; omitted fields inherit the global sun; and Map11
has zero volumes.

#### 4.4c — M203: colorModulate is applied

M195 parsed the placement tint but did not render it. It does now, and the symptom it fixes is measurable:
of the 172 placements carrying a `colorModulate`, **none is identity**, and **16 of the 29 (bin, system)
pairs** that use one carried several visibly distinct tints that all drew the same.
`TFT_Set6_ZaunCity_GlowIdle` alone ships **14** distinct tints.

**A multiply is the safe reading, and it is measured rather than assumed.** All 172 values lie in [0, 1]
with **no component above 1.0**, so the field can only darken or fade - which is what a modulate does. A
value above 1 would have argued for a scale instead; none exists. The operator is still an inference from
the name plus that range, not proof against the game, and the source says so.

**One insertion point covers everything.** The tint is applied where the instance buffer is filled, because
the billboard, ribbon (trail/beam) and mesh-particle paths all read their colour back out of that same
buffer - so a single multiply tints all three. It is applied before the `particleColorTexture` gradient, so
the gradient modulates the tinted colour rather than overwriting it.

VERIFIED: 5 tests. An untinted placement is unchanged; a real shipped tint (`Srs_Ray_Light_Maps_Bloom1`,
`(1, 0.97, 0.84, 0.6)`) multiplies every channel; an alpha-only tint - the most common shape, 72 of 172 -
leaves RGB alone; two placements of the same system with different tints now render differently; and an
identity tint is indistinguishable from none. Checked for bite: disabling the multiply turns 62 pass into
59 pass / 3 fail.

#### 4.5b — M197 result

A map document parses VFX from exactly one bin: the mapgeo's sibling `<name>.materials.bin`. Everything
else in the map WADs was unreachable in the app. Measured over all 8 shipping map WADs:

| source | bins | systems | emitters |
|---|---|---|---|
| `*.materials.bin` — **loaded today** | 199 | 10,023 | 49,689 |
| other `*.bin` — never loaded | 1,858 | 15,765 | 121,179 |
| `maps/modespecificdata/*.bin` — never loaded | 5 | 766 | 6,819 |
| **extensionless paths** — never loaded | 207 | 2,038 | 12,238 |

**The row undercounted by 3×.** It named two sources totalling ~5,995 systems; there are three and
**18,569**. The third is the extensionless one — `maps/modespecificdata/<mode>/<spell>/loadable` and
`maps/shipping/map30/spells/*/loadable` — which matters beyond the count, because a menu entry gated on a
`.bin` suffix (the way `CanOpenInMapBinEditor` is) would have hidden exactly those 207 bins.
`CanOpenInParticleEditor` therefore gates on nothing but "an asset is selected", and a non-VFX pick falls
through to the existing *"contains no VFX systems"* warning — cheap and clear. That also avoids touching
the extensionless type sniff in `OnSelectedNodeChanged`, whose blast radius covers the Map Bin Editor and
the Content Browser's type filters.

"They have no placements" is confirmed, so this is a browse-and-open surface rather than scene playback:
an **Open in Particle Editor** entry on both Content Browser context menus.

Two things the browse case forced, both measured rather than assumed:
- **The parse moved off the UI thread.** `data/maps/shipping/map22/map22.bin` measures **1,991 ms** through
  `ParticleDocument.Parse` + `VfxSystemResolver.ExtractAll`; map30 519 ms, kiwi.bin 224 ms. On the
  dispatcher that is a hard freeze. `ParticleEditorViewModel.Parse` is now a static, off-thread half and
  `Load` takes the pre-parsed result.
- **A system filter.** map22.bin alone yields **2,579** systems into what was a flat, unsearchable
  `ListBox`. The filter keeps the current selection when it survives, so typing does not reset the preview.

VERIFIED: 4 checks. There really are systems the app never loads (18,569); extensionless bins carry VFX
(207 bins / 2,038 systems), which is what makes the ungated menu predicate necessary; at least one bin is
slow enough to justify the off-thread parse (1,991 ms); and the biggest bins are far too large for an
unfiltered list (2,579 systems). Tier 3's 38 checks and tier 4's M192/M193/M194/M195/M196 checks all
still pass.

The UI wiring is checked by Avalonia's compiled bindings at build time; the new menu entry and filter box
have not been exercised in a running window.

### 5. Missing serialization support

| # | Work | File | Note |
|---|---|---|---|
| 5.1 | ~~Preserve original property order~~ | — | — | **RETIRED in M200** — see 5.1b. Property order **never** changes (0 of 1,599); the diff noise is *object* order, caused by the library's writer |
| 5.2 | ~~**`MapParticleWriter` can persist only the 64-byte transform**~~ | — | — | **DONE in M199** — see 5.2b. The silent duplicate-matrix failure is real and reproduced: **84 of 99** groups |
| 5.3 | ~~Regression test for Riot's typo fields~~ | — | — | **DONE in M198** — the repo's first test project, 53 tests |

#### 5.2b / 5.3b — M198 and M199 results

**5.3 (M198).** There was no test project at all, so this adds one: `tests/ReyEngine.Formats.Tests`
(xunit, in `ReyEngine.slnx`), **53 tests**, no game install needed. The typo tests are synthetic — build a
tree, write it, read it back — so they cannot be weakened by whatever a particular install contains.

The premise is asserted first, because everything else depends on it: `palleteSrcMixColor` hashes to
`0x143f06d1` and the "corrected" `paletteSrcMixColor` to `0x8af10fa7`. They are **different keys**, so
fixing Riot's spelling does not tidy the file, it writes a field the game never looks up. Same for
`TextureMultFilpU/V` vs `…FlipU/V`. The tests assert the typo fields survive a round trip with their
values intact, that the corrected spellings are *not* invented, and that a second round trip is
byte-identical to the first (which is what makes repeated Save Override runs safe).

M193's recon recorded that the coverage invariants lived only in a scratchpad harness, so those are now
33 repo tests as well. **The suite was checked for bite:** temporarily unparking one field
(`miscRenderFlags`) turns 45 pass into 44 pass / 1 fail.

**5.2 (M199) — the silent failure is real, and worse than the row implies.**

Measured over the 8 shipping map WADs: **1,450 of 30,628 placements, in 365 groups, share an identical
transform with another placement in the same bin.** The old writer located placements by scanning the raw
file for those exact 64 bytes, so it patched whichever occurrence came first. Exercised on 99 real
duplicate groups, **the old writer moved the wrong placement in 84 of them.** (In the other 15 the first
byte match happened to be the intended one — which is precisely what makes the bug silent: it is
order-dependent, not consistent.) The app already knew something was wrong here and worked around it by
*refusing to save* moved sounds derived from particle systems, because those share the particle's
transform bytes.

Identity is now `(container object hash, items map key)`. That pair is proven unique: across 151,457
items every key is a `BinTreeHash` and there are **zero** duplicate keys within a container. It also
addresses the 2 placements that carry no transform and were previously unreachable, and it is what makes
re-tinting, re-linking, renaming and removing possible at all — none of which has a byte signature.

**The rewrite is not trusted.** A tree rewrite touches the whole file where the old patcher touched 64
bytes, so `MapPlaceableWriter` re-parses its own output and compares it object-by-object and
property-by-property against the original, refusing to save unless the only differences are the requested
ones. Containers holding an edited placement are compared entry-by-entry, so one edit does not excuse its
neighbours. Across 199 real bins there were **0 refusals**.

VERIFIED: 8 synthetic tests plus 4 checks against real bins. The headline synthetic case reproduces the
duplicate-matrix shape and asserts the twin does not move; others cover the transform-less placement,
re-tint/re-link/rename, remove-drops-only-the-target, unrelated objects surviving, an unknown placement
being refused rather than silently ignored, and a partial batch reporting what it missed. On real data:
199 bins round-trip with 0 self-check refusals, and 99 of 99 duplicate-matrix groups now edit correctly.

**Completed in M202.** `MapSoundPlacement` now carries an id too, so standalone sound moves go through
the same identity-addressed writer and `MapParticleWriter` has been **deleted** — nothing addresses a
placement by its transform bytes any more. That mattered: `MapAudio` placements are part of the same
30,628 of which 1,450 share a matrix, so a standalone sound move was exposed to the identical bug.
Measured: **817 of 817** standalone sounds are addressable by identity.

**M204 gave the verbs an editor surface.** The PARTICLE inspector card now carries Rename, Tint
(`colorModulate` as `r, g, b, a`) and a "Mark for deletion" toggle beside the existing position fields, and
Save to Mod persists all of them through `MapPlaceableWriter`. Two deliberate behaviours: an edit is only
an edit if it *differs* from what was authored (re-typing the existing name does not dirty the map), and a
deletion stops the placement rendering immediately while touching nothing on disk until save, so it stays
undoable. A tint that is not four numbers is flagged in red rather than silently dropped, and the render
falls back to the authored tint rather than blanking.

**M205 added the re-link picker**, so all four verbs are now reachable. A map bin defines a few hundred
systems, so it is a filter box plus a dropdown rather than a bare list — the shape M197 already used for
the Particle Editor's SYSTEMS panel. Only systems with a visual emitter are offered: linking a placement
to one that draws nothing would look like a broken save. Choosing the placement's *current* system clears
the edit instead of recording a no-op re-link, which matters because the picker starts pointed at it.

**M206 added the clone**, which completes the writer: a placement can now be duplicated as well as moved,
renamed, re-tinted, re-linked and removed. The source is deep-cloned, so the copy keeps every field
ReyEngine does not model, and the edit's other verbs then apply on top of the copy.

The item key is minted rather than derived, which is safe because it is measured to be **opaque**: across
151,457 shipped items, **0** keys match FNV-1a of the placement's name in either casing, and 92,079 items
have no name at all — so nothing reconstructs a key from content, and a newly added placement is by
definition not yet referenced by anything. `NewItemKey` mixes the source key and walks forward on
collision, so it is stable (saving twice lands on the same key) and never overwrites a neighbour. Cloning
onto an occupied key, or from a source that does not exist, is refused rather than attempted.

The writer's self-check had to learn about additions: a clone's key is legitimately absent from the
"before" tree, so it is whitelisted per container — and any *other* new key still trips the refusal.

#### 5.1b — M200: the optional item was chasing the wrong thing

Re-measured over **1,599** champion particle bins (the row's 393 was a smaller slice):

| | |
|---|---|
| byte-identical after `Serialize` | 454 (28.4%) |
| differ, same length | 1,143 |
| differ, different length | 2 |
| **property order differs** | **0** |
| object order differs | 1,145 |

**The row blamed property order; property order never changes.** The diff noise is entirely *object*
order: LeagueToolkit's `BinTree.Write` emits objects sorted ascending by path hash, and Riot's files are
unsorted. Verified directly — for every differing bin the input is unsorted, the output is sorted, and the
object set is identical. The 28.4% that come back byte-identical are the ones Riot happened to write in
sorted order already.

The first clause of the row stands: `Serialize` **is** structurally lossless. Total corpus size moves by
32 bytes across 178 MB, no bin fails to serialize, and the object set, properties and values all survive.

**Retired rather than implemented.** Fixing it would mean replacing the library's bin writer with our own
to control object emission order, and the benefit is purely cosmetic: nothing downstream reads that order.
`BinThreeWayMerge` — the one consumer that compares a mod against Riot's original — keys every lookup by
object hash (`Objects.Keys`, `TryGetValue`, `ContainsKey`), so it is order-independent by construction.
Owning a format writer to tidy a diff is not a trade worth making.

Four tests pin what actually matters instead: a round trip preserves every object and property
semantically, property order within each object is preserved (the measured 0), writing is a fixed point so
repeated Save Override runs do not churn the file, and the writer's hash-sorted object order is asserted
explicitly — so a contributor who sees a large diff against Riot's original knows it is this and not
corruption.

### 6. Rare / low priority

**Audited in M201.** Most of this list was already shipped by the time tier 4 finished, and its one open
question now has an answer. Nothing here was taken on faith: every row was re-counted across all 239 WADs
(45,931 bins).

| item | count | state |
|---|---|---|
| `mIsPoseAfterimage`, `hudAnchorPositionFromWorldProjection`, `hudLayerDimension`, `ClockToUse`, `selfIllumination` | 303 / 74 / 10 / 7 / 1 | **DONE (M194)** — parsed into `VfxSystemExtras`, all badged |
| `colorblindVisibility`, `censorModulateValue`, `WriteAlphaOnly`, `offsetLifeScalingSymmetryMode`, `offsetLifetimeScaling`, `doesLifetimeScale` | 154 / 6,048 / 249 / 54 / 38 / 7 | **DONE (M193)** — parsed into `VfxEmitterExtras`, all badged |
| `MapParticle.quality` | 3 | **DONE (M195)** — raw integer, no bitmask reading applied |
| `MapParticle.TextureOverride` | 16 | **DONE (M201)** — see below |
| the `flex*` value family | ~4,100 | **RETIRED (M179)** — measured to be a no-op at reference size; also needs runtime IDs that live in game code |
| `.troybin` legacy particles | 1,189 files | **RETIRED (M201)** — orphaned; see below |
| `MapClouds` | 1 | not implemented — one object in the entire install |
| `MapSkinColorizationPostEffect` | 3 | not implemented |
| the Viktor-only 8-class modifier tree | 16 systems | not implemented |

**The `.troybin` question is answered: nothing references them.** The report left open "whether any live
champion still references them". Measured across all 45,931 PROP bins in 239 WADs: **0 references by
string and 0 by WAD hash link** to any of the 1,189 `.troybin` chunks. The first pass only checked strings,
which would have been an overclaim — a bin can reference an asset by hash — so the hash route was checked
separately against the full set of `.troybin` chunk hashes. Both are zero. The caveat that remains: this
covers `.bin` files only, so a reference from game code or a non-bin format would not show up. On the
evidence available the files are orphaned, and implementing a legacy format nothing points at is not worth
doing.

**`TextureOverride` (M201)** was the one genuine gap left, and it is now read. Layout verified directly
rather than assumed: a struct of class `0x115b5460` carrying `TextureToOverride` (`Hash`, 16 of 16) and
`TacticianIndex` (`U32`, 14 of 16, values 1–4 observed). All 16 occurrences are TFT/Arena, which with the
index reads as a per-tactician skin swap — **inferred from the field names and index values, not
confirmed**. Because CDTB cannot name class `0x115b5460`, the read is guarded on *field presence* rather
than on the class hash: if a future hash list resolves that class differently, a class check would silently
stop matching while a field check keeps working.

**Not implemented, and why.** `MapClouds` is a single object in the entire install and
`MapSkinColorizationPostEffect` is three; the Viktor modifier tree is 16 systems behind an 8-class
structure. Each would be bespoke parsing serving a handful of objects, with no editor surface to put them
on. They are recorded here with their real counts so the decision can be revisited, not silently dropped.

One correction to the audit itself: the first pass counted `MapClouds`/`MapParticle` by walking top-level
objects only and reported 0 for both — wrong, because placements live inside `MapPlaceableContainer.items`
as structs. Counting nested structs too gives `MapParticle` 29,815, which agrees with M195's independent
29,811 over the shipping maps.

---

## Evidence and confidence

**Rule applied to every row above.** Each finding cites either a real `.bin` path together with the WAD it came from, or a real `ReyEngine` source `file:line`. Frequencies are counts printed by a harness over the stated corpus, never estimates. Anything read off a field name rather than measured is marked **[inferred]** or **UNKNOWN** at the point of use. Field and class names are FNV-1a-confirmed against the observed hash wherever the report states a name — a confirmed name is not the same as confirmed semantics, and the two are kept separate throughout.

**What the harnesses did.** Six independent investigations, each with its own throwaway `net10.0` console project under the session scratchpad referencing `ReyEngine.Core` + `ReyEngine.Formats`, parsing with LeagueToolkit `BinTree` and resolving names against `data\hashes\merged_hashes.cache` plus the CommunityDragon shards. Two of them additionally ran ReyEngine's own `VfxSystemResolver.ExtractAll` over the whole corpus so the "what does ReyEngine see" numbers are the real reader's output, not a model of it. Nothing was written to the Riot directory or anywhere in `D:\GamingTools\ReyEngine` except this file.

**This report self-corrected.** An adversarial verifier re-ran every load-bearing claim on independently written harnesses, frequently on deliberately disjoint corpora. Where a verifier refuted a claim, the corrected version is what appears above. The substantive corrections:

- **"blendMode's default is 0."** Refuted. The premise — that BIN omits default-valued properties — is false: `alphaRef` is explicitly written as `0` on 391,078 emitters and `pass` as `-1` on 37,697. Absence means the author never touched the control. The default is UNKNOWN, and the absent-mode texture authoring actually favours ReyEngine's current additive assumption. This also invalidates the tempting "one-valued BitBool ⇒ opposite default" inference across the whole schema.
- **Spawn-shape loss was undercounted and the class census was incomplete.** 216,819 volume shapes carry no `emitOffset` (not 182,911 with a dimension parameter), and the two most common shape classes — `0xee39916f` (308,070) and `VfxShapeLegacy` (185,017), 69.4% of all shapes — were missing from the original enumeration and are in fact handled correctly. Quoted radius/size ranges were 3-6 orders of magnitude too small.
- **`AttachedMesh` mesh loss was ~10× overstated.** Only 1,678 of 56,309 `AttachedMesh` `mMesh` structs name a mesh file; 54,690 hold a host-model submesh mask instead — a different and separate gap.
- **`VfxPrimitiveRay` is an empty struct** in 100% of its 58,434 instances, so "rays are velocity-stretched trail geometry" has no data behind it. 431 emitters with primitive class `0x8df5fcf7` are not billboarded — they are not drawn at all (no texture).
- **Editor read-only classification was wrong for a dozen fields.** `BinTreeEmbedded` subclasses `BinTreeStruct`, so `emitterPosition` and every other `Value*` constant is editable; `alphaRef`, `miscRenderFlags`, `depthBiasFactors`, `disableBackfaceCull`, the stencil fields and `renderPhaseOverride` are all editable too — measured live on real bins. The correct and worse framing is "editable but ignored by the renderer". Only `pass` (I16), `Optional<T>` and non-`Value` structs are genuinely read-only.
- **`isLocalOrientation` is `false` in 100% of its 301,827 occurrences**, so it was cited as an affected population when it is the opposite — an explicit opt-out of behaviour ReyEngine already provides.
- **`isUniformScale`'s affected population** drops from 657,069 to ≤634,368 because `VfxParticleSimulator.cs:243` already collapses the `Y == 0` subset.
- **`depthBiasFactors.X` is not a −1/0 selector** (−1 79.0%, 0 10.7%, +1 6.9%, plus a continuous tail), so the slope-scaled-bias reading has no support. The `glPolygonOffset` mapping is a name-based guess, and because depth writes are off the symptom is a depth-*test* difference, not z-fighting.
- **`BloomThreshold` governs an opt-in `CustomShaderDef`**, not the default particle path — 36 of 94,979 sampled emitters carry a `CustomMaterial` at all and none links a particle shader. The missing bloom is a frame-level post-process gap, correctly identified but wrongly attributed.
- **`buildUpTime` is not a `MapAction` companion** — the action occurs twice in the entire map corpus while the field occurs 1,147 times in champion bins where no `MapAction` class exists. Semantics UNKNOWN.
- **`overrideScaleCap`** is −1 (a sentinel) in 38.9% of cases with the remainder at world-unit magnitudes, so the "scale cap" reading is unverified.
- **`VfxSystemDefinitionData.transform`** is 67.7% pure scale, so ignoring it mis-sizes rather than mispositions most affected systems.
- **`childrenProbability` is not a probability** — 478 of 2,262 constants exceed 1.0.
- **`IntegratedValue*` `constantValue` is present**, not absent, on 81.5% / 87.9% / 31.1% of the four fields concerned; what is universal is `dynamics`.
- **`flags` is not the highest-frequency unread property overall** — `MapParticle.eyeCandy` (13,165 placements, 44.2%) is unread and more than twice as frequent within its own population — and it has 81 distinct values, not "25+". Values ≥2000 are concentrated in, but not exclusive to, shipping bins.
- **Champion VFX bin counts were swapped**: 8,189 under `data/characters/*/skins/*.bin` and 3,147 under `*_multi_skins_*`, not the reverse. Chunk-path resolution is 99.9995%, not 100%.
- **`pass` has 2,913 distinct values**, not "48+" — a 60× understatement of how much ordering information is being discarded.
- **`texDiv` sub-1 values are a mesh phenomenon** (16,202 of 20,504) and the mesh path already honours them; the quad-path damage is 2,293 negative + 390 sub-1, not the ~33,663 originally claimed.
- **`simpleEmitterDefinitionData` does not define `LegacySimple` membership** — 844/869 simple-list emitters have it, 25 do not, and 56 `LegacySimple` emitters live in the complex list, which means `UseTextureAspect` (`VfxSystemResolver.cs:275`) is set wrongly for 81 emitters.
- **`ultbook.bin` ships in two WADs**, so the modespecificdata totals are 766 systems / 6,819 emitters as shipped versus 750 / 6,696 de-duplicated by path.
- One hash-labelling convention error was corrected: `VfxEmitterDefinitionData`'s class hash is **0x09cde442**; hexes previously shown after that class name in several places were the *field* hashes of the property being described.

**Where two investigations disagreed on a count**, this report uses the figure that a verifier reproduced independently. One residual discrepancy is recorded rather than papered over: emitter property occurrences were counted as 28,569,369 by one harness (independently reproduced by its verifier) and 30,443,432 by another over the same corpus. The former is used throughout. Bin counts occasionally differ by ~0.1% between sections because some sections count bin *chunks* and others distinct bin *paths* — 11 champion VFX bin paths ship in two WADs each.
