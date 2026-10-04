using System.Globalization;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// M815: what the declaration plan needs to know about a project's forced map skin - the project's recorded switches
/// (<see cref="ReyProject.BinRecipes"/>) and the names a recipe replay shows (<c>null</c> is fine). Passing it to
/// <see cref="BinDeclarationPlanner.Plan"/> turns the by-reference route on; passing none leaves the plan exactly as M814
/// made it, which is also what a project with declarations switched off gets (its bins are never planned at all).
/// </summary>
public sealed record MapSkinDeclarationOptions(IReadOnlyList<BinRecipeRecord> Recipes, Func<uint, string?>? Resolve = null);

/// <summary>
/// M815: a forced map skin (<see cref="MapSkinSwitcher"/>) declared BY REFERENCE.
///
/// <para><b>The problem.</b> The Map Skin Switcher writes whole bins; M814's declarations turn a whole bin into its
/// values. Both freeze Riot's data at the project's patch. What the switch means is "every slot loads that slot's
/// environment", and Crauzer's <c>winter-rift-2025_0.3.0.fantome</c> (ltk_mod_project 0.16.2) states it that way: base module 0
/// sets <c>Default</c>'s <c>mMapContainerLink</c>, <c>mGrassTintTexture</c>, <c>mResourceResolvers</c> and the unnamed
/// <c>0x2d3285eb</c> each to <c>{"ref": "Maps/Shipping/Map11/MapSkins/Milkshake_SRS:&lt;field&gt;"}</c>, which LTK resolves against the
/// INSTALLED game on every install (<c>ltk_game_data</c> <c>apply</c>: one reading of the game's copy before any edit applies,
/// never the bin being built). A module like that survives patches that change the source slot.</para>
///
/// <para><b>What is declared by reference</b> - exactly the writes <see cref="MapSkinSwitcher"/> makes, and only where the
/// written value IS the source's (measured against the real Map11 and Map12 bins, see the M815 commit):
/// <list type="bullet">
/// <item>every MapSkin object (registered slot or alias) but the source: each route field
/// (<see cref="MapSkinSwitcher.EnvironmentRouteFieldHashes"/>) the slot AND the source carry is set to the source's value - the
/// switcher never adds a route field a slot shipped without, and neither does a reference;</item>
/// <item>with the recipe's character-skin opt-in, each <see cref="MapSkinSwitcher.CharacterSkinFieldHashes"/> field the source carries
/// (the unit-skin list <c>0x2d3285eb</c>, <c>mObjectSkinFallbacks</c>) is set on every slot, one that lacks it included -
/// the part Riot's own data has no example of;</item>
/// <item>the target slot's FeatureAudio object takes the source profile's properties (all but <c>feature</c>);</item>
/// <item>the source container's server-addressed shop placeables sit under the base container's keys
/// (<see cref="MapSkinSwitcher.BuildCompatibleContainer"/>): <c>-items</c> the old keys, <c>+items</c> the new keys with
/// <c>{"ref": "&lt;container&gt;:items{&lt;old key, decimal&gt;}"}</c>, as Crauzer's second module does.</item>
/// </list>
/// Anything the switch writes that is not a copy of game data stays a value (a hand edit; a field the source lacks).</para>
///
/// <para><b>How it stays exact.</b> Fields are decided in GROUPS, all or nothing: a slot's route fields, a slot's character-skin
/// fields, the target's audio profile, and the key moves of one container. A group is KEPT by reference only if the project's bin
/// already holds, for every field of the group, the value the reference would produce. A group the project holds differently or
/// only in part - a hand edit, a switch undone on that slot, a project from an older patch - goes whole to the module of values,
/// because references for the fields that happen to match today (the CFG and particles two slots share) would leave that slot,
/// after the next patch, a mixture the switcher never produces. The kept references are then applied
/// (<see cref="ApplyReferences"/>, which reads values from the game's bin the way the loader does) and the project's bin is diffed
/// against THAT, so the module that follows holds only what the switch does not explain, and the two modules in order give the
/// project's bin. Where a switch cannot be replayed on the installed game (the source slot is gone, Riot renamed it, the recipe is
/// unreadable) nothing is declared by reference and the plan falls back to M814's values, with the reason in the plan's notes.</para>
///
/// <para><b>Limits, measured and not.</b></para>
/// <list type="bullet">
/// <item>The references follow the SOURCE slot's values in whatever patch is installed. Nothing follows a slot the declaration does
/// not name, so a map-skin slot a later patch ADDS or RENAMES is not routed until the project is exported again; the export re-reads
/// the installed game and plans again.</item>
/// <item>A reference is resolved by LTK against the game chunk that declares the entry; the plan checks the entry in the reference
/// WAD the project has. A patch that renames or retypes a referenced field, or removes the source slot, makes LTK skip that edit - a
/// warning in LTK Manager's log (<c>PropertyEditSkipped</c>, <c>log_game_data_diagnostic</c>), nothing in its window - and the slot
/// keeps Riot's value. Measured on a game whose source slot lost <c>mMapObjectsCFG</c> and held <c>mGrassTintTexture</c> as a string:
/// 71 of 136 edits skipped (<c>ReferenceUnresolved</c>, <c>KindMismatch</c>), the other 65 applied.</item>
/// <item><b>The container is the sharp edge.</b> <c>-items</c> and <c>+items</c> are one group to the loader. If Riot re-keys the
/// source container's shop placeables, <c>-items</c> finds no entry (<c>RemovalUnmatched</c>) and LTK skips the whole move, leaving
/// the source's own keys where the server asks for the base container's - the StartSpawn crash <see cref="MapSkinSwitcher"/>
/// documents (League RVA 0x1246fb0). Export again after a patch.</item>
/// <item>A field added to a slot that lacks it (the carried character-skin fields: 35 of 36 slots on Map11) has no base value to
/// type it, so LTK types it from the manager's class schema (<c>ltk_game_data</c> 0.6 has no fallback shape). The databases bundled
/// with LTK Manager v1.20.0 and v1.21.0 type <c>MapSkin.0x2d3285eb</c> (List2 of Embed, from build 7231955) and
/// <c>mObjectSkinFallbacks</c> (Map of Hash to I32), but the manager answers no type for a game build newer than its database (16.18,
/// build 8175716, in both) until it has refreshed the database online; the add is then skipped (<c>Untypable</c>) and that slot keeps
/// no unit skins. The plan counts these adds in its note.</item>
/// <item>The audio source is the one the switcher's own name heuristic picked on today's data; the reference freezes WHICH object,
/// not the choice.</item>
/// </list>
/// </summary>
internal sealed class MapSkinDeclarations
{
    private enum Role { Shipping, SourceContainer }

