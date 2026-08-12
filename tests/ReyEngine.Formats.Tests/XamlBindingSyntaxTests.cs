using System.Text.RegularExpressions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M437: a lint for ancestor-lookup syntax in .axaml bindings.
///
/// <para>Why it exists: a malformed binding is NOT a compile error. XAML compilation succeeded, the
/// solution built, 710 tests passed, and the app still died with
/// <c>ExpressionParseException: Expected end of expression</c> the moment the offending DataTemplate was
/// instantiated — which only happened once a map was open and a feature had fields, so nothing short of
/// running the app with real data would have caught it.</para>
///
/// <para>The mistake was chaining ancestor lookups: <c>$parent[X].$parent[Y]</c>. Avalonia spells the
/// Nth ancestor <c>$parent[Type;index]</c>; a chain parses as far as the first <c>$parent[X]</c> and then
/// rejects the remainder.</para>
///
/// <para><b>Scope, stated honestly:</b> this checks ancestor-lookup shape only. It is not a binding
/// parser — Avalonia's grammar entry point takes a <c>ref CharacterReader</c> (a ref struct) and cannot
/// be driven by reflection, and a hand-rolled parser would be a second implementation to get wrong.
/// A binding can still be malformed in ways this does not see.</para>
/// </summary>
public class XamlBindingSyntaxTests
{
    /// <summary>Walk up from THIS SOURCE FILE, not from the output directory. Build output can be
    /// redirected (e.g. -p:BaseOutputPath while the app holds a lock on bin/), which moves
    /// AppContext.BaseDirectory outside the repo and silently broke this scan.</summary>
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found from " + here);
    }

    private static List<string> AxamlFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.axaml", SearchOption.AllDirectories).ToList();

    [Fact]
    public void The_scan_actually_reaches_the_views()
    {
        var files = AxamlFiles();
        Assert.True(files.Count > 5, $"only {files.Count} .axaml found — the lint is not looking at the views");
        Assert.Contains(files, f => Path.GetFileName(f) == "SceneObjectInspectorView.axaml");
        Assert.Contains(AxamlFiles().Select(File.ReadAllText), t => t.Contains("$parent"));
    }

    /// <summary>The exact shape that shipped broken and crashed the app on opening a map.</summary>
    [Fact]
    public void Ancestor_lookups_are_never_chained()
    {
        var rx = new Regex(@"\$parent\s*\[[^\]]*\]\s*\.\s*\$parent", RegexOptions.Compiled);
        var offenders = AxamlFiles()
            .Where(f => rx.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "$parent cannot be chained — use $parent[Type;index] for the Nth ancestor, or bind through a "
            + "single unambiguous ancestor such as $parent[UserControl]. In: " + string.Join(", ", offenders));
    }

    /// <summary>Every ancestor lookup must be one of the forms Avalonia accepts: <c>$parent</c>,
    /// <c>$parent[N]</c>, <c>$parent[Type]</c> or <c>$parent[Type;N]</c>.</summary>
    [Fact]
    public void Ancestor_lookups_use_a_form_avalonia_accepts()
    {
        var usage = new Regex(@"\$parent(\s*\[(?<arg>[^\]]*)\])?", RegexOptions.Compiled);
        var valid = new Regex(@"^\s*([A-Za-z_][\w.:]*)?\s*(;\s*\d+\s*)?$|^\s*\d+\s*$", RegexOptions.Compiled);

        var failures = new List<string>();
        foreach (var file in AxamlFiles())
            foreach (Match m in usage.Matches(File.ReadAllText(file)))
            {
                if (!m.Groups[1].Success) continue;                 // bare $parent is fine
                string arg = m.Groups["arg"].Value;
                if (!valid.IsMatch(arg))
                    failures.Add($"{Path.GetFileName(file)}: $parent[{arg}]");
            }

        Assert.True(failures.Count == 0,
            "ancestor lookup(s) Avalonia will not parse:\n  " + string.Join("\n  ", failures));
    }
}
