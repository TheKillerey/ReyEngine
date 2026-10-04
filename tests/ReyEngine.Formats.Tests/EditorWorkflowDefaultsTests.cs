using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Settings;
using ReyEngine.Formats.Characters;

namespace ReyEngine.Formats.Tests;

public sealed class EditorWorkflowDefaultsTests
{
    [Fact]
    public void FreshEditorUsesTheRequestedViewportDefaults()
    {
        var settings = new EditorSettings();
        Assert.False(settings.UseOpenGlViewport);
        Assert.True(settings.AutoSaveEdits);
        var map = new MainWindowViewModel();
        try
        {
            Assert.True(map.AnimationsPlaying);
            Assert.True(map.ShowBloom);
            Assert.False(map.ShowSunShadows);
            Assert.True(map.ClientDepthRules);
            Assert.False(map.ShowWireframe);
            Assert.False(map.RenderRegionsEnabled);
            Assert.Equal(0, map.SelectedSkyboxIndex);
        }
        finally { map.StopEditorAutoSave(); }
        var preview = new MeshPreviewViewModel();
        Assert.True(preview.UseDx11Preview);
        Assert.False(preview.ShowBones);
    }

    private sealed class ProjectHost : ICharacterBrowserHost
    {
        public string? GameDirectory => null;
        public IHashResolver? Resolver => null;
        public IReadOnlyList<string> ProjectCharacterPaths => new[]
        {
            "data/characters/customhero/skins/skin0.bin",
            "data/characters/customhero/skins/skin1.bin",
        };
        public void OpenSkin(ChampionPackage champion, CharacterEntry character, CharacterSkinInfo skin) { }
    }

    [Fact]
    public void ProjectOnlyCharactersAndTheirSkinsAreListedWithoutAnInstalledRiotWad()
    {
        using var browser = new CharacterBrowserViewModel(new ProjectHost());
        var champion = Assert.Single(browser.Champions);
        Assert.Equal("PROJECT", champion.SourceLabel);
        browser.SelectedChampion = champion;
        Assert.Single(browser.Characters);
        Assert.Equal(new[] { 0, 1 }, browser.Skins.Select(s => s.Number));
    }

    [Fact]
    public void DrivingCorrectsTheRenderedFacingWithoutChangingMovementCoordinates()
    {
        var preview = new MeshPreviewViewModel { CharacterPosition = new Vector3(30, 10, 50), CharacterYaw = 0 };
        Assert.Equal(0, preview.RenderedCharacterYaw);
        preview.ControlMode = true;
        try
        {
            Assert.Equal(Math.PI, preview.RenderedCharacterYaw);
            Assert.Equal(preview.CharacterPosition, preview.ModelWorld.Translation);
            Assert.True(Vector3.TransformNormal(Vector3.UnitZ, preview.ModelWorld).Z < 0);
            preview.OrderMove(new Vector3(500, 10, 500));
            preview.StopMovement();
            Assert.Equal(new Vector3(30, 10, 50), preview.CharacterPosition);
        }
        finally { preview.StopControl(); }
    }

    [Theory]
    [InlineData("7.5", true)]
    [InlineData("not a number", false)]
    public void SaveAppliesTypedBinValuesAndRejectsInvalidInput(string text, bool valid)
    {
        var tree = new LeagueToolkit.Core.Meta.BinTree(new[]
        {
            new LeagueToolkit.Core.Meta.BinTreeObject(1, 2, new LeagueToolkit.Core.Meta.BinTreeProperty[]
            { new LeagueToolkit.Core.Meta.Properties.BinTreeF32(3, 1f) }),
        }, Array.Empty<string>());
        using var stream = new MemoryStream();
        tree.Write(stream);
        var doc = ReyEngine.Formats.Meta.BinEditorDocument.Parse(stream.ToArray(), _ => null);
        var editor = new BinEditorViewModel();
        editor.Load(doc, null!, stream.ToArray());
        var field = Assert.Single(Assert.Single(editor.Roots).Children);
        field.EditedText = text;
        Assert.True(editor.HasPendingChanges);
        Assert.False(editor.IsDirty);
        Assert.Equal(valid, editor.ApplyPendingEdits());
        Assert.Equal(!valid, field.HasError);
        var saved = ReyEngine.Formats.Meta.BinEditorDocument.Parse(editor.Serialize()!, _ => null);
        Assert.Equal(valid ? "7.5" : "1", Assert.Single(Assert.Single(saved.Roots).Children).OriginalText);
    }
}
