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

        // Mirrors MapgeoAddon.update_environment_visibility exactly.
        // STEP 1 — dragon. The Base bit stays visible under every dragon (the foundation), UNLESS the mesh's
        // visibility controller carries dragon bits: then the controller OVERRIDES the bitmask (this is how a
        // base mesh gets disabled), applying ParentMode 3 as an inversion. A controller dragon set that
        // includes Base (bit 1) counts as "in-list" for any dragon.
        bool controllerOverrodeDragon = false;
        if (dragonBit == 0)
        {
            d.DragonVisible = true; // "All" — no dragon filter
        }
        else if (d.HasController && ctrl.DragonBits != 0)
        {
            controllerOverrodeDragon = true;
            bool inList = (ctrl.DragonBits & MapVisibility.BaseBit) != 0 || (ctrl.DragonBits & dragonBit) != 0;
            d.DragonVisible = ctrl.NotVisible ? !inList : inList;
        }
        else
        {
            d.DragonVisible = MapVisibility.VisibleForDragon(flags, dragonBit); // bitmask; Base foundation on
        }

        // STEP 2 — baron. Controller baron bits + ParentMode (bit 0 / no baron bits = "All" = visible).
        d.BaronVisible = ctrl.VisibleForBaron(baronBit);

        d.Reason = BuildReason(d, controllerOverrodeDragon);
        return d;
    }

    private static string BuildReason(VisibilityDiagnostic d, bool controllerOverrodeDragon)
    {
        if (!d.DragonVisible)
            return controllerOverrodeDragon
                ? $"hidden: controller {(d.ControllerNotVisible ? "excludes" : "restricts to")} dragon bits {d.ControllerDragonBits}, which {(d.ControllerNotVisible ? "includes" : "omits")} '{d.DragonName}'"
                : $"hidden: dragon '{d.DragonName}' (bit {d.DragonBit}) is not in the mesh's layer mask {d.Flags} [{d.FlagLabel}]";
        if (!d.BaronVisible)
            return d.ControllerNotVisible
                ? $"hidden: controller excludes baron '{d.BaronName}' (controller baron bits {d.ControllerBaronBits}, inverted)"
                : $"hidden: baron '{d.BaronName}' (bit {d.BaronBit}) is not in the controller's baron bits {d.ControllerBaronBits}";

        string why = d.DragonBit == 0 ? "no dragon filter"
            : controllerOverrodeDragon ? $"controller dragon bits {d.ControllerDragonBits} allow '{d.DragonName}'"
            : (d.Flags & MapVisibility.BaseBit) != 0 && (d.Flags & d.DragonBit) == 0 ? $"Base foundation (layer mask {d.Flags}) is visible under every dragon"
            : $"layer mask {d.Flags} [{d.FlagLabel}] includes '{d.DragonName}'";
        string baronWhy = d.BaronBit == 0 ? "no baron filter" : "controller allows this baron state";
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
