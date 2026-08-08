using System;
using System.Linq;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M392: the import dialog's logic, exercised without disk or UI (the view-model takes an
/// injected reader). The behaviour that matters most is that an undecodable file stays in the list with
/// its reason — an import of five images must not silently become an import of four.</summary>
public class TextureImportViewModelTests
{
    /// <summary>Real bytes the decoder accepts: a .tex written by our own writer.</summary>
    private static byte[] SampleImage(int w = 32, int h = 32, byte alpha = 255)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        { px[i * 4] = 200; px[i * 4 + 1] = 100; px[i * 4 + 2] = 50; px[i * 4 + 3] = alpha; }
        return TexWriter.Write(new TextureImage(w, h, px), TexFormat.Bc3, mipmaps: false);
    }

    private static TextureImportViewModel Vm(params (string Path, byte[]? Bytes)[] files)
        => new(files.Select(f => f.Path),
               p => files.First(f => f.Path == p).Bytes ?? throw new InvalidOperationException("boom"));

    [Theory]
    [InlineData("a.png", true)]
    [InlineData("a.PNG", true)]
    [InlineData("a.tga", true)]
    [InlineData("a.dds", true)]
    [InlineData("a.jpeg", true)]
    [InlineData("a.tex", false)]     // already the target format - re-encoding would be lossy for nothing
    [InlineData("a.bin", false)]
    [InlineData("a", false)]
    public void ConvertibleExtensionsAreRecognised(string name, bool expected)
        => Assert.Equal(expected, TextureImportViewModel.IsConvertibleImage(name));

    [Fact]
    public void ADecodableImageBecomesAConvertibleRowNamedDotTex()
    {
        var vm = Vm(("C:/in/logo.png", SampleImage()));
        var item = Assert.Single(vm.Items);
        Assert.True(item.CanConvert);
        Assert.Equal("logo.tex", item.TargetName);
        Assert.Contains("32x32", item.Detail);
    }

    /// <summary>The point of the list: a file that cannot be read is still shown, with a reason.</summary>
    [Fact]
    public void AnUnreadableFileIsKeptWithItsReasonRatherThanDropped()
    {
        var vm = Vm(("C:/in/good.png", SampleImage()), ("C:/in/broken.png", null));
        Assert.Equal(2, vm.Items.Count);

        var bad = vm.Items.Single(i => i.Name == "broken.png");
        Assert.False(bad.CanConvert);
        Assert.False(string.IsNullOrWhiteSpace(bad.Detail));
        Assert.Contains("1 unreadable", vm.TotalSummary);
        Assert.True(vm.HasConvertible);          // the good one can still go
    }

    [Fact]
    public void WhenNothingDecodesTheDialogCannotProceed()
    {
        var vm = Vm(("C:/in/a.png", null), ("C:/in/b.png", null));
        Assert.False(vm.HasConvertible);
        Assert.Equal("Nothing here can be converted.", vm.TotalSummary);
        Assert.Empty(vm.Encode());
    }

    [Fact]
    public void ChangingTheFormatUpdatesTheSizePreview()
    {
        var vm = Vm(("C:/in/a.png", SampleImage()));
        vm.Format = TexFormatChoice.Bc3;
        string bc3 = vm.TotalSummary;
        vm.Format = TexFormatChoice.Bc1;
        Assert.NotEqual(bc3, vm.TotalSummary);
        Assert.Contains("BC1", vm.Items[0].Detail);
    }

    [Fact]
    public void ChangingMipmapsUpdatesTheSizePreview()
    {
        var vm = Vm(("C:/in/a.png", SampleImage()));
        vm.Mipmaps = true;
        string mipped = vm.TotalSummary;
        vm.Mipmaps = false;
        Assert.NotEqual(mipped, vm.TotalSummary);
        Assert.Contains("no mips", vm.Items[0].Detail);
    }

    /// <summary>What Encode returns must match what the preview promised, or the dialog lies.</summary>
    [Fact]
    public void EncodedBytesMatchThePredictedSize()
    {
        var vm = Vm(("C:/in/a.png", SampleImage(64, 64)));
        var (name, bytes) = Assert.Single(vm.Encode());
        Assert.Equal("a.tex", name);
        Assert.Equal(vm.Options.PredictBytes(vm.Items[0].Image!), bytes.LongLength);
        Assert.Equal((byte)'T', bytes[0]);
    }

    [Fact]
    public void OnlyDecodableFilesAreEncoded()
    {
        var vm = Vm(("C:/in/good.png", SampleImage()), ("C:/in/broken.png", null));
        var (name, _) = Assert.Single(vm.Encode());
        Assert.Equal("good.tex", name);
    }
}
