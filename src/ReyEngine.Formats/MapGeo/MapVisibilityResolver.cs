namespace ReyEngine.Formats.MapGeo;

/// <summary>Explains why a mesh is visible or hidden under the current dragon + baron selection.</summary>
public sealed class VisibilityDiagnostic
{
    public int Flags;
    public string FlagLabel = "";
    public uint ControllerHash;
    public bool HasController;
    public int ControllerDragonBits;
    public int ControllerBaronBits;
    public bool ControllerNotVisible;

    public int DragonBit;
    public string DragonName = "All";
    public int BaronBit;
    public string BaronName = "All";

    public bool DragonVisible;
    public bool BaronVisible;
    public bool Visible => DragonVisible && BaronVisible;
    public string Reason = "";
}

/// <summary>
/// Resolves a mapgeo mesh/group's final visibility from ALL relevant inputs together (M33): its per-mesh
/// dragon layer bitmask AND its baron/visibility <see cref="MapVisibilityControllers">controller</see>.
/// Produces a <see cref="VisibilityDiagnostic"/> so the UI can show exactly why a mesh is on or off.
/// Lives in Formats (not Core) because it depends on the mesh/controller types decoded here.
/// </summary>
public sealed class MapVisibilityResolver
{
    private readonly MapVisibilityControllers? _controllers;

    public MapVisibilityResolver(MapVisibilityControllers? controllers) => _controllers = controllers;

    /// <summary>Fast boolean check used by the per-submesh visibility array.</summary>
    public bool IsVisible(int flags, uint controllerHash, int dragonBit, int baronBit)
        => Resolve(flags, controllerHash, dragonBit, baronBit).Visible;

    public VisibilityDiagnostic Resolve(int flags, uint controllerHash, int dragonBit, int baronBit)
    {
        var d = new VisibilityDiagnostic
        {
            Flags = flags,
            FlagLabel = MapVisibility.DragonLabel(flags),
            ControllerHash = controllerHash,
            HasController = controllerHash != 0,
            DragonBit = dragonBit,
            DragonName = NameForDragon(dragonBit),
            BaronBit = baronBit,
            BaronName = NameForBaron(baronBit),
        };

        var ctrl = (controllerHash != 0 ? _controllers?.Resolve(controllerHash) : null) ?? BaronResolution.Unconstrained;
        d.ControllerDragonBits = ctrl.DragonBits;
        d.ControllerBaronBits = ctrl.BaronBits;
        d.ControllerNotVisible = ctrl.NotVisible;

        // Dragon: the per-mesh layer bitmask is authoritative (verified against Map11's flag histogram — a
        // mesh belongs to exactly the dragon layers its bits name). The controller's dragon bits are reported
        // in the diagnostic for transparency but do NOT widen visibility, otherwise base-flagged meshes with
        // an elemental controller would wrongly re-appear under every dragon (the very bug M33 fixes).
        d.DragonVisible = MapVisibility.VisibleForDragon(flags, dragonBit);

        // Baron: resolved purely from the controller (bit 0 = "All" = no filter).
        d.BaronVisible = ctrl.VisibleForBaron(baronBit);

        d.Reason = BuildReason(d, d.DragonVisible);
        return d;
    }

    private static string BuildReason(VisibilityDiagnostic d, bool bitmaskVis)
    {
        if (!d.DragonVisible)
            return $"hidden: dragon '{d.DragonName}' (bit {d.DragonBit}) is not in the mesh's layer mask {d.Flags} [{d.FlagLabel}]";
        if (!d.BaronVisible)
            return d.ControllerNotVisible
                ? $"hidden: controller excludes baron '{d.BaronName}' (controller baron bits {d.ControllerBaronBits}, inverted)"
                : $"hidden: baron '{d.BaronName}' (bit {d.BaronBit}) is not in the controller's baron bits {d.ControllerBaronBits}";

        var why = d.DragonBit == 0 ? "no dragon filter" : $"layer mask {d.Flags} [{d.FlagLabel}] includes '{d.DragonName}'";
        var baronWhy = d.BaronBit == 0 ? "no baron filter" : "controller allows this baron state";
        return $"visible: {why}; {baronWhy}";
    }

    private static string NameForDragon(int bit)
    {
        if (bit == 0) return "All";
        foreach (var l in MapVisibility.Dragons) if (l.Bit == bit) return l.Name;
        return $"bit {bit}";
    }

    private static string NameForBaron(int bit)
    {
        if (bit == 0) return "All";
        foreach (var l in MapVisibility.Barons) if (l.Bit == bit) return l.Name;
        return $"bit {bit}";
    }
}
