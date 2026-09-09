using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M620: control mode on D3D11, and the shader cache that stopped every character resolving.
/// </summary>
public sealed class CharacterDx11MovementTests
{
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    // ===================================================== the world transform

    [Fact]
    public void TheWorldMatrixDefaultsToIdentityForEveryExistingCaller()
    {
        // The map viewport draws its geometry at the coordinates the data puts it. Anything but identity
        // here would move every map in the editor.
        Assert.True(new PreviewSettings().World.IsIdentity);
    }

    [Fact]
    public void TheModelWorldIsTheSameCompositionTheGlViewportBuilds()
    {
        // Scale, then facing, then position. A different order here and the character would move
        // differently depending on which renderer was drawing it - the one thing an A/B must not do.
        var preview = new MeshPreviewViewModel { ModelScale = 2.0 };
        preview.ControlMode = true;
        preview.OrderMove(new Vector3(100, 0, 200));
        preview.StopControl();

        var expected = Matrix4x4.CreateScale(2.0f)
                       * Matrix4x4.CreateRotationY((float)preview.CharacterYaw)
                       * Matrix4x4.CreateTranslation(preview.CharacterPosition);

        Assert.Equal(expected, preview.ModelWorld);
    }

    [Fact]
    public void AStationaryCharacterHasAnIdentityWorld()
    {
        // So a subject that is not being driven goes through exactly the path it did before M620.
        var preview = new MeshPreviewViewModel();
        Assert.True(preview.ModelWorld.IsIdentity);
    }

    [Fact]
    public void TheWorldTransformReachesTheBonePaletteRatherThanAWorldConstant()
    {
        // The load-bearing decision. Riot's character vertex shaders transform a vertex by its bone
        // matrices and nothing else, so a world matrix written anywhere else is one no character shader
        // reads - the model would animate perfectly and never move.
        if (RepoRoot() is not { } root) return;
        string renderer = Path.Combine(root, "src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs");
        if (!File.Exists(renderer)) return;

        string text = File.ReadAllText(renderer);
        // M676: the model transform is the placement while a placed prop is mid-loop over its placements
        // (_instanceWorld), and the frame's World otherwise - either way it lands in the bone palette.
        Assert.Contains("(_instanceWorld ?? s.World).IsIdentity ? view : (_instanceWorld ?? s.World) * view", text);
        Assert.Contains("_ => _instanceWorld ?? s.World,", text);
    }

    [Fact]
    public void TheSkeletonOverlayTravelsWithTheCharacter()
    {
        // M614 fixed exactly this on the GL side: the segments are model-space joint positions, and
        // without the transform the skeleton stays at the origin while the character walks away.
        if (RepoRoot() is not { } root) return;
        string vm = Path.Combine(root, "src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.Dx11.cs");
        if (!File.Exists(vm)) return;

        string text = File.ReadAllText(vm);
        Assert.Contains("var world = ModelWorld;", text);
        Assert.Contains("Vector3.Transform(", text);
    }

    [Fact]
    public void TheWindowPublishesTheWorldEveryFrame()
    {
        if (RepoRoot() is not { } root) return;
        string window = Path.Combine(root, "src", "ReyEngine.App", "Views", "MeshPreviewWindow.Dx11.cs");
        if (!File.Exists(window)) return;

        Assert.Contains("_dx11.World = vm.ModelWorld", File.ReadAllText(window));
    }

    // ===================================================== the shader cache

    [Fact]
    public void OpeningTheShaderCacheIsNotBehindAMapBeingOpen()
    {
        // The bug: the cache was opened only inside BuildDx11SceneAsync, which returns early unless a MAP
        // is open. Opening a champion never opened it, so every character in the game reported "no
        // materials" for a reason that had nothing to do with the character.
        if (RepoRoot() is not { } root) return;
        string host = Path.Combine(root, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        string characters = Path.Combine(root, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.Characters.cs");
        if (!File.Exists(host) || !File.Exists(characters)) return;

        Assert.Contains("private Formats.Shaders.ShaderCacheReader? OpenDx11ShaderCache(", File.ReadAllText(host));
        Assert.Contains("OpenDx11ShaderCache(out var cacheError)", File.ReadAllText(characters));
    }

    [Fact]
    public void ASpecificReasonIsNeverReplacedByAGenericOne()
    {
        // The second bug, and the one that hid the first. The host had already put the real reason on the
        // status; ticking the box overwrote it with a guess, so "the shader cache never opened" read as
        // "this is not a character" - and sent the search in the wrong direction.
        var preview = new MeshPreviewViewModel();
        preview.SetDx11Scene(null, "Shader cache: ShaderCache.dx11.wad.client not found");

        preview.UseDx11Preview = true;

        Assert.Contains("Shader cache", preview.Dx11Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not a character", preview.Dx11Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithNothingSaidAtAllTheGenericReasonStillAppears()
    {
        // The fallback has to survive the fix: silence would be worse than a guess.
        var preview = new MeshPreviewViewModel();
        preview.SetDx11Scene(null, "");

        preview.UseDx11Preview = true;

        Assert.False(string.IsNullOrWhiteSpace(preview.Dx11Status));
    }
}
