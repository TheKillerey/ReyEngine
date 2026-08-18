# The .troybin Rosetta stone (M520)

Everything here is measured against files on disk. Where a claim is an inference rather than a
measurement it says so, and where a reading is still unresolved it is left unread rather than guessed.

## Where the evidence came from

Two sources that the earlier troybin work (M422–M429) did not have:

1. **A text/binary twin.** `K:\LeagueSandbox\League_Sandbox_Client\DATA\Particles` ships
   `FireTorch_Simple.troy` (authored text, 287 lines) *and* `FireTorch_Simple.troybin` (the shipped
   binary, 2,170 bytes) — the same effect in both forms. It is the only such pair in 5,851 files.
2. **Riot's own conversion.** Riot re-authored the legacy particles as modern
   `VfxSystemDefinitionData` in the map bins. **201 distinct systems** exist under the same name in
   both the legacy corpus and a shipped bin, so each is a worked example of the conversion, done by
   the people who defined both formats.

The same folder also holds **5,851** `.troybin` files against the 1,189 in `DATA.wad.client` — a
corpus roughly five times larger than the one the format was originally cracked on. All 5,851 parse,
consuming the body to the exact final byte.

## The text form

```ini
[Flame]
e-life=-1
e-rate=100
field-drag-1="FlameDrag"
p-texture="flames03.tga"
p-texdiv=4.0 4.0
...
[FlameDrag]
f-drag=0.1
f-pos=0.0 50.0 0.0
f-radius=200
...
[System]
GroupPart1="FlameSparks"
GroupPart1Importance="Low"
GroupPart1Type="Simple"
GroupPart2="Flame"
...
SimulateEveryFrame=1
```

A section is either an **emitter**, a **force field**, or the single `[System]` section. The binary
key is `sdbm65599(lowercase(section + "*" + field))`, which is why the field constants in
`TroyFields` all begin with `*`.

### Self-verification

The text is itself a decompiler's output — seven of its lines are `;UNKNOWN_HASH <decimal>`, which
means whoever produced it had a partial dictionary. That does not weaken it, because every name it
*does* give is checkable: a wrong name cannot hash onto a key that exists in the binary.

| | |
|---|---|
| assignments in the text | 257 |
| entries in the binary | 257 |
| text names landing on a real key | **250** |
| text lines marked `;UNKNOWN_HASH` | 7 |

250 + 7 = 257. The dictionary is complete for this file and every name in it is confirmed.

## `[System]` — the prefix that was magic

`TroyHash.EmitterNameKey` used to carry `0xAE671AB7` as an unexplained seed, documented as "a prefix
string whose literal spelling is still unrecovered".

```
sdbm("*grouppart", sdbm("system")) == 0xAE671AB7
```

Exact. The section is literally `System` and the emitter list is `System*GroupPart{n}`, so the
constant is now derived from the two strings rather than pasted in. Beside each entry the same
section carries `GroupPart{n}Type` (the quality tier — `Simple` / `High` / `Low` / `Basic` /
`Medium`, which the game uses to drop emitters on low settings) and sometimes
`GroupPart{n}Importance`.

Corpus: 20,354 emitters, **11,628** of them carrying a quality tier that was previously invisible.

## Force fields

An emitter references a field by name — `field-drag-1="SparksDrag"` — and the field lives in its own
section, which never appears in the group list. **The kind comes from the reference, not the
section**: `f-accel` is a vec3 of acceleration on an acceleration field and a scalar pull strength on
an attraction field, so a reader that finds the section without knowing why it was named would type
half its values wrong.

All five legacy kinds survive one-for-one into the modern engine, which is the strongest single piece
of evidence that `VfxFieldCollectionDefinitionData` is a descendant of this subsystem rather than a
redesign:

| legacy reference | modern class | modern list |
|---|---|---|
| `field-accel-{n}` | `VfxFieldAccelerationDefinitionData` | `fieldAccelerationDefinitions` |
| `field-attract-{n}` | `VfxFieldAttractionDefinitionData` | `fieldAttractionDefinitions` |
| `field-drag-{n}` | `VfxFieldDragDefinitionData` | `fieldDragDefinitions` |
| `field-orbit-{n}` | `VfxFieldOrbitalDefinitionData` | `fieldOrbitalDefinitions` |
| `field-noise-{n}` | `VfxFieldNoiseDefinitionData` | `fieldNoiseDefinitions` |