    private sealed class Switch
    {
        public required MapSkinForceRecipe Recipe { get; init; }
        public required bool Inferred { get; init; }
        public required MapSkinReplayResult Replay { get; init; }
        public required string? SourceContainerPath { get; init; }
        public required string? TargetContainerPath { get; init; }
        public required Func<ulong, string, byte[]?> ReadRiot { get; init; }
        public string Describe() => (Inferred ? "read out of the bin: " : "") + Recipe.Describe();
    }

    private readonly Dictionary<string, (Switch Switch, Role Role)> _byPath = new(StringComparer.Ordinal);
    private readonly List<string> _notes = new();

    /// <summary>A line for the plan's report. The same bin in two layers says the same thing twice; once is enough.</summary>
    private void Note(string line)
    {
        if (!_notes.Contains(line)) _notes.Add(line);
    }

    /// <summary>One line per switch the plan met: what was declared by reference and by value, or why nothing was.</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>How many values of the plan are references to the installed game.</summary>
    public int References { get; private set; }

    private static string Norm(string rel) => rel.Replace('\\', '/').Trim('/').ToLowerInvariant();

    /// <summary>
    /// Find the switches that govern the project's bins: for each shipping map bin (<c>data/maps/shipping/mapNN/mapNN.bin</c>)
    /// the project holds and that differs from the game's, the recorded recipe or, for a project older than recipes, the recipe
    /// <see cref="MapSkinForceRecipe.Infer"/> reads back out of the bin (M730 does the same for the patch updater). The
    /// recipe is replayed on the GAME's bin - Riot's bin with the switch applied - which is what everything else is checked against.
    /// </summary>
    public static MapSkinDeclarations Read(IReadOnlyList<DeclarationFile> files, Func<ulong, string, byte[]?> readRiot,
        MapSkinDeclarationOptions options)
    {
        var set = new MapSkinDeclarations();
        foreach (var f in files)
        {
            if (!f.RelPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || !MapSkinForceRecipe.IsShippingMapBinPath(f.RelPath)) continue;
            string shipping = Norm(f.RelPath);
            if (set._byPath.ContainsKey(shipping)) continue;   // the same bin in a second folder: the first decides
            try { set.ReadOne(f, shipping, readRiot, options); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                set.Note($"{f.RelPath}: the map skin switch could not be read ({ex.Message}); its bins are declared as values.");
            }
        }
        return set;
    }

