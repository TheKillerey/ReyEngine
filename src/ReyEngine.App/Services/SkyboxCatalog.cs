using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;

namespace ReyEngine.App.Services;

/// <summary>What a viewport should draw as its sky. Exactly one of the three sources is set.</summary>
public sealed record SkyboxSpec(
    CubemapImage? Cubemap = null,
    TextureImage? Equirect = null,
    float[]? MeshPositions = null, float[]? MeshUvs = null, uint[]? MeshIndices = null, TextureImage? MeshTexture = null);

public enum SkyboxSourceKind { Cubemap, Mesh, Texture }

/// <summary>One discovered skybox asset (League ships hundreds, mostly TFT/Arena domes).</summary>
public sealed record SkyboxOption(string Label, SkyboxSourceKind Kind, WadAssetEntry Main, WadAssetEntry? PairedTexture)
{
    public override string ToString() => Label;
}

/// <summary>
/// M122: finds every skybox asset in the mounted content. Three shapes exist in the game files:
/// DDS cubemaps (assets/maps/skyboxes/riots_sru_skybox_cubemap.dds - the SR sky), authored dome
/// meshes (.scb/.sco/.skn with "skybox" in the name, textured by a sibling .tex), and plain sky
/// textures (ha_skybox_01.tex) which render on the built-in dome via equirect sampling.
/// </summary>
public static class SkyboxCatalog
{
    public static List<SkyboxOption> Discover(IEnumerable<WadAssetEntry> entries)
    {
        const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
        var all = entries.Where(e => e.IsResolved && e.Path.Contains("skybox", OIC)).ToList();

        var options = new List<SkyboxOption>();
        var seen = new HashSet<ulong>();
        var texByDir = all
            .Where(e => e.Path.EndsWith(".tex", OIC) || e.Path.EndsWith(".dds", OIC))
            .GroupBy(e => Dir(e.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // cubemaps first - the classic skies
        foreach (var e in all.Where(e => e.Path.EndsWith(".dds", OIC) && e.Path.Contains("cubemap", OIC)))
            if (seen.Add(e.PathHash))
                options.Add(new SkyboxOption($"☁ {Stem(e.Path)}", SkyboxSourceKind.Cubemap, e, null));

        // authored domes, paired with the most name-similar texture in their folder
        var pairedTex = new HashSet<ulong>();
        foreach (var e in all.Where(e => e.Path.EndsWith(".scb", OIC) || e.Path.EndsWith(".sco", OIC) || e.Path.EndsWith(".skn", OIC)))
        {
            if (!seen.Add(e.PathHash)) continue;
            WadAssetEntry? tex = null;
            if (texByDir.TryGetValue(Dir(e.Path), out var candidates))
            {
                var stem = Stem(e.Path);
                tex = candidates
                    .Where(t => t.Path.EndsWith(".tex", OIC))
                    .OrderByDescending(t => CommonPrefix(Stem(t.Path), stem))
                    .FirstOrDefault();
            }
            if (tex is not null) pairedTex.Add(tex.PathHash);
            options.Add(new SkyboxOption($"⛰ {Stem(e.Path)}", SkyboxSourceKind.Mesh, e, tex));
        }

        // remaining plain sky textures render on the built-in dome
        foreach (var e in all.Where(e => e.Path.EndsWith(".tex", OIC)))
        {
            if (pairedTex.Contains(e.PathHash) || !seen.Add(e.PathHash)) continue;
            if (e.Path.Contains("_mask", OIC) || e.Path.Contains("_mult", OIC)) continue;   // shader stages, not skies
            options.Add(new SkyboxOption($"🖼 {Stem(e.Path)}", SkyboxSourceKind.Texture, e, null));
        }

        return options
            .OrderBy(o => o.Kind switch { SkyboxSourceKind.Cubemap => 0, SkyboxSourceKind.Mesh => 1, _ => 2 })
            .ThenBy(o => o.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Dir(string path)
    {
        int i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    private static string Stem(string path) => Path.GetFileNameWithoutExtension(path);

    private static int CommonPrefix(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        while (i < n && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
        return i;
    }

    /// <summary>M122: decode a user-supplied image file for the custom skybox — .tex/.dds through the
    /// game decoders (cubemaps detected), anything else through Avalonia's imaging (png/jpg/...).</summary>
    public static SkyboxSpec? LoadCustomFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".dds" && CubemapDecoder.TryDecodeDds(bytes) is { } cm)
                return new SkyboxSpec(Cubemap: cm);
            if (ext is ".tex" or ".dds")
                return new SkyboxSpec(Equirect: TextureDecoder.Decode(bytes));

            using var ms = new MemoryStream(bytes, writable: false);
            var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
            int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
            var bgra = new byte[w * h * 4];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
            try { bmp.CopyPixels(new Avalonia.PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), bgra.Length, w * 4); }
            finally { handle.Free(); }
            for (int i = 0; i < bgra.Length; i += 4)   // BGRA -> RGBA
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            return new SkyboxSpec(Equirect: new TextureImage(w, h, bgra));
        }
        catch { return null; }
    }
}
