using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M861: an emitter's <c>CustomMaterial</c> (VfxMaterialDefinitionData -> a StaticMaterialDef link; 4,703 emitters) is read, and a mesh
/// emitter draws with that material's own shader through the path a placed prop takes.
/// </summary>
public sealed class VfxCustomMaterialTests
{
    private static float[] Instance(Vector3 pos, float scale, float spin, Vector3 euler)
    {
        var inst = new float[ParticleQuadBuilder.Stride];
        inst[ParticleQuadBuilder.OffPos] = pos.X; inst[ParticleQuadBuilder.OffPos + 1] = pos.Y; inst[ParticleQuadBuilder.OffPos + 2] = pos.Z;
        inst[ParticleQuadBuilder.OffSizeX] = scale;
        inst[ParticleQuadBuilder.OffRot] = spin;
        inst[ParticleQuadBuilder.OffEuler] = euler.X; inst[ParticleQuadBuilder.OffEuler + 1] = euler.Y; inst[ParticleQuadBuilder.OffEuler + 2] = euler.Z;
        return inst;
    }

    private static Vector3 Place(float[] inst, Vector3 local, Vector3? right = null, Vector3? up = null, Vector3? forward = null)
        => Vector3.Transform(local, VfxCustomMaterial.ParticleWorld(inst, 0, right ?? Vector3.UnitX, up ?? Vector3.UnitY, forward ?? Vector3.UnitZ));

    [Fact]
    public void AParticleWorldScalesThenMovesLikeTheParticleMeshShader()
    {
        var p = Place(Instance(new Vector3(10, 20, 30), 2f, 0f, Vector3.Zero), new Vector3(1, 0, 0));
        Assert.Equal(12f, p.X, 4); Assert.Equal(20f, p.Y, 4); Assert.Equal(30f, p.Z, 4);
    }

    [Fact]
    public void TheBirthEulerAndTheSpinTurnTheMeshAsTheShaderDoes()
    {
        // mesh_vs: rotateEuler (Z, then X, then Y), then the spin x' = x c - z s, z' = x s + z c
        float quarter = MathF.PI / 2f;
        var yaw = Place(Instance(Vector3.Zero, 1f, 0f, new Vector3(0, quarter, 0)), new Vector3(1, 0, 0));
        Assert.Equal(0f, yaw.X, 4); Assert.Equal(-1f, yaw.Z, 4);   // x' = x cos + z sin ; z' = -x sin + z cos
        var spun = Place(Instance(Vector3.Zero, 1f, quarter, Vector3.Zero), new Vector3(1, 0, 0));
        Assert.Equal(0f, spun.X, 4); Assert.Equal(1f, spun.Z, 4);
    }

    [Fact]
    public void ThePlacementBasisIsAppliedAfterTheParticlesOwnTransform()
    {
        // right = +Z, up = +Y, forward = -X: a local +X lands on +Z
        var p = Place(Instance(new Vector3(0, 5, 0), 1f, 0f, Vector3.Zero), new Vector3(1, 0, 0), Vector3.UnitZ, Vector3.UnitY, -Vector3.UnitX);
        Assert.Equal(1f, p.Z, 4); Assert.Equal(5f, p.Y, 4);
    }

    [Fact]
    public void TheEmittersMeshBecomesOneSubmeshNamedForTheMaterialWithFlatNormals()
    {
        var mesh = new StaticMeshData(new float[] { 0, 0, 0, 1, 0, 0, 0, 0, 1 }, new float[] { 0, 0, 1, 0, 0, 1 }, new uint[] { 0, 1, 2 }, "m");
        var asset = VfxCustomMaterial.MeshOf(mesh, "Characters/X/Materials/Fresnel_inst");
        Assert.Equal("Characters/X/Materials/Fresnel_inst", Assert.Single(asset.SubMeshes).Material);
        Assert.Equal(3, asset.VertexCount);
        // the triangle lies in XZ: the cross product of (1,0,0) and (0,0,1) is -Y
        Assert.Equal(-1f, asset.Normals[1], 4);
    }

    [Fact]
    public void GarensLionDragonShieldLinksItsFresnelMaterial()
    {
        const string wadPath = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions\Garen.wad.client";
        if (!File.Exists(wadPath)) return;
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); } catch { return; }
        using var wad = WadArchive.Open(wadPath, new WadPathResolver(db));
        ulong hash = HashAlgorithms.WadPath("data/characters/garen/skins/skin44.bin");
        if (!wad.TryGetEntry(hash, out _)) return;
        var systems = VfxSystemResolver.ExtractAll(wad.Extract(hash));
        var system = systems.Values.FirstOrDefault(s => s.Name == "Garen_Skin44_W_Cas");
        if (system is null) return;
        var shield = system.Emitters.First(e => e.Name == "shield13");
        Assert.NotEqual(0u, shield.CustomMaterialLink);
        Assert.True(shield.IsMeshPrimitive);
        Assert.Equal(0u, system.Emitters.First(e => e.Name == "ground_glow1").CustomMaterialLink);
    }
}
