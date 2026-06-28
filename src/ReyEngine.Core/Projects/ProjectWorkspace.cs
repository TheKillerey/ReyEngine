namespace ReyEngine.Core.Projects;

/// <summary>Manages the project's on-disk workspace: override store + build output folders.</summary>
public static class ProjectWorkspace
{
    public static string OverridesDir(ReyProject project)
    {
        var dir = project.OverridesDirectory
            ?? throw new InvalidOperationException("Save the project before importing assets.");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string BuildDir(ReyProject project)
    {
        var root = project.OutputDirectory
            ?? (project.WorkspaceDirectory is { } w ? Path.Combine(w, "build") : null)
            ?? throw new InvalidOperationException("No output directory.");
        var dir = Path.Combine(root, project.Name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Copy a chosen file into the workspace as an override; returns the stored path.</summary>
    public static string StoreOverride(ReyProject project, ulong pathHash, string sourceFile)
    {
        var ext = Path.GetExtension(sourceFile);
        var dest = Path.Combine(OverridesDir(project), $"{pathHash:x16}{ext}");
        File.Copy(sourceFile, dest, overwrite: true);
        return dest;
    }
}
