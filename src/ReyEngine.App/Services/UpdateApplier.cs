using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace ReyEngine.App.Services;

/// <summary>
/// M681: installs a release the checker found.
///
/// <para>Two kinds of install exist. The zip that every release ships is unpacked wherever the user put it,
/// so the update is "replace the files in that folder": the build is downloaded and unpacked under
/// %LocalAppData%\ReyEngine\updates, a PowerShell script is handed the job, and the app exits - a running
/// exe cannot overwrite itself. The script waits for the process to end, copies the new build over the
/// install folder (robocopy, retried), and starts the app again; when the folder is not writable the
/// script is started elevated. An MSI install (the installer writes HKCU\Software\ReyEngine\InstallKind =
/// msi) takes the .msi asset instead and lets msiexec do the replacing, passively, then restarts the app.
/// Either way the log is updates\apply.log, and a failure leaves the unpacked build where the log says.</para>
/// </summary>
public static class UpdateApplier
{
    public enum InstallKind { Zip, Msi }

    public sealed record Prepared(string ScriptPath, bool Elevate, string Summary);

    /// <summary>Where the app runs from - the folder the zip was unpacked to, or the MSI's install dir.</summary>
    public static string InstallDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string? ExecutablePath => Environment.ProcessPath;

    public static string UpdatesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReyEngine", "updates");

    public static InstallKind CurrentInstallKind =>
        string.Equals(UpdateService.ReadRegistry("InstallKind") as string, "msi", StringComparison.OrdinalIgnoreCase)
            ? InstallKind.Msi : InstallKind.Zip;

    /// <summary>The asset this install takes: the .msi for an MSI install when the release ships one, the
    /// win-x64 zip otherwise (any zip as a last resort). Null when the release has no usable build.</summary>
    public static UpdateService.ReleaseAsset? PickAsset(IReadOnlyList<UpdateService.ReleaseAsset>? assets, InstallKind kind)
    {
        if (assets is null || assets.Count == 0) return null;
        const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
        if (kind == InstallKind.Msi)
        {
            var msi = assets.FirstOrDefault(a => a.Name.EndsWith(".msi", OIC));
            if (msi is not null) return msi;
        }
        return assets.FirstOrDefault(a => a.Name.EndsWith("win-x64.zip", OIC))
               ?? assets.FirstOrDefault(a => a.Name.EndsWith(".zip", OIC));
    }

    /// <summary>Download and unpack the build, write the hand-over script, and say what will happen. Throws
    /// with a readable message when the release cannot be applied; nothing has replaced anything by then.</summary>
    public static async Task<Prepared> PrepareAsync(UpdateService.UpdateCheck check,
        IProgress<(double Fraction, string Status)>? progress, CancellationToken ct)
    {
        var kind = CurrentInstallKind;
        var asset = PickAsset(check.Assets, kind)
                    ?? throw new InvalidOperationException("This release ships no build this install can take - use the download page.");
        string exe = ExecutablePath ?? throw new InvalidOperationException("The running executable could not be located.");
        string tag = check.LatestVersion ?? "update";
        string dir = Path.Combine(UpdatesDirectory, Sanitize(tag));
        Directory.CreateDirectory(dir);
        string log = Path.Combine(UpdatesDirectory, "apply.log");
        string file = Path.Combine(dir, asset.Name);

        progress?.Report((0, $"Downloading {asset.Name}…"));
        await DownloadAsync(asset, file, progress, ct);

        int pid = Environment.ProcessId;
        string script;
        string summary;
        bool elevate = false;
        if (asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            script = BuildMsiApplyScript(pid, file, exe, log);
            summary = $"msiexec installs {asset.Name}, then ReyEngine restarts.";
        }
        else
        {
            progress?.Report((0.92, "Unpacking…"));
            string payload = Path.Combine(dir, "payload");
            if (Directory.Exists(payload)) Directory.Delete(payload, recursive: true);
            await Task.Run(() => ZipFile.ExtractToDirectory(file, payload, overwriteFiles: true), ct);
            if (!File.Exists(Path.Combine(payload, Path.GetFileName(exe))))
                throw new InvalidOperationException($"{asset.Name} does not contain {Path.GetFileName(exe)} at its root - not a build this install can take.");
            elevate = !IsWritable(InstallDirectory);
            script = BuildZipApplyScript(pid, payload, InstallDirectory, exe, log);
            summary = $"The new build replaces the files in {InstallDirectory}, then ReyEngine restarts."
                      + (elevate ? " That folder needs administrator rights, so Windows will ask." : "");
        }
        string scriptPath = Path.Combine(dir, "apply-update.ps1");
        await File.WriteAllTextAsync(scriptPath, script, ct);
        progress?.Report((1, "Ready to install."));
        return new Prepared(scriptPath, elevate, summary);
    }