    private void ReadOne(DeclarationFile f, string shipping, Func<ulong, string, byte[]?> readRiot, MapSkinDeclarationOptions options)
    {
        byte[]? riot = readRiot(HashAlgorithms.WadPath(f.RelPath.Replace('\\', '/')), f.RelPath);
        if (riot is null) return;                                   // new content: nothing to compare with
        byte[] mod = File.ReadAllBytes(f.AbsPath);
        if (mod.AsSpan().SequenceEqual(riot)) return;               // the plain path reports it unchanged

        MapSkinForceRecipe? recipe = null;
        bool inferred = false;
        var record = options.Recipes.FirstOrDefault(r => Norm(r.Path) == shipping && r.Kind == BinRecipeRecord.ForceMapSkinKind);
        if (record is not null)
        {
            try { recipe = MapSkinForceRecipe.FromRecord(record); }
            catch (Exception ex) when (ex is InvalidDataException or FormatException)
            {
                Note($"{f.RelPath}: the recorded map skin switch is unreadable ({ex.Message}); declared as values.");
                return;
            }
        }
        else
        {
            recipe = MapSkinForceRecipe.Infer(mod, riot, options.Resolve);
            inferred = recipe is not null;
        }
        if (recipe is null) return;                                  // not a switch: an ordinary bin

        MapSkinReplayResult replay;
        try { replay = recipe.Replay(riot, options.Resolve); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or KeyNotFoundException)
        {
            Note($"{f.RelPath}: the map skin switch ({recipe.Describe()}) cannot be replayed on the installed game ({ex.Message}); declared as values.");
            return;
        }
        if (replay.UsedSnapshot)
        {
            // the recorded copy of a vaulted slot is the only source of its values: there is no game entry to point at
            Note($"{f.RelPath}: the source slot {replay.SourceName} is not in the installed game, so the switch has nothing to refer to; "
                       + "declared as values from the recorded copy.");
            return;
        }

        string? sourceContainer = MapSkinSwitcher.ContainerBinPath(replay.Swap.Source.MapContainerLink);
        string? targetContainer = MapSkinSwitcher.ContainerBinPath(replay.Swap.Target.MapContainerLink);
        var sw = new Switch
        {
            Recipe = recipe,
            Inferred = inferred,
            Replay = replay,
            SourceContainerPath = sourceContainer is null ? null : Norm(sourceContainer),
            TargetContainerPath = targetContainer,
            ReadRiot = readRiot,
        };
        _byPath[shipping] = (sw, Role.Shipping);
        if (sw.SourceContainerPath is not null && !_byPath.ContainsKey(sw.SourceContainerPath))
            _byPath[sw.SourceContainerPath] = (sw, Role.SourceContainer);
    }

    /// <summary>
    /// The modules a governed bin becomes, in apply order: the references first, then whatever the project's bin holds beyond
    /// the switch as values. Null when <paramref name="relPath"/> is not governed by a switch or nothing about it can be
    /// declared by reference - the caller then diffs the bin as M814 does.
    /// </summary>
    public IReadOnlyList<DeclaredChunk>? Declare(string relPath, string target, byte[] riot, byte[] mod, IDeclarationNames names, DeclarationBaseline? baseline = null)
    {
        if (!_byPath.TryGetValue(Norm(relPath), out var governed)) return null;
        try
        {
            return governed.Role == Role.Shipping
                ? DeclareShipping(governed.Switch, relPath, target, riot, mod, names, baseline)
                : DeclareContainer(governed.Switch, relPath, target, riot, mod, names, baseline);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Note($"{relPath}: could not be declared by reference ({ex.Message}); declared as values.");
            return null;
        }
    }

    // ===================================================== the shipping map bin

