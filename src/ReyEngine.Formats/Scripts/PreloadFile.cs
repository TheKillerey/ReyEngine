using System.Text;

namespace ReyEngine.Formats.Scripts;

/// <summary>The kind of a <c>.preload</c> building block.</summary>
public enum PreloadKind { Unknown, Spell, Character, Particle, Module, AssetsLua }

/// <summary>One <c>{Function=BBPreloadX, Params={Name="x", SkinID="-1"}}</c> entry. <see cref="Params"/> keeps every key of the entry, in file order.</summary>
public sealed record PreloadEntry(PreloadKind Kind, string Function, string Name, string? SkinId, IReadOnlyList<KeyValuePair<string, string>> Params);

/// <summary>
/// M891: a <c>.preload</c> file - ASCII Lua table text, <c>PreLoadBuildingBlocks = { {Function=BBPreloadSpell, Params={Name="x"[, SkinID="-1"]}}, ... }</c>. It names the spells,
/// characters, particles and modules a script needs loaded, so it is an authored list of what an ability brings with it (Kalista W: <c>kalistaspawn</c>).
/// Unknown functions are kept as <see cref="PreloadKind.Unknown"/> with their text; a malformed tail is a note and the entries read before it are kept.
/// </summary>
public sealed class PreloadFile
{
    private const int MaxEntries = 100_000;

    public IReadOnlyList<PreloadEntry> Entries { get; init; } = Array.Empty<PreloadEntry>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public IEnumerable<string> Names(PreloadKind kind) => Entries.Where(e => e.Kind == kind).Select(e => e.Name);

    public static PreloadFile Parse(ReadOnlySpan<byte> data) => Parse(Encoding.Latin1.GetString(data));

    public static PreloadFile Parse(string text)
    {
        var entries = new List<PreloadEntry>();
        var notes = new List<string>();
        int i = text.IndexOf("PreLoadBuildingBlocks", StringComparison.Ordinal);
        if (i < 0) { notes.Add("no PreLoadBuildingBlocks table"); return new PreloadFile { Notes = notes }; }
        i = text.IndexOf('{', i);
        if (i < 0) { notes.Add("PreLoadBuildingBlocks has no table"); return new PreloadFile { Notes = notes }; }
        i++;
        while (true)
        {
            SkipSpace(text, ref i);
            if (i >= text.Length) { notes.Add("table is not closed"); break; }
            if (text[i] == '}') break;
            if (text[i] == ',') { i++; continue; }
            if (text[i] != '{') { notes.Add($"unexpected '{text[i]}' at {i}"); break; }
            if (entries.Count >= MaxEntries) { notes.Add("entry limit reached"); break; }
            int start = i;
            if (!TryReadEntry(text, ref i, out var entry)) { notes.Add($"malformed entry at {start}"); break; }
            entries.Add(entry!);
        }
        return new PreloadFile { Entries = entries, Notes = notes };
    }

    private static bool TryReadEntry(string t, ref int i, out PreloadEntry? entry)
    {
        entry = null;
        i++; // {
        string? function = null;
        var pars = new List<KeyValuePair<string, string>>();
        while (true)
        {
            SkipSpace(t, ref i);
            if (i >= t.Length) return false;
            if (t[i] == '}') { i++; break; }
            if (t[i] == ',') { i++; continue; }
            string key = ReadIdent(t, ref i);
            if (key.Length == 0) return false;
            SkipSpace(t, ref i);
            if (i >= t.Length || t[i] != '=') return false;
            i++; SkipSpace(t, ref i);
            if (i >= t.Length) return false;
            if (key.Equals("Params", StringComparison.OrdinalIgnoreCase))
            {
                if (t[i] != '{') return false;
                i++;
                while (true)
                {
                    SkipSpace(t, ref i);
                    if (i >= t.Length) return false;
                    if (t[i] == '}') { i++; break; }
                    if (t[i] == ',') { i++; continue; }
                    string pk = ReadIdent(t, ref i);
                    if (pk.Length == 0) return false;
                    SkipSpace(t, ref i);
                    if (i >= t.Length || t[i] != '=') return false;
                    i++; SkipSpace(t, ref i);
                    if (!TryReadValue(t, ref i, out var pv)) return false;
                    pars.Add(new(pk, pv));
                }
            }
            else
            {
                if (!TryReadValue(t, ref i, out var v)) return false;
                if (key.Equals("Function", StringComparison.OrdinalIgnoreCase)) function = v;
            }
        }
        if (function is null) return false;
        string name = pars.FirstOrDefault(p => p.Key.Equals("Name", StringComparison.OrdinalIgnoreCase)).Value ?? "";
        string? skin = pars.Cast<KeyValuePair<string, string>?>().FirstOrDefault(p => p!.Value.Key.Equals("SkinID", StringComparison.OrdinalIgnoreCase))?.Value;
        entry = new PreloadEntry(KindOf(function), function, name, skin, pars);
        return true;
    }

    private static PreloadKind KindOf(string f) => f switch
    {
        "BBPreloadSpell" => PreloadKind.Spell,
        "BBPreloadCharacter" => PreloadKind.Character,
        "BBPreloadParticle" => PreloadKind.Particle,
        "BBPreloadModule" => PreloadKind.Module,
        "BBPreloadAssetsLua" => PreloadKind.AssetsLua,
        _ => PreloadKind.Unknown,
    };

    /// <summary>Skips white space and Lua line comments (<c>--{Function=...}</c> is a commented-out entry).</summary>
    private static void SkipSpace(string t, ref int i)
    {
        while (i < t.Length)
        {
            if (char.IsWhiteSpace(t[i])) i++;
            else if (t[i] == '-' && i + 1 < t.Length && t[i + 1] == '-') { while (i < t.Length && t[i] != (char)10) i++; }
            else break;
        }
    }

    private static string ReadIdent(string t, ref int i)
    {
        int s = i;
        while (i < t.Length && (char.IsLetterOrDigit(t[i]) || t[i] == '_')) i++;
        return t[s..i];
    }

    /// <summary>A quoted string (no escapes in the corpus; a backslash escapes the next character) or a bare word.</summary>
    private static bool TryReadValue(string t, ref int i, out string value)
    {
        value = "";
        if (i >= t.Length) return false;
        if (t[i] == '"')
        {
            var sb = new StringBuilder();
            i++;
            while (i < t.Length && t[i] != '"')
            {
                if (t[i] == (char)92 && i + 1 < t.Length) i++;
                sb.Append(t[i++]);
            }
            if (i >= t.Length) return false;
            i++;
            value = sb.ToString();
            return true;
        }
        string w = ReadIdent(t, ref i);
        value = w;
        return w.Length > 0;
    }
}
