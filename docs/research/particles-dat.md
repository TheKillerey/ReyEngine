# Particles.dat — the legacy placement list (M531)

The file that says where every particle in a legacy map stands. Plain text, CRLF, one placement per
line, sitting beside the room in `LEVELS/<Map>/Particles.dat`.

```
Data\Particles\CANDLE.troy 10581.3 223.72 5570.36 -2147483648 0 0 0
```

## The format is the client's, not a guess

Recovered from `League of Legends.exe` rather than inferred from the data. The loader at VA `0x6C86D0`
reads each line and calls `sscanf` with the literal format string at VA `0x10A5354`
(file offset `0xCA5354`), which sits immediately beside the literals `Particles.dat` (`0xCA5324`) and
`Failed to open particle file %s` (`0xCA538C`):

```
%s %g %g %g %d %g %g %g %s
```

Nine conversions. Argument slots read off the push order at VA `0x6C884B..0x6C88DC`:

| # | Token | Type | Meaning |
|---|-------|------|---------|
| 1 | path | string | `.troy` reference; on disk it is `.troybin`, and the case differs |
| 2-4 | x y z | float | world position |
| 5 | detailLevel | int | **graphics-detail gate** — see below |
| 6-8 | f6 f7 f8 | float | a vec3 the client builds as `(f6, f7, f7)` — meaning unresolved |
| 9 | group | string | emitter group name |

A 10th token exists in `map11` (`CHAOSONLY` / `ORDERONLY`) and **the client never reads it** — there are
only nine conversions, and neither string appears anywhere in the executable.

## Three things the data alone cannot tell you

**Token 5 is a detail gate, not an id.** The loader pre-sets it to `0x80000000` at VA `0x6C88B7`, before
`sscanf` runs, so `-2147483648` means "the writer left it unspecified". At VA `0x6C8990` it is compared
against the environment-quality global at VA `0x1223E44` and `jg` skips the entire emitter-creation
block. Every line of all 8 corpus files carries the default, so nothing in the shipped data is gated.

**Token 8 is parsed and thrown away.** At VA `0x6C8A51..0x6C8A84` the client stores `f6` then stores
`f7` **twice**, and passes `(f6, f7, f7)` to emitter vfunc `[+0x3C]`. Grepping the loader body for the
`f8` stack slot returns exactly two hits — the `lea` that forms the sscanf argument and the zero-init —
and no read. This is the mechanical reason `f7 == f8` on **2,302 of 2,306** corpus lines.

**The vector and the group are mutually exclusive.** VA `0x6C8A4C` guards the vector block on
`cmp ebx, 8`, VA `0x6C8A89` guards the group block on `cmp ebx, 9`, where `ebx` is the sscanf return.
A line carries one or the other, never both. Confirmed in the data: in `Map12 - Kopie` all 34 nine-token
lines have `(0 0 0)`, and the one rotated line is eight-token.

## What f6/f7/f8 mean — unresolved, deliberately

Euler degrees is *plausible* and is **not established**. It was proposed and then refuted:

- The receiving class has its RTTI stripped, so the vfunc has no name.
- Of the 42 call sites of the spawn helper at VA `0x7172B0`, the Particles.dat loader is the **only** one
  that also touches `[eax+0x3c]` — so there is no second usage to cross-read semantics from.
- 11 of the 68 rotated rows target **audio-only** emitters (`Audio-Emitter_TT_FireMedium.troybin` is 135
  bytes and holds no visual geometry to orient).
- `Map8`'s 39 rotated rows all carry the identical value `82`, and `Map1`'s 18 all carry `-37` — a
  per-file constant, which is not what artist-authored orientation looks like.

**The porter does not apply it** and says so in a warning. All 554 lines of Map2 carry zero here, so for
that map the question is moot.

## Corpus

2,306 records across 8 files. `map11` is 560 records, not 559 — its last line has no CRLF.

| File | Lines | Notes |
|------|-------|-------|
| Map1 | 623 | 18 rows with f7=f8=-37 |
| Map2 | 554 | **all 8-token, all zero** — 16 distinct systems |
| Map8 | 171 | 39 rows all valued 82; 32 are a contiguous tail block |
| Map10 | 244 | 6 rotated rows, all Audio-Emitter troys |
| map11 | 560 | the only file with a 10th token |
| Map12 / Map16 | 27 each | the only three-distinct-float line |
| Map12 - Kopie | 100 | 34 nine-token rows tagged `highWinds` |

## Blank lines are a client bug, not a convention

The line reader does not skip them, `sscanf` converts nothing, and the path buffer at `ebp-0x218` still
holds the **previous** line's path — so a whitespace-only line spawns a duplicate of the line above it.
`LegacyParticlePlacements` skips blank lines instead. That is a deliberate divergence: no shipped file
relies on it.

## Porting

`LegacyParticlePorter` joins the placement list to `TroyBinConverter`. Positions get the **same
translation the geometry got and nothing else** — legacy particle coordinates were measured to share the
NVR's space (541 of Map2's 554 have an NVR vertex within 25 world units in XZ; the map porter applies no
rotation, scale, axis swap or negation). The shift is a port-time parameter, so for an already-ported map
the durable way to recover it is `Transform.M41/M42/M43` off any imported mesh.

A converted system's identity is **`FNV-1a(particlePath)`**, not its name — verified 22 of 22 in Riot's
shipped `jade.materials.bin`, where 0 of 22 match `FNV-1a(particleName)`.

Verified end-to-end against the user's real Map453: 16 systems and 554 placements added, 16 of 16 hashes
present, 0 collisions with pre-existing objects, 0 dangling links, 0 empty containers, and the placement
container's `items` map keeps `valueType = Struct` (pointer `0x82`) — which is what Riot ships in 59 of
59 containers, and is the one place the "Embedded not pointer" rule does **not** apply.
