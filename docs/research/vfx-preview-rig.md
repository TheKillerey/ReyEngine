# What an effect is for, and how the preview should carry it

M712. The particle preview parked every system at the origin, which shows a missile as a puff standing
still and a trail with nothing to trail behind. This is the rig that carries it instead, and the research
behind guessing which rig a system wants.

**M713 supersedes the guessing half of this, and the second part of this document records it.** The rig now
comes from the spell record that names the system, when there is one; the name guess below is what is left
for the systems nothing names.

## The file does not say

A `VfxSystemDefinitionData` describes emitters and nothing else. Whether the thing it describes sits on a
champion's hand, lies on the ground or flies across the map is decided by the spell that instantiates it.
ltk-manager's own plan states the finding and stops there: "What makes an effect a missile is the spell
script that flies that instance, not a field in the bin. A preview has no script, so the reader picks."
Every one of their previews opens on `still` and the rig is always chosen by hand.

## The rig

Four modes, and the numbers are theirs, which are the game's. A champion is 200 units tall and every
constant is a multiple of that.

| mode | motion | run length |
| --- | --- | --- |
| Still | fixed at `(0, height, 0)` | the system's own span |
| Burst | still, and the whole run starts over when it ends | the system's own span |
| Missile | straight line on X, `-600` to `+600`, at 1,600 u/s - a flight of 0.75 s - holding where it lands | flight plus the linger tail |
| Trail | a circle of radius 300, once every 3 seconds | at least one revolution |

Height is the system's own Y offset in every mode, not a flight altitude: every motion is authored on the
ground plane and the height lifts the whole thing. A missile stops emitting where it lands, which is what
the game does with one; any other mode can be asked to stop halfway through its run so the teardown plays
without holding the Stop button. The seed is the whole of a run's randomness, and it defaults to 1337 so
that two readers of one effect see the same run.

Our rig is a record and a `Pose(t)` function in `ReyEngine.Formats`, the shape `SpellAim.Plan` already
uses: no viewport state, no renderer, so the motion is pinned by tests without a device.

**The seed had to become explicit.** A simulator's seed was derived from the system's path hash and its
position, which is right for a map - two placements of one effect must not flicker in lockstep - and wrong
for a preview, because dragging the rig's height then silently re-rolled the randomness. Two knobs on one
wire.

## Guessing which rig, and what a guess is worth

We do go one step further than the reference renderer: the rig opens on a guess from the system's name,
and the guess says why, so it can be corrected rather than obeyed.

**The reliable signal exists and we do not read it yet.** A spell's own record names the system it flies.
Over 180 archives, the complete inventory of fields that point at a system definition includes
`SpellDataResource.mMissileEffectKey` (24,844 references, 15,182 distinct systems) and
`mHitEffectKey` (10,442 / 5,500), resolved through the skin's `ResourceResolver.resourceMap`. 15,742
systems are named as a missile that way, in 160 of 173 champion archives. And it hands over the rig's
parameters, not just its class: 97.5% of those spell records carry a missile spec, 81.2% of which have an
authored speed. Ahri's charm comes back at 1,550 units a second, which is its real speed. Reading that
would replace the guess with the game's own answer and set the rig's numbers from it. It needs the
champion's spell bins open beside the particle bin, which this window does not do.

**The naming, measured.** Only 57.0% of 206,774 system rows carry any recognised role token at all. Scored
against the wiring above, a full six-way name classifier reaches 90.5% overall and **84.1% balanced across
its classes**, and the second number is the honest one - the population is 64% bone-attached and the
classifier's default is bone, so the first number flatters it. `_mis_` alone detects a missile at 86.4%
precision and 87.9% recall.

Two things about matching that were measured rather than assumed. It has to be hybrid - a substring for
the long unambiguous words and an exact token for the three-letter ones - because "Impact" and "Hit" turn
up inside half the emotes in the game; that beats exact-token by 2.2 points and pure substring by 3.9.
And the right default is "attached to a champion", not "static": flipping that moved the rule from 71.4%
to 84.9%, a bigger gain than any token change.

