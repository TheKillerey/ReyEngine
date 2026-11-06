using System;
using System.Collections.Generic;
using System.Linq;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>M861: the bins of the loaded skin's VFX closure (the skin bin and everything it links), kept so a
    /// <c>CustomMaterial</c> link can be looked up in the bin that defines it.</summary>
    private IReadOnlyList<byte[]> _vfxClosureBins = Array.Empty<byte[]>();

    /// <summary>The bin (of the closure) defining a StaticMaterialDef, by object hash; null when none does. Cached.</summary>
    private readonly Dictionary<uint, (byte[] Bin, string Name)?> _customMaterialBins = new();

    private (byte[] Bin, string Name)? FindCustomMaterial(uint link)
    {
        if (_customMaterialBins.TryGetValue(link, out var cached)) return cached;
        (byte[] Bin, string Name)? found = null;
        foreach (var bin in _vfxClosureBins)
        {
            try
            {
                var tree = SafeBinTree.Parse(bin, out _);
                if (!tree.Objects.ContainsKey(link)) continue;
                var doc = MaterialDocument.Parse(bin, ResolveBinName, ResolveWadPath, p => { try { return ReadAsset(HashAlgorithms.WadPath(p)); } catch { return null; } });
                if (doc.Materials.FirstOrDefault(m => m.ObjectPathHash == link) is { } binding) { found = (bin, binding.Name); break; }
            }
            catch { /* a bin that will not parse is not the one */ }
        }
        return _customMaterialBins[link] = found;
    }

    private readonly Dictionary<(uint System, int Emitter), Services.PreparedCharacterScene?> _customMaterialScenes = new();

    /// <summary>M861: per emitter, Riot's material for its <c>CustomMaterial</c>, prepared over the emitter's mesh the way a prop is
    /// (Dx11CharacterScene) - shader, textures, parameters. Null where there is none to draw. The emitter's colour and fade do not reach
    /// these materials; their own drivers stand at rest.</summary>
    private IReadOnlyList<Services.PreparedCharacterScene?>? ResolveSystemCustomMaterials(VfxSystemDefinition sys, IReadOnlyList<StaticMeshData?>? meshes)
    {
        if (meshes is null || OpenDx11ShaderCache(out _) is not { } cache) return null;
        var result = new Services.PreparedCharacterScene?[sys.Emitters.Count];
        for (int i = 0; i < sys.Emitters.Count; i++)
        {
            var def = sys.Emitters[i];
            if (def.CustomMaterialLink == 0 || i >= meshes.Count || meshes[i] is not { } mesh) continue;
            if (_customMaterialScenes.TryGetValue((sys.PathHash, i), out var cached)) { result[i] = cached; continue; }
            Services.PreparedCharacterScene? scene = null;
            if (FindCustomMaterial(def.CustomMaterialLink) is { } material)
            {
                try
                {
                    scene = Services.Dx11CharacterScene.Prepare(Array.Empty<byte>(), material.Bin, cache, ShaderPerms(),
                        readAsset: h => { try { return ReadAsset(h); } catch { return null; } },
                        resolveBinName: ResolveBinName, resolveWadPath: ResolveWadPath,
                        fallbackShader: null,
                        decodedMesh: Services.VfxCustomMaterial.MeshOf(mesh, material.Name));
                    if (scene is not { Slices.Count: > 0 })
                    {
                        _log.Warn("VFX", $"'{sys.Name}' / {def.Name}: custom material {material.Name} did not resolve"
                                         + (scene?.Failures.FirstOrDefault() is { } why ? ": " + why : "") + " - drawn with the particle shader.");
                        scene = null;
                    }
                }
                catch (Exception ex) { _log.Warn("VFX", $"'{sys.Name}' / {def.Name}: custom material - {ex.Message}"); scene = null; }
            }
            else _log.Warn("VFX", $"'{sys.Name}' / {def.Name}: custom material 0x{def.CustomMaterialLink:x8} is in none of the skin's bins - drawn with the particle shader.");
            result[i] = _customMaterialScenes[(sys.PathHash, i)] = scene;
        }
        return result;
    }
}
