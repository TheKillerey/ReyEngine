using System.Runtime.CompilerServices;

namespace ReyEngine.Formats.LtkGameData;

/// <summary>
/// M817: what one application may cost before the engine refuses it: the guard for a package that is not trusted. A limit that is
/// exceeded ends the application with a <see cref="GameDataException"/> of kind <see cref="GameDataErrorKind.LimitExceeded"/>, which is
/// never a <see cref="GameDataErrorKind.Bin"/>: the bytes were not wrong, the work they ask for was too much.
///
/// <para><b>Work</b> is counted in units, about one for each value, property, entry, element or path segment the engine decodes, writes,
/// clones, coerces or walks, one for each element a removal or an insertion scans, and eight for each diagnostic it keeps. It is a count of what
/// the engine does and so a bound on time: the largest bin of the installed game, Map22.wad.client (33 MB, 18,954 objects, 4.2 million values), costs
/// 6.3 million units to decode and write again, which is about half a second. The default is 2^28 units, more than forty times that, and a few tens of
/// seconds of one core at the dearest a unit is.</para>
///
/// <para><b>Output</b> is counted in bytes: the target as it was, plus every value an edit inserts or an object it clones (so a tree that
/// is too large is refused before it is built), and then the bytes written. The default is 256 MiB, a little over eight times the largest bin
/// of the installed game. It is a limit on what is written and not on the memory the tree holds, which is more: measured, a decoded tree
/// takes about eight times its file's size (Map22: 32 MiB of file, 267 MiB of tree), and up to forty times for a file that is nothing but
/// single-byte values.</para>
///
/// <para><b>Override files</b> are the bytes of a package, where everything else the engine decodes is the game's own, and a file of
/// single-byte values is the densest thing a hostile package can hand it, so they have a limit of their own: the bytes of every override
/// file one application decodes, and of a PTCH target, which is the same format. The default is 32 MiB, as much as the largest bin of the
/// installed game and five hundred times the largest PTCH the game ships (59 KB), and it holds the tree built from such files to about a
/// gigabyte (forty times 32 MiB).</para>
///
/// <para>The defaults are for content that is not hostile: a mod that reaches them asks for more than the game itself ships. A caller that
/// wants a tighter bound passes its own; <see cref="Unlimited"/> turns the counting off (cancellation still works). <see cref="GameDataApplyResult.WorkUsed"/>
/// says what an application cost. What a caller hands the engine is the caller's to size first: a file read from a package is as large in
/// memory as the caller read it, whatever these limits then refuse.</para>
/// </summary>
public sealed record GameDataLimits
{
    /// <summary>2^28 units: more than forty times the work of decoding and writing the largest bin of the installed game.</summary>
    public const long DefaultMaxWork = 1L << 28;

    /// <summary>256 MiB: a little over eight times the largest bin of the installed game.</summary>
    public const long DefaultMaxOutputBytes = 256L << 20;

    /// <summary>The units of work one application may use.</summary>
    public long MaxWork { get; init; } = DefaultMaxWork;

    /// <summary>32 MiB: as much as the largest bin of the installed game, and five hundred times its largest PTCH.</summary>
    public const long DefaultMaxOverrideBytes = 32L << 20;

    /// <summary>The bytes one application may hold or write: the target, what the edits insert, and what is written.</summary>
    public long MaxOutputBytes { get; init; } = DefaultMaxOutputBytes;

    /// <summary>The bytes of override files (and of a PTCH target) one application may decode, all of them together.</summary>
    public long MaxOverrideBytes { get; init; } = DefaultMaxOverrideBytes;

    public static GameDataLimits Default { get; } = new();

    public static GameDataLimits Unlimited { get; } = new() { MaxWork = long.MaxValue, MaxOutputBytes = long.MaxValue, MaxOverrideBytes = long.MaxValue };
}

/// <summary>
/// The meter of one application: the work done, the output projected, and the cancellation token, checked together and cheaply. A charge
/// is an addition and a comparison; the limit and the token are looked at once per <see cref="PollEvery"/> units and at the boundaries of
/// the phases.
/// </summary>
internal sealed class WorkMeter
{
    private const long PollEvery = 1 << 12;

    private readonly long _maxWork;
    private readonly long _maxOutput;
    private readonly long _maxOverride;
    private readonly CancellationToken _token;
    private long _work;
    private long _nextPoll;
    private long _projected;
    private long _override;

    public WorkMeter(GameDataLimits? limits, CancellationToken token, long baseLength)
    {
        limits ??= GameDataLimits.Default;
        _maxWork = limits.MaxWork;
        _maxOutput = limits.MaxOutputBytes;
        _maxOverride = limits.MaxOverrideBytes;
        _token = token;
        _nextPoll = Math.Min(PollEvery, Next(_maxWork));
        _projected = baseLength;
        if (_projected > _maxOutput) throw OutputExceeded();
    }

    /// <summary>The units counted so far.</summary>
    public long Work => _work;

    /// <summary>The output bytes the tree is projected to hold: the target and everything inserted.</summary>
    public long Projected => _projected;

    public long MaxOutput => _maxOutput;

    private static long Next(long max) => max == long.MaxValue ? long.MaxValue : max + 1;

    /// <summary>Counts <paramref name="units"/> of work.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Charge(long units)
    {
        long work = _work + units;
        _work = work;
        if (work >= _nextPoll) Poll(work);
    }

    private void Poll(long work)
    {
        if (work > _maxWork) throw new GameDataException(GameDataErrorKind.LimitExceeded, $"work limit of {_maxWork} units exceeded");
        _token.ThrowIfCancellationRequested();
        _nextPoll = Math.Min(work + PollEvery, Next(_maxWork));
    }

    /// <summary>The boundary of a phase: the limit and the token, whatever has been charged since they were last looked at.</summary>
    public void Check()
    {
        if (_work > _maxWork) throw new GameDataException(GameDataErrorKind.LimitExceeded, $"work limit of {_maxWork} units exceeded");
        _token.ThrowIfCancellationRequested();
    }

    /// <summary>Counts <paramref name="bytes"/> the tree has been given: a value inserted into it.</summary>
    public void Grow(long bytes)
    {
        _projected += bytes;
        if (_projected > _maxOutput) throw OutputExceeded();
    }

    /// <summary>Counts the bytes of an override file the engine is about to decode, before it is decoded.</summary>
    public void Override(long bytes)
    {
        _override += bytes;
        if (_override > _maxOverride) throw new GameDataException(GameDataErrorKind.LimitExceeded, $"override limit of {_maxOverride} bytes exceeded");
    }

    private GameDataException OutputExceeded() => new(GameDataErrorKind.LimitExceeded, $"output limit of {_maxOutput} bytes exceeded");

    /// <summary>The output limit as the writer enforces it.</summary>
    public GameDataException WriteExceeded() => OutputExceeded();
}
