using System.IO.Compression;

namespace ValheimSync.Core.Sync;

/// <summary>
/// Packs/unpacks a world's whole file set as a single deterministic zip archive, so
/// transferring a world costs one upload/download instead of one Drive API call per file.
///
/// Determinism matters here the same way it mattered for the old per-file MD5 checks: two
/// zips built from identical file content must hash identically, or "nothing changed" would
/// keep re-uploading anyway. That means a fixed entry order (sorted by file name — the
/// order <see cref="Directory.EnumerateFiles"/> returns is not guaranteed stable) and a
/// fixed per-entry timestamp (ZipArchiveEntry defaults to "now", which is never stable).
/// </summary>
public static class WorldZip
{
    private static readonly DateTimeOffset FixedEntryTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task CreateAsync(IEnumerable<string> filePaths, string destZipPath,
        CancellationToken ct = default)
    {
        if (File.Exists(destZipPath)) File.Delete(destZipPath);

        await using var zipStream = new FileStream(destZipPath, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        foreach (var path in filePaths.OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            var entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal);
            entry.LastWriteTime = FixedEntryTimestamp;
            await using var entryStream = entry.Open();
            await using var fileStream = File.OpenRead(path);
            await fileStream.CopyToAsync(entryStream, ct);
        }
    }

    /// <summary>Extracts every entry flat into <paramref name="destDir"/> — a world folder
    /// never has subfolders of its own.</summary>
    public static async Task ExtractAsync(string zipPath, string destDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        using var zipStream = File.OpenRead(zipPath);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            await using var entryStream = entry.Open();
            await using var destStream = File.Create(Path.Combine(destDir, entry.Name));
            await entryStream.CopyToAsync(destStream, ct);
        }
    }
}
