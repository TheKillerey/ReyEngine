using System;
using System.Collections.Generic;
using System.Linq;
using ReyEngine.Core.Settings;

namespace ReyEngine.App.ViewModels;

/// <summary>M689: one row of the What's New list - either a release header or a feature under it.</summary>
/// <param name="IsHeader">A release header row ("v0.4.4") rather than a feature.</param>
/// <param name="Version">The release, without the "v".</param>
/// <param name="Label">The feature text (empty on a header).</param>
/// <param name="IsNew">Still unacknowledged by this install - the rows that glow in the menus.</param>
/// <param name="IsCurrent">The release this build is; its header says so.</param>
public sealed record WhatsNewRow(bool IsHeader, string Version, string Label, bool IsNew, bool IsCurrent)
{
    public string Title => "v" + Version + (IsCurrent ? "  ·  this release" : "");
}

/// <summary>M689: the What's New list, built from the same registry the glows read. Releases newest
/// first, each followed by its features in registry order; a feature is new for the same reason its
/// control glows, so the list and the menus can never disagree.</summary>
public static class WhatsNewRows
{
    public static IReadOnlyList<WhatsNewRow> Build(IReadOnlyList<NewFeature> features, string lastSeenVersion)
    {
        var rows = new List<WhatsNewRow>();
        var versions = features.Select(f => f.Version).Distinct().ToList();
        versions.Sort((a, b) => NewFeatures.Compare(b, a));   // newest first
        foreach (string version in versions)
        {
            bool current = string.Equals(version, NewFeatures.CurrentVersion, StringComparison.Ordinal);
            rows.Add(new WhatsNewRow(true, version, "", false, current));
            foreach (var f in features.Where(f => f.Version == version))
                rows.Add(new WhatsNewRow(false, version, f.Label, NewFeatures.IsNew(f.Id, lastSeenVersion), current));
        }
        return rows;
    }
}
