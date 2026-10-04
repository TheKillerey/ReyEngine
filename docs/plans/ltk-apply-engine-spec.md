# LTK game-data apply engine: port spec (for M817)

What a C# port of LeagueToolkit's game-data APPLY engine must reproduce to be byte-identical to the Rust one.
Researched 2026-10-03 from league-mod 219d84a (ltk_game_data 0.8.0, ltk_overlay, ltk_game_index), ltk_meta 0.8.6,
and LTK Manager v1.21.0 (1b1953a). It goes deeper than the format summary in `ltk-game-data-mods.md`, which it
assumes.

**Citation roots.**
- `gd/` = `.codex_tmp/league-mod/crates/ltk_game_data/src`
- `ov/` = `crates/ltk_overlay/src`
- `gi/` = `crates/ltk_game_index/src`
- `mp/` = `crates/ltk_mod_project/src`
- `lm/` = `~/.cargo/registry/src/index.crates.io-1949cf8c6b5b557f/ltk_meta-0.8.6/src`
- `lh/` = `ltk_hash-0.4.0/src` and `lio/` = `ltk_io_ext-0.4.7/src` (same registry)
- `mgr/` = `.codex_tmp/ltk-manager/crates/ltk-manager-core/src`

The normative prose is `league-mod/docs/design/game-data.md` §6. Everything below was checked against the code.

## 1. Entry point, phases, output

### 1.1 Signature

`apply(base, edits, read_override, read_entry, schema) -> Result<ApplyResult, Error>` (gd/apply/mod.rs:452-458).

- **Input is ONE module's `edits` list.** An `entries` module is lowered by the overlay into one synthetic
  `Edit {entries: {name: props}, links}` per entry per declaring chunk (ov/builder/game_data.rs:162-177).
- **`ApplyResult`:** `{bytes, dependencies, applied {records, objects, properties, links_added, links_removed},
  diagnostics}` (mod.rs:320-370). `changed()` is true when any count is non-zero.
- **Fatal errors only:**
  - base is not PROP/PTCH, or does not decode → `Bin`;
  - PROP v1 → `UnsupportedBase`;
  - header overflow → `DependencyOverflow`.
- Everything else is a diagnostic.

### 1.2 PROP pipeline (mod.rs:459-573), in order

1. If the bytes start with `PTCH`, go to the PTCH path (§5.3).
2. Mount the bytes; the version must be 2 or 3. Compute `body_offset = 12 + Σ(2+len)` over the ORIGINAL
   dependencies.
3. **Dedup the dependencies at load**, ASCII case-insensitive, keeping the FIRST spelling (476-478). This runs
   even with no link edits.
4. `resolve_references`: every ref of every edit is read once per distinct object hash, BEFORE any edit
   (394-430).
5. Per edit, in order:
   - a. **Override files** in listed order (each re-read even if listed twice), then their
     `OverrideRecordSkipped`.
   - b. **Object creation**, including each created object's `set`.
   - c. **Reporting:** creation skips, then the `set` diagnostics, then the entry edits (`entries::run`).
   - d. **Object removals.**
   - e. **`-links`:** removes ALL ASCII-case-insensitive matches; none removed → `LinkRemovalUnmatched`.
   - f. **`links`:** appended as written. A duplicate (ASCII-lowercase seen-set) is silently ignored and not
     counted.
6. **Output:**
   - If `records>0 || objects>0 || properties>0`: re-encode the whole tree as **PROP v3**.
   - Otherwise rewrite the header only: `base[..8]` keeps the magic and version (v2 stays v2), then the new
     dependency count and dependencies, then `base[body_offset..]` verbatim (559-566, 646-664).

### 1.3 Diagnostic order inside one apply