    // The groups a switch's references are decided in, all or nothing (see the class summary).
    private const string RouteGroup = "route fields";
    private const string CharacterGroup = "character skins";
    private const string AudioGroup = "audio profile";

    private IReadOnlyList<DeclaredChunk>? DeclareShipping(Switch sw, string relPath, string target, byte[] riotBytes, byte[] modBytes,
        IDeclarationNames names, DeclarationBaseline? baseline)
    {
        if (modBytes.AsSpan().SequenceEqual(riotBytes)) return null;   // the project holds the game's own bin: the plain path says unchanged
        var riot = SafeBinTree.Parse(riotBytes, out var riotIssues);
        var mod = SafeBinTree.Parse(modBytes, out var modIssues);
        if (riotIssues.Count > 0 || modIssues.Count > 0) return null;   // the plain path says why a lossy parse ships whole
        // M823: a bin the imported GameData also targets: the references still read their values from the game's bin (riot), but are applied over the bin the modules in front of this
        // one leave (over), and what is left is diffed against that and the project's bin with the same modules applied (mod)
        if (!TryBaseline(baseline, out var over, ref mod)) return null;

        var wanted = ShippingEdits(sw, riot, out int notExpressible);
        var kept = KeepHeld(wanted, riot, mod, out int groupsDropped);
        if (kept.Count == 0)
        {
            Note($"{relPath}: the project's bin holds none of the switch ({sw.Describe()}) whole; declared as values.");
            return null;
        }
        if (notExpressible > 0)
            Note($"{relPath}: {notExpressible} object(s) the switch writes cannot be produced by copying from the source slot and ship as values.");

        var expected = ApplyReferences(riot, kept, over);
        var residual = BinDeclarations.ConvertTrees(target, expected, mod, names);
        if (residual.WhyNot is not null)
        {
            Note($"{relPath}: beyond the switch the bin cannot be declared ({residual.WhyNot}); it ships whole.");
            return null;
        }

        var chunks = new List<DeclaredChunk> { ReferenceModule(target, kept, riot, names) };
        if (residual.Declared) chunks.Add(residual);
        int slots = kept.Select(e => e.Entry).Distinct().Count();
        // a field the slot lacks has no base value to type it: LTK Manager types it from its class schema (ltk_game_data 0.6 has no fallback)
        int adds = kept.OfType<SetByReference>().Count(e => !riot.Objects[e.Entry].Properties.ContainsKey(e.Field));
        Note($"{relPath}: map skin switch {sw.Describe()} declared by reference - {chunks[0].References} value(s) over {slots} object(s), "
             + $"which follow {sw.Replay.Swap.Source.Name}'s values in the installed patch; map-skin slots a later patch adds or renames are not routed "
             + "until the project is exported again"
             + (adds > 0 ? $"; {adds} field(s) are added to slots that lack them, which LTK Manager types from its class schema (its database must describe the installed game build)" : "")
             + (wanted.Count > kept.Count ? $"; {wanted.Count - kept.Count} value(s) in {groupsDropped} group(s) the project's bin holds differently or only in part "
                 + "(a hand edit, a switch undone on that slot, or a patch the project has not been updated to) are declared as values, whole" : "")
             + (residual.Declared ? $"; {residual.Properties} further edit(s), {residual.ObjectsAdded} object(s) added and {residual.ObjectsRemoved} removed as values" : "")
             + ".");
        References += chunks[0].References;
        return chunks;
    }

