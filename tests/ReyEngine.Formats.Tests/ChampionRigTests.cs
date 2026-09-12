using ReyEngine.App.ViewModels;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M715: the rig reaches the champion window, for the one thing in it that stands still.
///
/// <para>Everything else there already moves better than a rig could. A cast composite flies its missile
/// over the real distance at the ability's own authored speed, with the real aim and the real cast delay;
/// a clip event rides an animated bone on a real skeleton. The rig is for the manual CHAMPION VFX pick,
/// which sets no motion of any kind, and the viewport refuses to rig anything carrying a bone or a travel
/// destination so it can never make those three worse.</para>
///
/// <para>It also carries the half of M713 the particle editor could not: a system the skin hangs on a bone
/// is hung on that bone here, because this window is the champion.</para>
/// </summary>
public sealed class ChampionRigTests
{
    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact]
    public void ThereIsNoRigUntilASystemIsPickedByHand()
    {
        var vm = new MeshPreviewViewModel();
        Assert.Null(vm.Rig);

        // the settings can be moved with nothing picked - they just do not reach the viewport
        vm.IsRigMissile = true;
        Assert.Equal(VfxRigMode.Missile, vm.RigMode);
        Assert.Null(vm.Rig);

        // and they are one enum, as in the particle editor
        Assert.False(vm.IsRigStill);
        vm.IsRigMissile = false;
        Assert.Equal(VfxRigMode.Missile, vm.RigMode);   // a segment does not clear itself
        vm.RigMode = VfxRigMode.Burst;
        Assert.True(vm.RigReplay);                       // Burst is Still that starts over
        Assert.True(vm.IsRigBurst);
    }

    [Fact]
    public void TheViewportRefusesToRigWhatSomethingElseIsCarrying()
    {
        string? viewport = Source("src", "ReyEngine.App", "Views", "ViewportControl.cs");
        Assert.NotNull(viewport);
        // a travelling missile and a bone-attached effect both already have an owner for their transform,
        // and the bone re-anchor runs in the same frame as the rig loop
        Assert.Contains("if (item.TravelTo is not null) continue;", viewport);
        Assert.Contains("if (item.AttachBone is not null) continue;", viewport);
        // and the rig moves around where the item was placed, not around the world origin - the particle
        // editor places at the origin so nothing changes there, the champion window anchors at the caster
        Assert.Contains("rig.Pose(phase) * Matrix4x4.CreateTranslation(item.WorldPos)", viewport);
    }

    [Fact]
    public void TheChampionWindowCarriesTheBoneRigTheParticleEditorCannot()
    {
        string? vm = Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.cs");
        Assert.NotNull(vm);
        // M713 reads the bone out of the skin bin and the particle editor can only stand the effect still.
        // Here the skeleton exists, so the effect is hung on the bone the skin names.
        Assert.Contains("link.Role == ReyEngine.Formats.Characters.VfxSystemRole.Bone", vm);
        Assert.Contains("item with { AttachBone = bone }", vm);
        // the spell record still sets the missile and its speed, with the name guess as the fallback
        Assert.Contains("RigSpeed = rig.Speed;", vm);
        Assert.Contains("VfxRigNaming.For(def.Name)", vm);

        string? host = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        Assert.NotNull(host);
        Assert.Contains("MeshPreview.SetVfxRoles(BuildParticleRoles(", host);

        string? window = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml");
        Assert.NotNull(window);
        Assert.Contains("ParticleRig=\"{Binding Rig}\"", window);
        // and the panel only appears while a system is picked, so it cannot be mistaken for something
        // that governs the cast composite
        Assert.Contains("IsVisible=\"{Binding SelectedVfx, Converter={x:Static ObjectConverters.IsNotNull}}\"", window);
    }
}
