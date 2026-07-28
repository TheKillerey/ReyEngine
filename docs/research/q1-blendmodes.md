# M260 - Q1: blend modes 6/7/8, and an anchor result that was not asked for

Q1 asked what each `blendMode` integer means, and modes 6/7/8 specifically - they fall off the end of the
shipped guess `IsAdditive(m) => m is 1 or 3 or 4 or 5`. The plan expected the texture-authoring census to
"at least bound them". It bounds them, and it also contradicts the mapping on the modes nobody asked about.

## Method

Additive blending treats black as transparent, so additive sprites are authored on a black field and
their alpha channel goes unused. Straight-alpha sprites carry the silhouette in alpha and their borders
are transparent rather than black. Those are measurably different things.

Per texture, over 1,221,115 emitters and 26,924 decoded textures in 203 WADs:

- `darkBorder` - fraction of border pixels with luma < 16
- `clearBorder` - fraction of border pixels with alpha < 16
- `opaque` - fraction of all pixels with alpha == 255

and then each texture is **classified**, not averaged. That matters: a mean of 46% darkBorder is equally
consistent with "genuinely intermediate" and with "half of one cluster, half of another", and mode 4 -
the largest mode - sits exactly where that ambiguity would hide.

## Result

| mode | emitters | additive | straight | premult | opaque | other |
|---|---:|---:|---:|---:|---:|---:|
| absent | 96,890 | **65%** | 3% | 23% | 6% | 3% |
| 1 | 487,359 | 1% | **54%** | 27% | 9% | 10% |
| 2 | 7,310 | **65%** | 5% | 21% | 6% | 4% |
| 3 | 25,475 | 9% | 4% | 2% | **75%** | 10% |
| 4 | 598,849 | 23% | **42%** | 23% | 4% | 8% |
| 5 | 5,004 | 1% | 17% | **58%** | 14% | 9% |
| 6 | 85 | 0% | 0% | 0% | 0% | **100%** |
| 7 | 49 | 15% | 5% | 25% | **55%** | 0% |
| 8 | 94 | 0% | 0% | 0% | **100%** | 0% |

**Mode 0 does not exist.** Not one emitter in 1.2 million carries the literal value 0; `absent` is the
only zero case. The shipped `IsBlendModeUnderstood(m) => m is >= 0 and <= 5` therefore covers a value that
is never authored.

## The answer for 6, 7 and 8

**Mode 6 (85 emitters, 6 textures) - all six are shadow masks with a pure white border.** `borderLuma` is
exactly 255.0 on every one, and none matches any of the four conventions. White is the identity for
*multiply*, the way black is for additive, so this looks like a multiply/mask mode.

```
    0 %    0 %   46 %   255  Vex_Main_ShadowVFX_v2_mask.tex
    0 %    0 %   49 %   255  Vex_Skin01_Shadow_Mask.tex
    0 %    0 %   59 %   255  Vex_Skin01_Shadow_MaskOutline.tex
    0 %    0 %   41 %   255  Shadow_Mask.tex
    0 %    0 %   32 %   255  Vex_S_Shadow_Mask_Test.tex
    0 %    0 %   60 %   255  Vex_S_Shadow_Mask_Test_1.tex
```

Consistent, but be honest about what n=6 means here: every one is Vex's shadow mechanic. That is one
design decision sampled six times, not six independent samples. It bounds mode 6 as "not additive, not
straight alpha, plausibly multiply" and no further.

**Mode 7 (49 emitters, 20 textures) - no single convention.** Additive, premultiplied, opaque and
straight all appear in the same 20 textures: `Corki_Skin26_R_Muzzle_Flash` is 100% dark border and 100%
opaque (clean additive), `Tristana_Skin51_BA_BlastShapes` is 100% clear border and 3% opaque (clean
straight), and nine `FizzShark_*_TX_CM` colour maps are fully opaque. Art authoring cannot assign this
one a blend state, and saying so is the finding.

**Mode 8 (94 emitters, 14 textures) - flat colour sources, alpha entirely unused.** Every texture is 100%
opaque with mean alpha 255, and the set includes `Vex_Blank.tex` and `color-hold.tex`. These are not
sprites; they are flat colour holds. Whatever mode 8 is, the texture is a colour input rather than a
shape.

**The bound the plan wanted:** none of 6/7/8 shows the additive signature (0%, 15%, 0%). Extending
`IsAdditive` to cover them would be wrong. The current fallback - anything above 5 renders as alpha - is
consistent with the art for 6 and 8 and undecidable for 7.

## The anchor result, which is the bigger news

The anchors were included to calibrate the columns. They came back contradicting the shipped mapping:

| mode | art says | we render as |
|---|---|---|
| absent | additive (65%) | additive (defaults to 1) - **agrees** |
| 1 | straight alpha (54%, only 1% additive) | **additive** |
| 2 | additive (65%) | **alpha** |
| 3 | opaque (75%) | **additive** |
| 4 | mixed, leaning straight (42% vs 23%) | **additive** |
| 5 | premultiplied (58%, only 1% additive) | **additive** |

`IsAdditive(m) => m is 1 or 3 or 4 or 5` marks as additive the four modes whose art is *least* additive,
and marks as alpha the one mode besides `absent` whose art is *cleanly* additive. That covers 1,119,000
emitters.

### How much to trust this

**The additive class is robust.** It requires a dark border *and* `opaque > 0.5` - alpha near 255
everywhere - so the RGB it measures is real authored content, not encoder padding.

**The premultiplied class is not.** In BC1/BC3, RGB is arbitrary wherever alpha is 0, and encoders
routinely zero it, which produces a dark border and a clear border at once - exactly the premultiplied
signature. Some share of the `premult` column is compression artifact rather than authoring intent. That
weakens "mode 5 is premultiplied" but not "mode 5 is not additive" (1%).

**This does not prove the renderer is wrong.** Art authoring implies intent, not the pipeline state Riot
actually sets. And the visual evidence is neutral: our additive is `SrcAlpha, One`, not `One, One`, so
alpha-authored art rendered additively still respects its silhouette and merely comes out brighter. That
is why nobody has noticed - not evidence that the mapping is right.

## What was NOT changed

Nothing. No mapping was flipped. The evidence here is indirect, and flipping a boolean on indirect
evidence is the same move as flipping `Corner()` on a broken V-axis test would have been in M258 - it
would look like a fix and go green either way. `BlendFromRiotMode`'s comment now records the measurement
so the next person meets the contradiction rather than the guess.

## What this does to the capture plan

It **promotes Q1**. The plan concluded that Q5 (stencil) was realistically the only question needing a
capture, on the grounds that Q1 was partly bounded by this very census. Having run it, the census cannot
settle the integer-to-state table and now actively disputes the shipped one over a million emitters. Q1
is the highest-value thing a capture would answer, ahead of stencil.

If a capture is taken, read the output-merger blend state on particle draws and match each back to its
emitter via the bound `TEXTURE__TX` filename. Six or seven emitters covers modes 1-5, which is where the
population is.

Raw output: `q1-blendmodes-data.txt`. Harness mode: `blendcensus`.
