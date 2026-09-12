# What the engine binds when a particle emitter has no texture

M707. The first of the readings taken from `LeagueToolkit/ltk-manager`, whose particle renderer records 50
measured statements about engine behaviour in `docs/plans/vfx-particle-renderer.md`. This is reading 2.44,
checked against their implementation and their own tests before anything here changed.

## The rule

An emitter's base (diffuse) texture field has three states, not two, and the third is the one we collapsed:

| the emitter | what the engine binds on slot 0 | what it draws |
| --- | --- | --- |
| names a texture that is loaded | that texture | the sprite |
| names a texture that has not arrived | no sampler at all | untextured |
| names **no** texture | a 1x1 **transparent black** | nothing |

Any slot other than the base that is left unbound is a different story per stage: only the erosion map gets
a white constant, and the multiply, ramp and palette stages are compiled out of the pass entirely.

Evidence in the clone: `src/modules/workshop/bin/vfx/useVfxTextures.ts` builds `const UNNAMED =
unnamedTexture()`, which is `new DataTexture(new Uint8Array(4), 1, 1)` - four zero bytes - and its
`samplersOf` returns the unnamed bundle only when `definition.emitter.texture === null`, the no-sampler
bundle otherwise. Their `useVfxTextures.test.ts` pins both arms: one case binds the transparent texel where
the emitter names no texture, the other waits on a named texture until it arrives. The rule reaches their
mesh and ribbon draws through the same bundle, not just quads.

## What we were doing

One case where the engine has three, and each host was wrong in its own way.

- Both OpenGL viewports substituted the soft placeholder dot for *any* missing base texture, named or not.
- The Direct3D 11 map viewport did the same and counted every one as an unresolved sprite.
- The Direct3D 11 preview window bound nothing at all, and an unbound slot is filled by our own renderer's
  stand-in with an opaque 1x1 **white**. That is the hard white card reported on `Ahri_Skin89_E_mis`.

The white card had a second arm one level down, reached when a texture *is* named and cannot be read: the
decode returned null, nothing was bound, and the same opaque white stood in. Only the preview window had
this; the other two hosts showed the soft dot. Both arms are fixed.

## The population

Measured over 180 archives - `DATA/FINAL/Maps/Shipping` and all 174 champion WADs - 15,581 bins carrying
VFX, 214,201 system definitions, 1,581,956 emitters. The probe reads the emitter's `texture` field through
the editor's own reader and cross-checks the wire form, because patch 16.17 turned several texture fields
into 64-bit chunk links; **0** emitters carry a link-form base texture, so the parsed view and the wire form
agree and the count is not inflated by a field we cannot read.

**23,006 emitters name no base texture.** By draw path: 19,658 billboard, 3,186 mesh, 162 ribbon.

That number is *not* the number of emitters this change affects, and the difference is the whole reason to
write it down. `VfxParticleSimulator.SetSystem` never runs an emitter `IsVisual` refuses, and `IsVisual`
wants a base texture, a multiply texture, a mesh file or a distortion normal map. So of the 23,006:

- **18,417 never reach a renderer at all.** The 12,845 that exist only to spawn child systems, the 3,155
  that author nothing, and the colour-ramp, erosion, palette and reflection-only emitters are all dropped
  before any texture is bound. They were never drawing a placeholder dot, because they were never drawn.
- **2,493** name a real `.scb`/`.sco` mesh and no sprite. Every host already refuses an untextured mesh
  emitter, so they were invisible before and are invisible now. That is worth its own look: real geometry,
  silently dropped for want of a sprite.
- **about 240** actually change - the 231 that author a multiply texture and no base, and the 9 that author
  a distortion normal map and no base. These stop showing a placeholder they never had in game.

Every one of those stages is a *modulator* of the base texel in Riot's `quad_ps`: erosion multiplies alpha,
the multiply texture multiplies RGB, the palette replaces RGB and keeps the base alpha, distortion is masked
by base alpha. With a transparent base the product is zero in the client too, so drawing nothing is the
client-accurate answer and the tinted dot was the divergence. The build report names them anyway, because
"the effect is missing" and "the effect was never drawn" look identical on screen.

## What the port had to avoid breaking

Three things the transparent texel breaks if it is dropped in naively, all found by review before shipping:

1. **Heat haze turns black.** `ShaderPreviewRenderer.DrawDistortion` tints the refracted scene by the
   emitter's diffuse RGB and takes its output alpha from the normal map. Its "this emitter ships no diffuse"
   identity was a *null handle* test, and a bound transparent texel is not null - so the tint would collapse
   to zero while the alpha survived, painting a black smear exactly where the engine draws nothing. The pass
   now refuses an emitter whose base slot holds the texel, which is what OpenGL already did by masking on
   that texel's alpha.
2. **Ribbons cost a frame to draw nothing.** The OpenGL beam and trail guards test the texture *handle*, and
   the texel is a handle. Without a matching refusal the viewport would assemble and upload a ribbon every
   frame to show a transparent one, while the Direct3D 11 host refused the same emitter and said so.
3. **Stand-ins named by hand.** The mesh and ribbon guards compared against the soft-dot key by literal, so
   the new key walked straight past both. The test is now `VfxPlaybackSim.IsStandIn`, asked of the set.

## Related, found on the way

`ViewportControl.SpawnQueuedChildren` indexed the per-emitter child list by the *simulator's* emitter
position while the list is built by *authored* position. Since the simulator drops non-visual emitters -
12,845 of which exist for nothing but spawning children - a spawning emitter read a different emitter's
child list as soon as anything before it was dropped. Fixed as M708. The deeper question, whether a
non-visual carrier should be simulated at all so its children spawn, is not answered here.
