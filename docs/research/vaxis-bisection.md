# M258 - the V axis is correct, and how a test said otherwise

Q3 of the RenderDoc plan asked whether our V axis is right. The first answer was "flipped". The real
answer is "correct", and the interesting part is the gap between them.

## The claim

A probe built a 64x64 texture, labelled row 0 RED and the last row BLUE, drew one face-on quad, and
reported red at the bottom of the image - i.e. every sprite upside down. That was committed as a finding.

## The bisection

The chain from a texture row to a screen row has three links, and inspection said all three were fine,
so one of them had to be lying. Two probes split them:

| probe | isolates | how |
|---|---|---|
| A - geometry | world +up -> screen top | 1x1 WHITE texture, two quads at +Y and -Y with different brightness; V cannot influence the result |
| B - upload | source row -> v | red/blue texture sampled at a CONSTANT v across the whole quad; geometry cannot influence the result |

Probe A: top band 207, bottom band 41. World +up renders at screen top. Geometry correct.

Probe B, first run, was ambiguous: v=0 and v=1 both averaged 127/127. A band mean cannot tell "flat grey"
from "half red over half blue" - both give 127. Reporting a vertical profile instead of a mean, and
sampling away from the edges, resolved it:

```
   v=   0.25:   0/ 64   0/255 ...   = BLUE
   v=   0.75:  64/  0 255/  0 ...   = RED
```

v=0 and v=1 sit exactly on the wrap seam, where linear filtering straddles row 0 and row 63 - which is
where the 127 came from, and why the original probe's edge-adjacent reading was never trustworthy.

## The actual bug - in the test

`MakeTexture` declares `Format.FormatR8G8B8A8Unorm`. The render target and the readback are BGRA. The
probe hand-authored its test pattern as **BGRA**, so the bytes it labelled "red" - `(0,0,255,255)` -
were uploaded as R=0, B=255, i.e. **blue**.

The source texture was therefore blue on top, not red. Blue rendered at the top. That is *correct*, and
the probe called it flipped, because it compared the rendered colour against the label rather than
against the bytes.

Re-run with GREEN over BLACK - green is byte 1 under both channel orders, so no ordering slip in the test
can masquerade as a V flip in the engine:

```
   v=builder:  58 255 255 252   3   0   0   6     <- green in the TOP slabs
   v=   0.25: 255 (source top half)
   v=   0.75:   0 (source bottom half)
```

All three links confirmed. `vaxis` now reports CORRECT.

## What this cost and what prevents it

Nothing shipped: the retracted commit changed one comment. But it was one step from a "fix" that would
have flipped `Corner()` and broken every correct sprite in the engine to satisfy a broken test.

The rule that would have caught it immediately: **a test that asserts against a label it wrote itself is
only testing the label.** The probe never verified that the bytes it called red were red. Choosing a
channel-order-immune pattern is not a workaround for that - it removes the degree of freedom entirely,
which is why the fixed probe is trustworthy in a way the original could not have been made to be.

Both modes live in the scratchpad harness: `vaxis` for the verdict, `vaxisbisect` for the three-link
split.