    /// <summary>Start the hand-over script and leave: the script waits for this process to end before it
    /// touches a file. The window that called this should already have saved what it wants saved.</summary>
    public static void ApplyAndExit(Prepared prepared)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{prepared.ScriptPath}\"")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(prepared.ScriptPath) ?? UpdatesDirectory,
        };
        if (prepared.Elevate) psi.Verb = "runas";
        Process.Start(psi);
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    // ---- the pieces, public so the tests can hold them still ----------------------------------------

    public static async Task DownloadAsync(UpdateService.ReleaseAsset asset, string file,
        IProgress<(double Fraction, string Status)>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ReyEngine", AppInfo.Version));
        using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? asset.Size;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(file);
        var buffer = new byte[1 << 16];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0) progress?.Report((0.9 * done / total, $"Downloading {asset.Name}… {done / 1048576.0:0.0} / {total / 1048576.0:0.0} MB"));
        }
    }

    public static bool IsWritable(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, ".reyengine-write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>The zip hand-over. robocopy's exit codes below 8 are successes (0 = nothing to do, 1 = files
    /// copied, 2/4 = extras/mismatches), so that is the line. Every step lands in the log.</summary>
    public static string BuildZipApplyScript(int pid, string payloadDir, string targetDir, string exePath, string logPath) => $$"""
        $AppPid = {{pid}}
        $Source = '{{Q(payloadDir)}}'
        $Target = '{{Q(targetDir)}}'
        $Exe = '{{Q(exePath)}}'
        $Log = '{{Q(logPath)}}'
        function Note($m) { "$(Get-Date -Format o) $m" | Out-File -FilePath $Log -Append -Encoding utf8 }
        Note "zip update: waiting for pid $AppPid to exit"
        try { Wait-Process -Id $AppPid -Timeout 120 -ErrorAction SilentlyContinue } catch { }
        Start-Sleep -Milliseconds 800
        $ok = $false
        for ($attempt = 1; $attempt -le 5 -and -not $ok; $attempt++) {
          robocopy $Source $Target /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
          if ($LASTEXITCODE -lt 8) { $ok = $true } else { Note "robocopy exit $LASTEXITCODE (attempt $attempt)"; Start-Sleep -Seconds 2 }
        }
        if ($ok) {
          Note "copied into $Target - starting $Exe"
          Start-Process -FilePath $Exe -WorkingDirectory $Target
        } else {
          Note "FAILED - the new build is unpacked at $Source"
          Start-Process explorer.exe -ArgumentList $Source
        }
        """;

    /// <summary>The MSI hand-over: passive install (progress bar, no questions), then the same exe path -
    /// an upgrade keeps the install folder. 3010 is "done, reboot pending", which still restarts the app.</summary>
    public static string BuildMsiApplyScript(int pid, string msiPath, string exePath, string logPath) => $$"""
        $AppPid = {{pid}}
        $Msi = '{{Q(msiPath)}}'
        $Exe = '{{Q(exePath)}}'
        $Log = '{{Q(logPath)}}'
        function Note($m) { "$(Get-Date -Format o) $m" | Out-File -FilePath $Log -Append -Encoding utf8 }
        Note "msi update: waiting for pid $AppPid to exit"
        try { Wait-Process -Id $AppPid -Timeout 120 -ErrorAction SilentlyContinue } catch { }
        Start-Sleep -Milliseconds 800
        $p = Start-Process msiexec.exe -ArgumentList "/i `"$Msi`" /passive /norestart" -Wait -PassThru
        Note "msiexec exit $($p.ExitCode)"
        if ($p.ExitCode -eq 0 -or $p.ExitCode -eq 3010) { Start-Process -FilePath $Exe }
        else { Start-Process explorer.exe -ArgumentList (Split-Path $Msi) }
        """;

    private static string Q(string s) => s.Replace("'", "''");

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 ? "update" : name;
    }
}
