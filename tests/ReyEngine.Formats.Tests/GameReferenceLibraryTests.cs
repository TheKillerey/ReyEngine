using ReyEngine.Core.Assets;

namespace ReyEngine.Formats.Tests;

public sealed class GameReferenceLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "reyengine-game-ref-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Inspect_NormalizesInstallRootAndFinalFolderToGameDirectory()
    {
        string game = CreateInstall();
        string installRoot = Directory.GetParent(game)!.FullName;
        string final = Path.Combine(game, "DATA", "FINAL");

        var fromRoot = GameReferenceLibrary.Inspect(installRoot);
        var fromFinal = GameReferenceLibrary.Inspect(final);

        Assert.True(fromRoot.IsValid);
        Assert.Equal(game, fromRoot.GameDirectory, ignoreCase: true);
        Assert.True(fromFinal.IsValid);
        Assert.Equal(game, fromFinal.GameDirectory, ignoreCase: true);
    }

    [Fact]
    public void Inspect_RejectsFolderWithOnlyDataWad()
    {
        string game = Path.Combine(_root, "League of Legends", "Game");
        string final = Path.Combine(game, "DATA", "FINAL");
        Directory.CreateDirectory(final);
        File.WriteAllBytes(Path.Combine(final, "DATA.wad.client"), []);

        var status = GameReferenceLibrary.Inspect(game);

        Assert.False(status.IsValid);
        Assert.Contains("Global.wad.client", status.Message);
    }

    [Fact]
    public void Inspect_AcceptsCurrentLiveLayoutWithoutRetiredCommonWad()
    {
        string game = CreateInstall(includeCommon: false);

        var status = GameReferenceLibrary.Inspect(game);

        Assert.True(status.IsValid);
        Assert.DoesNotContain("Common.wad.client", status.Message);
    }

    [Fact]
    public void Discover_IncludesRequestedMapWad()
    {
        string game = CreateInstall();
        string shipping = Path.Combine(game, "DATA", "FINAL", "Maps", "Shipping");
        Directory.CreateDirectory(shipping);
        string map11 = Path.Combine(shipping, "Map11.wad.client");
        File.WriteAllBytes(map11, []);

        var wads = GameReferenceLibrary.Discover(game, ["Map11"]);

        Assert.Contains(wads, path => string.Equals(path, map11, StringComparison.OrdinalIgnoreCase));
    }

    private string CreateInstall(bool includeCommon = true)
    {
        string game = Path.Combine(_root, "League of Legends", "Game");
        string final = Path.Combine(game, "DATA", "FINAL");
        Directory.CreateDirectory(final);
        var names = new List<string> { "DATA.wad.client", "Global.wad.client" };
        if (includeCommon) names.Add("Common.wad.client");
        foreach (string name in names)
            File.WriteAllBytes(Path.Combine(final, name), []);
        return game;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
