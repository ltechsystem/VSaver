using ValheimSync.Core.Sync;
using Xunit;

namespace ValheimSync.Tests;

public sealed class WorldScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vstests-scan-" + Guid.NewGuid().ToString("N"));

    public WorldScannerTests() => Directory.CreateDirectory(_dir);

    private string WorldDir(string name)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Touch(string dir, string fileName) => File.WriteAllText(Path.Combine(dir, fileName), "x");

    private static void WriteRevision(string dir, int revision, params string[] chunkNames)
    {
        Touch(dir, $"_main.{revision}.db2");
        Touch(dir, $"_main.{revision}.fwl2");
        Touch(dir, $"_main.{revision}.chunks");
        Touch(dir, $"_main.{revision}.ok");
        foreach (var chunk in chunkNames) Touch(dir, chunk);
    }

    [Fact]
    public void FindsCompleteRevision_WithItsChunkFiles()
    {
        var dir = WorldDir("Alpha");
        WriteRevision(dir, 1, "1e_1e__1_1.chunk", "1e_20__1_1.chunk");

        var worlds = WorldScanner.Scan(_dir);

        var world = Assert.Single(worlds);
        Assert.Equal("Alpha", world.Name);
        Assert.Equal(1, world.Revision);
        Assert.Equal(2, world.ChunkPaths.Count);
        Assert.All(world.AllFilePaths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void SortedCaseInsensitively()
    {
        WriteRevision(WorldDir("beta"), 1);
        WriteRevision(WorldDir("Alpha"), 1);

        var worlds = WorldScanner.Scan(_dir);

        Assert.Equal(new[] { "Alpha", "beta" }, worlds.Select(w => w.Name));
    }

    [Fact]
    public void IncompleteRevision_WithNoEarlierComplete_IsNotListed()
    {
        // Mirrors a freshly created world: Valheim has written the metadata file but
        // hasn't finished (or even started) a real save yet — no .db2/.chunks/.ok.
        Touch(WorldDir("Newborn"), "_main.0.fwl2");

        Assert.Empty(WorldScanner.Scan(_dir));
    }

    [Fact]
    public void IncompleteLatestRevision_FallsBackToPreviousComplete()
    {
        // A save-in-progress: revision 2's .ok hasn't landed yet, but revision 1 (the
        // previous completed save) is still fully intact on disk.
        var dir = WorldDir("MidSave");
        WriteRevision(dir, 1, "1e_1e__1_1.chunk");
        Touch(dir, "_main.2.db2");
        Touch(dir, "_main.2.fwl2");
        // no _main.2.ok — revision 2 is not yet certified complete

        var world = Assert.Single(WorldScanner.Scan(_dir));
        Assert.Equal(1, world.Revision);
    }

    [Fact]
    public void BackupSnapshotFolders_AreIgnored()
    {
        WriteRevision(WorldDir("World"), 1, "1e_1e__1_1.chunk");
        Touch(WorldDir("World_backup_auto-20260909-172620"), "_main.0.fwl2");

        var world = Assert.Single(WorldScanner.Scan(_dir));
        Assert.Equal("World", world.Name);
    }

    [Fact]
    public void ChunksIndexFile_IsNotTreatedAsAChunkFile()
    {
        // "_main.<N>.chunks" (the index) must not be picked up by the ".chunk" glob.
        var dir = WorldDir("World");
        WriteRevision(dir, 1, "1e_1e__1_1.chunk");

        var world = Assert.Single(WorldScanner.Scan(_dir));
        Assert.DoesNotContain(world.ChunkPaths, p => p.EndsWith(".chunks", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingDirectory_ReturnsEmpty()
    {
        Assert.Empty(WorldScanner.Scan(Path.Combine(_dir, "does-not-exist")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