Corpus: **3,485** force fields on 3,219 emitters — Drag 1,367, Acceleration 1,322, Orbital 302,
Noise 268, Attraction 226.

## Riot's conversion, field for field

`DestroyedBuilding_idle` exists as a legacy `.troybin` and as a shipped
`VfxSystemDefinitionData` in `Map12.wad.client:data/maps/mapgeometry/map12/jade.materials.bin`.
Its `sparkburst3` emitter, both ways:

| legacy troybin | Riot's modern bin |
|---|---|
| `[sparkburst3]` (GroupPart5) | `emitterName: string = "sparkburst3"` |
| `e-rate = 10` | `rate.constantValue: f32 = 10` |
| `e-ratep1 = (0, 0)`, `e-ratep2 = (0.98, 0)`, `e-ratep3 = (1, 2)` | `rate.dynamics.probabilityTables[0]`: `keyTimes = 0, 0.98, 1`; `keyValues = 0, 0, 2` |
| `p-life = 1` | `particleLifetime.constantValue: f32 = 1` |
| `p-lifep1 = (0, 0.5)`, `p-lifep2 = (0.9, 0.6)`, `p-lifep3 = (1, 1)` | `particleLifetime.dynamics.probabilityTables[0]`: `keyTimes = 0, 0.9, 1`; `keyValues = 0.5, 0.6, 1` |
| `p-vel = (0, 800, 0)` | `birthVelocity.constantValue: vec3 = { 0, 800, 0 }` |
| `[SparksDrag] f-radius = 1000`, `f-drag = 6` | `fieldDragDefinitions[0]`: `radius.constantValue = 1000`, `strength.constantValue = 6` |

Every value matches with no rescaling. The mapping is mechanical, which is what makes a parity
harness against all 201 pairs worth building.

## Section encodings

The body is a presence mask followed by one section per set bit (M422). Typing is **value-adaptive**:
the same field lands in a different section depending on its value, so `e-rate` alone appears in
sections 1, 2, 3, 4, 5 and 12 across the corpus. A reader that declines a section does not read that
field badly — it reads it as *absent*, which everything downstream treats as "the author wanted the
default".

| bit | width | holds | entries in corpus |
|---|---|---|---|
| 0 | 4 | never used | 0 |
| 1 | 4 | f32 | 8,405 |
| 2 | 1 | u8 **tenths** | 14,516 |
| 3 | 2 | i16 | 6,967 |
| 4 | 1 | u8 raw | 44,751 |
| 5 | 0 | one bit | 106,158 |
| 6 | 3 | 3 × u8 tenths | 40,716 |
| 7 | 12 | 3 × f32 | 41,079 |
| 8 | 2 | 2 × u8 tenths | 161,698 |
| 9 | 8 | 2 × f32 | 44,204 |
| 10 | 4 | 4 × u8 tenths | 21,796 |
| 11 | 16 | 4 × f32 | 6,086 |
| 12 | 2 | string-block offset | 353,442 |

**Section 10 is 4 × u8 tenths, not a float.** Raw `0a0a0a0a` reads as `(1.0, 1.0, 1.0, 1.0)`;
`0a020202` as `(1.0, 0.2, 0.2, 0.2)`; `06090a0a` as `(0.6, 0.9, 1.0, 1.0)`. As f32 the same bytes are
absurd denormals (6.6e-33), and 88% of components fall in [0, 1] under the tenths reading. It is
dominated by `p-xscale{n}` (9,985 of 21,796), so an xscale key looks like `(time, x, y, z)`.

### Section 12 is not only strings

It is the largest section, and 42% of every key in the corpus lands there. Its payloads:

| payload | count |
|---|---|
| text (asset paths, section names) | 115,605 |
| **one number as text** | **101,530** |
| two numbers | 55,403 |
| five numbers (colour keys) | 23,391 |
| four numbers | 15,523 |
| three numbers | 10,370 |
| offset points INTO a string, not at its start | 31,598 |