1. All `ReferenceUnreadable`.
2. Then per edit:
   1. override diagnostics, per file;
   2. creation `ObjectSkipped`;
   3. `set` diagnostics;
   4. entry diagnostics (a key's `SchemaFallback` precedes its skip);
   5. removal `ObjectSkipped`;
   6. `LinkRemovalUnmatched`.

### 1.4 Several modules on one target (ov/builder/game_data.rs:410-669)

**Module order.**
- Mods run from lowest to highest precedence.
- Per mod, active layers only: base first, then ascending priority, then natural name order (digit runs numeric,
  ties by bytes; mp/lib.rs:632-637, 694-731).
- Then module order, then edit order.

**Base bytes.**
- The highest-precedence mod's copy of the chunk.
- Else the game copy from the chunk's first holder.

**File precedence inside one mod** (ov/builder/metadata.rs:36-57, 117-158, 291-310).
- Layers are read in apply order. Each layer's WAD files are inserted into ONE map keyed by chunk path hash,
  whatever WAD folder they sit in. A later layer's copy therefore REPLACES an earlier one: the higher-priority
  layer wins over base.
- RAW files are collected after every layer, so they win over all of them.
- A GameData target's base is the mod's copy from this map before game-identical copies are filtered out.

**Layer activation** (ov/builder/mod.rs:254-264).
- Base is always active.
- Another layer is active when the mod's `enabled_layers` is None (the default) or names it.
- An inactive layer contributes no files, no GameData (game_data.rs:423) and no StringOverrides
  (strings.rs:257).

**Applications on one chunk.**
- Each application is one `apply` call over the bytes the previous one left.
- `changed()==false` discards the output and records `NoEffect`; the bytes stay, undeduped dependencies
  included. `Err` records `TargetSkipped`.
- Several `edits` in one module share one decode and one encode. Several modules mean several cycles. They
  are equivalent because the encode is canonical, except for the v2 header-only case.

### 1.5 Wire layout and the normalisations byte equality depends on

**Layout** (lm/tree/write.rs:37-56, tree/object.rs:128-143).

`"PROP" u32 3 | u32 nDeps {u16 len, utf8}* | u32 nObj | u32 class * nObj | per object: u32 size, u32 path, u16 nProps, {u32 name, u8 kind, value}*`

- Bool and Flag are 1 byte, 0 or 1.
- Integers and f32 are little-endian; f32 is bit-exact.
- Mtx44 is 16 f32 in ROW (wire) order. The JSON list order is the wire order (gd/apply/coerce.rs:153-156,
  lio/writer.rs:92-97).
- Color is RGBA u8. String is u16 len + UTF-8. Hash and Link are u32. File is u64.
- List / List2: `u8 item, u32 size, u32 count, items`.
- Option: `u8 item, u8 present, [item]`.
- Map: `u8 key, u8 val, u32 size, u32 count, (k v)*`.
- Pointer / Embed: `u32 class`; if class≠0 then `u32 size, u16 n, props`. **Class 0 is exactly 4 bytes, for
  Embed too.**

**Kind bytes** (property/kind.rs:21-53):

| Kind | Byte | Kind | Byte | Kind | Byte |
|---|---|---|---|---|---|
| None | 0 | U64 | 9 | Hash | 17 |
| Bool | 1 | F32 | 10 | File | 18 |
| I8 | 2 | Vec2 | 11 | List | 0x80 |
| U8 | 3 | Vec3 | 12 | List2 | 0x81 |
| I16 | 4 | Vec4 | 13 | Pointer | 0x82 |
| U16 | 5 | Mtx44 | 14 | Embed | 0x83 |
| I32 | 6 | Color | 15 | Link | 0x84 |
| U32 | 7 | String | 16 | Option | 0x85 |
| I64 | 8 | | | Map | 0x86 |
| | | | | Flag | 0x87 |

**Normalisations on decode then re-encode.**
- Duplicate object path hashes: the FIRST position keeps the LAST value and class; the object count shrinks.
  Duplicate properties in an object or struct behave the same way.
- Bool and Flag bytes of 2+ are written back as 1.
- Legacy kind numbering is rewritten in the current numbering.
- All size fields are recomputed.
- Bytes after the object table are dropped.
- Map entries keep their order, duplicates included.
- Invalid UTF-8, nested containers, or invalid map key kinds make the decode fail (`Err`).
- **Insertion order:** a new property is appended at the end of its object or struct; a replaced one keeps its
  slot. New objects are appended. `shift_remove` keeps the order of the rest.

## 2. Entry edits (gd/apply/entries.rs, address.rs)

### 2.1 Per entry (196-223)

- `Edit.entries` is keyed by spelling, so two spellings of one hash are two entries.
- An absent object gives `MissingObject` per edit, with the signed key as spelled.
- Otherwise:
  1. **Flatten every edit against the object as it stands before this entry's edits.**
  2. Group the leaves.
  3. Settle the groups in first-occurrence order.

### 2.2 Block expansion (227-279)

A value is descended into when it is a mapping that is neither a pin (one key that is a type name) nor a ref
(one key `ref` with a string value). Then `address::resolve(object, path)`:
- **Null pointer:** `NullPointer`, keyed `sign+path`.
- **Non-null struct or embed:**
  - With a sign other than set: `SignOnScalar`.
  - Otherwise each inner key is re-parsed with its own `+`/`-`. The joined path is `outer.inner`, and the leaf
    takes the inner sign.
  - An unparsable inner key gives `InvalidPath` (`outer.rawkey`).
  - **An empty `{}` on an existing struct produces nothing at all.**
- **Anything else:** the mapping is a leaf value.

### 2.3 Grouping

- **Identity** (97-105): the segments joined by `.`. Each segment is `{fieldhash:08x}{subscript}`:
  - the field hash honours hash-form (`0x` plus 8 hex), else FNV;
  - `[n]` renders as the canonical u32;
  - `{k}` renders as the KeyLiteral Display: number text kept as written, strings re-escaped
    `\" \\ \b \f \n \r \t`, other characters below 0x20 as `\u00xx`, whitespace inside braces dropped.
- So `mFoo`, `MFOO` and `0x<hash>` are one group, while `{"Key"}` and `{"key"}` are two.
- The group's path is the FIRST leaf's spelling.
- `set` is last-wins; removals and additions are appended in leaf order.
- **The operation order is fixed: set, then all removals, then all additions.**
- `first_sign` is Set if there is one; else Add if there are no removals; else Remove (142-151).

### 2.4 Typing (`locate`, 282-342)

**Parent.**
- A null pointer gives `NullPointer`.
- A struct or embed gives its ACTUAL class and properties.
- Anything else gives `CannotDescend`.
- With no parent: the object's class and properties.

**Last segment with a subscript:** the shape is that of the existing element. No schema is consulted and no
`SchemaFallback` is reported.

**Otherwise:**
1. `schema.expected(class, field)` (Schema typing). **It outranks a base value that disagrees.**
2. Else the base field's own shape (Base typing).
3. Else `schema.fallback` (Fallback typing).
4. Else `Untypable`.

A shape is {kind, key, item}, with no class.

### 2.5 Writing a group (372-427)

1. `current = base`. If there is a set, `current = coerce(set, shape, base)`; an error is reported under the Set
   sign.
2. If there are removals or additions, `Contained::of` (445-469):

   | Case | Result |
   |---|---|
   | Shape is not List, List2 or Map | `SignOnScalar` |
   | Current value is not a container | `SignOnScalar` |
   | Container shape differs from the site shape (e.g. List vs List2) | `TypeMismatch` |
   | Absent, with no removals | an empty container of the shape (`Untypable` if item or key is missing) |
   | Absent, with removals | `ContainerAbsent` |

3. **Removals** (510-575):
   - **List of Pointer/Embed: by integer index.**
     - The operand must be a list of integers; otherwise `KindMismatch`.
     - Each index must be `0 ≤ i < length`, otherwise `RemovalUnmatched`.
     - All indices are validated, then sorted, deduplicated and removed from the highest down.
   - **Other lists: by value.** `coerce(op, fullShape)` (pins and refs allowed), then for each element remove
     EVERY equal element. Nothing removed gives `RemovalUnmatched`, so `-tags: [a, a]` fails on the second
     `a`. Equality is IEEE for floats and exact for strings.
   - **Map: by key.**
     - The operand must be a list of String, Integer or Bool (`"true"`/`"false"`); a Float, null, mapping or
       ref gives `KindMismatch`.
     - Each key goes through the map-key rule (§3.5) and removes all `==` keys. Nothing removed gives
       `RemovalUnmatched`.
4. **Additions** (577-598): `coerce(op, fullShape)`. A list is extended. For a map, the first equal key is
   replaced in place, otherwise the entry is appended.
5. **PTCH, non-owned object:** a hash-form segment gives `HashFormPath`.
6. **`address::value_path`:** each segment becomes Field(hash) plus Index or Key(`MapKey::from_literal`). A
   prefix that is not a map gives `NotIndexable`; a key that does not convert gives `InvalidKey`.
7. **`patch_at`** (lm/path/resolve.rs:1021-1047):
   - **Last segment a field:** the parents must be non-null structs or embeds. An absent field is INSERTED
     (appended); a present one is `replace`d.
   - **Last segment a subscript:** resolve, then `replace`.
   - `replace` needs equal ValueShape: kind, item, key, and the class for Embed only (pointer class ignored).
     Otherwise `TypeMismatch`.
   - **No parent is ever created.** A missing intermediate gives `MissingProperty`. A missing last index or
     key gives `IndexOutOfRange` / `KeyNotFound`.
8. Success counts one property.

### 2.6 Path grammar and keys (ltk_meta)

**Grammar** (lm/path/parse.rs).
- A name is any characters except `. [ ] { } ( )` and control characters. Only one leading sign is stripped.
- `[idx]` is **strtol base 0**: decimal, `0x` hex, leading-`0` octal; no sign; must fit u32. Write plain
  decimal.
- `{key}` is a JSON scalar (string with JSON escapes, `true`/`false`, or a number kept as text), with
  whitespace padding allowed.

**`{key}` conversion** (`key_as`, resolve.rs:346-372):

| Key kind | Accepts | Conversion |
|---|---|---|
| Bool | `true`/`false` | as is |
| Integer kinds | Number | `text.parse::<T>()`; `1.0` and `1e3` fail, `-` fails for unsigned |
| F32 | Number | `text.parse::<f32>()` |
| String | String | as is |
| Hash | String | FNV |
| Hash | Number | `parse::<u32>()` |
| File | String | xxh64 |
| File | Number | `parse::<u64>()` |

**There is no `"0x"` escape inside `{}`**: a `0x` string is hashed as text.

**Matching** in ValuePath navigation is BITWISE: NaN equals NaN, and -0 differs from +0.

### 2.7 PropertySkipReason (mod.rs:121-175; ResolveErrorKind mapping 87-111)

| Reason | When |
|---|---|
| MissingObject | Entry object absent |
| MissingProperty | Absent intermediate field, or absent field before a subscript |
| NullPointer | Block on a null pointer, null parent, or walk through a null pointer |
| CannotDescend | `.name` on a non-struct |
| NotIndexable | Subscript on the wrong kind |
| IndexOutOfRange | List index past the end; option index other than 0 or option empty |
| InvalidKey | A key that does not convert |
| KeyNotFound | `{k}` matches no entry |
| TypeMismatch | Container shape mismatch, or the `patch_at` rule |
| InvalidPath | Unparsable block key, or a struct-pin `set` key that is not one plain segment |
| Untypable | No type available |
| UnknownClass | Pinned class not attested and `!has_class` |
| PinMismatch | §3.4 |
| SignOnScalar | A sign where no container is allowed |
| ContainerAbsent | `-` on an absent container |
| RemovalUnmatched | A removal matched nothing |
| KindMismatch | No coercion row, a ref shape mismatch, or removal operand errors |
| OutOfRange | An integer out of range |
| PrecisionLoss | An integer that is not exactly an f32 |
| ArityMismatch | Wrong element count |
| ReferenceMissingEntry | The ref's entry was not supplied |
| ReferenceUnresolved | The ref's path does not resolve |
| HashFormPath | A hash-form segment on a PTCH record |
| Unknown | Anything else |

### 2.8 SchemaFallback (345-369)

- At most one per group, with the unsigned group path.
- With Base or Fallback typing, it is reported before writing.
- With Schema typing, it is reported only when a struct pin's `set` field was typed by `fallback`.
- It precedes the key's skip. Never reported for subscripted paths or `locate` failures.

## 3. Values (gd/apply/coerce.rs, value.rs)

### 3.1 Dispatch (43-51)

1. **Ref**, a one-key `{ref: string}`:
   - The pre-read entry is looked up by object hash; a missing one gives `ReferenceMissingEntry`.
   - The path is resolved; any error gives `ReferenceUnresolved`.
   - The value is copied UNCOERCED, and `Shape::of(v)` must equal the shape (kind, key, item; embed class
     NOT compared here), otherwise `KindMismatch`.
2. **Pin**, a one-key mapping whose key is one of
   `bool i8 i16 i32 i64 u8 u16 u32 u64 f32 vec2 vec3 vec4 mtx44 rgba string hash file link flag option pointer embed`
   (case-sensitive).
3. **Otherwise bare**, recursing with no base for elements, map values and set fields.

### 3.2 Bare rows (118-243)

| Shape | Accepted values |
|---|---|
| None | Always `KindMismatch` |
| Bool | bool only |
| Flag | bool; integer 0/1; any other integer → OutOfRange |
| I8..U64 | Integer, range-checked → OutOfRange. Float (incl. 1.0), bool, string, null → KindMismatch |
| F32 | Float as f32: round-to-nearest-even, **overflow → ±inf, no error**. Integer only if exactly representable, else PrecisionLoss |
| Vec2/3/4, Mtx44 | List of f32 by the rule above. Element errors IN ORDER first, then count → ArityMismatch |
| Color | List of integers as u8. Element errors first (KindMismatch, OutOfRange), then count → ArityMismatch |
| String | String only; null → KindMismatch |
| Hash, Link | null or `""` → 0; `"0x"` + exactly 8 hex (lowercase `x`, any-case digits) → that value; other string → FNV-1a of the lowercased text; integer → KindMismatch |
| File | null or `""` → 0; `"0x"` + 16 hex → that value; else xxh64 of the ASCII-lowercased string, seed 0 |
| List, List2 | List required (a missing item gives Untypable first); each element bare(item). Elements of a Pointer/Embed list must be a struct pin, null (pointer) or a ref |
| Option | Untypable without an item. null or `[]` → empty; `[x]` → Some(x); ≥2 elements → ArityMismatch; **any non-list → Some(value)** (`opt: {}` → KindMismatch) |
| Map | Mapping required; Untypable without key and item. Keys by §3.5, values by the item. **No key dedup** |
| Pointer | null → null pointer; a non-pin mapping only via block descent; else KindMismatch |
| Embed | KindMismatch unless it is a pin or a block descent |

**Important:** a one-key mapping keyed by a type name is ALWAYS a pin, even as the only entry of a Map.

### 3.3 Struct pins (264-328)

- **Inner value:** `null` or `{}` on a Pointer gives a null pointer. At load time `{embed: {}}` and
  `{embed: null}` are refused.
- **Base class:** a non-null base pointer's class, or the base embed's class.
- **Class:**
  - a `class` string is hash-form or FNV;
  - absent → the base class, else `Untypable`;
  - an Embed whose base class differs → `PinMismatch`.
- **Attestation:** `has_class` is asked only when the class differs from the base class. False gives
  `UnknownClass`.
- **`set` keys:**
  - Each must be one plain segment with no subscript, else `InvalidPath`.
  - **There is no sign parsing: `+x` is a field named `+x`.**
  - Each is typed by `expected`, else `fallback` (which marks fell-back), else `Untypable`, then
    `coerce(v, shape)`.
  - Duplicates keep the first position and the last value.
- **Result:** a NEW struct of {class, set fields only}. **A struct pin replaces the whole struct.**

### 3.4 Type pins (74-115)

1. A ref inside a pin gives `PinMismatch`.
2. On a Pointer/Embed shape, only `pointer` on Pointer or `embed` on Embed is allowed, as a struct pin.
   Anything else gives `PinMismatch`.
3. On List/List2/Map, the pin names the ITEM kind; a mismatch gives `PinMismatch`. The elements still need
   their own pins.
4. On an Option:
   - an `option` pin with `{}` gives empty, otherwise `optional(inner)`;
   - a pin equal to the item is re-wrapped as `{name: inner}` for struct kinds, then `optional([element])`. So
     `!pointer null` gives **Some(null pointer)**, while plain `null` gives None;
   - anything else gives `PinMismatch`.
5. On a scalar, a pin equal to the kind gives bare(inner); anything else gives `PinMismatch`.

### 3.5 Map key text (246-261)

- **Bool:** exactly `"true"` / `"false"`.
- **Integer kinds:** parsed like Rust `str::parse::<i128>` (optional `+`/`-`, digits only), then
  range-checked → OutOfRange.
- **F32:** Rust f64 parse, which accepts `inf`, `infinity` and `nan` in any case, `1.`, `.5`, `1e5` and a
  leading `+`, then cast to f32 with no PrecisionLoss check.
- **String / Hash / File:** the bare string rules, so `0x` hash-form IS honoured here, unlike in `{}`
  subscripts.
- **Other key kinds:** `InvalidKey`.

### 3.6 JSON numbers (serde_json 1.0.150, no `float_roundtrip`)

- A non-negative integer up to u64::MAX, or a negative one down to i64::MIN, is an Integer.
- **`-0` is Float(-0.0).** An out-of-range integer is a Float. Anything with `.` or an exponent is a Float, so
  `1.0` on an integer kind is `KindMismatch`.
- Float parsing is `significand as f64`, then `*` or `/` `POW10[|e|]` (de.rs:639-672). It is correctly rounded
  for ≤15-16 significant digits and |exp|≤22. A long mantissa may double-round, and only an exact port
  guarantees bit equality.
- **Duplicate keys anywhere refuse the document.** C# `JsonDocument` does not; check for them yourself.

## 4. Objects (gd/apply/objects.rs)

### 4.1 Create (27-77)

- Created in mapping order; `Remove` entries are skipped here.
- **Check order:**
  1. **ObjectExists:** the name is in the target, or already created in this phase.
  2. **Clone:** the source must be in the target AS IT IS BEFORE this phase creates anything, else
     `SourceMissing`.
  3. **Construct:** `has_class` must hold, else `UnknownClass`. The new object is `BinObject(hash, class)` with
     NO properties; no schema defaults are filled in. With NoSchema every construction is refused.
- **Insertion:** every success is appended at the end of the object table, in mapping order. THEN each one's
  `set` runs as entry edits under its name, with the full block, sign, pin and ref rules.

### 4.2 Clone rewrite (85-101)

- A deep copy with the new path hash and the class copied.
- **Top-level properties only:**
  - a Hash value equal to the source object hash → the new hash;
  - a String equal to the source spelling (ASCII case-insensitive), when both names are path-form → the new
    name as spelled.
- ObjectLink, File, nested values and `0x` names are untouched.

### 4.3 Remove (105-125)

- Runs after the entry edits, in mapping order. `shift_remove` keeps the order of the rest.
- An absent object gives `RemovalUnmatched`.
- `objects` counts created plus removed.

## 5. Links, override files, PTCH targets

### 5.1 Links

See §1.2 steps e and f: removals before additions, ASCII case-insensitive.

### 5.2 Override files on a PROP target (lm/data_override/apply.rs:122-173)

- **Read:** PTCH v1 wrapping PROP v1-3 with 0 dependencies. A reader error gives `OverrideUnreadable`; a decode
  error gives `OverrideInvalid`.
- **Apply:**
  1. `deleted` objects are removed.
  2. Patch objects not deleted are inserted: replaced in place, or appended.
  3. Records apply in file order with **PropertyPath (client) semantics:**
     - the name hash is FNV of the text, with NO `0x` escape;
     - `{k}` uses `key_as` with IEEE equality;
     - an absent unsubscripted leaf is created;
     - the type rule is as `replace`.
- **Counts:** `records += applied`; `objects += added + replaced + actually deleted`.
- **Skips:** each skipped record gives `OverrideRecordSkipped`. PatchError Display texts are at
  resolve.rs:160-218.

### 5.3 PTCH target (gd/apply/patch.rs)

**Setup.** The edits run over a View: `bin` = the patch's objects, `owned` = their hashes.

**Per edit:**
1. **Override files** are merged. Their skipped records are NOT reported, `records` counts every record, and
   `objects` counts objects plus deletions.
2. **Load:** each entry name, object key and clone source not already present is read through `read_entry`,
   with the patch's own records laid over it.
   - `Err` gives `EntryUnreadable`.
   - `None` gives a later `MissingObject`.
3. **Create**, then mark the new objects owned.
4. **`run_recording`:**
   - Owned objects are edited in place.
   - Others produce `PropertyPatch{hash, first-spelled path, whole settled value}` records. A hash-form
     segment gives `HashFormPath`.
   - A new record drops held records at or under its identity (prefix followed by end, `.`, `[` or `{`).
5. **Remove:** an owned object leaves `owned`; a game object goes to `deleted`.
6. **Links:** every one gives `LinkUnsupported`.

**Output:**
- Unchanged → the base bytes.
- Otherwise PTCH v1 / PROP v3 / 0 deps: owned objects in view order, `deleted` deduped, records
  `{u32 obj, u32 size, u8 kind, u16 len path, value}` (lm/data_override/write.rs:35-74).

## 6. References and the object index

### 6.1 Collection (mod.rs:394-430)

- **Walk order:** per edit, the objects' `set` values in object order, then the entry edits. Lists, mappings,
  pin inners and struct-pin sets are walked recursively.
- **Keyed by object hash**, so one entry spelled two ways is read once.
- **`read_entry` once per hash, before any edit:**
  - `None` → absent;
  - `Err` → `ReferenceUnreadable` (`entry:path` as spelled).

### 6.2 Resolution

- **Source:** the PRE-EDIT game copy only, never the target or another mod.
- **Path:** ValuePath semantics.
- **Type:** shape equality as in §3.1.

### 6.3 Overlay `read_entry` (ov/builder/game_data.rs:688-721)

- **Lookup:** the first declaring chunk of the object index; mount it and read the object.
- **Memoised:** `Some` and `None` are cached per build, `Err` is not.
- **No index, or no declaration:** `None`.
- **A PTCH first holder fails to mount:** `ReferenceUnreadable`.
- **Duplicate objects in a chunk:** the LAST is kept.

### 6.4 First-declaring-chunk order (gi/)

- **Archives:** every `*.wad.client` under `DATA/FINAL`, with ids in BYTE order of the DATA/FINAL-relative
  name.
- **Chunks:** each chunk belongs to its first holder archive only. Unnamed chunks are sniffed for PROP/PTCH
  magic in ascending chunk hash.
- **Storage order:** archive id, then chunk hash, then object order. `declarations()` is a stable sort by object.

### 6.5 Index lifecycle (game_data.rs:463-476, 589-594, 726-752; gi/objects/mod.rs:237-249)

- **Loaded lazily** when a module is a non-empty `entries` module, holds a ref, or creates an object, or at the
  first PTCH target.
- **Cached** as MessagePack with a game fingerprint: xxh3 over each archive's name, length and mtime.
- **On failure:**
  - each `entries` module gets `IndexUnavailable`;
  - a target module with refs reads `None` for its refs;
  - created objects are made unchecked.

### 6.6 Entries fan-out (254-325)

- Each distinct declaring chunk once, in storage order.
- **0 chunks:** `EntryUnresolved`.
- **2 or more:** `EntryFanOut` (informational), and every chunk is edited, PTCH chunks included.
- **`ObjectShadowsGame`** (informational): a created name that another chunk declares.

## 7. Schema

### 7.1 Trait (gd/schema.rs:16-33)

- `expected(class, field) -> Option<Shape>`.
- `fallback(class, field) -> Option<Shape>`, default None.
- `has_class(class) -> bool`.
- The class is always the ACTUAL class of the holder, or the pinned class.
- **NoSchema** answers None, None, false. It is the overlay default.

### 7.2 LTK Manager `PatchSchema` (mgr/meta_schema/game_data.rs:17-53)

- **`describes(b)`:** `b <= latest`.
- **`expected`:**
  - None when the build is undescribed.
  - Otherwise a DFS: the class itself first, then its bases from the class revisions covering the build, in
    listed order, depth ≤16.
  - The first class whose property has a revision covering the build wins, the first such revision in file
    order.
  - **`covers` is `from <= b && (to absent || b <= to)`: `to` is INCLUSIVE.**
  - A type name that does not map gives None.
- **`has_class`:** true when the build is undescribed, or the class exists at ANY build.
- **No `fallback`.** Manager 1.21 pins ltk_game_data 0.6.0, which has none.
- **Shape from `type`:**
  - kind = slot 0;
  - key = slot 1, for Map only;
  - item = slot 2 unless `"0x0"`.
- **Type names:**
  - plain: `None Bool I8 U8 I16 U16 I32 U32 I64 U64 F32 Vec2 Vec3 Vec4 Mtx44 Color String Hash Option Map`;
  - renamed: `File` (WadChunkLink), `List` (Container), `List2` (UnorderedContainer), `Pointer` (Struct),
    `Embed` (Embedded), `Link` (ObjectLink), `Flag` (BitBool).
- **The build** is the third number of `Game/content-metadata.json` `"version"`.

### 7.3 `data/meta/meta.db.json` (formatVersion 1)

ReyEngine's copy has latest 8217343; the manager ships the same format with latest 8175716.

**Top level:** `formatVersion`, `hashSource`, `latest`, `versions[{patch, build}]`, `externalTypeNames`,
`classes`.

**A class:** `{name?, revisions[{from, to?, bases[hex], interface, value}], properties{hex: {name?, revisions[{from, to?, type:[k, s1, s2, s3], default?}]}}}`.

**The `type` slots:**
- s1 = the Map key kind, or a List fixed size, else `"0x0"`;
- s2 = the item or map value kind;
- s3 = the class of an Embed, Pointer, Link or item.

**`to` is inclusive.** 785 property revisions and 234 class revisions have `from == to`.

### 7.4 `MetaClassDatabase.cs`: what must change for an LTK-faithful schema

1. **`PickRevision` treated `to` as exclusive** (`build >= to`). It must be inclusive. **Fixed in M817.**
   - The exclusive reading dropped 491 property and 9 class revisions that end at 16.16 (8049184).
   - It never matched the 785 property and 234 class single-build revisions.
   - Across all builds, it lost 5,891 property and 1,485 class (entity, build) pairs.
   - No two revisions of one entity overlap in today's data, so "first in file order" (LTK) and "highest
     `from`" (the old code) agree.
   - The latest build was unaffected: all callers load it.
2. **The class set is build-filtered, but `has_class` must be "any build".** `expected` must look at the class's
   own properties even when no class revision covers the build; only the bases come from covering revisions.
3. **Resolve per query.** Add `describes` (`build <= latest`) and an undescribed mode: `expected` None,
   `has_class` true.
4. **`MetaProperty` docs are wrong.**
   - `KeyType` = s1 and `ValueType` = s2. `KeyHash` = s3.
   - The empty marker is `"0x0"`.
   - Type names are PascalCase.
5. **Lookup order:** use DFS in revision order with depth 16, not BFS. At latest the 94 multi-base classes give
   0 different answers, but DFS is the faithful order.
6. **Build and fallback.**
   - Read the build from `content-metadata.json`.
   - The C# engine and the Rust harness must use the identical schema data.
   - Pick a `fallback` policy explicitly: None (manager 1.21), or "latest" (ADR-0033).

## 8. Diagnostics model (mod.rs:26-306)

**`ApplyDiagnostic`:** `kind`, `edit`, `path`, optional `record`, `property`, `object`, `detail`.

**Kinds:** `overrideUnreadable`, `overrideInvalid`, `overrideRecordSkipped`, `linkRemovalUnmatched`,
`propertyEditSkipped`, `schemaFallback`, `referenceUnreadable`, `objectSkipped`, `linkUnsupported`,
`entryUnreadable`, `unknown`.

**Reason enums:**
- `RecordSkipReason`: 9 codes;
- `PropertySkipReason`: 23 codes;
- `ObjectSkipReason`: objectExists, sourceMissing, unknownClass, removalUnmatched.

**Overlay-only kinds** (ov/builder/game_data.rs:25-95): `declarationsRejected`, `targetSkipped`, `noEffect`,
`entryUnresolved`, `entryFanOut`, `indexUnavailable`, `objectShadowsGame`.

## 8b. Found while porting (M817)

The spec above was silent on these; the C# engine matches the Rust behaviour.
- **Refusal texts** follow serde_json's wording. The column counts UTF-8 bytes, and a duplicate key is reported
  only once its object closes.
- **Fields are read in document order.** The document, module and origin structs also accept serde's positional
  array form, so `[1, []]` is a valid document.
- **`validate()` runs after the whole document is read.** An empty `edits` error names the module's list
  position. An empty-name error carries its `target:` / `entries:` / `class:` location.
- **PTCH `deleted` order is not deterministic in Rust:** `drop_missing` iterates a std HashSet. The C# engine
  writes bin order.
- **Corrupt counts.** A bin whose counts are corrupt can make the Rust process abort on a huge allocation; the
  C# engine raises a Bin error.
- **Unicode.** `RustLowercase` and `RustDebug` are tables dumped from rustc 1.92.0 (Unicode 17).

## 9. Behaviours most likely to make a port differ

1. **Hash lowercasing.**
   - FNV (`BinHash`): ASCII fast path; non-ASCII uses Rust full Unicode `char::to_lowercase` (can expand). .NET
     `ToLowerInvariant` differs.
   - XXH64 paths: ASCII-ONLY lowercasing.
2. **Hash-form** is `0x` (lowercase x) plus exactly 8 or 16 hex digits; `0X` is a name. It is honoured in
   paths, `set` keys, names and values. It is NOT honoured in `{}` subscripts or override records.
3. **Two path semantics:**
   - ValuePath (declarations and refs): hash-form aware, bitwise float keys;
   - PropertyPath (override records): text-hashed, IEEE keys.
4. **Float equality:** IEEE for `-list`/`±map`, bitwise for `{k}` navigation.
5. **Number rules:**
   - an integer becomes f32 only if exact; a float may round and may overflow to ±inf;
   - JSON `-0`, `1.0` and out-of-range integers are Floats.
6. **Check order:**
   - element errors before arity;
   - `list()` checks Untypable before the value kind;
   - creation checks ObjectExists before SourceMissing or UnknownClass.
7. **Grouping and operation order:**
   - groups by field hash, `{k}` by literal text;
   - the first spelling names the group;
   - the last set wins;
   - set, then removals, then additions;
   - the `first_sign` rule.
8. **Removal by value removes ALL equal elements**, so a duplicated operand gives RemovalUnmatched. Struct lists
   are removed by deduplicated index.
9. **Maps:** `+map` replaces in place by key, otherwise appends. A map `set` keeps duplicates.
10. **Flatten timing:** flatten sees the pre-entry object. An empty block is a no-op. A block on a null pointer
    needs a struct pin.
11. **Schema:**
    - `expected` outranks the base;
    - subscripted paths never consult the schema;
    - `patch_at` compares the embed class only.
12. **Encoding:**
    - re-encode only when something changed (v3); otherwise rewrite the header only;
    - dependencies are deduped at load;
    - the overlay discards unchanged outputs.
13. **Canonicalisation:**
    - duplicates keep the first slot and the last value;
    - bools are written as 0/1;
    - legacy numbering is rewritten;
    - a null pointer or class-0 embed is 4 bytes;
    - additions are appended.
14. **Clone rewrite:** top-level Hash and String only, never ObjectLink.
15. **References:** read once per hash, before the edits, from the FIRST declaring chunk (byte-sorted archives,
    then ascending chunk hash). A PTCH first holder is unreadable.
16. **PTCH targets:**
    - merge skips are unreported;
    - every record is counted;
    - records carry the whole settled value;
    - the `covers` rule drops covered records;
    - `deleted` is deduped;
    - an unchanged target returns its original bytes.
17. **Module order:**
    - mods in reverse list order;
    - layers base first, then by priority, then natural name order;
    - each application re-decodes the previous output.
