using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReyEngine.Core.Decoding;

namespace ReyEngine.App.Services;

/// <summary>One texture a recolour can act on.</summary>
public sealed record RecolorTarget(ulong PathHash, string AssetPath);

public sealed record RecolorProgress(int Done, int Total, string Current);

public sealed record RecolorRunResult(
    int Written, int Skipped, int Failed, int MissingSources, long BytesWritten,
    IReadOnlyList<string> Notes, IReadOnlyList<RecolorTarget> WrittenTargets);

/// <summary>M171: applies one <see cref="TextureAdjustment"/> across a set of textures and writes each
/// result where the host says it belongs.
///
/// The host supplies the BASE bytes for each target, and it must supply the PRISTINE original — not
/// whatever the project currently holds. Every apply is a fresh decode/encode of the original, so a user
/// can drag the hue slider a hundred times and the shipped texture still carries exactly one generation
/// of BC loss. Feeding our own output back in would compound it: measured over ten successive edits,
/// destructive chaining lands at 28.6 dB against 40.0 dB for re-derivation.</summary>
public sealed class TextureRecolorService
{
    private readonly Func<RecolorTarget, byte[]?> _readBase;
    private readonly Func<string, byte[], string, string> _writeAsset;

    public TextureRecolorService(
        Func<RecolorTarget, byte[]?> readBase,
        Func<string, byte[], string, string> writeAsset)
    {
        _readBase = readBase;
        _writeAsset = writeAsset;
    }

    /// <summary>How many textures are decoded/encoded at once. The BC work is the whole cost of a run
    /// (measured: 451 ms for an average Map11 texture, against a few ms to read one), and it is perfectly
    /// parallel — but the bytes are big, so this batches rather than loading the map's whole texture set.
    /// A batch of 8 at 2048^2 peaks around 100 MB while keeping every core busy.</summary>
    private const int BatchSize = 8;

    /// <summary>Recolour every target. Never throws for a bad file — anything out of scope is counted and
    /// explained in <see cref="RecolorRunResult.Notes"/>, because a map has hundreds of textures and one
    /// odd blob must not abort the run.
    ///
    /// Reads and writes are SERIAL, only the codec work runs in parallel: the read side shares a WAD
    /// stream and the write side touches the project's folder/override lists, neither of which is safe to
    /// hit from several threads.</summary>
    public Task<RecolorRunResult> RunAsync(
        IReadOnlyList<RecolorTarget> targets, TextureAdjustment adjustment,
        IProgress<RecolorProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() =>
        {
            int written = 0, skipped = 0, failed = 0, missingSources = 0;
            long bytes = 0;
            var notes = new List<string>();
            var writtenTargets = new List<RecolorTarget>();

            for (int start = 0; start < targets.Count; start += BatchSize)
            {
                ct.ThrowIfCancellationRequested();
                int count = Math.Min(BatchSize, targets.Count - start);
                progress?.Report(new RecolorProgress(start, targets.Count, targets[start].AssetPath));

                // 1. read the batch's pristine sources (serial — shared WAD stream)
                var sources = new byte[count][];
                for (int i = 0; i < count; i++)
                {
                    var t = targets[start + i];
                    try
                    {
                        sources[i] = _readBase(t)!;
                        if (sources[i] is null)
                        {
                            missingSources++;
                            Note(notes, $"{t.AssetPath}: original source was not found in the configured game files");
                        }
                    }
                    catch (Exception ex)
                    {
                        missingSources++;
                        Note(notes, $"{t.AssetPath}: could not read original source ({ex.Message})");
                    }
                }

                // 2. decode / adjust / encode (parallel — this is the whole cost)
                var outcomes = new RecolorOutcome?[count];
                Parallel.For(0, count, new ParallelOptions { CancellationToken = ct }, i =>
                {
                    if (sources[i] is { } src) outcomes[i] = TextureRecolor.Apply(src, adjustment);
                });

                // 3. write (serial — mutates project state)
                for (int i = 0; i < count; i++)
                {
                    var t = targets[start + i];
                    if (outcomes[i] is not { } outcome) { failed++; continue; }
                    if (!outcome.Ok)
                    {
                        // NoChange is not worth a note — it just means the sliders are neutral.
                        if (outcome.Skip is RecolorSkip.DecodeFailed or RecolorSkip.EncodeFailed) failed++;
                        else skipped++;
                        if (outcome.Skip is not RecolorSkip.NoChange)
                            Note(notes, $"{t.AssetPath}: {Describe(outcome.Skip)} ({outcome.Detail})");
                        continue;
                    }
                    try
                    {
                        _writeAsset(t.AssetPath, outcome.Bytes!, ".tex");
                        written++;
                        bytes += outcome.Bytes!.Length;
                        writtenTargets.Add(t);
                    }
                    catch (Exception ex) { failed++; Note(notes, $"{t.AssetPath}: write failed ({ex.Message})"); }
                }
            }

            progress?.Report(new RecolorProgress(targets.Count, targets.Count, ""));
            return new RecolorRunResult(written, skipped, failed, missingSources, bytes, notes, writtenTargets);
        }, ct);

    /// <summary>Keep the note list bounded — a whole-map run over a broken folder could otherwise produce
    /// thousands of identical lines and the tail is never the interesting part.</summary>
    private static void Note(List<string> notes, string line)
    {
        const int Max = 40;
        if (notes.Count < Max) notes.Add(line);
        else if (notes.Count == Max) notes.Add("… further messages suppressed.");
    }

    private static string Describe(RecolorSkip skip) => skip switch
    {
        RecolorSkip.NotATexture => "not a .tex file, left alone",
        RecolorSkip.UnsupportedFormat => "pixel format we cannot write back, left alone",
        RecolorSkip.DecodeFailed => "could not be decoded",
        RecolorSkip.EncodeFailed => "could not be re-encoded",
        _ => skip.ToString(),
    };
}