`TryGetScalar` did not accept section 12, so every one of those 101,530 single numbers read as
absent. `[Flame] e-rate=100` and `[FlameAttract] f-radius=200` are both in that group — verified
against the text twin. Fixed in M520, with a deliberate restriction: **exactly one token**, because
`"0.0 30"` is a probability key and taking its first number as the value is how a curve silently
collapses into a constant.

Measured effect over all 5,851 files:

| | before | after |
|---|---|---|
| scalar keys readable | 165,031 | **257,554** |
| emitters with an emission rate | 10,148 | **18,904** |
| emitters with a particle lifetime | 11,625 | **18,549** |

## Coverage

Fraction of the corpus's 849,818 keys that resolve to a named field, discovering sections through
`System*GroupPart{n}` and then following each emitter's `field-*-{n}` references:

| dictionary | coverage |
|---|---|
| the field list before M520 | 31.1% |
| the recovered vocabulary | **58.6%** |

Treating *every* string in a file as a candidate section name reaches 65.7%, so roughly 7% of keys
sit on sections reachable by some route not yet identified — most likely reference field names beyond
the five kinds above. 404 files yield no sections at all through the GroupPart chain.

Those 31,598 are not corruption: an offset is allowed to point into the middle of a string, and
reading from there to the next NUL gives the suffix the writer meant. See the M521 section below.

## Open, and deliberately not guessed

- **`e-active`** is not the on/off flag it looks like. Present on only 270 emitters, **never** 0, and
  192 of the 270 hold something that is neither 0 nor 1. Exposed raw. Riot's `disabled: bool = true`
  on `sparkburst3` cannot be its counterpart — that emitter has no `e-active` at all.
- **Section 11's other users.** 4 × f32 is certain, but only ~1,350 of its 6,086 entries belong to a
  named field. Samples like `(255, 255, 255, 50)` and `(-1, -1, -1, -1)` look like RGBA with -1 as
  "unset", which would make it a colour field whose name is not yet known.
- **The remaining ~41% of keys**, whose field names are simply not in the dictionary yet.
- **Attraction toward another EMITTER.** `field-attract-1="Butterfly_Anim2"` names an emitter section
  rather than a field section, so there is no `f-accel` on it to read, yet Riot writes an
  `acceleration` (259 and 61 in the two observed cases). Where that number comes from is unresolved,
  so the property is left off for those fields rather than guessed.

## Converter parity (M521)

Running our converter over every paired legacy file and diffing the result against Riot's shipped
output, across the 199 paired systems and the 444 emitters matched by name:

| field | compared | agree |
|---|---|---|
| `rate` probability table | 36 | **100%** |
| `particleLifetime` probability table | 158 | **100%** |
| `birthVelocity` tables (X, Y) | 85 | **100%** |
| `birthScale0` table | 155 | **100%** |
| `birthVelocity` constants | 176 | **100%** |
| `birthScale0` constant | 285 | **100%** |
| `particleLifetime` constant | 395 | 98.5% |
| `rate` constant | 441 | 97.3% |
| `scale0` curve (separate run, 1,720 emitters) | 1,720 | **99.9%** |

The residue is not converter error. `runeTimeGlow`'s particle lifetime is 320 in the legacy file and
598 in the shipped one — Riot re-tuned it in the decade between. The single `scale0` disagreement is
0.9 against 0.99, which is the binary's own tenths quantisation.

Three findings came out of getting there:

- **`p-xscale` is a multiplier, not an enable flag.** It reads like a flag because the one file with a
  text twin has `p-xscale=1`. SRU_Lane_Motes has `(20,20,20)` against curve keys of 0.2/1/0.2, and
  Riot's `scale0` for it is 4/20/4. Applying it moved scale-curve agreement from 95.6% to 99.9%.
- **A string offset need not land on a string START.** The writer deduplicates by pointing into the
  middle of a string when the value it needs is a suffix of one already present:
  `SRU_DragonPit_WaterFall_01` stores `"0.000000 1.0 1.0 1.0"` at offset 389 and its `*e-rate` points
  at 406 — the trailing `"1.0"`, which is exactly Riot's converted rate. Resolving only against
  recorded starts left 31,598 references reading as absent. Fixing it moved rate-constant agreement
  from 87.5% to 97.3%.
