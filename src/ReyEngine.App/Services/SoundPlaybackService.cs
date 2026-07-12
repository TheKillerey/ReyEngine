using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ReyEngine.App.Services;

/// <summary>
/// M56: plays Wwise .wem audio. League wems are Wwise Vorbis, which no managed decoder handles —
/// decoding goes through an external vgmstream-cli.exe (auto-detected; ISC-licensed) into a temp
/// .wav, then NAudio plays it with per-voice volume (used for positional map ambience).
/// All playback is fire-and-forget on the audio thread; StopAll() kills everything.
/// </summary>
public sealed class SoundPlaybackService : IDisposable
{
    private readonly string _tempDir;
    private readonly List<(WaveOutEvent Out, AudioFileReader Reader, VolumeSampleProvider Vol, string Tag)> _active = new();
    private readonly object _lock = new();

    public string? VgmstreamPath { get; set; }
    public bool IsAvailable => VgmstreamPath is not null && File.Exists(VgmstreamPath);

    public SoundPlaybackService()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ReyEngine", "audio");
        Directory.CreateDirectory(_tempDir);
        VgmstreamPath = Detect();
    }

    private static string? Detect()
    {
        string[] candidates =
        {
            @"D:\LeagueTools\LtMAO\res\tools\vgmstream\vgmstream-cli.exe",
            Path.Combine(AppContext.BaseDirectory, "tools", "vgmstream", "vgmstream-cli.exe"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        try
        {
            var psi = new ProcessStartInfo("where.exe", "vgmstream-cli.exe")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            var line = p?.StandardOutput.ReadLine();
            p?.WaitForExit(2000);
            if (!string.IsNullOrWhiteSpace(line) && File.Exists(line)) return line;
        }
        catch { }
        return null;
    }

    /// <summary>Decode a wem to wav (cached per wem id). Null when vgmstream is missing or decode fails.</summary>
    public string? DecodeToWav(uint wemId, byte[] wemData)
    {
        if (!IsAvailable) return null;
        var wav = Path.Combine(_tempDir, $"{wemId}.wav");
        if (File.Exists(wav)) return wav;
        var wem = Path.Combine(_tempDir, $"{wemId}.wem");
        try
        {
            File.WriteAllBytes(wem, wemData);
            var psi = new ProcessStartInfo(VgmstreamPath!, $"-o \"{wav}\" \"{wem}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            p!.WaitForExit(15000);
            return p.ExitCode == 0 && File.Exists(wav) ? wav : null;
        }
        catch { return null; }
        finally { try { File.Delete(wem); } catch { } }
    }

    /// <summary>Play a decoded wav (fire and forget). <paramref name="tag"/> groups voices for StopTag/SetTagVolume.</summary>
    public void PlayWav(string wavPath, float volume = 1f, bool loop = false, string tag = "")
    {
        try
        {
            var reader = new AudioFileReader(wavPath);
            ISampleProvider src = reader;
            if (loop) src = new LoopingSampleProvider(reader);
            var vol = new VolumeSampleProvider(src) { Volume = Math.Clamp(volume, 0f, 1f) };
            var output = new WaveOutEvent();
            output.Init(vol);
            output.PlaybackStopped += (_, _) =>
            {
                lock (_lock) _active.RemoveAll(a => ReferenceEquals(a.Out, output));
                try { output.Dispose(); reader.Dispose(); } catch { }
            };
            lock (_lock) _active.Add((output, reader, vol, tag));
            output.Play();
        }
        catch { /* audio device issues must never crash the editor */ }
    }

    /// <summary>Adjust the volume of every playing voice with this tag (positional attenuation).</summary>
    public void SetTagVolume(string tag, float volume)
    {
        lock (_lock)
            foreach (var a in _active.Where(a => a.Tag == tag))
                a.Vol.Volume = Math.Clamp(volume, 0f, 1f);
    }

    public bool IsTagPlaying(string tag)
    { lock (_lock) return _active.Any(a => a.Tag == tag); }

    public void StopTag(string tag)
    {
        List<(WaveOutEvent, AudioFileReader, VolumeSampleProvider, string)> victims;
        lock (_lock) victims = _active.Where(a => a.Tag == tag).ToList();
        foreach (var v in victims) { try { v.Item1.Stop(); } catch { } }
    }

    public void StopAll()
    {
        List<(WaveOutEvent, AudioFileReader, VolumeSampleProvider, string)> victims;
        lock (_lock) victims = _active.ToList();
        foreach (var v in victims) { try { v.Item1.Stop(); } catch { } }
    }

    public void Dispose() => StopAll();

    /// <summary>Endless loop over a wav (ambient beds). Rewinds the reader at end-of-stream.</summary>
    private sealed class LoopingSampleProvider : ISampleProvider
    {
        private readonly AudioFileReader _reader;
        public LoopingSampleProvider(AudioFileReader reader) => _reader = reader;
        public NAudio.Wave.WaveFormat WaveFormat => _reader.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = _reader.Read(buffer, offset + total, count - total);
                if (n == 0)
                {
                    _reader.Position = 0;   // loop
                    continue;
                }
                total += n;
            }
            return total;
        }
    }
}
