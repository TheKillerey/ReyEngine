using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Meta;

/// <summary>
/// M590: bring a mod's asset references onto the wire form the installed patch uses.
///
/// <para>Patch 16.17 changed asset paths from <c>String</c> to <c>WadChunkLink</c>. Measured on the
/// installed patch: <c>texturePath</c> is 11,695 links and <b>zero</b> strings, <c>mAnimationFilePath</c>
/// 8,983 and zero, <c>oldAsset</c> 3,432 and zero.</para>
///
/// <para><b>Why a rebase alone does not fix it.</b> A three-way merge carries the mod's own objects
/// forward verbatim, so after rebasing a 16.10 map onto 16.17 its materials still held 1,029 String
/// texturePaths against 254 links inherited from Riot's untouched objects — one bin, two eras. The client
/// skips a property whose wire form disagrees with the schema rather than reporting it (M507, where a
/// mistagged container made it miss MULTIPLY_ALPHA entirely), so those materials lose their textures with
/// nothing said.</para>
///
/// <para><b>The field list is not hardcoded, and it is keyed by the CLASS that owns the field, not the
/// name alone.</b> It is derived from the patch's OWN copy of the same bin: a (owning class, field name)
/// pair that appears only as a link there is one this file must write as a link, and a pair that appears
/// both ways is left alone. Keying by name alone breaks the moment two unrelated classes reuse the same
/// field name for different things: <c>SkinMeshDataProperties.texture</c> (a champion skin's diffuse map —
/// link-only since 16.17) and <c>VfxEmitterDefinitionData.texture</c> (a particle's sprite sheet — still a
/// String) live in the same bin whenever a skin also carries particle systems. Surveying "texture" as one
/// name sees it written both ways in that bin and refuses to touch it, so the skin's own texture stays a
/// String and the client draws the model pure white. Measured on real bins: <c>sru_blue/skins/skin4.bin</c>,
/// <c>sru_red/skins/skin3.bin</c> and the earth/hextech/chemtech dragon <c>skin0.bin</c>s all failed to
/// convert this way, while the air/fire/water/elder dragon <c>skin0.bin</c>s — which carry no VFX, so
/// "texture" is never written as a String there — converted correctly. Splitting the survey per owning
/// class (a top-level property's owner is its <c>BinTreeObject.ClassHash</c>; a nested property's owner is
/// the enclosing <c>BinTreeStruct</c>/<c>BinTreeEmbedded.ClassHash</c>, re-rooted every time traversal
/// enters one — whether it arrived there via a struct field, a container element, a map value or an
/// Optional payload) separates the two fields and converts only the one that is genuinely link-only for
/// its own class.</para>
///
/// <para><b>A class the reference never mentions has no per-class evidence.</b> A mod can carry an object
/// of a class this reference bin happens not to contain at all (a class only used elsewhere, or one the
/// mod itself added). Per-class calibration alone would then never convert it, which is a real narrowing
/// versus the old name-only rule for fields like <c>texturePath</c> that are link-only game-wide. When the
/// owning class never appears anywhere in the reference, this falls back to the name-level verdict — link
/// under every class that DOES carry that name in the reference — exactly what the pre-per-class code did.
/// A name that is ambiguous even at that level (like <c>texture</c>) still is not touched.</para>
///
/// <para><b>An Optional hides its field from a survey that only looks at the payload.</b>
/// <c>iconCircle</c>/<c>iconSquare</c> on <c>SkinCharacterDataProperties</c> are <c>Optional&lt;File&gt;</c>;
/// LeagueToolkit always reads an Optional's payload back with <c>NameHash</c> 0 — there is no room on the
/// wire for a second name — so a survey that recursed into the payload and looked at ITS name saw nothing
/// and never converted it: 24 leftover icon Strings in one mod, 432 in another. The fix treats the Optional
/// itself as the field it names: the Optional's own <c>NameHash</c> and the current owning class identify
/// the payload underneath it, for both the survey and the conversion. An EMPTY Optional (no icon set) still
/// declares an element type on the wire, and that type goes stale the same way a set value's does — fixed
/// by reusing <see cref="BinTreeCloner"/>'s M756 mechanism for retyping an empty option in place.</para>
///
/// <para><b>A list of paths is invisible to a survey keyed by name hash, because its elements have none.</b>
/// <c>SkinCharacterDataProperties.alternateIconsCircle</c> is <c>List&lt;String&gt;</c> until build 8104348
/// and <c>List&lt;File&gt;</c> after. A container's elements carry no name of their own — only the container
/// property itself does — so this surveys the container under its own (owning class, field name) and its
/// declared <c>ElementType</c> (String vs WadChunkLink) rather than its elements' names, and converts every
/// String element the same way a bare field would, for both <c>BinTreeContainer</c> and
/// <c>BinTreeUnorderedContainer</c> (each keeping its own wire form).</para>
///
/// <para>The conversion is meaning-preserving: the stored hash is <c>WadPath(the old string)</c>, which is
/// exactly what the loader derived from that string anyway.</para>
/// </summary>
public static class BinAssetLinkMigration
{
    /// <summary>
    /// What the reference bin says about which fields are link-only, for <see cref="Apply"/> to act on.
    /// <see cref="ScalarFields"/> and <see cref="ContainerFields"/> are strict per-(owning class, field
    /// name) verdicts; <see cref="NameOnlyFallback"/> is the name-only verdict used only when
    /// <see cref="ClassesInReference"/> shows the owning class was never seen in the reference at all.
    /// </summary>
    public sealed record FieldCalibration(
        IReadOnlySet<(uint ClassHash, uint NameHash)> ScalarFields,
        IReadOnlySet<(uint ClassHash, uint NameHash)> ContainerFields,
        IReadOnlySet<uint> NameOnlyFallback,
        IReadOnlySet<uint> ClassesInReference)
    {
        public bool IsEmpty => ScalarFields.Count == 0 && ContainerFields.Count == 0 && NameOnlyFallback.Count == 0;
    }