- **Riot applies defaults its own source does not carry.** `particleLinger` is 10 on 224 emitters whose
  legacy file has no `p-linger` at all. Where the legacy file *does* carry one the mapping is exact
  (28 of 30), and a legacy 0 corresponds to Riot writing nothing (218 of 219) — so the field is
  converted when present and never invented when absent.

## Force-field conversion (M522)

A census of **5,138** shipped `VfxFieldCollectionDefinitionData` fixes every shape: the collection is a
POINTER on the emitter, each list a container of EMBEDDED elements, and the per-kind properties are:

| kind | modern property | wire | legacy source |
|---|---|---|---|
| Acceleration | `acceleration` | ValueVector3 | `f-accel` **as a vec3** |
| | `isLocalSpace` | bool | `f-localspace` |
| Attraction | `acceleration` | ValueFloat | `f-accel` **as a scalar** |
| | `radius` | ValueFloat | `f-radius` |
| | `Position` | ValueVector3 | `f-pos` |
| Drag | `strength` | ValueFloat | `f-drag` |
| | `radius` | ValueFloat | `f-radius` |
| | `Position` | ValueVector3 | `f-pos` |
| Orbital | `direction` | ValueVector3 | `f-direction` |
| | `isLocalSpace` | bool | `f-localspace` |
| Noise | `radius` | ValueFloat | `f-radius` |
| | `velocityDelta` | ValueFloat | `f-veldelta` |
| | `frequency` | ValueFloat | **1 / `f-period`** |
| | `axisFraction` | **plain vec3** | `f-axisfrac` |
| | `Position` | ValueVector3 | `f-pos` |

That `f-accel` is a vec3 on one kind and a scalar on another is independent confirmation of the
kind-comes-from-the-reference rule: the shipped classes type it both ways.

Three of Riot's rules, all measured:

- **`frequency` is the reciprocal of `f-period`.** Period 0.5 → frequency 2, 0.25 → 4, 0.4 → 2.5,
  exactly, every time. Copying the period across would make every noise field run at the wrong rate
  without looking broken.
- **A zero `Position` is omitted**, which is why only 222 of 848 shipped drag fields carry one.
- **A silent `f-axisfrac` becomes `(1,1,1)`** — 5 of 5 on the paired systems. This one *is* copied,
  unlike `particleLinger`'s 10, because it is the identity for a per-axis weighting and so cannot change
  how an effect renders; the 10 is a behavioural value and copying it would invent motion.

Converter agreement with Riot over the paired emitters that have fields: list length per kind 22/22 on
all five kinds, and every value at 100% — acceleration, isLocalSpace, radius, strength, direction,
frequency, velocityDelta, axisFraction and Position.

`*f-axisfrac` was recovered by guessing candidate names against the sdbm key on three noise sections
whose Riot-side value was known, and confirming all three: PolenNoise (2,1,0), ManaSnowNoise (1,1,0),
NoiseField1 (0,1,0).

## The editor side (M523)

Checked rather than assumed: the Particle Editor already rendered the force fields and the `scale0`
curve, because M189's nested-struct expansion covers any struct it does not recognise as a leaf. The
genuine gap was narrower and more important — **probability tables were invisible**.

A `Value*` struct is deliberately treated as a LEAF: one editable constant plus its over-life curve.
That is right for the constant and silently dropped `dynamics.probabilityTables`, which is the
per-particle randomisation. A rate of 10 with keys (0,0) (0.98,0) (1,2) is an emitter that idles and
then bursts; with the table hidden it reads as a steady trickle of 10.

Each non-empty table now gets its own row under its field — `spread` for a scalar, `spread X/Y/Z` for
a vector, labelled by POSITION because the container is read by index. The keys are shaped exactly like
a curve (two parallel float lists), so the M190 key editor drives them unchanged once the container
names are configurable: `times`/`values` for a curve, `keyTimes`/`keyValues` for a table. The preview
simulator applies these tables (M47), so an edit shows immediately rather than only on export.