    /// <summary>Every write the switch makes that copies the source, in the groups they are decided in: each slot's route fields and,
    /// when the recipe carries them, its character-skin fields; and the target audio profile. A field a reference cannot carry (the
    /// loader refuses another shape) is a <see cref="NotByReference"/> that keeps its group out of the references.
    /// <paramref name="notExpressible"/> counts the objects of the replay a reference does not reproduce (a retyped field, a property
    /// the audio profile loses).</summary>
    private static List<RefEdit> ShippingEdits(Switch sw, BinTree riot, out int notExpressible)
    {
        var swap = sw.Replay.Swap;
        uint sourceHash = swap.Source.PathHash;
        var edits = new List<RefEdit>();
        var source = riot.Objects[sourceHash];
        uint[] fields = sw.Recipe.CarryCharacterSkins
            ? MapSkinSwitcher.EnvironmentRouteFieldHashes.Concat(MapSkinSwitcher.CharacterSkinFieldHashes).ToArray()
            : MapSkinSwitcher.EnvironmentRouteFieldHashes.ToArray();
        int envFields = MapSkinSwitcher.EnvironmentRouteFieldHashes.Count;

        foreach (var (hash, skin) in riot.Objects)
        {
            if (skin.ClassHash != MapSkinSwitcher.MapSkinClassHash || hash == sourceHash) continue;
            for (int i = 0; i < fields.Length; i++)
            {
                uint field = fields[i];
                string group = i < envFields ? RouteGroup : CharacterGroup;
                if (!source.Properties.TryGetValue(field, out var sourceValue)) continue;
                bool has = skin.Properties.TryGetValue(field, out var own);
                if (i < envFields && !has) continue;                 // the switcher never adds a route field a slot lacks
                edits.Add(has && !SameShape(own!, sourceValue)
                    ? new NotByReference(hash, group)                // the loader refuses a reference of another shape (TypeMismatch)
                    : new SetByReference(hash, group, field, sourceHash, field));
            }
        }

        if (swap.ChangedAudioProperties > 0 && swap.RoutedAudioTargetHash is { } audioTarget && swap.RoutedAudioSourceHash is { } audioSource
            && riot.Objects.TryGetValue(audioTarget, out var targetAudio) && riot.Objects.TryGetValue(audioSource, out var sourceAudio))
        {
            foreach (var (field, value) in sourceAudio.Properties)
            {
                if (field == MapSkinSwitcher.FeatureFieldHash) continue;
                edits.Add(targetAudio.Properties.TryGetValue(field, out var own) && !SameShape(own, value)
                    ? new NotByReference(audioTarget, AudioGroup)
                    : new SetByReference(audioTarget, AudioGroup, field, audioSource, field));
            }
        }

        // What would the references produce, against what the switcher produced? A difference is not an error - the module
        // that follows carries it as values - but it is worth saying, because it is a place the rule is more than a copy.
        var applied = ApplyReferences(riot, edits);
        var replayed = SafeBinTree.Parse(sw.Replay.Bytes);
        notExpressible = replayed.Objects.Count(kv => !applied.Objects.TryGetValue(kv.Key, out var o) || !BinPropEquality.ObjectsEqual(o, kv.Value));
        return edits;
    }

    /// <summary>
    /// The edits whose GROUP the project's bin holds entirely. A group that is held in part - a route field edited by hand, a switch
    /// undone on that slot - is dropped whole, so a slot is either all references or all values for that group: a reference for the
    /// fields that happen to equal the source's today would turn the slot, after the next patch, into a mixture the switcher never writes.
    /// </summary>
    private static List<RefEdit> KeepHeld(IReadOnlyList<RefEdit> wanted, BinTree game, BinTree project, out int groupsDropped)
    {
        var kept = new List<RefEdit>();
        groupsDropped = 0;
        foreach (var group in wanted.GroupBy(e => (e.Entry, e.Group)))
        {
            if (group.All(e => e.HeldBy(game, project))) kept.AddRange(group);
            else groupsDropped++;
        }
        return kept;
    }

    // ===================================================== the source container

