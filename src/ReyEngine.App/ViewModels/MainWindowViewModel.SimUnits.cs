using System.Text.RegularExpressions;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Meshes;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M874: the host's part of a summoned unit (Kalista's Sentinel). A unit is a PROP: the same decode a map's props use (<see cref="TryBuildPropMesh"/>: the skin bin's mesh,
/// skeleton, materials and animation graph, read from whatever is mounted - the champion's own WAD holds <c>Characters/KalistaSpawn</c>), drawn by the prop driver the dummies use.
/// There is no second loader.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The unit's meshes for the simulation: one standing in <paramref name="idleClip"/>, one walking in <paramref name="runClip"/> (graph clip names; the prop builder falls back to the skin's
    /// idle when a name resolves to nothing), with the skin's <c>initialSubmeshToHide</c> left out. Null when the skin is not mounted or does not decode.</summary>
    private SimUnitMeshes? BuildSimUnitMeshes(string skin, string? idleClip, string? runClip)
    {
        try
        {
            if (!ContentLoaded) return null;
            var cache = new Dictionary<string, Core.Decoding.TextureImage?>(StringComparer.OrdinalIgnoreCase);
            var idle = TryBuildPropMesh(skin, cache, idleClip);
            if (idle is null) return null;
            var run = runClip is null ? null : TryBuildPropMesh(skin, cache, runClip);
            PropMesh WithoutHidden(PropMesh m, string key)
            {
                var hidden = m.SkinBinBytes is { } bin ? SkinMeshExtractor.Extract(bin, ResolveWadPath)?.HiddenSubmeshes : null;
                var names = m.SknMesh?.SubMeshes;
                if (hidden is not { Count: > 0 } || names is null || names.Count != m.Submeshes.Count) return m with { Key = key };
                var kept = m.Submeshes.Where((s, i) => !hidden.Any(h => h.Equals(names[i].Material, StringComparison.OrdinalIgnoreCase))).ToList();
                return m with { Key = key, Submeshes = kept };
            }
            _log.Info("Simulation", $"summoned unit {skin}: {idle.Submeshes.Count} submesh(es), {idle.Submeshes.Count(x => x.Texture is not null)} with a diffuse, skin scale {idle.SkinScale}, skeleton {(idle.Skeleton is null ? "none" : idle.Skeleton.BoneCount + " bones")}, idle clip {(idle.IdleClip is null ? "none" : idle.IdleClip.Name)}, run clip {(run?.IdleClip is null ? "none" : run.IdleClip.Name)}.");
            return new SimUnitMeshes(WithoutHidden(idle, skin + "#idle"), run is null ? null : WithoutHidden(run, skin + "#run"));
        }
        catch (Exception ex)
        {
            _log.Warn("Simulation", $"summoned unit {skin}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The number of the skin on stage: <c>skin24</c> of the preview's skin bin gives 24 (null when no skin is loaded).</summary>
    private int? PreviewSkinNumber()
    {
        var m = Regex.Match(_previewSkinBin ?? "", @"skin(\d+)\.bin", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : null;
    }
}
