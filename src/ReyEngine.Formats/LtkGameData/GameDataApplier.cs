using System.Buffers.Binary;
using System.Text;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>An override file the caller supplies, or why it cannot (<see cref="Error"/> is the reader's own statement).</summary>
public readonly record struct GameDataBytesRead(byte[]? Bytes, string? Error)
{
    public static GameDataBytesRead Found(byte[] bytes) => new(bytes, null);

    public static GameDataBytesRead Failed(string error) => new(null, error);
}

/// <summary>The game's copy of an entry a reference or a PTCH target names: none (the game lacks it), the object, or an error (the game
/// holds it and it could not be read).</summary>
public readonly record struct GameDataEntryRead(PropObject? Object, string? Error)
{
    public static GameDataEntryRead None => default;

    public static GameDataEntryRead Found(PropObject obj) => new(obj, null);

    public static GameDataEntryRead Failed(string error) => new(null, error);
}

/// <summary>Supplies the bytes of an override file by its path, once per listed path in apply order.</summary>
public delegate GameDataBytesRead ReadOverrideFile(OverridePath path);

/// <summary>Supplies the installed game's copy of an entry a reference names, once per distinct entry referenced and before any edit
/// applies. The object is only read, and a PTCH target works on a copy of it.</summary>
public delegate GameDataEntryRead ReadGameEntry(EntryName name);

/// <summary>
/// M817: the game-data apply engine, one <c>ltk_game_data::apply</c> call. The edits of ONE module run over a target's bytes in order, and
/// each edit runs its phases: the override files, the object creations, the entry edits, the object removals, and the link edits.
/// Every outcome that is not one of the three fatal ones is a diagnostic, and the edit it is about is skipped.
///
/// <para>A target with an applied override file, an applied object edit, or an applied property edit is written from the decoded
/// tree at PROP version 3; a target with none keeps its object bytes and header version (a link edit is a header edit). A PTCH target
/// takes every edit as its own deletions, objects and records.</para>
///
/// <para><b>Untrusted input.</b> An application is bounded: by <see cref="GameDataLimits"/> (work, output, and the bytes of override files; the defaults are for content
/// that is not hostile), by a cancellation token, and by the depth the codec reads (256 containers), which an edit may not leave the tree
/// deeper than. Exceeding a limit is a <see cref="GameDataException"/> of kind <see cref="GameDataErrorKind.LimitExceeded"/>, cancelling is the
/// token's <see cref="OperationCanceledException"/>, and a tree the codec could not write is a <see cref="GameDataErrorKind.Bin"/>.</para>
/// </summary>
public static class GameDataApplier
{
    private static readonly byte[] PtchMagic = "PTCH"u8.ToArray();

    /// <summary>What a diagnostic is charged: the memory behind it is far more than a node's.</summary>
    internal const int DiagnosticWeight = 8;

    /// <summary>Applies ordered edits to a PROP version 2 or 3, or to a PTCH.</summary>
    /// <param name="baseBytes">The target's bytes.</param>
    /// <param name="edits">The module's edits, in order.</param>
    /// <param name="readOverride">Supplies an override file by its layer-relative path.</param>
    /// <param name="readEntry">Supplies the game's copy of an entry, or none, or an error. The object it returns is shared by the caller and
    /// only read: this engine never changes it, so one decoded object may be handed to applications on several threads.</param>
    /// <param name="schema">Types every property edit and knows every constructed class; <see cref="NoSchema.Instance"/> for none.</param>
    /// <param name="limits">What the application may cost (work, output, and the bytes of override files); <see cref="GameDataLimits.Default"/> when null.</param>
    /// <param name="cancellationToken">Polled about every four thousand units of work and between phases.</param>
    /// <exception cref="GameDataException">The base is neither a PROP version 2 or 3 nor a PTCH, or it does not decode, or its
    /// dependency list cannot be counted, or the output is a tree the codec cannot write (<see cref="GameDataErrorKind.Bin"/>); or a limit was
    /// exceeded (<see cref="GameDataErrorKind.LimitExceeded"/>).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static GameDataApplyResult Apply(
        byte[] baseBytes,
        IReadOnlyList<GameDataEdit> edits,
        ReadOverrideFile readOverride,
        ReadGameEntry readEntry,
        IGameDataSchema schema,
        GameDataLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseBytes);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(readOverride);
        ArgumentNullException.ThrowIfNull(readEntry);
        ArgumentNullException.ThrowIfNull(schema);

        var meter = new WorkMeter(limits, cancellationToken, baseBytes.Length);
        meter.Check();
        try
        {
            return baseBytes.AsSpan().StartsWith(PtchMagic)
                ? PatchTarget.Apply(baseBytes, edits, readOverride, readEntry, schema, meter)
                : ApplyToProp(baseBytes, edits, readOverride, readEntry, schema, meter);
        }
        catch (InsufficientExecutionStackException)
        {
            // a value or a document nested past what the stack holds: the same refusal as nesting past what the codec reads
            throw new GameDataException(GameDataErrorKind.Bin, "Container nesting is too deep for the stack");
        }
    }

    private static GameDataApplyResult ApplyToProp(
        byte[] baseBytes,
        IReadOnlyList<GameDataEdit> edits,
        ReadOverrideFile readOverride,
        ReadGameEntry readEntry,
        IGameDataSchema schema,
        WorkMeter meter)
    {
        // reading a reference costs the caller a chunk read and a decode per entry, so the base is refused first
        PropMount mount;
        PropBin bin;
        try
        {
            mount = PropCodec.Mount(baseBytes);
            if (mount.Version is not (2 or 3)) throw new GameDataException(GameDataErrorKind.UnsupportedBase);
            bin = mount.Read(meter);
        }
        catch (PropDecodeException e)
        {
            throw new GameDataException(GameDataErrorKind.Bin, e.Message);
        }

        meter.Charge(bin.Dependencies.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bin.Dependencies.RemoveAll(path => !seen.Add(AsciiLower(path)));

        var diagnostics = new List<GameDataDiagnostic>();
        var references = ResolveReferences(edits, readEntry, diagnostics, meter);
        var coercer = new Coercer(schema, references, meter);
        var applied = new GameDataApplied();

        // the dependencies by the spelling the engine compares them in, kept as edits add and remove them
        var held = seen;

        for (int index = 0; index < edits.Count; index++)
        {
            meter.Check();
            var edit = edits[index];
            foreach (var path in edit.Overrides)
            {
                var patch = ReadOverrideFileOf(path, readOverride, index, diagnostics, meter);
                if (patch is null) continue;
                var report = OverrideApplier.Apply(patch, bin.Objects, meter);
                applied.Records += report.Applied;
                applied.Objects += report.Added + report.Replaced + report.Deleted;
                meter.Charge(report.Skipped.Count * DiagnosticWeight);
                foreach (var skipped in report.Skipped)
                {
                    diagnostics.Add(new GameDataDiagnostic(
                        GameDataDiagnosticKind.OverrideRecordSkipped, index, path.Text,
                        Record: new SkippedRecord(skipped.Index, skipped.ObjectHash, skipped.Path.Text, skipped.Failure.RecordReason),
                        Detail: skipped.Failure.Message));
                }
            }

            var created = ObjectPhase.Create(bin.Objects, coercer, edit.Objects);
            applied.Objects += created.Objects;
            ReportObjects(diagnostics, index, created.Skipped, meter);
            var entryPhase = new EntryPhase(bin.Objects, coercer, null);
            entryPhase.Run(edit.Entries);
            applied.Properties += ReportProperties(diagnostics, index, created.Sets, meter);
            applied.Properties += ReportProperties(diagnostics, index, entryPhase.Outcome, meter);

            var (skippedRemovals, removed) = ObjectPhase.Remove(bin.Objects, edit.Objects, meter);
            applied.Objects += removed;
            ReportObjects(diagnostics, index, skippedRemovals, meter);

            foreach (var path in edit.Links.Remove)
            {
                int gone = 0;
                meter.Charge(1);
                // a dependency is there to remove only if the set of those held names it: one scan of the list for each that is
                if (held.Remove(AsciiLower(path.Text)))
                {
                    meter.Charge(bin.Dependencies.Count);
                    int count = bin.Dependencies.Count;
                    bin.Dependencies.RemoveAll(value => EqualsAsciiIgnoreCase(value, path.Text));
                    gone = count - bin.Dependencies.Count;
                }
                applied.LinksRemoved += gone;
                if (gone == 0)
                {
                    meter.Charge(DiagnosticWeight);
                    diagnostics.Add(new GameDataDiagnostic(GameDataDiagnosticKind.LinkRemovalUnmatched, index, path.Text));
                }
            }
            foreach (var path in edit.Links.Add)
            {
                meter.Charge(1);
                if (!held.Add(AsciiLower(path.Text))) continue;
                bin.Dependencies.Add(path.Text);
                applied.LinksAdded++;
            }
        }

        // only an edit that reached the object tree costs the re-encode, which writes PROP v3 and so changes the version of a v2 base;
        // a link edit is a header edit, and an override file whose every record skipped reached nothing
        meter.Check();
        byte[] bytes = applied.TreeChanged
            ? PropCodec.WriteProp(bin, meter)
            : HeaderRewrite(baseBytes, mount.BodyOffset, bin.Dependencies, meter);
        return new GameDataApplyResult(bytes, bin.Dependencies, applied, diagnostics, meter.Work);
    }

    /// <summary>The game's copy of every entry the edits reference, read once before the first edit applies, by object hash. An entry the
    /// caller says it lacks is absent from the map; one it could not read is absent too, and reported against the first edit naming it.</summary>
    internal static Dictionary<uint, PropObject> ResolveReferences(IReadOnlyList<GameDataEdit> edits, ReadGameEntry readEntry, List<GameDataDiagnostic> diagnostics, WorkMeter meter)
    {
        var resolved = KeyHash.Map<PropObject>();
        var asked = KeyHash.Set();
        for (int index = 0; index < edits.Count; index++)
        {
            meter.Check();
            foreach (var reference in edits[index].References())
            {
                meter.Charge(1);
                if (!asked.Add(reference.Entry.ObjectHash)) continue;
                var read = readEntry(reference.Entry);
                meter.Check();
                if (read.Error is not null)
                {
                    meter.Charge(DiagnosticWeight);
                    diagnostics.Add(new GameDataDiagnostic(GameDataDiagnosticKind.ReferenceUnreadable, index, reference.ToString(), Detail: read.Error));
                }
                else if (read.Object is not null) resolved[reference.Entry.ObjectHash] = read.Object;
            }
        }
        return resolved;
    }

    /// <summary>The override file at <paramref name="path"/> of edit <paramref name="index"/>, decoded, or null with its diagnostic.</summary>
    internal static PtchBin? ReadOverrideFileOf(OverridePath path, ReadOverrideFile readOverride, int index, List<GameDataDiagnostic> diagnostics, WorkMeter meter)
    {
        meter.Check();
        var read = readOverride(path);
        meter.Check();
        if (read.Bytes is null)
        {
            meter.Charge(DiagnosticWeight);
            diagnostics.Add(new GameDataDiagnostic(GameDataDiagnosticKind.OverrideUnreadable, index, path.Text, Detail: read.Error ?? ""));
            return null;
        }
        // the bytes of a package, decoded: counted against their own limit before anything is read, and what the file holds goes into the
        // tree, so it counts toward what the tree may grow to
        meter.Override(read.Bytes.Length);
        meter.Grow(read.Bytes.Length);
        try
        {
            return PropCodec.ReadPtch(read.Bytes, meter);
        }
        catch (PropDecodeException e)
        {
            meter.Charge(DiagnosticWeight);
            diagnostics.Add(new GameDataDiagnostic(GameDataDiagnosticKind.OverrideInvalid, index, path.Text, Detail: e.Message));
            return null;
        }
    }

    /// <summary>Reports the entry-edit outcome of edit <paramref name="index"/>, and answers how many property keys it set.</summary>
    internal static int ReportProperties(List<GameDataDiagnostic> diagnostics, int index, PhaseOutcome outcome, WorkMeter meter)
    {
        foreach (var report in outcome.Reports)
            diagnostics.Add(new GameDataDiagnostic(report.Kind, index, report.Path, Property: report.Property, Detail: report.Detail));
        meter.Charge(outcome.Reports.Count);
        return outcome.Properties;
    }

    /// <summary>Reports each skipped object of edit <paramref name="index"/> as an <c>ObjectSkipped</c> diagnostic.</summary>
    internal static void ReportObjects(List<GameDataDiagnostic> diagnostics, int index, List<SkippedObject> skipped, WorkMeter meter)
    {
        meter.Charge(skipped.Count * DiagnosticWeight);
        foreach (var obj in skipped)
            diagnostics.Add(new GameDataDiagnostic(GameDataDiagnosticKind.ObjectSkipped, index, obj.Name.Text, Object: obj));
    }

    /// <summary>The base with its dependency header replaced and its object table copied byte for byte.</summary>
    private static byte[] HeaderRewrite(byte[] baseBytes, int bodyOffset, List<string> dependencies, WorkMeter meter)
    {
        var encoded = new byte[dependencies.Count][];
        long length = 12 + (baseBytes.Length - bodyOffset);
        for (int i = 0; i < encoded.Length; i++)
        {
            encoded[i] = Encoding.UTF8.GetBytes(dependencies[i]);
            if (encoded[i].Length > ushort.MaxValue) throw new GameDataException(GameDataErrorKind.DependencyOverflow, null, "links");
            length += 2 + encoded[i].Length;
        }
        if (length > meter.MaxOutput) throw meter.WriteExceeded();

        var bytes = new byte[length];
        baseBytes.AsSpan(0, 8).CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)dependencies.Count);
        int at = 12;
        foreach (byte[] dependency in encoded)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), (ushort)dependency.Length);
            dependency.CopyTo(bytes, at + 2);
            at += 2 + dependency.Length;
        }
        baseBytes.AsSpan(bodyOffset).CopyTo(bytes.AsSpan(at));
        return bytes;
    }

    internal static string AsciiLower(string text)
    {
        foreach (char c in text)
        {
            if (c is < 'A' or > 'Z') continue;
            var chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] is >= 'A' and <= 'Z') chars[i] = (char)(chars[i] + 32);
            return new string(chars);
        }
        return text;
    }

    /// <summary><c>eq_ignore_ascii_case</c>: only A to Z fold.</summary>
    internal static bool EqualsAsciiIgnoreCase(string a, string b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            char x = a[i], y = b[i];
            if (x == y) continue;
            if (x is >= 'A' and <= 'Z') x = (char)(x + 32);
            if (y is >= 'A' and <= 'Z') y = (char)(y + 32);
            if (x != y) return false;
        }
        return true;
    }
}

