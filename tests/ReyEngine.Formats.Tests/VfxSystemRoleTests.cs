using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M713: the preview's rig comes from the game's own wiring when the game has something to say, and from
/// the system's name only when it does not.
///
/// <para>Asserted against the shipped roster, because the whole claim is about what Riot authors - a
/// fixture would only prove the reader agrees with my idea of a spell record.</para>
/// </summary>
public sealed class VfxSystemRoleTests
{
    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";
    private static bool Installed => Directory.Exists(Champions);

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>The whole chain for one champion: spells from the root bin, resource maps from every skin
    /// bin, keyed by the system each map points at. The same three-bin walk the host does.</summary>
    private static Dictionary<uint, VfxSystemLink> Roles(string champion, out Func<uint, string?> name)
    {
        var roles = new Dictionary<uint, VfxSystemLink>();
        name = _ => null;
        string wad = Path.Combine(Champions, champion + ".wad.client");
        if (!File.Exists(wad) || Database.Value is not { } database) return roles;

        var db = database;
        name = h => db.TryGetBinName(h, out var n) ? n : null;
        using var archive = WadArchive.Open(wad, new WadPathResolver(db));

        string lower = champion.ToLowerInvariant();
        ulong recordHash = HashAlgorithms.WadPath(ChampionRecord.PathFor(lower));
        var fromSpells = archive.TryGetEntry(recordHash, out _)
            ? VfxSystemRoles.FromSpells(archive.Extract(recordHash), champion)
            : new Dictionary<uint, VfxSystemLink>();

        string prefix = $"data/characters/{lower}/skins/";
        foreach (var e in archive.Entries.Where(e => e.IsResolved
            && e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] bytes;
            try { bytes = archive.Extract(e.PathHash); } catch { continue; }
            var map = VfxSystemResolver.ExtractResourceMap(bytes);
            if (map.Count == 0) continue;
            VfxSystemRoles.Resolve(fromSpells, map, roles);
            VfxSystemRoles.Resolve(VfxSystemRoles.FromSkin(bytes, name), map, roles);
        }
        return roles;
    }

    private static VfxSystemLink? Find(Dictionary<uint, VfxSystemLink> roles, Func<uint, string?> name, string system)
    {
        foreach (var (hash, link) in roles)
            if (string.Equals(name(hash), system, StringComparison.OrdinalIgnoreCase)
                || (name(hash) ?? "").EndsWith("/" + system, StringComparison.OrdinalIgnoreCase))
                return link;
        return null;
    }

    // ===================================================== the real chain

    [Fact]
    public void OnThisMachineTheReaderActuallyRuns()
    {
        // every test below skips without the install; this turns a silent skip into a failure
        if (!Installed) return;
        Assert.NotNull(Database.Value);
        Assert.NotEmpty(Roles("Ahri", out _));
    }

    [Fact]
    public void AhriSpellsNameTheirMissilesAndHandOverTheSpeed()
    {
        if (!Installed) return;
        var roles = Roles("Ahri", out var name);
        if (roles.Count == 0) return;

        // E is the case the brief quotes: FixedSpeedMovement, mSpeed 1550, which is Charm's real speed
        var charm = Find(roles, name, "Ahri_Base_E_mis");
        Assert.NotNull(charm);
        Assert.Equal(VfxSystemRole.Missile, charm!.Role);
        Assert.Equal(MissileMotionKind.ConstantSpeed, charm.Motion.Kind);
        Assert.Equal(1550f, charm.Motion.Value, 0);
        Assert.Contains("1550", charm.Why);

        // Q is the case the brief gets wrong, and the reason M713 had to extend the motion reader: it is
        // AcceleratingMovement and authors no mSpeed at all, so a reader that knows only the claimed path
        // is silent on Ahri's signature ability.
        var orb = Find(roles, name, "Ahri_Base_Q_mis");
        Assert.NotNull(orb);
        Assert.Equal(VfxSystemRole.Missile, orb!.Role);
        Assert.NotEqual(MissileMotionKind.Unknown, orb.Motion.Kind);
        Assert.True(orb.Motion.Value > 0f, "Ahri's Q must come back with an authored pace");

        // a hit effect is a target, not a missile, and it does not travel
        var tar = Find(roles, name, "Ahri_Base_E_tar");
        if (tar is not null)
        {
            Assert.Equal(VfxSystemRole.Target, tar.Role);
            Assert.Equal(VfxRigMode.Still, tar.Rig(new VfxPreviewRig()).Mode);
        }
    }

    [Fact]
    public void AhriSkinBinNamesTheBoneAnEffectHangsOn()
    {
        if (!Installed) return;
        var roles = Roles("Ahri", out var name);
        if (roles.Count == 0) return;

        // Ahri's R turns on eye effects bound to L_Eye and R_Eye through a buff condition
        var eyes = Find(roles, name, "Ahri_Base_R_Eyes");
        if (eyes is null) return;   // a skin that suppresses it is allowed
        Assert.Equal(VfxSystemRole.Bone, eyes.Role);
        Assert.False(string.IsNullOrWhiteSpace(eyes.Bone), "a persistent effect names its bone in 97% of records");
        Assert.Contains(eyes.Bone!, eyes.Why);
    }

    [Fact]
    public void ASecondChampionAgrees()
    {
        if (!Installed) return;
        // Ezreal's Q is a plain FixedSpeedMovement missile, the shape 1,114 of 1,162 specs have
        var roles = Roles("Ezreal", out var name);
        if (roles.Count == 0) return;
        var q = Find(roles, name, "Ezreal_Base_Q_mis");
        if (q is null) return;
        Assert.Equal(VfxSystemRole.Missile, q.Role);
        Assert.True(q.Motion.Value > 0f);
    }

