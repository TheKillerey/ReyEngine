# How the editor saves, and why edits used to disappear (M555)

The reported symptom: *"I save sun settings. It works also ingame. When I change materials or other
settings it somehow overrides it without the sunlight changes."*

## The mechanism

It was never a format problem. It is a **stale whole-file snapshot**.

* `MaterialEditorViewModel` holds a `MaterialDocument` parsed **once, when the map was opened**.
* `SaveMaterialOverride` calls `Serialize()` on that document and writes the **entire** `.bin`.
* The sun lives in the map's `materials.bin` (`MapSunProperties`, see M45), the *same file*.

So saving a material wrote a whole-file image that predated the sun edit, and the sun went back to what
it was when the map was opened. The sun save itself reads the file fresh (`ReadAsset`), which is exactly
why it works on its own and dies the moment a material is touched.

**Autosave made it worse rather than better.** It is a timer that fires the same whole-file save, so the
more the user edited, the more often the stale snapshot landed. It is also the source of the reported
lag: every tick re-serialises and rewrites the whole file.

Scale of the problem when it was found: **40 write sites** for the map bin in `MainWindowViewModel`, but
only **2 long-lived documents** (`MaterialEditorViewModel`, `BinEditorViewModel`). The clobbering has a
narrow choke point even though the writes do not.

## The fix: rebase, don't overwrite

`BinThreeWayMerge` already existed (M97, for carrying a mod across a game patch) and does exactly the
required job: re-apply `diff(oldBase -> mod)` onto a new base, at object level, and at property level
inside objects the mod touched.

A save is now that merge:

| merge input | what it is here |
|---|---|
| `oldBase` | the bytes the editor's document was parsed from (`BaseBytes`) |
| `mod` | what the editor serialises now |
| `newBase` | the file as it is on disk **right now** |

Untouched objects keep whatever the file has; only what the editor actually changed carries over.

**`BaseBytes` is never refreshed on save, and that is deliberate.** Three-way merge needs the base the
edits were made *against*. Moving it forward to the merge result would make the next save look like it
had DELETED whatever the merge just brought in.

**The fast path matters.** When the file has not moved underneath the editor - the overwhelmingly common
case - the bytes are compared and returned unchanged, so no autosave tick pays for three tree parses.

**A failed merge saves anyway, loudly.** Refusing would lose the user's edit; overwriting silently is how
the sun vanished in the first place. So it warns that other changes to that file may be lost.

## What this does NOT fix

Autosave still serialises and writes whole files, so it is still the source of the lag. That needs the
edit store (below), not a merge.

## Next: the edit store (M556+)

The agreed direction is a typed in-memory change set that is the single source of truth, with `.bin` /
`.mapgeo` materialised only at Build Package, LTK Manager send, Fantome export, or project save. That
gives three things the merge cannot:

1. **Autosave without lag** - appending to a journal instead of rewriting a 43 MB asset.
2. **One place to see every pending change**, across materials, sun, meshes, placements, particles, HUD
   and audio.
3. **No whole-file writes during editing at all**, which removes the class of bug above rather than
   containing it.

Staged deliberately after the merge: the merge stops the data loss today and keeps whatever has not
migrated yet safe while the store is built surface by surface.