    /// <summary>Surveys <paramref name="reference"/> for every verdict <see cref="Apply"/> needs.</summary>
    public static FieldCalibration LinkOnlyFields(BinTree reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var st = new SurveyState();
        foreach (var o in reference.Objects.Values) Survey(o.Properties.Values, o.ClassHash, st);

        st.ScalarLink.ExceptWith(st.ScalarString);
        st.ContainerLink.ExceptWith(st.ContainerString);
        st.NameLink.ExceptWith(st.NameString);
        return new FieldCalibration(st.ScalarLink, st.ContainerLink, st.NameLink, st.ClassesSeen);
    }

    /// <summary>
    /// Rewrite every String — including one hiding inside an Optional, an empty Optional's stale element
    /// type, or a container of Strings — on a link-only field into the WadChunkLink form.
    /// </summary>
    /// <returns>How many properties (or, for a container, individual elements) were converted.</returns>
    public static int Apply(BinTree tree, FieldCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(calibration);
        if (calibration.IsEmpty) return 0;

        int converted = 0;
        foreach (var o in tree.Objects.Values) Convert(o.Properties, o.ClassHash, calibration, ref converted);
        return converted;
    }

    /// <summary>Align <paramref name="tree"/> with the wire forms <paramref name="reference"/> uses.</summary>
    public static int AlignWith(BinTree tree, BinTree reference) =>
        Apply(tree, LinkOnlyFields(reference));

    // ---- internals --------------------------------------------------------------------------------

    /// <summary>Accumulates every verdict <see cref="LinkOnlyFields"/> needs in one survey pass.</summary>
    private sealed class SurveyState
    {
        public readonly HashSet<(uint ClassHash, uint NameHash)> ScalarLink = new();
        public readonly HashSet<(uint ClassHash, uint NameHash)> ScalarString = new();
        public readonly HashSet<(uint ClassHash, uint NameHash)> ContainerLink = new();
        public readonly HashSet<(uint ClassHash, uint NameHash)> ContainerString = new();
        public readonly HashSet<uint> NameLink = new();
        public readonly HashSet<uint> NameString = new();
        public readonly HashSet<uint> ClassesSeen = new();
    }

    private static void Survey(IEnumerable<BinTreeProperty> props, uint ownerClass, SurveyState st)
    {
        st.ClassesSeen.Add(ownerClass);   // recorded even if props is empty - the class still EXISTS here
        foreach (var p in props)
        {
            // An Optional's own NameHash is the field's real name, but its payload always comes back with
            // NameHash 0 (see the class doc) — survey the Optional itself under its own name so the field
            // is not invisible just because it happens to be wrapped.
            uint nameHash = p is BinTreeOptional optional ? optional.NameHash : p.NameHash;
            BinTreeProperty? value = p is BinTreeOptional o ? o.Value : p;

            if (nameHash != 0)
            {
                if (value is BinTreeWadChunkLink) { st.ScalarLink.Add((ownerClass, nameHash)); st.NameLink.Add(nameHash); }
                else if (value is BinTreeString) { st.ScalarString.Add((ownerClass, nameHash)); st.NameString.Add(nameHash); }
                else if (value is BinTreeContainer container)
                {
                    if (container.ElementType == BinPropertyType.WadChunkLink) st.ContainerLink.Add((ownerClass, nameHash));
                    else if (container.ElementType == BinPropertyType.String) st.ContainerString.Add((ownerClass, nameHash));
                }
                else if (value is null && p is BinTreeOptional empty)
                {
                    // An empty Optional (no value ever set) still declares an element type on the wire;
                    // treat that the same as a set value of that type, or a field that happens to be unset
                    // on every instance in the reference would contribute no evidence either way.
                    if (empty.ValueType == BinPropertyType.WadChunkLink) { st.ScalarLink.Add((ownerClass, nameHash)); st.NameLink.Add(nameHash); }
                    else if (empty.ValueType == BinPropertyType.String) { st.ScalarString.Add((ownerClass, nameHash)); st.NameString.Add(nameHash); }
                }
            }
            if (value is null) continue;   // an empty Optional has nothing further to descend into

            switch (value)
            {
                case BinTreeStruct s: Survey(s.Properties.Values, s.ClassHash, st); break;
                case BinTreeContainer c: Survey(c.Elements, ownerClass, st); break;
                case BinTreeMap m: Survey(m.Values, ownerClass, st); break;
            }
        }
    }

