using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Vfx;

/// <summary>One emitter that was switched off.</summary>
public sealed record VfxProjectionEdit(string System, string Emitter, float Size, string Texture);

/// <summary>
/// M741: switch off the projection sprites of the old jade effects.
///
/// <para><b>What a projection sprite is.</b> Riot's own name for it: the Map453 turret's basic attack
/// carries an emitter called <c>projected</c> drawing <c>blue-proj.tex</c>, and the reporter had already
/// disabled exactly that to fix the turret shots. The same emitter class runs through the jade champion
/// effects under three spellings - <c>Proj_*</c>, <c>*-proj</c> and <c>ProjectedLight*</c> - and
/// Malzahar's jade Q carries <c>Proj_purple</c> at 800x800 on additive blend, where his modern Q's largest
/// emitter is 200x450 and has no projection sprite at all.</para>
///
/// <para><b>Why disable rather than clip.</b> A navmesh mask bounds a GROUND layer; these are not ground
/// layers, so the mask does not reach them - measured, and the reason the first attempt at this changed
/// nothing. Disabling is what the turret needed and what the reporter asked for.</para>
/// </summary>
public static class VfxProjectionEmitters
{
    private static readonly uint EmitterClass = HashAlgorithms.Fnv1a("VfxEmitterDefinitionData");
    private static readonly uint F_emitterName = HashAlgorithms.Fnv1a("emitterName");
    private static readonly uint F_disabled = HashAlgorithms.Fnv1a("disabled");

    /// <summary>Riot's three spellings for a projection sprite, on the emitter NAME or its texture.</summary>
    public static bool IsProjection(string? emitterName, string? texturePath)
    {
        string n = emitterName ?? "";
        if (n.StartsWith("Proj", StringComparison.OrdinalIgnoreCase)) return true;   // Proj_purple, ProjectedLight, projected
        if (n.Contains("-proj", StringComparison.OrdinalIgnoreCase)) return true;    // dark-proj, hole-proj
        string tex = System.IO.Path.GetFileName(texturePath ?? "");
        return tex.Contains("-proj.", StringComparison.OrdinalIgnoreCase);           // blue-proj.tex
    }

    /// <summary>
    /// Disable every projection sprite of a <c>Jade*</c> system. Returns the new bytes, the input unchanged
    /// when nothing matched, or null with a reason.
    /// </summary>
    public static byte[]? Disable(byte[] bin, out IReadOnlyList<VfxProjectionEdit> edits, out string? error)
    {
        ArgumentNullException.ThrowIfNull(bin);
        var done = new List<VfxProjectionEdit>();
        edits = done;
        error = null;

        BinTree tree;
        IReadOnlyDictionary<uint, VfxSystemDefinition> systems;
        try
        {
            tree = SafeBinTree.Parse(bin);
            SafeBinTree.ThrowIfLossy(tree, "disabling projection sprites");
            systems = VfxSystemResolver.ExtractAll(bin);
        }
        catch (Exception ex) { error = $"could not read the bin: {ex.Message}"; return null; }

        foreach (var (hash, sys) in systems)
        {
            if (sys.Name is not { } sysName || !sysName.Contains("Jade", StringComparison.OrdinalIgnoreCase)) continue;
            if (!tree.Objects.TryGetValue(hash, out var obj)) continue;

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in sys.Emitters)
            {
                if (e.Disabled || !IsProjection(e.Name, e.TexturePath)) continue;
                var c = e.BirthScale.Constant;
                if (wanted.Add(e.Name ?? ""))
                    done.Add(new VfxProjectionEdit(sysName, e.Name ?? "(emitter)",
                        MathF.Max(MathF.Abs(c.X), MathF.Abs(c.Y)),
                        System.IO.Path.GetFileName(e.TexturePath ?? "")));
            }
            if (wanted.Count == 0) continue;

            foreach (var (_, prop) in obj.Properties)
            {
                if (prop is not BinTreeContainer c) continue;
                foreach (var el in c.Elements)
                {
                    if (el is not BinTreeStruct s || s.ClassHash != EmitterClass) continue;
                    string name = s.Properties.GetValueOrDefault(F_emitterName) is BinTreeString n ? n.Value : "";
                    if (!wanted.Contains(name)) continue;
                    // 'disabled' is absent when false (M652), so this adds it where it is missing.
                    if (s.Properties.GetValueOrDefault(F_disabled) is BinTreeBool b) b.Value = true;
                    else if (s.Properties.GetValueOrDefault(F_disabled) is BinTreeBitBool bb) bb.Value = true;
                    else s.Properties[F_disabled] = new BinTreeBool(F_disabled, true);
                }
            }
        }

        if (done.Count == 0) return bin;

        byte[] result;
        try { using var ms = new MemoryStream(); tree.Write(ms); result = ms.ToArray(); }
        catch (Exception ex) { error = $"could not write the bin: {ex.Message}"; return null; }

        // Re-read and prove every one of them is off.
        try
        {
            var back = VfxSystemResolver.ExtractAll(result);
            foreach (var edit in done)
            {
                var em = back.Values.FirstOrDefault(s => s.Name == edit.System)?
                    .Emitters.FirstOrDefault(e => e.Name == edit.Emitter);
                if (em is null || !em.Disabled)
                { error = $"'{edit.System}' / '{edit.Emitter}' did not stay disabled."; return null; }
            }
        }
        catch (Exception ex) { error = $"the rewritten bin no longer reads: {ex.Message}"; return null; }

        return result;
    }
}
