using System.Collections.Generic;
using System.Numerics;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// A request to play a placed VFX system live in the viewport (M36): the parsed system, the world
/// position of its placement, and one resolved sprite texture per emitter (aligned to
/// <see cref="VfxSystemDefinition.Emitters"/>; null → the viewport uses a procedural soft-dot fallback).
/// Built on the UI thread by the view-model; consumed on the GL thread by the viewport.
/// </summary>
public sealed record VfxPlayback(
    VfxSystemDefinition System,
    Vector3 WorldPos,
    IReadOnlyList<TextureImage?> EmitterTextures);