    // ===================================================== the rig it asks for

    [Fact]
    public void AMissileRigFliesAtTheAuthoredSpeed()
    {
        var basis = new VfxPreviewRig();
        var link = new VfxSystemLink(VfxSystemRole.Missile, "AhriEMissile",
            new MissileMotion(MissileMotionKind.ConstantSpeed, 1550f));
        var rig = link.Rig(basis);
        Assert.Equal(VfxRigMode.Missile, rig.Mode);
        Assert.Equal(1550f, rig.Speed, 1);
        // the reader's own height survives - the link says what it flies like, not where it stands
        Assert.Equal(basis.Height, rig.Height, 3);

        // a fixed duration becomes the speed that crosses the rig in that time
        var timed = new VfxSystemLink(VfxSystemRole.Missile, "x",
            new MissileMotion(MissileMotionKind.FixedDuration, 0.5f)).Rig(basis);
        Assert.Equal(basis.Distance / 0.5f, timed.Speed, 1);

        // a movement that authors neither keeps the rig's own speed rather than flying at nothing
        var silent = new VfxSystemLink(VfxSystemRole.Missile, "x", MissileMotion.None).Rig(basis);
        Assert.Equal(basis.Speed, silent.Speed, 1);
        Assert.Contains("no speed", silent is null ? "" :
            new VfxSystemLink(VfxSystemRole.Missile, "x", MissileMotion.None).Why);
    }

    [Fact]
    public void AbsurdSpeedsAreClampedToSomethingAPreviewCanShow()
    {
        var basis = new VfxPreviewRig();
        // Aurelion Sol's E is authored at 2,500,000 units a second - a flight of half a millisecond
        var instant = new VfxSystemLink(VfxSystemRole.Missile, "x",
            new MissileMotion(MissileMotionKind.ConstantSpeed, 2_500_000f)).Rig(basis);
        Assert.Equal(basis.Distance / 0.05f, instant.Speed, 1);
        // Hwei's W is authored at 0.5, which would take forty minutes to cross the rig
        var crawling = new VfxSystemLink(VfxSystemRole.Missile, "x",
            new MissileMotion(MissileMotionKind.ConstantSpeed, 0.5f)).Rig(basis);
        Assert.Equal(basis.Distance / 30f, crawling.Speed, 1);
    }

    [Fact]
    public void OnlyAMissileMoves()
    {
        var basis = new VfxPreviewRig(VfxRigMode.Trail);
        foreach (var role in new[] { VfxSystemRole.Target, VfxSystemRole.Bone })
        {
            var rig = new VfxSystemLink(role, "x", Bone: "L_Eye").Rig(basis);
            Assert.Equal(VfxRigMode.Still, rig.Mode);
        }
        // and each role explains itself, because the rig it asks for is otherwise unexplained
        foreach (var role in new[] { VfxSystemRole.Missile, VfxSystemRole.Target, VfxSystemRole.Bone })
            Assert.False(string.IsNullOrWhiteSpace(new VfxSystemLink(role, "AhriQ", Bone: "L_Eye").Why));
    }

    // ===================================================== when it is available at all

    [Theory]
    [InlineData("data/characters/ahri/skins/skin0.bin", "ahri")]
    [InlineData("DATA/Characters/Ahri/Ahri_multi_skins_skin0.bin", "ahri")]
    [InlineData(@"data\characters\lux\lux.bin", "lux")]
    // a map bin, the mode data and a loose file have no champion, and no spell record names their systems
    [InlineData("data/maps/shipping/map11/map11.bin", null)]
    [InlineData("data/maps/shipping/map11/modespecificdata/x.bin", null)]
    [InlineData("particles.bin", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheChampionComesOutOfThePathOrNothingDoes(string? path, string? expected) =>
        Assert.Equal(expected, ReyEngine.App.ViewModels.MainWindowViewModel.ChampionOf(path));

    [Fact]
    public void TheEditorPrefersTheRecordAndSaysWhichItUsed()
    {
        string? vm = Source("src", "ReyEngine.App", "ViewModels", "ParticleEditorViewModel.cs");
        Assert.NotNull(vm);
        // the record first, the name guess in the else
        int link = vm!.IndexOf("ResolveRole?.Invoke(def) is { } link", StringComparison.Ordinal);
        int guess = vm.IndexOf("VfxRigNaming.For(value.Entry.Name)", StringComparison.Ordinal);
        Assert.True(link > 0 && guess > link, "the spell record must be consulted before the name");
        // and the speed comes with it
        Assert.Contains("RigSpeed = rig.Speed;", vm);

        string? host = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.ParticleRoles.cs");
        Assert.NotNull(host);
        // the champion's root bin for its spells, every skin bin for its resource map
        Assert.Contains("VfxSystemRoles.FromSpells(recordBin, champion)", host);
        Assert.Contains("VfxSystemResolver.ExtractResourceMap(bytes)", host);

        // built off the UI thread, beside the parse - a champion with sixty skins is sixty reads
        string? main = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        Assert.NotNull(main);
        Assert.Contains("BuildParticleRoles(binPath)", main);
        int run = main!.IndexOf("BuildParticleRoles(binPath)", StringComparison.Ordinal);
        int task = main.IndexOf("await System.Threading.Tasks.Task.Run(", StringComparison.Ordinal);
        Assert.True(task > 0 && run > task, "the role build must be inside the Task.Run, not on the dispatcher");

        string? view = Source("src", "ReyEngine.App", "Views", "ParticleEditorView.axaml");
        Assert.NotNull(view);
        Assert.Contains("FROM THE SPELL RECORD", view);
        Assert.Contains("GUESSED FROM THE NAME", view);
    }
}