    /// <summary>Is (<paramref name="ownerClass"/>, <paramref name="nameHash"/>) a scalar (or Optional-
    /// wrapped scalar) field the reference writes only as a link? Falls back to the name-only verdict when
    /// the reference never mentions this class at all — there is no per-class evidence either way.</summary>
    private static bool IsScalarLinkOnly(uint ownerClass, uint nameHash, FieldCalibration calib) =>
        calib.ClassesInReference.Contains(ownerClass)
            ? calib.ScalarFields.Contains((ownerClass, nameHash))
            : calib.NameOnlyFallback.Contains(nameHash);

    /// <summary>A property dictionary: entries can be replaced in place, keyed by name hash.</summary>
    private static void Convert(IDictionary<uint, BinTreeProperty> props, uint ownerClass, FieldCalibration calib, ref int converted)
    {
        foreach (uint key in props.Keys.ToList())
        {
            var p = props[key];
            if (p is BinTreeString s && IsScalarLinkOnly(ownerClass, p.NameHash, calib))
            {
                props[key] = Link(p.NameHash, s.Value);
                converted++;
                continue;
            }
            if (p is BinTreeOptional { Value: BinTreeString os } opt && IsScalarLinkOnly(ownerClass, opt.NameHash, calib))
            {
                // The payload's own NameHash is written as 0 regardless of what it is constructed with (see
                // the class doc), so Link(0, ...) matches the wire form Riot itself writes for Optional<File>.
                props[key] = new BinTreeOptional(opt.NameHash, Link(0, os.Value));
                converted++;
                continue;
            }
            if (p is BinTreeOptional { Value: null, ValueType: BinPropertyType.String } emptyOpt
                && IsScalarLinkOnly(ownerClass, emptyOpt.NameHash, calib))
            {
                // No string to rehash - nothing was ever set - but the element type itself is part of the
                // wire form (M756) and must not stay String once the schema calls for File.
                props[key] = BinTreeCloner.EmptyOptional(emptyOpt.NameHash, BinPropertyType.WadChunkLink);
                converted++;
                continue;
            }
            // The declared ElementType says every element is a String; only act on it if that actually
            // holds - a container whose elements disagree with its own declared type is not a shape this
            // file understands, and guessing at it could write a wire-invalid mixed container.
            if (p is BinTreeContainer { ElementType: BinPropertyType.String } cont
                && calib.ContainerFields.Contains((ownerClass, cont.NameHash))
                && cont.Elements.All(e => e is BinTreeString))
            {
                converted += ConvertContainer(props, key, cont);
                continue;
            }
            Descend(p, ownerClass, calib, ref converted);
        }
    }

    /// <summary>Rebuilds a String-element container as a WadChunkLink-element one of the SAME concrete wire
    /// form (<see cref="BinTreeUnorderedContainer"/> stays unordered), converting every element.</summary>
    /// <returns>How many elements were converted (at least 1, even for an empty container, since its
    /// declared element type changed).</returns>
    private static int ConvertContainer(IDictionary<uint, BinTreeProperty> props, uint key, BinTreeContainer cont)
    {
        var newElements = cont.Elements.Select(e => (BinTreeProperty)Link(0, ((BinTreeString)e).Value)).ToList();
        props[key] = cont is BinTreeUnorderedContainer
            ? new BinTreeUnorderedContainer(cont.NameHash, BinPropertyType.WadChunkLink, newElements)
            : new BinTreeContainer(cont.NameHash, BinPropertyType.WadChunkLink, newElements);
        return Math.Max(newElements.Count, 1);
    }

    /// <summary>A container's elements carry NO name hash of their own, so an element is only ever
    /// descended into — never converted here. The named property that owns them is handled above.</summary>
    private static void Descend(BinTreeProperty p, uint ownerClass, FieldCalibration calib, ref int converted)
    {
        switch (p)
        {
            case BinTreeStruct s:
                Convert(s.Properties, s.ClassHash, calib, ref converted);
                break;
            case BinTreeContainer c:
                for (int i = 0; i < c.Elements.Count; i++) Descend(c.Elements[i], ownerClass, calib, ref converted);
                break;
            case BinTreeOptional o when o.Value is not null:
                Descend(o.Value, ownerClass, calib, ref converted);
                break;
            case BinTreeMap m:
                foreach (var v in m.Values) Descend(v, ownerClass, calib, ref converted);
                break;
        }
    }

    private static BinTreeWadChunkLink Link(uint nameHash, string value) =>
        new(nameHash, string.IsNullOrWhiteSpace(value) ? 0UL
            : BinTexturePath.TryParseHex(value, out ulong raw) ? raw
            : HashAlgorithms.WadPath(value));
}