**What it gets wrong**, named rather than hand-waved: 1,715 systems that really do fly read as something
else (`Sona_Skin26_CritAttack_Cas`, `Diana_Skin27_Q_Trail`, `Pantheon_Skin30_R_Spear_Landing`), and 217
that do not fly carry a `_mis` anyway, because a dash and a recall are written like a missile -
`Vayne_Skin52_Recall_mis2`. Four champions - Jarvan IV, Wukong, Sett, Xin Zhao - have `_mis` systems and
no linked missile at all, their dashes being script-driven. Indicators are the worst case for naming:
4,572 rows are named *Indicator* or *Targeter* and only 9.6% carry any link at all.

We implement the three rigs the preview can carry - missile, trail, still - with the child case folded
into still and a reason shown for each. The six-rig classifier and the spell-bin link are the next step.

## Where it plugs in

The rig reaches the viewport as a styled property, not as a field on the playback item: mode and height
are dragged on a slider, and putting them on the item would rebuild the whole playback and re-upload every
sprite on each tick of the drag. The seed is the exception, because a simulator's random stream is fixed
when it is built, so changing it rebuilds - which is what Restart already does.

The motion itself needed no new renderer machinery. `VfxParticleSimulator.SetWorldTransform` has existed
since bone attachment, and missile travel already lerps a system between two points every frame in both
renderers. The rig replaces that lerp with its own pose, and leaves a placement that travels of its own
accord alone - the map's own missiles are not the preview's to move.

## Loose ends

- The reliable classifier is unread: the spell records above, and the speed they carry.
- `TARGET`, `BONE`, `GROUND` and `CHILD` are real rigs in the game's own wiring and the preview has no
  motion for them. A bone rig in particular would need a champion loaded beside the effect.
- ltk-manager also carries a screen-bound rig - `AttachToCamera` on `PersistentVfxData` - on 7 records in
  30 archives. Rare enough to record and not build.

---

# M713 - the game answers, where the game has something to say

The guess above is now the fallback. When the previewed system belongs to a champion, the rig comes from
the spell record that names it, and flies at that ability's own speed.

## The chain, and it is three different files

| step | where |
| --- | --- |
| the spell object and its `mMissileEffectKey` | `data/characters/<champ>/<champ>.bin` - 5,885 of 5,888 |
| the key, turned into THIS skin's system | `ResourceResolver.resourceMap` in `data/characters/<champ>/skins/skin<N>.bin` |
| the system itself | usually a shared `<champ>_multi_skins_….bin`, which is 85% of resolved links |

The key is a Hash and the map's value is an object link, 14,361 of 14,361 in both cases. Not one record in
the corpus points at a system directly: the map is the only route. Exactly one `ResourceResolver` object
per bin, no exceptions, median 71 entries.

**Coverage is effectively total.** 1,255 spell records name a missile effect and **1,254 resolve end to
end**. The single miss is Aurora's E, where the skin maps the key to the null link on purpose. Per
(key × skin) pair: 61,640 of 61,935 resolve, and the whole residue is a skin deliberately suppressing an
effect rather than a broken lookup. A null link means "this skin draws nothing here" and is dropped, not
followed to another skin's system.

## The brief's worked case was right about Ahri's E and wrong as a rule

`mMissileSpec.movementComponent.mSpeed` holds for 1,114 of the 1,162 specs that author a speed. The
component is polymorphic over **twelve** classes:

| class | count | what it authors |
| --- | ---: | --- |
| FixedSpeedMovement | 1,118 | `mSpeed` |
| FixedTimeMovement | 141 | `mTravelTime` |
| AcceleratingMovement | 69 | `mInitialSpeed`, `mMinSpeed`, `mMaxSpeed`, `mAcceleration` |
| FixedSpeedSplineMovement | 38 | `mSpeed` |
| FixedTimeSplineMovement | 27 | `mTravelTime` |
| eight more | 28 | circles, walls, physics, parametric |

Ahri is the case that makes the point. Her passive, her Q, her Q-return and her W are all
`AcceleratingMovement` or `CircleMovement` and author **no `mSpeed` at all**, so a reader that knows only
the claimed path is silent on her signature ability. Two more places hide a speed: `ParametricMovement`
nests it two levels down through `MovementEntries`, and two specs put one inside a `behaviors` action.

Our reader switches on the FIELDS rather than the class, which is why it survives this - the discipline was
already there from M631, when a class table would have shipped a speed of zero for thirteen spells. M713
adds `mInitialSpeed` to it: an accelerating missile has no single speed and the one it leaves at is the
honest stand-in, and that covers 74 more specs across four classes.