    private IReadOnlyList<DeclaredChunk>? DeclareContainer(Switch sw, string relPath, string target, byte[] riotBytes, byte[] modBytes,
        IDeclarationNames names, DeclarationBaseline? baseline)
    {
        if (modBytes.AsSpan().SequenceEqual(riotBytes)) return null;   // the project holds the game's own container: the plain path says unchanged
        if (sw.TargetContainerPath is null) return null;            // a base slot with no container: the switch moves no key
        byte[]? baseContainer = sw.ReadRiot(HashAlgorithms.WadPath(sw.TargetContainerPath), sw.TargetContainerPath);
        if (baseContainer is null)
        {
            Note($"{relPath}: the base slot's container {sw.TargetContainerPath} is not in the game copy, so the shop keys cannot be matched; declared as values.");
            return null;
        }
        var compat = MapSkinSwitcher.BuildCompatibleContainer(baseContainer, riotBytes);   // throws when the containers do not match: Declare notes it
        if (compat.Remaps.Count == 0) return null;                   // already compatible: no key moves, the plain path decides

        var riot = SafeBinTree.Parse(riotBytes, out var riotIssues);
        var mod = SafeBinTree.Parse(modBytes, out var modIssues);
        if (riotIssues.Count > 0 || modIssues.Count > 0) return null;
        if (!TryBaseline(baseline, out var over, ref mod)) return null;   // M823: see DeclareShipping

        uint items = HashAlgorithms.Fnv1a("items");
        var wanted = compat.Remaps.OrderBy(r => r.ContainerHash).ThenBy(r => r.OldKey)
            .Select(r => (RefEdit)new MoveKeyByReference(r.ContainerHash, items, r.OldKey, r.NewKey)).ToList();
        var kept = KeepHeld(wanted, riot, mod, out _);               // a container's moves are one group, like the loader applies them
        if (kept.Count == 0)
        {
            Note($"{relPath}: the project's container does not hold the switch's shop key moves; declared as values.");
            return null;
        }

        // A container whose `items` map the project changed beyond the key moves is set whole by the diff (a map is a value),
        // which would overwrite what a reference just did there: such a container is left to the diff alone.
        string? why = null;
        DeclaredChunk residual;
        while (true)
        {
            var expected = ApplyReferences(riot, kept, over);
            residual = BinDeclarations.ConvertTrees(target, expected, mod, names);
            if (residual.WhyNot is not null) { why = residual.WhyNot; break; }
            var overwritten = OverwrittenMaps(residual, kept, names);
            if (overwritten.Count == 0) break;
            kept = kept.Where(e => !overwritten.Contains((e.Entry, ((MoveKeyByReference)e).Field))).ToList();
            if (kept.Count == 0)
            {
                Note($"{relPath}: the project changed the shop containers' items beyond the key moves; declared as values.");
                return null;
            }
        }
        if (why is not null)
        {
            Note($"{relPath}: beyond the switch the bin cannot be declared ({why}); it ships whole.");
            return null;
        }

        var chunks = new List<DeclaredChunk> { ReferenceModule(target, kept, riot, names) };
        if (residual.Declared) chunks.Add(residual);
        // -items and +items are one group to the loader: a key Riot has re-keyed makes -items fail (RemovalUnmatched) and the whole move is skipped
        Note($"{relPath}: {kept.Count} server shop key(s) of the switch's source container moved by reference; if a Riot patch re-keys that container's "
             + "shop placeables, LTK skips the whole move and the server's shop keys may be missing (a StartSpawn crash), so export again after a patch"
             + (residual.Declared ? $"; {residual.Properties} further edit(s), {residual.ObjectsAdded} object(s) added and {residual.ObjectsRemoved} removed as values" : "")
             + ".");
        References += chunks[0].References;
        return chunks;
    }

    /// <summary>
    /// M823: the trees a bin the imported GameData also targets is declared between. <paramref name="over"/> is the game's bin with the modules in front of the new one applied (what the references are
    /// applied over, and what the rest is diffed from); <paramref name="mod"/> becomes the project's copy with the same modules applied. Without a baseline <paramref name="over"/> is null and <paramref name="mod"/>
    /// stays. False when either does not parse without loss: the plain path then says why the bin ships whole.
    /// </summary>
    private static bool TryBaseline(DeclarationBaseline? baseline, out BinTree? over, ref BinTree mod)
    {
        over = null;
        if (baseline is null) return true;
        var game = SafeBinTree.Parse(baseline.Game, out var gameIssues);
        var copy = SafeBinTree.Parse(baseline.Mod, out var copyIssues);
        if (gameIssues.Count > 0 || copyIssues.Count > 0) return false;
        over = game;
        mod = copy;
        return true;
    }

    /// <summary>The (object, map field) pairs the diff sets whole although a key move edits them.</summary>
    private static HashSet<(uint Entry, uint Field)> OverwrittenMaps(DeclaredChunk residual, List<RefEdit> kept, IDeclarationNames names)
    {
        var result = new HashSet<(uint, uint)>();
        if (residual.Edit is not { } edit) return result;
        foreach (var move in kept.OfType<MoveKeyByReference>())
        {
            string entry = BinDeclarations.EntryKey(move.Entry, names), field = BinDeclarations.FieldKey(move.Field, names);
            if (edit.Bodies.Any(b => b.Entry == entry && b.Edits.Any(e => e.Key == field))) result.Add((move.Entry, move.Field));
        }
        return result;
    }

