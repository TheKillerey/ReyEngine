using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// M754: turn an emitter's <see cref="VfxEmissionSurface"/> into something the simulator can sample, reading
/// the files it names through the host's asset reader. Every failure leaves the emitter emitting from its
/// point - the pre-M754 behaviour - and says why, so a surface that did not load is never mistaken for one
/// the game ignores.
/// </summary>
public static class VfxEmissionSurfaceLoader
{
    /// <summary>The loaded sampler, or null with the reason.</summary>
    public readonly record struct Result(VfxSurfaceSampler? Sampler, string? Why);

    public static Result Load(VfxEmissionSurface surface, Func<string, byte[]?> read)
    {
        try
        {
            return surface.Kind switch
            {
                VfxEmissionSurfaceKind.LegacyMesh or VfxEmissionSurfaceKind.Mesh => LoadMesh(surface, read),
                VfxEmissionSurfaceKind.Skeleton => LoadSkeleton(surface, read),
                _ => new Result(null, "emits from the character it is attached to - it needs a character host"),
            };
        }
        catch (Exception ex) { return new Result(null, $"{Path.GetFileName(surface.MeshPath ?? surface.SkeletonPath)}: {ex.Message}"); }
    }

    private static Result LoadMesh(VfxEmissionSurface surface, Func<string, byte[]?> read)
    {
        string path = surface.MeshPath!;
        if (read(path) is not { } bytes) return new Result(null, $"{Path.GetFileName(path)} is not in the game or the project");
        if (path.EndsWith(".skn", StringComparison.OrdinalIgnoreCase))
        {
            // bind pose: AnimationName (a full .anm path) and the host's pose are M755's
            var m = SkinnedMeshDecoder.Decode(bytes);
            var keep = SubmeshFilter(m, surface.Submeshes);
            if (surface.Submeshes is { Count: > 0 } && keep is null)
                // Locke_Skin05_Q_MissedTerrain lists a submesh Locke_Base.skn does not have. What the game does
                // then is unknown, so the preview does not guess the whole body for it.
                return new Result(null, $"none of its {surface.Submeshes.Count} listed submesh(es) is in {Path.GetFileName(path)}");
            var sampler = VfxSurfaceSampler.FromMesh(m.Positions, m.Indices, surface.Scale, keep);
            return sampler is null ? new Result(null, $"{Path.GetFileName(path)} has no triangles with area") : new Result(sampler, null);
        }
        var sm = StaticObjectDecoder.Decode(bytes, path);
        if (sm is null) return new Result(null, $"{Path.GetFileName(path)} did not decode");
        var s = VfxSurfaceSampler.FromMesh(sm.Positions, sm.Indices, surface.Scale);
        return s is null ? new Result(null, $"{Path.GetFileName(path)} has no triangles with area") : new Result(s, null);
    }

    /// <summary>The triangles of the listed submeshes. The list holds FNV-1a hashes of the submesh names -
    /// measured on Hwei, Udyr, Locke, Swain and Lissandra: every listed hash that names a submesh at all is
    /// the FNV-1a of it, none is the ELF. Null for "every triangle", and for a list that names nothing here.</summary>
    public static Func<int, bool>? SubmeshFilter(MeshAsset mesh, IReadOnlyList<uint>? submeshes)
    {
        if (submeshes is not { Count: > 0 }) return null;
        var wanted = submeshes.ToHashSet();
        var ranges = mesh.SubMeshes
            .Where(s => wanted.Contains(HashAlgorithms.Fnv1a(s.Material)))
            .Select(s => (First: s.StartIndex / 3, End: (s.StartIndex + s.IndexCount) / 3))
            .ToList();
        if (ranges.Count == 0) return null;
        return t => ranges.Any(r => t >= r.First && t < r.End);
    }

    private static Result LoadSkeleton(VfxEmissionSurface surface, Func<string, byte[]?> read)
    {
        string path = surface.SkeletonPath!;
        if (read(path) is not { } bytes) return new Result(null, $"{Path.GetFileName(path)} is not in the game or the project");
        var skl = SkeletonDecoder.Decode(bytes);
        var wanted = surface.Joints is { Count: > 0 } j ? j.ToHashSet() : null;
        // JointMask holds FNV-1a hashes of joint names (Shyvana's dragon: 127 of 127 match, ELF 0). A mask
        // may name joints this skeleton lacks - the base dragon lists 253 against 127 - which only drops them.
        var points = skl.Bones
            .Where(b => wanted is null || wanted.Contains(HashAlgorithms.Fnv1a(b.Name)))
            .Select(b => b.WorldPosition)
            .ToList();
        if (points.Count == 0 && wanted is not null)
            return new Result(null, $"none of its {wanted.Count} masked joint(s) is in {Path.GetFileName(path)}");
        var sampler = VfxSurfaceSampler.FromPoints(points, surface.Scale);
        return sampler is null ? new Result(null, $"{Path.GetFileName(path)} has no joints") : new Result(sampler, null);
    }

    /// <summary>Per emitter of a system, aligned to its emitter list; null when no emitter names a surface.</summary>
    public static IReadOnlyList<VfxSurfaceSampler?>? LoadSystem(VfxSystemDefinition system, Func<string, byte[]?> read,
        Action<string, string>? why = null)
    {
        VfxSurfaceSampler?[]? samplers = null;
        for (int i = 0; i < system.Emitters.Count; i++)
        {
            if (system.Emitters[i].EmissionSurface is not { } surface) continue;
            var r = Load(surface, read);
            if (r.Sampler is null) { why?.Invoke(system.Emitters[i].Name, r.Why ?? "did not load"); continue; }
            samplers ??= new VfxSurfaceSampler?[system.Emitters.Count];
            samplers[i] = r.Sampler;
        }
        return samplers;
    }
}
