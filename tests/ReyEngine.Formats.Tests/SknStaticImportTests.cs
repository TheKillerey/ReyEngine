using System;
using System.IO;
using System.Linq;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M662: a <c>.skn</c> imported as STATIC geometry.
///
/// <para>It was not in <see cref="SceneFileLoader.Extensions"/> at all, so Add Mesh and the Workshop
/// shelf both refused the format outright. A SimpleSkin carries bone indices and weights, but a map has
/// no skeleton to bind them to and the mapgeo append path writes Position/Normal/Texcoord0 and nothing
/// else — everything needed is already in the file without them, so they are dropped rather than the
/// file being refused.</para>
/// </summary>
public class SknStaticImportTests : IDisposable
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";
    private readonly string _dir = Directory.CreateTempSubdirectory("reyengine_m662_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A real champion's .skn on disk, which is the thing the user points the picker at. Null
    /// when the game is not installed on this machine.</summary>
    private string? ASknOnDisk(string champ = "Aatrox")
    {
        string wad = Path.Combine(Champions, champ + ".wad.client");
        if (!File.Exists(wad)) return null;
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { return null; }
        try
        {
            using var archive = WadArchive.Open(wad, new WadPathResolver(db));
            byte[]? Read(ulong h) => archive.TryGetEntry(h, out _) ? archive.Extract(h) : null;

            var bin = Read(HashAlgorithms.WadPath($"data/characters/{champ.ToLowerInvariant()}/skins/skin0.bin"));
            if (bin is null) return null;
            var doc = MaterialDocument.Parse(bin,
                h => db.TryGetBinName(h, out var n) ? n : null,
                h => db.TryGetPath(h, out var p) ? p : null);
            if (doc.SkinMesh?.SimpleSkin is not { Length: > 0 } sknPath) return null;
            var skn = Read(BinTexturePath.HashOfReference(sknPath));
            if (skn is null) return null;

            string path = Path.Combine(_dir, champ.ToLowerInvariant() + ".skn");
            File.WriteAllBytes(path, skn);
            return path;
        }
        catch { return null; }
    }

    [Fact]
    public void TheFormatIsOffered()
    {
        Assert.Contains(".skn", SceneFileLoader.Extensions);
        Assert.True(SceneFileLoader.IsSupported("aatrox.SKN"));
    }

    [Fact]
    public void ARealSkinLoadsAsAScene()
    {
        if (ASknOnDisk() is not { } skn) return;   // no installed game
        var loaded = SceneFileLoader.Load(skn, out string? error);
        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.False(loaded!.IsMapGeo);
        Assert.Null(loaded.SourceMaterialsBin);   // a champion has no sibling map materials bin
        Assert.NotEmpty(loaded.Scene.Meshes);
        Assert.NotEmpty(loaded.Scene.Materials);
    }

    /// <summary>
    /// The M654 rule, which matters here for the same reason it did for a mapgeo: a champion is ONE
    /// vertex buffer of a dozen submeshes. Handing every entry the whole buffer would size, gate and
    /// PLACE each piece as the whole character.
    /// </summary>
    [Fact]
    public void EverySubmeshCarriesOnlyItsOwnVertices()
    {
        if (ASknOnDisk() is not { } skn) return;
        var scene = SceneFileLoader.Load(skn, out _)!.Scene;

        foreach (var mesh in scene.Meshes)
        {
            int vertexCount = mesh.Positions.Length / 3;
            Assert.Equal(vertexCount * 3, mesh.Normals.Length);
            Assert.Equal(vertexCount * 2, mesh.Uvs.Length);
            Assert.Equal(0, mesh.Indices.Length % 3);
            Assert.All(mesh.Indices, i => Assert.InRange(i, 0, vertexCount - 1));
            Assert.Equal(vertexCount, mesh.Indices.Distinct().Count());   // dense: nothing carried unused
        }
    }

    /// <summary>Each submesh becomes its own entry with its own material, which is what makes a
    /// champion's pieces separately pickable in the window.</summary>
    [Fact]
    public void SubmeshesArriveSeparatelyWithTheirMaterials()
    {
        if (ASknOnDisk() is not { } skn) return;
        var scene = SceneFileLoader.Load(skn, out _)!.Scene;

        Assert.All(scene.Meshes, m => Assert.False(string.IsNullOrWhiteSpace(m.MaterialName)));
        // every mesh's material is one the scene declares, or the material mapping has nothing to map
        var declared = scene.Materials.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(scene.Meshes, m => Assert.Contains(m.MaterialName, declared));

        if (scene.Meshes.Count > 1)
            Assert.True(scene.Meshes.Select(m => m.Name).Distinct().Count() > 1,
                "submeshes must be distinguishable by name in the mesh list");
    }

    /// <summary>The geometry has to be real, not a degenerate hull - a champion is a few thousand
    /// triangles spread over a body-sized volume.</summary>
    [Fact]
    public void TheGeometryIsRealAndBodySized()
    {
        if (ASknOnDisk() is not { } skn) return;
        var scene = SceneFileLoader.Load(skn, out _)!.Scene;

        int triangles = scene.Meshes.Sum(m => m.Indices.Length / 3);
        Assert.True(triangles > 500, $"only {triangles} triangle(s) - that is not a champion");

        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var m in scene.Meshes)
            for (int i = 1; i < m.Positions.Length; i += 3)
            { minY = MathF.Min(minY, m.Positions[i]); maxY = MathF.Max(maxY, m.Positions[i]); }
        Assert.InRange(maxY - minY, 50f, 1000f);   // League characters are a couple of hundred units tall
    }

    [Fact]
    public void AFileThatIsNotASkinReportsRatherThanThrows()
    {
        string junk = Path.Combine(_dir, "broken.skn");
        File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Null(SceneFileLoader.Load(junk, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
