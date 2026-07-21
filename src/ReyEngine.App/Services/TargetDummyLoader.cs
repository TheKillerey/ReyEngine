using System;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.App.Services;

/// <summary>
/// M115: loads Riot's practice-tool target dummy out of Map11.wad.client (the enemy/red variant) as a
/// <see cref="PropMesh"/> — mesh + skeleton + the idle .anm, so the preview renders and breathes it via
/// the existing prop pipeline. Read straight from the game install rather than the project mounts: the
/// dummy is an editor stand-in, not project content, and champion projects don't mount Map11.
/// Decoded once per process; a missing install just means the caller keeps its cube fallback.
/// </summary>
public static class TargetDummyLoader
{
    private const string Dir = "assets/characters/practicetool_targetdummy/skins/base/";

    private static PropMesh? _cached;
    private static bool _attempted;

    public static PropMesh? Get(string? gameDirectory, IHashResolver resolver, Action<string>? warn = null)
    {
        if (_attempted) return _cached;
        _attempted = true;
        try
        {
            var wadPath = GameReferenceLibrary.FindMap11Wad(gameDirectory);
            if (wadPath is null)
            {
                warn?.Invoke("Target dummy: Map11.wad.client not found in the game install — using the cube.");
                return null;
            }

            using var wad = WadArchive.Open(wadPath, resolver);
            byte[]? Read(string path)
            {
                var e = wad.Entries.FirstOrDefault(x => x.IsResolved && x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                return e is null ? null : wad.Extract(e);
            }

            var skn = Read(Dir + "practicetool_targetdummy.skn");
            var skl = Read(Dir + "practicetool_targetdummy.skl");
            var tex = Read(Dir + "practicetool_targetdummy_red_tx_cm.tex");
            if (skn is null)
            {
                warn?.Invoke("Target dummy: practicetool_targetdummy.skn not in Map11.wad — using the cube.");
                return null;
            }

            var mesh = SkinnedMeshDecoder.Decode(skn);
            var skeleton = skl is null ? null : SkeletonDecoder.Decode(skl);
            TextureImage? diffuse = tex is null ? null : TextureDecoder.Decode(tex);

            AnimationClip? idle = null;
            if (skeleton is not null
                && Read(Dir + "animations/targetdummy_idle060.anm") is { } anm)
            {
                try { idle = AnimationDecoder.Decode(anm, "targetdummy_idle060"); }
                catch { /* dummy still renders in bind pose */ }
            }

            var subs = mesh.SubMeshes
                .Select(s => new PropSubmesh(s.StartIndex, s.IndexCount, diffuse))
                .ToList();
            _cached = new PropMesh("practicetool_targetdummy", mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Indices, subs)
            { SknMesh = mesh, Skeleton = skeleton, IdleClip = idle };
            return _cached;
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Target dummy: {ex.Message} — using the cube.");
            return null;
        }
    }
}
