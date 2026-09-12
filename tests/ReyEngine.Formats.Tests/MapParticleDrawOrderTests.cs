namespace ReyEngine.Formats.Tests;

/// <summary>
/// M710: the map viewport's Direct3D 11 particles had no draw order at all.
///
/// <para>It looked like they did. The driver ends its rebuild with
/// <c>_slices.OrderBy(... Pass)</c>, and that line has been read as the draw order since it was written.
/// It is not: every material was already handed to the renderer by <c>AddMaterial</c> inside the build
/// loop above it, and the renderer draws a material that does not write depth in the order it was given
/// one. So this host drew particles in BUILD order - placement-major, then authored emitter - and
/// <c>pass</c>, authored on 79.7% of emitters, never reached the picture. What the slice sort really
/// reaches is the quad budget's packing.</para>
///
/// <para>Registration is now collected and flushed once, sorted by the shared key M709 established. That
/// is also the only shape that reaches all four channels: quads, Riot meshes, legacy meshes and ribbons
/// register from four different places and draw from one list.</para>
/// </summary>
public sealed class MapParticleDrawOrderTests
{
    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string Driver() =>
        Source("src", "ReyEngine.App", "Services", "D3D11MapParticles.cs")
        ?? throw new InvalidOperationException("D3D11MapParticles.cs is not on disk");

    [Fact]
    public void EveryChannelRegistersThroughTheOneSeam()
    {
        string src = Driver();

        // four registration sites - quad, Riot mesh, legacy mesh, ribbon - and not one of them talks to the
        // renderer directly any more. A fifth channel added later will be ordered by construction.
        Assert.Equal(4, src.Split("Register(mat, def);").Length - 1 + (src.Split("Register(riotMat, def);").Length - 1));

        // exactly one AddMaterial in the whole driver, and it is the sorted flush. More than one means a
        // channel has slipped back out of the order.
        Assert.Equal(1, src.Split("_renderer.AddMaterial(").Length - 1);
        Assert.Contains("foreach (var pending in _pending.OrderBy(static e => VfxDrawOrder.KeyFor(e.Def)))", src);
        Assert.Contains("_renderer.AddMaterial(pending.Mat);", src);
    }

    [Fact]
    public void ThePackingOrderFollowsTheDrawOrder()
    {
        string src = Driver();
        // the slice list takes the SAME key as the registration. Pack thins the last slices first, and
        // "last" is only meaningful if it means the same thing the frame draws last.
        Assert.Contains("_slices = _slices.OrderBy(static s => VfxDrawOrder.KeyFor(s.Def)).ToList();", src);
        Assert.DoesNotContain("_slices.OrderBy(static s => s.Def.Pass)", src);
    }

    [Fact]
    public void AnAbandonedRebuildCannotLeakIntoTheNextDrawOrder()
    {
        string src = Driver();
        // cleared twice: once beside _mine at the top of a rebuild, once after the flush. A rebuild that
        // returns early - no playback, no shader toc - would otherwise hand the next one its materials.
        Assert.Equal(2, src.Split("_pending.Clear();").Length - 1);
        int reset = src.IndexOf("_mine.Clear();", StringComparison.Ordinal);
        int nearby = src.IndexOf("_pending.Clear();", StringComparison.Ordinal);
        Assert.True(reset > 0 && nearby > reset && nearby - reset < 400,
            "the pending list must be cleared beside _mine, not somewhere else");
    }
}