/// <summary>M817: laying a PTCH over the PROP it patches, in the order the client does (<c>BinOverride::apply</c>).</summary>
internal static class OverrideApplier
{
    internal sealed record SkippedPatch(int Index, uint ObjectHash, PropertyPath Path, PatchFailure Failure);

    internal sealed class Report
    {
        public int Deleted { get; set; }
        public int Added { get; set; }
        public int Replaced { get; set; }
        public int Applied { get; set; }
        public int Inserted { get; set; }
        public List<SkippedPatch> Skipped { get; } = new();
    }

    /// <summary>Every hash the patch deletes is dropped; its own objects go in, except any it deletes, one whose hash the base has
    /// replacing it; the records apply in file order against the merged table. A record that does not apply is reported and the rest carry on.</summary>
    public static Report Apply(PtchBin patch, OrderedMap<PropObject> objects, WorkMeter? meter = null)
    {
        var report = new Report();
        foreach (uint hash in patch.Deleted)
        {
            meter?.Charge(1);
            if (!objects.Remove(hash, out int shifted)) continue;
            report.Deleted++;
            meter?.Charge(shifted >> 5);
        }

        var deleted = KeyHash.Set(patch.Deleted);
        for (int i = 0; i < patch.Objects.Count; i++)
        {
            meter?.Charge(1);
            uint hash = patch.Objects.KeyAt(i);
            if (deleted.Contains(hash)) continue;
            if (objects.Set(hash, patch.Objects.ValueAt(i))) report.Replaced++;
            else report.Added++;
        }

        for (int index = 0; index < patch.Patches.Count; index++)
        {
            var record = patch.Patches[index];
            PatchFailure? failure;
            bool inserted = false;
            if (objects.TryGetValue(record.ObjectHash, out var obj)) failure = PropWalk.PatchClient(obj, record.Path, record.Value, out inserted, meter);
            else failure = PatchFailure.Unresolved(new ResolveError(0, ResolveErrorKind.MissingObject, record.ObjectHash));

            if (failure is null)
            {
                report.Applied++;
                if (inserted) report.Inserted++;
            }
            else report.Skipped.Add(new SkippedPatch(index, record.ObjectHash, record.Path, failure));
        }
        return report;
    }
}