    // ===================================================== references: the edits, applied and spelled

    /// <summary>One edit of a reference module. It names what it reads (in the game's bin) and what it writes, and the
    /// <see cref="Group"/> of one object it is decided in (all of a group by reference, or none).</summary>
    private abstract record RefEdit(uint Entry, string Group)
    {
        /// <summary>Does the project's bin already hold what this edit produces? Only then may the edit stand for it.</summary>
        public abstract bool HeldBy(BinTree game, BinTree project);
    }

    /// <summary><c>entry: { field: {"ref": "sourceEntry:sourceField"} }</c> - the field takes the game's value of the source.</summary>
    private sealed record SetByReference(uint Entry, string Group, uint Field, uint SourceEntry, uint SourceField) : RefEdit(Entry, Group)
    {
        public override bool HeldBy(BinTree game, BinTree project) =>
            project.Objects.TryGetValue(Entry, out var mine) && mine.Properties.TryGetValue(Field, out var have)
            && game.Objects.TryGetValue(SourceEntry, out var source) && source.Properties.TryGetValue(SourceField, out var want)
            && BinPropEquality.PropsEqual(have, want);
    }

    /// <summary>A field of the group the loader would refuse to set by reference (another shape). It is never held, so its group
    /// goes to the module of values; it is neither applied nor spelled.</summary>
    private sealed record NotByReference(uint Entry, string Group) : RefEdit(Entry, Group)
    {
        public override bool HeldBy(BinTree game, BinTree project) => false;
    }

    /// <summary><c>entry: { -field: [old], +field: { new: {"ref": "entry:field{old}"} } }</c> - the map entry under <see cref="OldKey"/>
    /// moves to <see cref="NewKey"/>, carrying the game's value. All the moves of one map property are one group.</summary>
    private sealed record MoveKeyByReference(uint Entry, uint Field, uint OldKey, uint NewKey)
        : RefEdit(Entry, "key moves of 0x" + Field.ToString("x8", CultureInfo.InvariantCulture))
    {
        public override bool HeldBy(BinTree game, BinTree project)
        {
            if (!game.Objects.TryGetValue(Entry, out var g) || !g.Properties.TryGetValue(Field, out var gp) || gp is not BinTreeMap gameMap) return false;
            if (!project.Objects.TryGetValue(Entry, out var p) || !p.Properties.TryGetValue(Field, out var pp) || pp is not BinTreeMap mine) return false;
            var was = Find(gameMap, OldKey);
            var now = Find(mine, NewKey);
            return was is not null && now is not null && Find(mine, OldKey) is null && BinPropEquality.PropsEqual(was, now);
        }
    }

    private static BinTreeProperty? Find(BinTreeMap map, uint key)
    {
        foreach (var pair in map)
            if (pair.Key is BinTreeHash h && h.Value == key) return pair.Value;
        return null;
    }

    /// <summary>What the patch type rule compares (<c>ltk_meta</c> <c>ValueShape</c>): the kind; a container's or option's item kind; a map's key
    /// and value kinds; and the class of an EMBED. A pointer's class is not compared. A value of another shape is a <c>TypeMismatch</c> skip,
    /// and a reference is read only into a property of the same shape (<c>coerce.rs</c> <c>referenced</c>).</summary>
    private static bool SameShape(BinTreeProperty a, BinTreeProperty b)
    {
        if (a.Type != b.Type) return false;
        return (a, b) switch
        {
            (BinTreeEmbedded x, BinTreeEmbedded y) => x.ClassHash == y.ClassHash,
            (BinTreeContainer x, BinTreeContainer y) => x.ElementType == y.ElementType,
            (BinTreeOptional x, BinTreeOptional y) => x.ValueType == y.ValueType,
            (BinTreeMap x, BinTreeMap y) => x.KeyType == y.KeyType && x.ValueType == y.ValueType,
            _ => true,
        };
    }

