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

## What this sets up

1. ~~Probability tables → `VfxAnimatedFloatVariableData.probabilityTables`~~ — done, M521.
2. ~~`p-xscale1..4` → the scale-over-life curve~~ — done, M521; section 10 confirmed as 4 × u8 tenths
   against Riot's own `scale0`.
3. Force fields → `VfxFieldCollectionDefinitionData`, one class per kind. Read since M520, not yet
   written by the converter.
4. Widening the parity harness beyond the eight fields it covers today — colour curves, texture paths,
   flipbook state, the spawn shape.
