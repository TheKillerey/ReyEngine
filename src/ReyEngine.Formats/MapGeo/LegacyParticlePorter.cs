using System.Numerics;
using ReyEngine.Formats.Particles;

namespace ReyEngine.Formats.MapGeo;

/// <summary>One legacy particle system, converted and ready to import.</summary>
/// <param name="Name">The stem as the legacy map wrote it, e.g. <c>CANDLE</c>.</param>
/// <param name="ParticlePath">The path the converted object is authored at. This is the IDENTITY - the
/// bin object's hash is FNV-1a of this string (verified 22 of 22 in a shipped Riot bin), and it is what
/// a placement's <c>system</c> ObjectLink points at. <c>particleName</c> is NOT the identity.</param>
/// <param name="PlacementCount">How many lines of the .dat referenced it.</param>
public sealed record LegacyParticleSystemPlan(
    string Name,
    string ParticlePath,
    TroyConversionResult Conversion,
    int PlacementCount)
{
    public uint SystemHash => Conversion.SystemHash;
    public string SourceFile { get; init; } = "";
}

/// <summary>One placement, resolved to a system and moved into the ported map's space.</summary>
public sealed record LegacyParticlePlacementPlan(
    LegacyParticlePlacement Source,
    string SystemName,
    uint SystemHash,
    Matrix4x4 Transform,
    string Name);