    /// <summary>
    /// The game's bin after the edits, with every value read from the game's bin - the way <c>ltk_game_data</c> reads a reference
    /// (once, before any edit applies) - and written where the loader writes it: a set replaces the field in place or appends it,
    /// a key move removes every old key and then appends the new ones. The same reading the harness checks against LTK's own
    /// <c>apply</c> over the installed Map11 and Map12 bins.
    /// </summary>
    private static BinTree ApplyReferences(BinTree game, IReadOnlyList<RefEdit> edits, BinTree? over = null)
    {
        // M823: the values are read from the game's bin (game); what they are written into is the bin the modules in front of these left (over), when there is one
        var target = over ?? game;
        var byEntry = edits.GroupBy(e => e.Entry).ToDictionary(g => g.Key, g => g.ToList());
        var objects = new List<BinTreeObject>(target.Objects.Count);
        foreach (var obj in target.Objects.Values)
        {
            if (!byEntry.TryGetValue(obj.PathHash, out var mine)) { objects.Add(obj); continue; }
            var props = obj.Properties.Values.ToList();
            foreach (var set in mine.OfType<SetByReference>())
            {
                var value = BinTreeCloner.Clone(game.Objects[set.SourceEntry].Properties[set.SourceField], set.Field);
                int at = props.FindIndex(p => p.NameHash == set.Field);
                if (at >= 0) props[at] = value; else props.Add(value);
            }
            foreach (var group in mine.OfType<MoveKeyByReference>().GroupBy(m => m.Field))
            {
                int at = props.FindIndex(p => p.NameHash == group.Key);
                var map = (BinTreeMap)props[at];
                var entries = map.ToList();
                var moved = group.ToDictionary(m => m.OldKey, m => Find(map, m.OldKey) ?? throw new InvalidDataException($"No entry 0x{m.OldKey:x8} to move."));
                entries.RemoveAll(kv => kv.Key is BinTreeHash h && moved.ContainsKey(h.Value));
                foreach (var m in group)
                    entries.Add(new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, m.NewKey), BinTreeCloner.Clone(moved[m.OldKey], 0)));
                props[at] = new BinTreeMap(group.Key, map.KeyType, map.ValueType, entries);
            }
            objects.Add(new BinTreeObject(obj.PathHash, obj.ClassHash, props));
        }
        return new BinTree(objects, target.Dependencies);
    }

    /// <summary>The module of the kept edits: one body per object, in the order the game's bin lists them; a key move is a
    /// removal and an addition under the same key, which <c>ltk_game_data</c> applies as one group (all or nothing).</summary>
    private static DeclaredChunk ReferenceModule(string target, IReadOnlyList<RefEdit> edits, BinTree game, IDeclarationNames names)
    {
        var bodies = new List<DeclaredBody>();
        var byEntry = edits.ToLookup(e => e.Entry);
        int properties = 0;
        foreach (var hash in game.Objects.Keys)
        {
            if (!byEntry.Contains(hash)) continue;
            var entries = new List<DeclEntry>();
            foreach (var set in byEntry[hash].OfType<SetByReference>())
                entries.Add(new DeclEntry(BinDeclarations.FieldKey(set.Field, names),
                    new DeclRef(RefEntry(set.SourceEntry, names), DeclPath.Field(BinDeclarations.FieldKey(set.SourceField, names)))));
            foreach (var group in byEntry[hash].OfType<MoveKeyByReference>().GroupBy(m => m.Field))
            {
                string field = BinDeclarations.FieldKey(group.Key, names);
                entries.Add(DeclEntry.Remove(field, new DeclList(group.Select(m => (DeclValue)new DeclText(Hex(m.OldKey))).ToList())));
                entries.Add(DeclEntry.Add(field, new DeclMap(group.Select(m => new DeclEntry(Hex(m.NewKey),
                    new DeclRef(RefEntry(hash, names), DeclPath.Field(field).Keyed((ulong)m.OldKey)))).ToList())));
            }
            bodies.Add(new DeclaredBody(BinDeclarations.EntryKey(hash, names), entries));
            properties += entries.Count;
        }
        return BinDeclarations.FromModel(target, new DeclaredEdit(Array.Empty<string>(), Array.Empty<string>(), bodies, Array.Empty<DeclaredObject>()), properties);
    }

    private static string Hex(uint hash) => "0x" + hash.ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>The entry of a reference: its name, or its hash where the name holds the one character a reference splits at.</summary>
    private static string RefEntry(uint hash, IDeclarationNames names)
    {
        string key = BinDeclarations.EntryKey(hash, names);
        return key.Contains(':') ? Hex(hash) : key;
    }
}
