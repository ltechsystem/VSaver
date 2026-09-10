using ValheimSync.Core.Models;
using ValheimSync.Core.Util;
using Xunit;

namespace ValheimSync.Tests;

public sealed class WorldLockTests
{
    [Fact]
    public void FreshLock_IsNotStale() =>
        Assert.False(new WorldLock("me", DateTimeOffset.UtcNow).IsStale);

    [Fact]
    public void LockOlderThan12Hours_IsStale() =>
        Assert.True(new WorldLock("me", DateTimeOffset.UtcNow.AddHours(-13)).IsStale);

    [Fact]
    public void StaleThreshold_Is12Hours() =>
        Assert.Equal(TimeSpan.FromHours(12), WorldLock.StaleAfter);
}

public sealed class WorldSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vstests-ws-" + Guid.NewGuid().ToString("N"));

    public WorldSaveTests() => Directory.CreateDirectory(_dir);

    private string Write(string name, int bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void SizeBytes_SumsMainFilesAndChunks()
    {
        var db2 = Write("_main.1.db2", 10);
        var fwl2 = Write("_main.1.fwl2", 3);
        var chunksIndex = Write("_main.1.chunks", 2);
        var ok = Write("_main.1.ok", 4);
        var chunk = Write("1e_1e__1_1.chunk", 100);

        var world = new WorldSave("W", _dir, 1, db2, fwl2, chunksIndex, ok, new[] { chunk });

        Assert.Equal(119, world.SizeBytes);
    }

    [Fact]
    public void SizeBytes_MissingFileCountsAsZero()
    {
        var db2 = Write("_main.1.db2", 10);
        var world = new WorldSave("W", _dir, 1, db2,
            Path.Combine(_dir, "missing.fwl2"), Path.Combine(_dir, "missing.chunks"),
            Path.Combine(_dir, "missing.ok"), Array.Empty<string>());

        Assert.Equal(10, world.SizeBytes);
    }

    [Fact]
    public void LastWriteUtc_IsMinValue_WhenNothingExists()
    {
        var world = new WorldSave("W", _dir, 1,
            Path.Combine(_dir, "nope.db2"), Path.Combine(_dir, "nope.fwl2"),
            Path.Combine(_dir, "nope.chunks"), Path.Combine(_dir, "nope.ok"), Array.Empty<string>());

        Assert.Equal(DateTime.MinValue, world.LastWriteUtc);
    }

    [Fact]
    public void AllFilePaths_IncludesMainFilesAndEveryChunk()
    {
        var db2 = Write("_main.1.db2", 1);
        var fwl2 = Write("_main.1.fwl2", 1);
        var chunksIndex = Write("_main.1.chunks", 1);
        var ok = Write("_main.1.ok", 1);
        var chunkA = Write("1e_1e__1_1.chunk", 1);
        var chunkB = Write("20_20__1_1.chunk", 1);

        var world = new WorldSave("W", _dir, 1, db2, fwl2, chunksIndex, ok, new[] { chunkA, chunkB });

        Assert.Equal(new[] { db2, fwl2, chunksIndex, ok, chunkA, chunkB }, world.AllFilePaths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}

public sealed class HashingTests
{
    [Fact]
    public async Task Md5_MatchesKnownVector_LowercaseHex()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "vstests-md5-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(tmp, "hello world");
            // Well-known MD5 of "hello world" — and lowercase, exactly as Drive reports it.
            Assert.Equal("5eb63bbbe01eeed093cb22bb8f5acdc3", await Hashing.Md5Async(tmp));

            await File.WriteAllBytesAsync(tmp, Array.Empty<byte>());
            Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", await Hashing.Md5Async(tmp));
        }
        finally { File.Delete(tmp); }
    }
}