### A converter bug the editor surfaced

Seeing `birthScale0` render `spread X`, `spread Y` and `spread Z` all populated exposed an M521 defect
that the parity harness had missed by only ever comparing table index 0.

`TroyProbability.ForAxis` fell back to the unlettered table for **every** axis, so a uniform legacy
spread was written to all three. Riot writes it to **X alone**. It is not cosmetic: three tables means
three INDEPENDENT rolls, so a particle authored to scale uniformly comes out 1.0 x 1.5 x 1.2.

Measured over the paired systems, comparing which axes carry a table at all:

| | before | after |
|---|---|---|
| `birthScale0` axis shape | 139/156 (89.1%) | **156/156** |
| `birthVelocity` axis shape | 69/69 | 69/69 |

The 17 disagreements were all `riot[K,-,-]` against `ours[K,K,K]` — every one the uniform case.

## The parity harness (M524)

`TroyConversionParity` walks the WHOLE property tree of a converted system against Riot's, rather than
checking an enumerated list of fields. That is the point: the enumerated version compared
`probabilityTables[0]` and never the other two, and missed a real defect for two milestones. A walk can
be wrong about what it finds — which shows in the report — but not about what it forgot to look at.

Emitters are matched by NAME, with duplicates paired one-to-one (DestroyedBuilding_idle names two of
its group parts `smoke`). Asset paths compare by FILE NAME, because the converter deliberately rehomes
legacy art under `ASSETS/Legacy` while Riot's lives elsewhere — by full path that is 226 differences
that are all policy; by filename, 223 of 228 agree, which is the number that says the emitter points at
the right art.

Over all 199 paired systems and 442 emitters: **10,051 property comparisons, 93.9% agreement.**

### What it caught immediately

| | |
|---|---|
| `lifetime` was the wrong WIRE TYPE | Riot ships `option[f32]` in 136 of 136; the converter wrote a `ValueFloat`, which the client reads as malformed. |
| `blendMode` was hardcoded to 1 | It *is* the legacy `*rendermode`, identity, in 324 of 325 paired emitters — and mode 0 is omitted rather than written. Right for 168 emitters, wrong for 157. |
| `pass` was read and never written | M520 decoded it; nothing emitted it. 188 emitters. |

Overall agreement went **86.2% → 93.9%** on those three alone.

### The work-list it produces

Fields Riot writes that the converter still does not, by emitter count — every one has a plausible
legacy source already in the reader, and none should be written until the mapping is measured the way
`frequency = 1/f-period` was:

| field | emitters | likely legacy source |
|---|---|---|
| `bindWeight` | 254 | `*p-bindtoemitter` |
| `birthRotation0` | 226 | `*p-quadrot` |
| `particleLinger` | 193 | Riot's own default (see above) |
| `birthUvScrollRate` | 169 | not yet identified |
| `isUniformScale` | 168 | not yet identified |
| `textureMult` | 156 | `*p-texture-mult` |
| `isSingleParticle` | 123 | not yet identified |
| `EmitterPosition` | 107 | not yet identified |

And the largest remaining disagreement is `Color`: 1,036 agreed against 456 differed. The colour curve
is still bound by the M46 string-scan heuristic — runs of five-token strings assigned to emitters
positionally — while section 12 holds 23,391 five-number entries that are keyed like everything else.
Binding colour by key is the single biggest fidelity win left.

## What this sets up

1. ~~Probability tables → `VfxAnimatedFloatVariableData.probabilityTables`~~ — done, M521.
2. ~~`p-xscale1..4` → the scale-over-life curve~~ — done, M521; section 10 confirmed as 4 × u8 tenths
   against Riot's own `scale0`.
3. ~~Force fields → `VfxFieldCollectionDefinitionData`~~ — done, M522.
4. ~~Widening the parity harness~~ — done, M524; it is a whole-tree walk now.
5. Binding the colour curve BY KEY instead of by the string-scan heuristic — the largest remaining
   disagreement, 456 differed against 1,036 agreed.
6. Working through the field work-list above, measuring each mapping before writing it.
