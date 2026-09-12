# What an effect is for, and how the preview should carry it

M712. The particle preview parked every system at the origin, which shows a missile as a puff standing
still and a trail with nothing to trail behind. This is the rig that carries it instead, and the research
behind guessing which rig a system wants.

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
