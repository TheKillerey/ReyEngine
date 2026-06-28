using System.Text.Json;

namespace ReyEngine.Core.Projects;

/// <summary>Tracks recently-opened project folders in the user's app-data directory.</summary>
public static class RecentProjects
{
    private const int Max = 12;

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReyEngine", "recent.json");

    public static List<string> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePath)) ?? new();
        }
        catch { /* ignore corrupt/missing */ }
        return new();
    }

    public static List<string> Add(string folder)
    {
        var list = Load();
        list.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, folder);
        if (list.Count > Max) list.RemoveRange(Max, list.Count - Max);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(list));
        }
        catch { /* best-effort */ }
        return list;
    }
}
