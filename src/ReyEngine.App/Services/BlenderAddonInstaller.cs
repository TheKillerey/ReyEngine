using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReyEngine.App.Services;

/// <summary>
/// M682: installs the Blender add-on (tools/blender/reyengine_bridge.py, shipped beside the app under
/// blender/) into every Blender the user has, from inside the tool - instead of Edit ▸ Preferences ▸
/// Add-ons ▸ Install… by hand for each version.
///
/// <para>A Blender install is found by its per-user config folder, %AppData%\Blender Foundation\Blender\
/// &lt;major.minor&gt;, which every version creates on first start; the add-on goes into that version's
/// scripts\addons, where a single-file legacy add-on is loaded by every Blender from 2.8 to 5.x (4.2's
/// extension system kept that path for "legacy add-ons"). Enabling it needs Blender itself: when the
/// matching blender.exe is found under Program Files, it is run in the background with a one-line script
/// that enables the module and saves the preferences - otherwise the file is in place and one tick in
/// Blender's add-on list finishes the job, which the status says.</para>
/// </summary>
public static class BlenderAddonInstaller
{
    public const string ModuleName = "reyengine_bridge";
    public const string FileName = ModuleName + ".py";

    /// <summary>One Blender the user has, by its config folder. The executable is optional - a Blender
    /// unpacked from a zip or run from Steam has no Program Files folder to find.</summary>
    public sealed record BlenderInstall(string Version, string ConfigDir, string? Executable)
    {
        public string AddonsDir => Path.Combine(ConfigDir, "scripts", "addons");
        public string AddonFile => Path.Combine(AddonsDir, FileName);
        public bool AddonInstalled => File.Exists(AddonFile);

        /// <summary>Installed AND byte-identical to the add-on this build ships.</summary>
        public bool AddonCurrent(string? shippedFile)
        {
            if (!AddonInstalled || shippedFile is null || !File.Exists(shippedFile)) return false;
            try { return File.ReadAllBytes(AddonFile).AsSpan().SequenceEqual(File.ReadAllBytes(shippedFile)); }
            catch { return false; }
        }
    }

    public sealed record InstallResult(bool Copied, bool Enabled, string Message);

    public static string DefaultConfigRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Blender Foundation", "Blender");

    public static string DefaultProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    /// <summary>The add-on this build ships: blender\reyengine_bridge.py beside the exe, or, when running
    /// from a source checkout, the repository's tools\blender copy found by walking up. Null when neither
    /// exists - a broken build rather than a missing Blender.</summary>
    public static string? ShippedAddonPath(string? baseDirectory = null)
    {
        string root = baseDirectory ?? AppContext.BaseDirectory;
        string beside = Path.Combine(root, "blender", FileName);
        if (File.Exists(beside)) return beside;
        for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tools", "blender", FileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static readonly Regex VersionFolder = new(@"^\d+\.\d+$", RegexOptions.Compiled);

    /// <summary>Every Blender version with a config folder, newest first.</summary>
    public static IReadOnlyList<BlenderInstall> Discover(string? configRoot = null, string? programFiles = null)
    {
        configRoot ??= DefaultConfigRoot;
        programFiles ??= DefaultProgramFiles;
        if (!Directory.Exists(configRoot)) return Array.Empty<BlenderInstall>();

        var found = new List<BlenderInstall>();
        foreach (string dir in Directory.GetDirectories(configRoot))
        {
            string version = Path.GetFileName(dir);
            if (!VersionFolder.IsMatch(version)) continue;
            string exe = Path.Combine(programFiles, "Blender Foundation", "Blender " + version, "blender.exe");
            found.Add(new BlenderInstall(version, dir, File.Exists(exe) ? exe : null));
        }
        return found
            .OrderByDescending(b => Version.TryParse(b.Version, out var v) ? v : new Version(0, 0))
            .ToList();
    }

    /// <summary>The line Blender runs, in the background, to switch the add-on on and remember it.</summary>
    public static string EnableExpression =>
        $"import bpy; bpy.ops.preferences.addon_enable(module='{ModuleName}'); bpy.ops.wm.save_userpref()";

    /// <summary>Copy the add-on into the install's scripts\addons and, when its blender.exe is known and
    /// <paramref name="enable"/> is set, enable it there. Never throws: the message says what happened.</summary>
    public static async Task<InstallResult> InstallAsync(BlenderInstall target, string shippedFile, bool enable,
        CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(target.AddonsDir);
            File.Copy(shippedFile, target.AddonFile, overwrite: true);
        }
        catch (Exception ex)
        {
            return new InstallResult(false, false, $"Could not copy the add-on into {target.AddonsDir}: {ex.Message}");
        }

        if (!enable || target.Executable is null)
            return new InstallResult(true, false, target.Executable is null
                ? $"Installed for Blender {target.Version}. Its blender.exe was not found under Program Files, so tick \"ReyEngine Bridge\" once in Edit ▸ Preferences ▸ Add-ons."
                : $"Installed for Blender {target.Version}.");

        try
        {
            var psi = new ProcessStartInfo(target.Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-b");
            psi.ArgumentList.Add("--python-expr");
            psi.ArgumentList.Add(EnableExpression);
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Blender did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string err = (await error).Trim();
            bool ok = process.ExitCode == 0 && !err.Contains("Traceback", StringComparison.Ordinal);
            return new InstallResult(true, ok, ok
                ? $"Installed and enabled for Blender {target.Version}."
                : $"Installed for Blender {target.Version}, but enabling it there failed (exit {process.ExitCode}). Tick \"ReyEngine Bridge\" once in Edit ▸ Preferences ▸ Add-ons."
                  + (err.Length > 0 ? "\n" + err[^Math.Min(err.Length, 300)..] : ""));
        }
        catch (Exception ex)
        {
            return new InstallResult(true, false,
                $"Installed for Blender {target.Version}, but Blender could not be run to enable it ({ex.Message}). Tick \"ReyEngine Bridge\" once in Edit ▸ Preferences ▸ Add-ons.");
        }
    }
}
