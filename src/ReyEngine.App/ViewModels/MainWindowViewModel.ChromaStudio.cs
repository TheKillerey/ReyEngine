using ReyEngine.Core.Assets;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M812: the Chroma Studio's host half - the read-only colour inventory behind the Character window's CHROMA card.
///
/// <para>The card holds the button and the lists (<see cref="MeshPreviewViewModel"/>); this holds the files. The scan
/// reads through the same mount-aware path every preview read in this window uses (<see cref="ReadAssetFrom"/>), so a
/// skin the project has overridden is scanned as the project has it, and a champion opened from the game folder is
/// read from the read-only fallback mount. It writes nothing - no asset, no project file, no setting.</para>
///
/// <para><b>Readers.</b> The scan runs for a second or more on a worker while the editor goes on: a project that saves
/// rebuilds the mounts (<see cref="BuildMounts"/>) and a second skin opened from the game folder adds a WAD to them. So the
/// worker never looks at <c>_mounts</c>. The hook is called on the UI thread, which is where nothing is rebuilding anything:
/// it takes the mounts and the archive as they are THEN, takes a lease on exactly those two (<see cref="AcquireReaderLease"/>,
/// so a rebuild that retires them waits for the scan the way it waits for a map thumbnail), and the worker reads through that
/// pair alone. What it reads is therefore one consistent state, never a half-built service and never a disposed one. An add to
/// the fallback list while it reads is safe because the list is copy-on-write (<see cref="AssetMountService.AddFallback"/>), and
/// so is the index (<see cref="AssetMountService.Rebuild"/> swaps it whole).</para>
///
/// <para><b>A scan that straddled a change in place.</b> Sync Hashes re-resolves the LIVE mounts and rebuilds them
/// (<see cref="ApplyHashesToOpenWad"/>), and mounting another champion's WAD adds to them; both move
/// <c>_mapThumbnailInputs</c>, the version the map thumbnails already use for the same reason. A scan that finishes under a
/// different version than it started under is not trusted: it is run again (the readers are taken again, on the UI thread), and
/// if it keeps straddling changes it is discarded with a warning that says so, never handed over as a result that may be short.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>One scanner for the session: it keeps the parsed bins of the last few champions, validated by content, so
    /// scanning a second skin of the same champion re-reads its files and re-parses almost nothing.</summary>
    private readonly SkinColorScanner _skinColors = new();

    /// <summary>How often a scan that straddled a change in place is run again before it is given up on.</summary>
    private const int MaxScanAttempts = 3;

    private const string ScanOverlappedChange =
        "The project's files changed while this scan ran (hashes were synced or another champion was mounted) every time it was tried, "
        + "so its result was discarded rather than shown possibly short. Press Scan colours again.";

    /// <summary>The inventory of one skin bin, called by the CHROMA card on the UI thread (see the class remarks): the part
    /// before the first await takes the readers, the scan itself runs on a worker.</summary>
    private async Task<SkinColorInventory> ScanSkinColours(string skinBinPath, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            // ---- the UI thread (the caller's, and after an await the one it resumes on) --------------------------
            var mounts = _mounts;
            var archive = _archive;
            long inputs = Interlocked.Read(ref _mapThumbnailInputs);
            using var lease = AcquireReaderLease(mounts, archive);

            // the client's names for the skins ("Petals of Spring Lillia (Rose Quartz)"), when the browser has them; skinN otherwise
            var browser = MeshPreview.Browser;
            string folder = SkinColorScanner.TryParseSkinPath(skinBinPath, out string f, out _) ? f : "";
            Func<int, string?>? name = browser is null || folder.Length == 0 ? null : n => browser.SkinDisplayName(folder, n);
            // the skins the champion's WAD lists for the character that was opened: what a sibling the scan cannot read is checked against
            IReadOnlyList<int>? expected = _openedCharacterSkins is { } opened && opened.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)
                ? opened.Numbers
                : null;

            var request = new SkinColorRequest(skinBinPath, path => ReadForColourScan(mounts, archive, path),
                ResolveBinName, ResolveWadPath, SkinNumbers: null, SkinName: name, ExpectedSkinNumbers: expected);

            // ---- a worker ------------------------------------------------------------------------------------
            var inventory = await Task.Run(() => _skinColors.Scan(request, cancellationToken), cancellationToken);

            // ---- back on the UI thread -----------------------------------------------------------------------
            if (Interlocked.Read(ref _mapThumbnailInputs) == inputs)
            {
                _log.Info("Chroma", $"{inventory.SkinLabel}: {inventory.Summary}"
                    + (inventory.Warnings.Count > 0 ? $" - {inventory.Warnings.Count} warning(s): {inventory.Warnings[0]}" : ""));
                return inventory;
            }

            _log.Info("Chroma", $"{skinBinPath}: the project's files changed while it was scanned (attempt {attempt} of {MaxScanAttempts}).");
            if (attempt >= MaxScanAttempts) return SkinColorInventory.Empty(skinBinPath, ScanOverlappedChange);
        }
    }

    /// <summary>
    /// A scan's asset read, through the readers it was given: null when the asset is not there, an exception when it is there
    /// and cannot be read - the scanner turns that into a warning, so a failed read is never mistaken for a missing file (the
    /// thumbnails' reader, <see cref="ReadForThumbnail"/>, counts the same failure as a fault instead). A texture REFERENCE
    /// addresses its chunk the way <see cref="ReadAssetByPath"/> does: by the hash it carries, so an unnamed <c>0x...</c> link works.
    /// </summary>
    private byte[]? ReadForColourScan(AssetMountService? mounts, WadArchive? archive, string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        ulong hash = BinTexturePath.HashOfReference(path);
        return HasIn(mounts, archive, hash) ? ReadAssetFrom(mounts, archive, hash) : null;
    }
}