public sealed record LegacyParticlePortPlan(
    IReadOnlyList<LegacyParticleSystemPlan> Systems,
    IReadOnlyList<LegacyParticlePlacementPlan> Placements,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<TroyAssetMapping> Assets =>
        Systems.SelectMany(s => s.Conversion.Assets)
            .GroupBy(a => a.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

    public string Summary =>
        $"{Placements.Count} placement(s) across {Systems.Count} system(s), {Assets.Count} asset(s)"
        + (Warnings.Count == 0 ? "." : $"; {Warnings.Count} warning(s).");
}

/// <summary>
/// Ports a legacy map's particles - both the systems and where they stand (M531).
///
/// <para>Two halves that have to agree. The systems come from <c>.troybin</c> files converted by
/// <see cref="TroyBinConverter"/>; the placements come from <c>Particles.dat</c>, read by
/// <see cref="LegacyParticlePlacements"/>. This joins them: it converts each DISTINCT system once - Map2
/// has 554 placements over 16 systems - and resolves every placement to that system's hash.</para>
///
/// <para><b>On positions.</b> Legacy particle coordinates were measured to be in the same space as the
/// legacy NVR vertices: of Map2's 554 placements, 541 have an NVR vertex within 25 world units in XZ.
/// The map porter applies no rotation, scale, axis swap or negation to geometry - only a translation -
/// so a particle needs exactly that same translation and nothing else. It is passed in rather than
/// hardcoded because it is a port-time PARAMETER the user can edit; the durable way to recover it for
/// an already-ported map is to read <c>Transform.M41/M42/M43</c> off any imported mesh.</para>
///
/// <para><b>What is deliberately not carried across.</b> The rotation-looking triple in tokens 6-8 is
/// NOT applied - see <see cref="LegacyParticlePlacement.ClientVector"/> for why its meaning is
/// unresolved. Every one of Map2's 554 lines has it at zero, so for that map the question is moot; for
/// a map where it is not, the port says so in a warning rather than inventing an orientation.</para>
/// </summary>
public static class LegacyParticlePorter
{
    /// <summary>Where converted systems are authored. Riot's own maps use
    /// <c>Maps/Shipping/Map453/Particles/&lt;Name&gt;</c>; the existing Workshop importer uses the short
    /// form, and the two must not disagree or identical systems would land on different hashes.</summary>
    public const string DefaultPathPrefix = "Particles";

    /// <summary>
    /// Build the plan. Nothing is written here - the caller imports
    /// <see cref="LegacyParticleSystemPlan.Conversion"/> and applies the placements.
    /// </summary>
    /// <param name="placements">Parsed <c>Particles.dat</c>.</param>
    /// <param name="readTroy">System stem (lower-case) to the bytes of its <c>.troybin</c>, or null when
    /// it cannot be found. A system that does not resolve is reported and its placements are dropped -
    /// a placement pointing at a system that is not there is a hard error at map load.</param>
    /// <param name="translation">The same shift the ported geometry received.</param>
    public static LegacyParticlePortPlan Plan(
        LegacyParticlePlacementSet placements,
        Func<string, byte[]?> readTroy,
        Vector3 translation,
        string pathPrefix = DefaultPathPrefix)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(readTroy);

        var warnings = new List<string>(placements.Warnings);
        var systems = new Dictionary<string, LegacyParticleSystemPlan>(StringComparer.Ordinal);
        var failed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var placement in placements.Placements)
        {
            string key = placement.SystemName;
            if (systems.ContainsKey(key) || failed.Contains(key)) continue;

            byte[]? bytes;
            try { bytes = readTroy(key); }
            catch (IOException e) { bytes = null; warnings.Add($"{key}: {e.Message}"); }

            if (bytes is null)
            {
                failed.Add(key);
                warnings.Add($"{key}: no .troybin found - its placements are skipped");
                continue;
            }

            if (!TroyBinFile.TryParse(bytes, out var troy, out string? error) || troy is null)
            {
                failed.Add(key);
                warnings.Add($"{key}: could not be read ({error}) - its placements are skipped");
                continue;
            }

            // The name is the legacy stem so the result is recognisable in the editor; the PATH is what
            // the placement links to, so it has to be stable and unique per system.
            string name = OriginalStem(placement.SystemPath);
            var conversion = TroyBinConverter.Convert(troy, name, $"{pathPrefix}/{name}");
            systems[key] = new LegacyParticleSystemPlan(name, conversion.ParticlePath, conversion, 0)
            {
                SourceFile = key + ".troybin",
            };

            if (troy.Emitters.Count == 0)
                warnings.Add($"{name}: converted with no emitters - it will render nothing");
        }

        var plans = new List<LegacyParticlePlacementPlan>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int vectored = 0, grouped = 0, gated = 0;

        foreach (var placement in placements.Placements)
        {
            if (!systems.TryGetValue(placement.SystemName, out var system)) continue;

            counts[placement.SystemName] = counts.GetValueOrDefault(placement.SystemName) + 1;
            int ordinal = counts[placement.SystemName];

            if (placement.VectorIsApplied && placement.RawVector != Vector3.Zero) vectored++;
            if (placement.EmitterGroup is not null) grouped++;
            if (!placement.AlwaysSpawns) gated++;

            var transform = Matrix4x4.Identity;
            transform.Translation = placement.Position + translation;

            plans.Add(new LegacyParticlePlacementPlan(
                placement, system.Name, system.SystemHash, transform,
                $"{system.Name}_{ordinal:000}"));
        }

        // Say what did not come across. Each of these is a real property of the source that this port
        // does not reproduce, and a user who is not told will read the difference as a bug.
        if (vectored > 0)
            warnings.Add($"{vectored} placement(s) carry a non-zero orientation triple that is NOT applied - "
                + "its meaning is unresolved (the client discards the third component and the receiving "
                + "class has its RTTI stripped, so nothing establishes what it means).");
        if (grouped > 0)
            warnings.Add($"{grouped} placement(s) name an emitter group; groups are a runtime "
                + "pause/restart handle with no map-format equivalent, so the name is not carried.");
        if (gated > 0)
            warnings.Add($"{gated} placement(s) are gated on a graphics-detail level; modern maps have no "
                + "equivalent gate, so they are imported unconditionally.");

        var withCounts = systems.Values
            .Select(s => s with { PlacementCount = plans.Count(p => p.SystemHash == s.SystemHash) })
            .OrderByDescending(s => s.PlacementCount)
            .ToArray();

        return new LegacyParticlePortPlan(withCounts, plans, warnings);
    }

    /// <summary>Convenience: read the .dat and the .troybin folder off disk.</summary>
    /// <param name="particlesDat">Path to the legacy <c>Particles.dat</c>.</param>
    /// <param name="particleFolders">Folders holding the <c>.troybin</c> files, searched in order.</param>
    public static LegacyParticlePortPlan PlanFromDisk(
        string particlesDat,
        IEnumerable<string> particleFolders,
        Vector3 translation,
        string pathPrefix = DefaultPathPrefix)
    {
        var index = new LegacyAssetIndex(particleFolders, new[] { ".troybin" });
        return Plan(
            LegacyParticlePlacements.ParseFile(particlesDat),
            stem => index.TryResolve(stem + ".troybin", out string file) ? File.ReadAllBytes(file) : null,
            translation,
            pathPrefix);
    }

    /// <summary>The stem with its ORIGINAL casing - the .dat writes CANDLE, FireTorch_Med, env_fog_green,
    /// and a port that lower-cased them all would look nothing like the source map in the outliner.</summary>
    private static string OriginalStem(string reference)
    {
        string s = reference.Replace('\\', '/');
        int slash = s.LastIndexOf('/');
        if (slash >= 0) s = s[(slash + 1)..];
        int dot = s.LastIndexOf('.');
        return dot >= 0 ? s[..dot] : s;
    }
}
