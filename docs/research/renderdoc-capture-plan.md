# RenderDoc capture plan

Five questions this project currently guesses at would be settled by one frame capture of the live client.
This is the plan for taking it and what to extract.

---

## Read this first

**League runs Vanguard.** Attaching a graphics debugger to the running game is very likely to be blocked,
and attempting it may put the account at risk under Riot's terms. That is a decision for you, not for me,
and nothing below is worth an account.

Before trying it, note that **three of the five questions can be answered without a capture at all** — see
"Capture-free alternatives" at the end. Do those first regardless; they cost nothing and may leave only one
question actually needing a capture.

If you do proceed, use a throwaway account and Practice Tool, not ranked.

---

## What to capture

One frame, in Practice Tool, on **Howling Abyss (Map12)** — it is the map every measurement in this project
was taken against, so the comparison is like-for-like.

Frame contents that matter:

| need | why |
|---|---|
| terrain in view | the `defaultenv_flat` / lightmap path |
| foliage in view | `vertexdeform` — the shadow-sampling path from M254 |
| a champion, mid-cast | `skinnedmesh/*` plus live particles in one frame |
| a bush edge | the see-through alpha path from M230 |

A single frame with all four is worth more than four frames with one each — every question below is
answered by reading a draw's bound state, and having them in one capture means one set of matrices.

---

## Question 1 — the `blendMode` integer table

**Status:** guessed. `IsAdditive(m) => m is 1 or 3 or 4 or 5`, carried identically in both renderers so they
cannot disagree with each other while both being wrong. Modes 6/7/8 (258 emitters) fall off the end.

**Extract:** for several particle draws, the **output-merger blend state** — `SrcBlend`, `DestBlend`,
`BlendOp`, and the alpha trio. Then match each draw back to its emitter via the bound `TEXTURE__TX` filename
and read that emitter's `blendMode` from the bin.

**Answers:** the integer → blend-state mapping, directly. Six or seven distinct emitters should cover
modes 0–5; 6/7/8 are rare enough that they may not appear, and their absence is itself informative.

---

## Question 2 — is particle `mProj` really view × projection?

**Status:** inferred, correctly as far as anything shows, but never confirmed. The reasoning: `quad_vs`
computes `POSITION - vCamera` against a world-space camera position, so POSITION is world-space and `mProj`
must complete world → clip. The census backs it — 17 shaders use `mProj` without a bone buffer and all 17
are particle shaders.

**Extract:** the contents of `PerFrameVertexCB` at offset 0 on a particle draw, and separately the camera's
view and projection. Multiply and compare.

**Answers:** confirmation or refutation in one arithmetic check. Low risk of surprise, but it underpins the
entire particle path.

---

## Question 3 — the V-axis convention

**Status:** reasoned, not measured. GL maps up → v=1, DX11 up → v=0, and the two backends have opposite
texture origins, so the flip *probably* cancels. "Probably" is doing real work in that sentence.

**Extract:** one particle draw's vertex buffer — the four corners of a quad with their TEXCOORD0 values —
plus the bound sprite. Directional art (an arrow, a flame) makes the orientation unambiguous.

**Answers:** whether our quads are upside down. This is the cheapest of the five and the one most likely to
be silently wrong today.

---

## Question 4 — `miscRenderFlags`

**Status: ANSWERED without a capture (M259) - nothing to decode.** The premise below was wrong: it is
not a three-bit space, it is the constant 1. Over 484,286 emitters, 99.3% hold the literal value 1 and
only 3,364 (0.69%) hold anything else. Correlating against every co-field - value, presence and nested
type - the strongest predictor of a non-default value is 25.2% at n=309, and nothing with real support
exceeds 18%; a driver would sit near 100%. Presence of the field tracks other fields' presence, which
is schema versioning rather than meaning. Conclusion: treat it as a no-op. See `q4-miscrenderflags.md`.

The original entry, kept for the record:

**Status:** undecoded. U8 bitfield on 547,010 emitters (39.1%), only bits 0–2 ever set, distribution flat
across blend modes so it is not a blend qualifier.

**Extract:** this one is not a single lookup. Find emitters whose `miscRenderFlags` differ but whose other
fields match, locate their draws, and diff the full pipeline state — blend, depth, raster, stencil. Whatever
differs is what the bits control.

**Answers:** possibly nothing, if the flag drives CPU-side behaviour rather than pipeline state. Budget it
last and treat a null result as a real finding.

---

## Question 5 — `stencilMode` / `stencilRef`

**Status:** unresolved. 26,393 emitters set `stencilMode` {2:15741, 3:7387, 1:3206, 4:59}; ReyEngine never
enables stencil at all, so masked effects draw everywhere.

**Extract:** on a draw whose emitter sets `stencilMode`, the **depth-stencil state**: `StencilEnable`,
`StencilReadMask`, `StencilWriteMask`, and both face ops. Repeat for two different modes.

**Answers:** the mode → stencil-state mapping. M182 already implemented mode 1 by inference; this confirms
or corrects it and covers 2/3/4.

---

## Also worth grabbing while you are in there

- `Alpha_Offset` — the one constant M257 could not resolve. Not in `shaders.bin`, unauthored by any Map12
  material. Read its value from the bound constant buffer on a draw whose shader declares it.
- The **shadow map** itself: its format, resolution, and the real `mShadowProj`. M256 bound identity as a
  placeholder; this is what would replace it.
- Whether `SHADOW_COLOR` / `SHADOW_COLOR_COMPLEMENT` really sum to 1.0 in the client, which M212 inferred
  from black-box sweeps.

---

## Capture-free alternatives — do these first

Three of the five need no capture and no risk:

**Q3 (V axis)** — render the same sprite through both renderers on a quad with deliberately asymmetric test
art. The A/B diff from M252 already does everything except supply the test texture. If the two agree, the
flip cancels as predicted; if they differ, one is upside down and the diff will show it as a coverage
mismatch band.

**Q1 (blend modes), partially** — the M174 texture-authoring correlation already narrows this: additive art
must be black-bordered and alpha-free, and mode 0/absent showed the clean additive signature while mode 1
showed the clean straight-alpha one. Extending that census to modes 6/7/8 specifically would at least
bound them.

**Q4 (`miscRenderFlags`)** - DONE (M259). Ran, and it retires the question: the flag is the constant 1
on 99.3% of emitters and no parsed field predicts the rest. Not flat across `isGroundLayer` as guessed -
that correlation turned out to be with field *presence*, i.e. schema version, not with the value.

That leaves **Q2** and **Q5** as the ones genuinely needing a capture — and Q2 is low-risk inference that
has never contradicted anything, so realistically **Q5 (stencil) is the only question where a capture is
the sole route.**

Since writing that, Q3 and Q4 have both been answered capture-free, and neither needed one: Q3 found
the V axis correct (the test was wrong), Q4 found nothing to decode. Two of the five questions have
dissolved. If the capture is ever taken, note that Q4's only real signal - bit 2 - clusters with the
stencil fields, so reading `miscRenderFlags` on the Q5 draws would settle both at once.

Which reframes the whole exercise: the capture is worth taking if you want stencil correct. For everything
else, cheaper evidence exists.
