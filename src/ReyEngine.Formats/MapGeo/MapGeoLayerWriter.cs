using System.Buffers.Binary;
using LeagueToolkit.Core.Environment;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M105: surgical writer for the mapgeo layer system — the per-mesh dragon-layer bitmask
/// (EnvironmentVisibility byte), the visibility-controller hash, and the backface-culling flag.
///
/// v17/18 mesh record layout around those bytes (verified against every mesh of the live
/// map11/map12/map21/map30 mapgeos — 6,450/6,450 matched on all anchors):
///
///   … indexBufferId │ vis u8 │ region u32 (v18 only) │ ctrl u32 │ submeshCount u32 │
///   submeshes[] │ backface u8 │ bbox (24) │ transform (64) │ …
///
/// Each mesh is located by its unique 88-byte [bbox][transform] signature (the same mechanism
/// MapGeoWriter uses for transforms; duplicate signatures are ranked in file order). Every byte is
/// VERIFIED against the decoded value before anything is written — one mismatch aborts the whole
/// save rather than corrupting the file.
/// </summary>
public static class MapGeoLayerWriter
{
    public static bool HasEdits(IEnumerable<MapGeoMesh> meshes) => meshes.Any(m => m.HasLayerEdit);

    /// <summary>Patch all pending layer/controller/backface edits into a copy of the mapgeo bytes.
    /// Null + <paramref name="error"/> when the version is unsupported or an anchor check fails.</summary>
    public static byte[]? TryWriteLayerEdits(byte[] originalMapgeo, IReadOnlyList<MapGeoMesh> meshes, out string? error)
    {
        error = null;
        var edits = meshes.Where(m => m.HasLayerEdit).ToList();
        if (edits.Count == 0) return originalMapgeo;

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(originalMapgeo.AsSpan(4, 4));
        if (version is < 17 or > 18)
        {
            error = $"Layer editing supports mapgeo v17/18 — this file is v{version}.";
            return null;
        }
        bool hasRegion = version >= 18;

        EnvironmentAsset env;
        try
        {
            using var ms = new MemoryStream(originalMapgeo, writable: false);
            env = new EnvironmentAsset(ms);
        }
        catch (Exception ex) { error = $"Mapgeo did not parse: {ex.Message}"; return null; }

        // signature → file occurrences, ranked so exact-duplicate meshes resolve in file order
        var byEdit = edits.ToDictionary(m => m.Index);
        var groups = new Dictionary<string, (byte[] Sig, List<int> Indices)>(StringComparer.Ordinal);
        for (int i = 0; i < env.Meshes.Count; i++)
        {
            var sig = MapGeoWriter.BuildSignature(env.Meshes[i]);
            var key = Convert.ToBase64String(sig);
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = (sig, new List<int>());
            g.Indices.Add(i);
        }

        var result = (byte[])originalMapgeo.Clone();
        int written = 0;
        foreach (var (_, g) in groups)
        {
            if (!g.Indices.Any(byEdit.ContainsKey)) continue;
            var occurrences = MapGeoWriter.FindAll(result, g.Sig);
            if (occurrences.Count != g.Indices.Count)
            {
                error = $"Mesh signature matched {occurrences.Count} location(s) for {g.Indices.Count} mesh(es) — file layout not as expected, nothing was written.";
                return null;
            }
            for (int rank = 0; rank < g.Indices.Count; rank++)
            {
                int meshIndex = g.Indices[rank];
                if (!byEdit.TryGetValue(meshIndex, out var edit)) continue;
                var lt = env.Meshes[meshIndex];
                int sigOff = occurrences[rank];

                int submeshBytes = 0;
                foreach (var sm in lt.Submeshes)
                    submeshBytes += 4 + 4 + System.Text.Encoding.UTF8.GetByteCount(sm.Material) + 16;

                int backfaceOff = sigOff - 1;
                int ctrlOff = backfaceOff - submeshBytes - 4 - 4;
                int regionOff = hasRegion ? ctrlOff - 4 : -1;
                int visOff = (hasRegion ? regionOff : ctrlOff) - 1;
                if (visOff < 0) { error = $"Mesh #{meshIndex}: computed offset out of range."; return null; }

                // ---- verify EVERY anchor against the decoded values before touching a byte ----
                if (result[visOff] != (byte)lt.VisibilityFlags)
                { error = $"Mesh #{meshIndex}: visibility anchor mismatch (file 0x{result[visOff]:x2}, expected 0x{(byte)lt.VisibilityFlags:x2}) — nothing was written."; return null; }
                if (BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(ctrlOff)) != lt.VisibilityControllerPathHash)
                { error = $"Mesh #{meshIndex}: controller anchor mismatch — nothing was written."; return null; }
                if (hasRegion && BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(regionOff)) != lt.UnknownVersion18Int)
                { error = $"Mesh #{meshIndex}: region anchor mismatch — nothing was written."; return null; }
                if (result[backfaceOff] != (lt.DisableBackfaceCulling ? 1 : 0))
                { error = $"Mesh #{meshIndex}: backface anchor mismatch — nothing was written."; return null; }

                result[visOff] = (byte)edit.EffectiveVisibility;
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(ctrlOff), edit.EffectiveController);
                result[backfaceOff] = (byte)(edit.EffectiveDisableBackface ? 1 : 0);
                written++;
            }
        }

        if (written != edits.Count)
        {
            error = $"Only {written} of {edits.Count} edited mesh(es) could be located — nothing usable was produced.";
            return null;
        }
        return result;
    }
}
