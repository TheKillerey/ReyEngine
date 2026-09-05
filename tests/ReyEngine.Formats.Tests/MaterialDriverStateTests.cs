using System.Numerics;
using System.Text.Json;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M647: the character editor's state switch - lebend / tot / W-Form.
///
/// <para>A champion's materials do not draw what their paramValues say. Locke authors
/// <c>VCDissolve_Value = 1.23</c> and drives it from an IsDead lerp to -0.8 while alive and 3 once dead;
/// his <c>Transition_Value</c> rides his W buff. M646 taught the preview to draw the resting values, and
/// this milestone lets it draw any of the situations the skin's own drivers describe.</para>
///
/// <para>The class defaults the evaluator leans on are pinned against Riot's own schema here rather than
/// asserted from memory - they decide what 72 of 346 driven entries across the champion corpus read.</para>
/// </summary>
public sealed class MaterialDriverStateTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed record Fixture(byte[] Skn, byte[] SkinBin, WadArchive Archive, HashDatabase Database) : IDisposable
    {
        public string? BinName(uint h) => Database.TryGetBinName(h, out var n) ? n : null;
        public string? WadPath(ulong h) => Database.TryGetPath(h, out var p) ? p : null;
        public byte[]? Read(ulong h) => Archive.TryGetEntry(h, out _) ? Archive.Extract(h) : null;
        public MaterialDocument Document => MaterialDocument.Parse(SkinBin, BinName, WadPath);
        public void Dispose() => Archive.Dispose();
    }

    private static Fixture? Champion(string champ, string skn)
    {
        string wad = Path.Combine(Final, "Champions", champ + ".wad.client");
        if (!File.Exists(wad) || Database.Value is not { } database) return null;
        var archive = WadArchive.Open(wad, new WadPathResolver(database));
        ulong sknHash = HashAlgorithms.WadPath(skn);
        ulong binHash = HashAlgorithms.WadPath($"data/characters/{champ.ToLowerInvariant()}/skins/skin0.bin");
        byte[]? mesh = archive.TryGetEntry(sknHash, out _) ? archive.Extract(sknHash) : null;
        byte[]? bin = archive.TryGetEntry(binHash, out _) ? archive.Extract(binHash) : null;
        if (mesh is null || bin is null) { archive.Dispose(); return null; }
        return new Fixture(mesh, bin, archive, database);
    }

    private static Fixture? Locke() => Champion("Locke", "assets/characters/locke/skins/base/locke_base.skn");
    private static Fixture? Aatrox() => Champion("Aatrox", "assets/characters/aatrox/skins/base/aatrox.skn");

    private static MaterialBinding Material(MaterialDocument doc, string suffix) =>
        doc.Materials.First(m => m.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static MaterialDynamicParameter Driven(MaterialBinding b, string name) =>
        b.DynamicParameters.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ===================================================== the schema the evaluator leans on

    [Fact]
    public void TheClassDefaultsAreRiotsOwnAndNotRemembered()
    {
        // Riot writes these fields inconsistently - 72 of 346 driven entries leave a lerp branch out - so
        // what an unwritten branch reads decides most of the corpus. Every constant is checked against the
        // schema database the editor already ships, by class hash and property name.
        string path = Path.Combine(RepoRoot(), "data", "meta", "meta.db.json");
        if (!File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var classes = doc.RootElement.GetProperty("classes");

        JsonElement Latest(string classHash, string property)
        {
            var cls = classes.GetProperty(classHash);
            foreach (var p in cls.GetProperty("properties").EnumerateObject())
                if (p.Value.TryGetProperty("name", out var n) && n.GetString() == property)
                {
                    var revs = p.Value.GetProperty("revisions");
                    return revs[revs.GetArrayLength() - 1];
                }
            throw new Xunit.Sdk.XunitException($"{classHash} declares no '{property}'");
        }
        float Scalar(string classHash, string property) => Latest(classHash, property).GetProperty("default").GetSingle();
        Vector4 Vec(string classHash, string property)
        {
            var a = Latest(classHash, property).GetProperty("default");
            return new Vector4(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle(), a[3].GetSingle());
        }

        Assert.Equal(Scalar("0xb4c4e463", "mOnValue"), MaterialDrivers.LerpOnDefault);
        Assert.Equal(Scalar("0xb4c4e463", "mOffValue"), MaterialDrivers.LerpOffDefault);
        Assert.Equal(Scalar("0xb4c4e463", "mTurnOnTimeSec"), MaterialDrivers.TurnTimeDefault);
        Assert.Equal(Scalar("0x70fff2b7", "mValue"), MaterialDrivers.LiteralDefault);
        Assert.Equal(Scalar("0xa9f7c49f", "mMaxValue"), MaterialDrivers.RemapInMaxDefault);
        Assert.Equal(Scalar("0xa9f7c49f", "mOutputMaxValue"), MaterialDrivers.RemapOutMaxDefault);
        Assert.Equal(Scalar("0xcc8cc1ee", "DefaultFloat"), MaterialDrivers.KeyFrameDefault);
        Assert.Equal(Vec("0x4aff6f5a", "mColorOn"), MaterialDrivers.ChooserOnDefault);
        Assert.Equal(Vec("0x4aff6f5a", "mColorOff"), MaterialDrivers.ChooserOffDefault);
        Assert.Equal(Vec("0x466b06ef", "mColor"), MaterialDrivers.SpecificColorDefault);
        Assert.Equal(Vec("0x22d8a036", "OnValue"), MaterialDrivers.Vec4OnDefault);
        Assert.Equal(Vec("0x22d8a036", "OffValue"), MaterialDrivers.Vec4OffDefault);
        Assert.Equal(Vec("0xf4c0192d", "value"), MaterialDrivers.Float4LiteralDefault);
    }

    // ===================================================== which switches a skin offers

    [Fact]
    public void OnlyTheSkinsOwnBoolConditionsBecomeSwitches()
    {
        if (Locke() is not { } f) return;
        using (f)
        {
            var conditions = MaterialDrivers.ConditionsOf(f.Document.Materials);
            // Exactly the two questions Locke's materials ask - alive/dead, and his W. A LerpMaterialDriver
            // also ends in "Driver"; before the leaf test looked at the BoolDriver suffix it became a
            // switch of its own and the card offered "Lerp" and "ColorGraph" as champion states.
            Assert.Equal(new[] { "IsDead", "HasBuff:LockeW" }, conditions.Select(c => c.Key).ToArray());
            Assert.Equal(new[] { "Dead", "LockeW" }, conditions.Select(c => c.Label).ToArray());
            Assert.Contains("dead", conditions[0].Describe, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("LockeW", conditions[1].Describe, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ASkinWithoutDriversOffersNoSwitchesAtAll()
    {
        // The card hides itself for the 619 of 690 skin bins that carry no dynamicMaterial.
        if (Champion("Ahri", "assets/characters/ahri/skins/base/ahri_base.skn") is not { } f) return;
        using (f)
        {
            var withDrivers = f.Document.Materials.Where(m => m.DynamicParameters.Count > 0).ToList();
            var conditions = MaterialDrivers.ConditionsOf(f.Document.Materials);
            // Ahri drives one parameter off her R; whatever the count, every condition is a real bool
            // driver and never a value driver.
            Assert.All(conditions, c => Assert.DoesNotContain("Lerp", c.Kind, StringComparison.Ordinal));
            Assert.All(conditions, c => Assert.DoesNotContain("Graph", c.Kind, StringComparison.Ordinal));
            Assert.Equal(withDrivers.Count > 0, conditions.Count > 0);
        }
    }

    // ===================================================== what a state reads

    [Fact]
    public void TheDeathDissolveRampsOverEachDriversOwnDuration()
    {
        if (Locke() is not { } f) return;
        using (f)
        {
            var doc = f.Document;
            var body = Driven(Material(doc, "Locke_Body_inst"), "VCDissolve_Value");
            Assert.Equal(8f, body.TransitionSeconds, 3);              // the body's own mTurnOnTimeSec

            // alive: the off branch, which is what M646 already drew
            Assert.Equal(-0.8f, body.Evaluate(MaterialDriverState.Rest).Value!.Value.X, 3);
            // dead, held long enough to settle: the on branch
            Assert.Equal(3f, body.Evaluate(new MaterialDriverState(new[] { "IsDead" }, 999f)).Value!.Value.X, 3);
            // and part way: linear across the driver's OWN 8 s, not the skin's longest
            Assert.Equal(1.1f, body.Evaluate(new MaterialDriverState(new[] { "IsDead" }, 4f)).Value!.Value.X, 3);
            Assert.Contains("50% toward mOnValue", body.Evaluate(new MaterialDriverState(new[] { "IsDead" }, 4f)).Reason, StringComparison.Ordinal);

            // the weapon ramps the same parameter over 10 s, so at the same instant it reads differently -
            // which is why the clock is seconds and not a fraction
            var weapon = Driven(Material(doc, "Locke_Weapon_inst"), "VCDissolve_Value");
            Assert.Equal(10f, weapon.TransitionSeconds, 3);
            Assert.NotEqual(body.Evaluate(new MaterialDriverState(new[] { "IsDead" }, 4f)).Value!.Value.X,
                            weapon.Evaluate(new MaterialDriverState(new[] { "IsDead" }, 4f)).Value!.Value.X, 3);
        }
    }

    [Fact]
    public void ABuffMovesOnlyWhatAsksAboutIt()
    {
        if (Locke() is not { } f) return;
        using (f)
        {
            var body = Material(f.Document, "Locke_Body_inst");
            var transition = Driven(body, "Transition_Value");
            var dissolve = Driven(body, "VCDissolve_Value");
            var w = new MaterialDriverState(new[] { "HasBuff:LockeW" }, 999f);

            Assert.Equal(-0.2f, transition.Evaluate(MaterialDriverState.Rest).Value!.Value.X, 3);
            Assert.Equal(1.6f, transition.Evaluate(w).Value!.Value.X, 3);
            // the death dissolve is not his W: it stays where it was
            Assert.Equal(dissolve.Evaluate(MaterialDriverState.Rest).Value!.Value.X,
                         dissolve.Evaluate(w).Value!.Value.X, 3);
        }
    }

    [Fact]
    public void AnUnwrittenLerpBranchReadsItsSchemaDefaultRatherThanNothing()
    {
        if (Locke() is not { } f) return;
        using (f)
        {
            // Locke writes neither branch of Dissolve_LerpOverride's lerp. M646 called that unknown and
            // left the authored 0.9275 standing; the schema says mOffValue is 0, and the game reads 0.
            // Measured on the viewport, the difference is 2,281 of 108,825 covered pixels - which is why
            // this was safe to change and why it is worth pinning rather than eyeballing.
            var over = Driven(Material(f.Document, "Locke_Body_inst"), "Dissolve_LerpOverride");
            Assert.Equal(0f, over.Evaluate(MaterialDriverState.Rest).Value!.Value.X, 3);
            Assert.Contains("mOffValue", over.Evaluate(MaterialDriverState.Rest).Reason, StringComparison.Ordinal);
            Assert.Equal(1f, over.Evaluate(new MaterialDriverState(new[] { "IsDead" }, 999f)).Value!.Value.X, 3);
        }
    }

    [Fact]
    public void AatroxsSwitchAndMaxDriversResolveToo()
    {
        // Lerp is 116 of the corpus's 346 driven entries; Switch is 95, Max 27, FloatGraph 16. A state
        // switch that only understood lerps would do nothing for most of the 33 champions that have any.
        if (Aatrox() is not { } f) return;
        using (f)
        {
            var driven = f.Document.Materials.SelectMany(m => m.DynamicParameters).Where(p => p.Enabled).ToList();
            Assert.NotEmpty(driven);
            var kinds = driven.Select(p => p.Driver.Split('(')[0]).Distinct().ToList();
            Assert.Contains("Max", kinds);
            Assert.Contains("Switch", kinds);

            var max = driven.First(p => p.Driver.StartsWith("Max(", StringComparison.Ordinal));
            Assert.NotNull(max.Evaluate(MaterialDriverState.Rest).Value);
            var sw = driven.First(p => p.Driver.StartsWith("Switch(", StringComparison.Ordinal));
            Assert.NotNull(sw.Evaluate(MaterialDriverState.Rest).Value);

            // and his death moves the dissolve his Max/FloatGraph pair drives
            var dead = new MaterialDriverState(new[] { "IsDead" }, 999f);
            Assert.NotEqual(max.Evaluate(MaterialDriverState.Rest).Value!.Value.X,
                            max.Evaluate(dead).Value!.Value.X, 3);
        }
    }

    // ===================================================== the scene draws the state it is given

    [Fact]
    public void ThePreparedSceneCarriesTheStateItWasAskedFor()
    {
        if (Locke() is not { } f) return;
        using (f)
        {
            var cache = ShaderCacheReader.Open(Final, new WadPathResolver(f.Database), out _);
            if (cache is null) return;
            using (cache)
            {
                var perms = new ShaderPermutationIndex(Final);
                float Dissolve(MaterialDriverState? state)
                {
                    var scene = Dx11CharacterScene.Prepare(f.Skn, f.SkinBin, cache, perms, f.Read, f.BinName, f.WadPath,
                        Dx11CharacterScene.DefaultCharacterShader, state);
                    Assert.NotNull(scene);
                    var slice = scene!.Slices.First(s => s.Submesh == "Body");
                    return slice.Parameters.Single(p => p.Name == "VCDissolve_Value").Value[0];
                }

                Assert.Equal(-0.8f, Dissolve(null), 3);                                              // default is rest
                Assert.Equal(-0.8f, Dissolve(MaterialDriverState.Rest), 3);
                Assert.Equal(1.1f, Dissolve(new MaterialDriverState(new[] { "IsDead" }, 4f)), 3);
                Assert.Equal(3f, Dissolve(new MaterialDriverState(new[] { "IsDead" }, 999f)), 3);

                // and the scene tells a host what switches to offer, with each condition's own ramp
                var rest = Dx11CharacterScene.Prepare(f.Skn, f.SkinBin, cache, perms, f.Read, f.BinName, f.WadPath,
                    Dx11CharacterScene.DefaultCharacterShader)!;
                Assert.Equal(new[] { "IsDead", "HasBuff:LockeW" }, rest.Conditions.Select(c => c.Key).ToArray());
                Assert.Equal(10f, rest.LongestTransitionSeconds, 3);
                Assert.Equal(10f, rest.TransitionByCondition["IsDead"], 3);
            }
        }
    }

    // ===================================================== the switch in the window

    /// <summary>A switcher seeded like Locke's, with a counter for the rebuilds it asks the host for.</summary>
    private sealed class Switcher
    {
        public MeshPreviewViewModel Vm { get; } = new();
        public int Rebuilds { get; private set; }

        public Switcher()
        {
            Vm.RequestDriverState = () => Rebuilds++;
            Vm.SetConditions(
                new[]
                {
                    new MaterialDriverCondition("IsDead", "IsDead", "Dead"),
                    new MaterialDriverCondition("HasBuff:LockeW", "HasBuff", "LockeW"),
                },
                longestTransition: 10f,
                ramps: new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                {
                    ["IsDead"] = 10f,
                    ["HasBuff:LockeW"] = 0.7f,
                });
        }
    }

    [Fact]
    public void FlippingASwitchParksTheClockInThatConditionsRampAndAsksForARebuild()
    {
        var sw = new Switcher();
        var vm = sw.Vm;
        Assert.True(vm.HasConditions);
        Assert.Equal(10, vm.StateSecondsMax);
        Assert.True(vm.IsRestState);
        Assert.Contains("Alive", vm.StateSummary, StringComparison.Ordinal);

        int before = sw.Rebuilds;
        vm.Conditions.First(c => c.Key == "IsDead").IsOn = true;
        Assert.True(sw.Rebuilds > before);
        Assert.False(vm.IsRestState);
        // 40% into the 10 s ramp - measured: at 2-3 s Locke looks untouched, at 5 s his head has gone
        Assert.Equal(4d, vm.StateSeconds, 3);
        Assert.Equal(new[] { "IsDead" }, vm.DriverState.ActiveKeys.ToArray());
        Assert.Equal(4f, vm.DriverState.Seconds, 3);
        Assert.Contains("Dead", vm.StateSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void AClockTheUserSetIsNotMovedByTheNextSwitch()
    {
        var vm = new Switcher().Vm;
        vm.StateSeconds = 7;                       // as the slider does it
        vm.Conditions.First(c => c.Key == "IsDead").IsOn = true;
        Assert.Equal(7d, vm.StateSeconds, 3);      // still theirs
    }

    [Fact]
    public void ResetGoesBackToRestInOneStep()
    {
        var sw = new Switcher();
        var vm = sw.Vm;
        foreach (var c in vm.Conditions) c.IsOn = true;
        Assert.False(vm.IsRestState);
        int before = sw.Rebuilds;
        vm.ResetStateCommand.Execute(null);
        Assert.True(vm.IsRestState);
        Assert.Empty(vm.DriverState.ActiveKeys);
        Assert.Equal(before + 1, sw.Rebuilds);   // ONE rebuild, not one per switch
    }

    [Fact]
    public void ASwitchSurvivesTheRebuildAMaterialEditCauses()
    {
        // Editing a material rebuilds the scene, which re-seeds the switches. Losing the state there would
        // revive the corpse under the user mid-edit.
        var sw = new Switcher();
        var vm = sw.Vm;
        vm.Conditions.First(c => c.Key == "IsDead").IsOn = true;
        vm.StateSeconds = 6;

        var same = new[]
        {
            new MaterialDriverCondition("IsDead", "IsDead", "Dead"),
            new MaterialDriverCondition("HasBuff:LockeW", "HasBuff", "LockeW"),
        };
        int before = sw.Rebuilds;
        vm.SetConditions(same, 10f, new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["IsDead"] = 10f });
        Assert.True(vm.Conditions.First(c => c.Key == "IsDead").IsOn);
        Assert.Equal(6d, vm.StateSeconds, 3);
        Assert.Equal(before, sw.Rebuilds);   // re-seeding must not ask for yet another rebuild

        // a DIFFERENT skin is a different clock, and starts clean
        vm.SetConditions(new[] { new MaterialDriverCondition("IsDead", "IsDead", "Dead") }, 4f,
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { ["IsDead"] = 4f });
        Assert.Equal(4, vm.StateSecondsMax);
    }

    [Fact]
    public void TheHostActuallyWiresTheSwitchToASceneRebuild()
    {
        // The switch is a callback the host has to set, and a rebuild that does not carry the state is a
        // switch that silently does nothing. Both ends are one line each, in two different files, and
        // neither a green suite nor a compiling build can see them missing - so they are read here.
        string wiring = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs"));
        Assert.Contains("MeshPreview.RequestDriverState = () => RebuildCharacterDx11Scene();", wiring, StringComparison.Ordinal);

        string host = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.CharacterMaterials.cs"));
        Assert.Contains("var state = MeshPreview.DriverState;", host, StringComparison.Ordinal);
        Assert.Contains("BuildCharacterDx11Scene(skn, bytes, state)", host, StringComparison.Ordinal);

        string load = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.Characters.cs"));
        Assert.Contains("driverState: driverState", load, StringComparison.Ordinal);
    }

    [Fact]
    public void ASkinWithNoConditionsHidesTheCard()
    {
        var vm = new MeshPreviewViewModel();
        vm.SetConditions(Array.Empty<MaterialDriverCondition>(), 0f);
        Assert.False(vm.HasConditions);
        Assert.True(vm.IsRestState);
        Assert.Empty(vm.DriverState.ActiveKeys);
    }
}