**The legacy `missileSpeed` scalar is not used.** It is authored 3,096 times and agrees with the movement
component on only 80.8% of the records that carry both; where it disagrees it disagrees badly (Akali's Q:
99,999 against 6,200) and its maximum is 1,000,000,000.

## What a preview does with an authored speed

Speeds run from 0.5 to 2,500,000 units a second, median 1,750, p10 to p90 of 1,200 to 3,000. A plain clamp
on the extremes is useless, so the rig clamps on the flight instead: at most the speed that crosses the rig
in a twentieth of a second (Aurelion Sol's E would otherwise be half a millisecond) and at least the one
that crosses it in thirty (Hwei's W, authored at 0.5, would take forty minutes). The note always quotes the
spell's own number, not the clamped one.

## When the link is available: only from a champion

All six routes into the particle editor pass a `WadAssetEntry` and nothing else, and the editor has no WAD,
no mounts and no way to read a second bin - by design, since every other thing it needs arrives as a
callback keyed by the system. So the champion is read out of the entry's path, `data/characters/<champ>/…`,
and the host does the three-bin walk on the same background thread as the parse.

A map's `mapXX.bin`, the mode-specific data and a workshop loose file have no champion. They get nothing
and keep the name guess, and that is not a gap to close later: nothing in a spell record names a map's
placed systems.

## The bone rigs are read and deliberately not carried

The skin bin also holds two bone-attached populations, in the same file as the resource map, so reading
them is free: `idleParticlesEffects` (23,372 keys, a bone named in 24,387 records) and the
`PersistentEffectConditions` a buff switches on (10,553 keys, 9,857 with a bone). Both are surfaced - the
panel says which bone and which buff - and neither gets a motion, because a bone rig needs the champion
standing beside the effect and this window cannot load one. Saying "it hangs on `L_Eye` under `AhriR`" is
the useful half and it is honest about the rest.

Two field names had to be read out of the real bins rather than guessed: the idle container is
`idleParticlesEffects`, and `OwnerCondition.Spell` is a **hash**, not a string. Reading either wrongly
returns nothing, silently, which is the M590 failure shape.

Still unread: `ParticleEventData` (56,380 keys), the largest bone population, which lives in the animation
bins this does not open.

---

# M715 - the rig in the champion window, and the bone rig at last

The champion window plays VFX five ways and four of them already move better than a rig could. A cast
composite flies its missile over the real distance at the ability's authored speed, with the real aim, the
real cast delay and a return phase; a clip event rides an animated bone on a real skeleton; the attack
cycle does windup then flight then hit. A rig offering a straight line at a flat speed would be a
regression on every one of them.

**One thing in that window stands still**: the manual CHAMPION VFX pick, which sets no motion of any kind.
That pick also binds none of the playback controls, so it loops forever and its teardown is never visible.
That is what the rig is for here, and the panel only appears while a system is picked.

Two guards make it safe rather than merely scoped. The viewport's rig loop already skipped an item with a
travel destination; it now skips one with a bone as well, because the bone re-anchor runs in the same frame
and two owners of one transform means the later one wins. And the rig's pose is composed with the item's
own placement instead of the world origin - the particle editor places at the origin so nothing changes
there, but this window anchors at the caster or at the target dummy, and a missile has to fly from where
the effect actually is.

## The half of M713 that could not be carried before

M713 reads three things out of a champion's bins: the spell that flies an effect, the spell that plays one
where it lands, and the bone a skin hangs one on. The particle editor can act on the first and can only
stand the other two still, because it has no champion.

This window **is** the champion. A picked system whose skin hangs it on a bone is now hung on that bone,
through the same `AttachBone` both renderers have honoured since M86. `idleParticlesEffects` carries 23,372
of those keys and the buff-gated `PersistentEffectConditions` another 10,553, with a bone named in over
93% of records - an effect like Ahri's R eye glow lands on `L_Eye` where the game puts it, rather than at
her feet.

## Still not carried

`ParticleEventData`, the clip particle events - 56,380 keys, the largest bone-attached population of all.
They live in the animation bins, which neither the particle editor nor the role reader opens. The champion
window already plays them through its own clip path, so the gap is in what the ROLE reader can say about a
system, not in what the window can show.

