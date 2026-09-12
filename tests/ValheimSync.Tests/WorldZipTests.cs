using ValheimSync.Core.Sync;
using ValheimSync.Core.Util;
using Xunit;

namespace ValheimSync.Tests;

/// <summary>
/// WorldZip determinism matters as much as the old per-file MD5 comparison did: two zips
/// built from identical file content must hash identically, or SyncEngine's "nothing
/// changed" check would never actually skip a transfer.
/// </summary>
public sealed class WorldZipTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsziptests-" + Guid.NewGuid().ToString("N"));

    public WorldZipTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Create_IsDeterministic_RegardlessOfInputOrder()
    {
        var srcDir = Path.Combine(_dir, "src");
        Directory.CreateDirectory(srcDir);
        var pathA = Path.Combine(srcDir, "a.txt");
        var pathB = Path.Combine(srcDir, "b.txt");
        await File.WriteAllTextAsync(pathA, "alpha");
        await File.WriteAllTextAsync(pathB, "beta");

        var zip1 = Path.Combine(_dir, "one.zip");
        var zip2 = Path.Combine(_dir, "two.zip");
        // Same files, opposite input order — the resulting bytes (and therefore MD5) must
        // still match, or a divergence with no real content change would still re-upload.
        await WorldZip.CreateAsync(new[] { pathA, pathB }, zip1);
        await WorldZip.CreateAsync(new[] { pathB, pathA }, zip2);

        Assert.Equal(await Hashing.Md5Async(zip1), await Hashing.Md5Async(zip2));
    }

    [Fact]
    public async Task CreateThenExtract_RoundTripsContent()
    {
        var srcDir = Path.Combine(_dir, "src");
        Directory.CreateDirectory(srcDir);
        var dbPath = Path.Combine(srcDir, "world.db2");
        var chunkPath = Path.Combine(srcDir, "zone.chunk");
        await File.WriteAllTextAsync(dbPath, "hello world");
        await File.WriteAllTextAsync(chunkPath, "zone data");

        var zipPath = Path.Combine(_dir, "world.zip");
        await WorldZip.CreateAsync(new[] { dbPath, chunkPath }, zipPath);

        var destDir = Path.Combine(_dir, "dest");
        await WorldZip.ExtractAsync(zipPath, destDir);

        Assert.Equal("hello world", await File.ReadAllTextAsync(Path.Combine(destDir, "world.db2")));
        Assert.Equal("zone data", await File.ReadAllTextAsync(Path.Combine(destDir, "zone.chunk")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
