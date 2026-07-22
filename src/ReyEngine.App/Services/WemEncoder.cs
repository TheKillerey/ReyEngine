using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ReyEngine.App.Services;

/// <summary>
/// M138: convert ordinary audio (.wav/.mp3/.ogg/…) into a real Wwise <c>.wem</c>.
///
/// League ships ONLY Wwise Vorbis media (verified: 8,212 of 8,212 shipped wems are fmt 0xFFFF), and
/// that codec needs Wwise's own encoder — so this drives <c>WwiseConsole.exe convert-external-source</c>
/// against a Wwise project, exactly like the community pipeline. Output matches Riot's files
/// byte-for-byte in shape: fmt 0xFFFF, <c>fmt+hash+data</c> chunk layout.
/// </summary>
public sealed class WemEncoder
{
    public const string DefaultConversion = "Vorbis Quality High";

    /// <summary>Explicit paths (Preferences); when empty the probes below are used.</summary>
    public string? ConsolePathSetting { get; set; }
    public string? ProjectPathSetting { get; set; }
    /// <summary>vgmstream, used to decode inputs Windows Media Foundation can't read (e.g. .ogg).</summary>
    public string? VgmstreamPath { get; set; }

    public string? ConsolePath => FirstExisting(ConsolePathSetting, ProbeConsolePaths());
    public string? ProjectPath => FirstExisting(ProjectPathSetting, ProbeProjectPaths());
    public bool IsAvailable => ConsolePath is not null && ProjectPath is not null;

    /// <summary>A human-readable reason when <see cref="IsAvailable"/> is false.</summary>
    public string UnavailableReason =>
        ConsolePath is null
            ? "WwiseConsole.exe not found. Install Wwise (or LtMAO, which bundles it) and set the path in Preferences ▸ Audio."
            : ProjectPath is null
                ? "A Wwise project (.wproj) is required for conversion — set one in Preferences ▸ Audio."
                : "";

    private static string? FirstExisting(string? setting, IEnumerable<string> probes)
    {
        if (!string.IsNullOrWhiteSpace(setting) && File.Exists(setting)) return setting;
        foreach (var p in probes) if (File.Exists(p)) return p;
        return null;
    }

    private static IEnumerable<string> ProbeConsolePaths()
    {
        const string rel = @"res\wiwawe\WwiseApp\Authoring\x64\Release\bin\WwiseConsole.exe";
        foreach (var root in LtMaoRoots()) yield return Path.Combine(root, rel);
        // a real Wwise install
        foreach (var pf in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        })
        {
            var ak = Path.Combine(pf, "Audiokinetic");
            if (!Directory.Exists(ak)) continue;
            foreach (var ver in SafeDirs(ak))
                yield return Path.Combine(ver, "Authoring", "x64", "Release", "bin", "WwiseConsole.exe");
        }
    }

    private static IEnumerable<string> ProbeProjectPaths()
    {
        const string rel = @"res\wiwawe\WwiseLeagueProjects\WWiseLeagueProjects.wproj";
        foreach (var root in LtMaoRoots()) yield return Path.Combine(root, rel);
    }

    private static IEnumerable<string> LtMaoRoots()
    {
        yield return @"D:\LeagueTools\LtMAO";
        yield return @"C:\LeagueTools\LtMAO";
        foreach (var drive in new[] { "C", "D", "E" })
            yield return $@"{drive}:\LtMAO";
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); } catch { yield break; }
        foreach (var d in dirs) yield return d;
    }

    /// <summary>Extensions we can hand to Wwise directly.</summary>
    private static bool IsWav(string path) => path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Convert an audio file to .wem bytes. Returns null with a reason on failure.
    /// Non-wav input is decoded to PCM wav first (Media Foundation, then vgmstream).
    /// </summary>
    public byte[]? Convert(string inputPath, out string? error, string conversion = DefaultConversion)
    {
        error = null;
        if (!IsAvailable) { error = UnavailableReason; return null; }

        string work = Path.Combine(Path.GetTempPath(), "ReyEngine", "wem", Guid.NewGuid().ToString("n")[..8]);
        string inDir = Path.Combine(work, "input"), outDir = Path.Combine(work, "output");
        try
        {
            Directory.CreateDirectory(inDir);
            Directory.CreateDirectory(outDir);

            // Wwise keys the output name off the source file name — keep it simple and unique.
            string stem = "reyaudio";
            string wav = Path.Combine(inDir, stem + ".wav");
            if (IsWav(inputPath)) File.Copy(inputPath, wav, overwrite: true);
            else if (!TryDecodeToWav(inputPath, wav, out error)) return null;

            string wsources = Path.Combine(work, "sources.wsources");
            var xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            xml.Append($"<ExternalSourcesList SchemaVersion=\"1\" Root=\"{inDir.Replace('\\', '/')}\">\n");
            xml.Append($"\t<Source Path=\"{stem}.wav\" Conversion=\"{conversion}\" />\n");
            xml.Append("</ExternalSourcesList>");
            File.WriteAllText(wsources, xml.ToString(), new UTF8Encoding(false));

            var psi = new ProcessStartInfo(ConsolePath!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("convert-external-source");
            psi.ArgumentList.Add(ProjectPath!);
            psi.ArgumentList.Add("--source-file");
            psi.ArgumentList.Add(wsources);
            psi.ArgumentList.Add("--output");
            psi.ArgumentList.Add(outDir);

            using var proc = Process.Start(psi);
            if (proc is null) { error = "WwiseConsole could not be started."; return null; }
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(120_000)) { try { proc.Kill(true); } catch { } error = "WwiseConsole timed out."; return null; }

            // output lands under <out>/<Platform>/<stem>.wem
            var produced = Directory.Exists(outDir)
                ? Directory.EnumerateFiles(outDir, "*.wem", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (produced is null)
            {
                var msg = (stdout + " " + stderr).Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (msg.Length > 300) msg = msg[..300] + "…";
                error = $"WwiseConsole produced no .wem. {msg}";
                return null;
            }
            var bytes = File.ReadAllBytes(produced);
            if (bytes.Length < 12 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
            { error = "The converted file isn't a RIFF/wem."; return null; }
            return bytes;
        }
        catch (Exception ex) { error = ex.Message; return null; }
        finally { try { Directory.Delete(work, recursive: true); } catch { } }
    }

    /// <summary>Decode mp3/m4a/wma (Media Foundation) or ogg and friends (vgmstream) to PCM wav.</summary>
    private bool TryDecodeToWav(string input, string destWav, out string? error)
    {
        error = null;
        try
        {
            using var reader = new NAudio.Wave.MediaFoundationReader(input);
            NAudio.Wave.WaveFileWriter.CreateWaveFile16(destWav,
                new NAudio.Wave.SampleProviders.SampleChannel(reader, forceStereo: false));
            return true;
        }
        catch { /* fall through to vgmstream */ }

        if (VgmstreamPath is not null && File.Exists(VgmstreamPath))
        {
            try
            {
                var psi = new ProcessStartInfo(VgmstreamPath)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add(destWav);
                psi.ArgumentList.Add(input);
                using var p = Process.Start(psi);
                p?.WaitForExit(60_000);
                if (File.Exists(destWav) && new FileInfo(destWav).Length > 44) return true;
            }
            catch { }
        }
        error = $"Could not decode {Path.GetFileName(input)} — convert it to .wav first.";
        return false;
    }
}
