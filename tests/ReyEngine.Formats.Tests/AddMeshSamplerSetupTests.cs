using ReyEngine.App.ViewModels;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M514: setting the textures up while the material is being created.
///
/// <para>Before this, a new material was born pointing at the shader's DECLARED default — which for
/// DefaultEnv_Flat is <c>ASSETS/Shared/Materials/rock_texture.tex</c>, a path that exists in no wad and in
/// no project. The material was structurally perfect (right shader link, right sampler, right tint) and
/// drew nothing, which is what "the material is empty" looks like from the outside.</para>
/// </summary>
public sealed class AddMeshSamplerSetupTests
{
    private static AddMeshMaterialViewModel Material(
        Func<string, bool>? exists = null, params string[] shaders)
    {
        return new AddMeshMaterialViewModel
        {
            Source = new ReyEngine.Formats.Meshes.ImportedSceneMaterial("Imported", null),
            ExistingMaterials = Array.Empty<string>(),
            ShaderChoices = shaders.Length > 0 ? shaders : new[] { "Shaders/StaticMesh/DefaultEnv_Flat" },
            SamplersForShader = shader => shader.EndsWith("DefaultEnv_Flat", StringComparison.Ordinal)
                ? new[] { ("DiffuseTexture", "ASSETS/Shared/Materials/rock_texture.tex") }
                : new[] { ("DiffuseTexture", ""), ("Mask_Texture", "assets/masks/default.tex") },
            TextureExists = exists,
        };
    }

    [Fact]
    public void TheShadersOwnDefaultsAreOfferedAndTheMissingOneIsMarked()
    {
        var m = Material(exists: path => !path.Contains("rock_texture"));
        m.RefreshSamplers();

        var sampler = Assert.Single(m.Samplers);
        Assert.Equal("DiffuseTexture", sampler.Name);
        Assert.Equal("ASSETS/Shared/Materials/rock_texture.tex", sampler.Path);
        Assert.True(sampler.IsMissing);
        Assert.Contains("not found", sampler.Note);
    }

    [Fact]
    public void WithNoWayToCheckNothingIsMarked()
    {
        // Same restraint the audit uses: a check with no way to answer must not render as a warning.
        var m = Material(exists: null);
        m.RefreshSamplers();

        var sampler = Assert.Single(m.Samplers);
        Assert.Null(sampler.Resolved);
        Assert.False(sampler.IsMissing);
        Assert.Equal("", sampler.Note);
    }

    [Fact]
    public void TypingAPathReEvaluatesIt()
    {
        var m = Material(exists: path => path.StartsWith("assets/real/", StringComparison.Ordinal));
        m.RefreshSamplers();
        Assert.True(m.Samplers[0].IsMissing);

        m.Samplers[0].Path = "assets/real/river.tex";
        Assert.False(m.Samplers[0].IsMissing);
        Assert.Equal("found", m.Samplers[0].Note);
    }

    [Fact]
    public void ChangingTheShaderRebuildsTheRowsAndKeepsWhatWasTyped()
    {
        var m = Material(exists: _ => true,
            "Shaders/StaticMesh/DefaultEnv_Flat", "Shaders/StaticMesh/Other");
        m.RefreshSamplers();
        m.Samplers[0].Path = "assets/mine/river.tex";

        m.ShaderIndex = 1;              // the setter refreshes

        Assert.Equal(2, m.Samplers.Count);
        // The sampler of the same name keeps the path the user typed; the new one takes its default.
        Assert.Equal("assets/mine/river.tex", m.Samplers.Single(s => s.Name == "DiffuseTexture").Path);
        Assert.Equal("assets/masks/default.tex", m.Samplers.Single(s => s.Name == "Mask_Texture").Path);
    }

    [Fact]
    public void ASamplerLeftEmptyIsNotAnOverride()
    {
        // An empty box means "leave the shader's own default", which is a different intent from pointing
        // the sampler at nothing — and the plan must not carry it as an override either way.
        var m = Material(exists: _ => true, "Shaders/StaticMesh/Other");
        m.RefreshSamplers();

        Assert.Equal("", m.Samplers.Single(s => s.Name == "DiffuseTexture").Path);
        Assert.Equal("assets/masks/default.tex", m.Samplers.Single(s => s.Name == "Mask_Texture").Path);
    }
}
