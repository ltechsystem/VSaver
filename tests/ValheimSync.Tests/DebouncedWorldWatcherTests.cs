using ValheimSync.Core.Sync;
using Xunit;

namespace ValheimSync.Tests;

public sealed class DebouncedWorldWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vstests-watch-" + Guid.NewGuid().ToString("N"));

    public DebouncedWorldWatcherTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task NonWorldFiles_AreIgnored()
    {
        using var watcher = new DebouncedWorldWatcher(_dir, TimeSpan.FromMilliseconds(150));
        var fired = false;
        watcher.WorldChanged += _ => fired = true;

        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "hi"); // top-level file
        Directory.CreateDirectory(Path.Combine(_dir, "Alpha"));
        File.WriteAllText(Path.Combine(_dir, "Alpha", "notes.txt"), "hi"); // wrong extension

        await Task.Delay(700);
        Assert.False(fired);
    }

    [Fact]
    public async Task Fires_ForMainFile_InWorldSubfolder()
    {
        using var watcher = new DebouncedWorldWatcher(_dir, TimeSpan.FromMilliseconds(250));
        var fired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.WorldChanged += name => fired.TrySetResult(name);

        Directory.CreateDirectory(Path.Combine(_dir, "Alpha"));
        File.WriteAllText(Path.Combine(_dir, "Alpha", "_main.1.db2"), "save data");

        var winner = await Task.WhenAny(fired.Task, Task.Delay(10_000));
        Assert.Same(fired.Task, winner);
        Assert.Equal("Alpha", await fired.Task);
    }

    [Fact]
    public async Task Fires_ForZoneChunkFile_WithNoMainPrefix()
    {
        using var watcher = new DebouncedWorldWatcher(_dir, TimeSpan.FromMilliseconds(250));
        var fired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.WorldChanged += name => fired.TrySetResult(name);

        Directory.CreateDirectory(Path.Combine(_dir, "Beta"));
        File.WriteAllText(Path.Combine(_dir, "Beta", "1e_1e__1_1.chunk"), "zone data");

        var winner = await Task.WhenAny(fired.Task, Task.Delay(10_000));
        Assert.Same(fired.Task, winner);
        Assert.Equal("Beta", await fired.Task);
    }

    [Fact]
    public async Task RepeatedWrites_AcrossDifferentFiles_ResetTheTimer_SingleEvent()
    {
        using var watcher = new DebouncedWorldWatcher(_dir, TimeSpan.FromMilliseconds(300));
        int count = 0;
        var fired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.WorldChanged += _ => { Interlocked.Increment(ref count); fired.TrySetResult(true); };

        var worldDir = Path.Combine(_dir, "Gamma");
        Directory.CreateDirectory(worldDir);
        var files = new[] { "_main.1.db2", "_main.1.fwl2", "1e_1e__1_1.chunk", "_main.1.ok" };
        foreach (var name in files)
        {
            File.WriteAllText(Path.Combine(worldDir, name), "x");
            await Task.Delay(100); // keep poking inside the debounce window
        }

        await Task.WhenAny(fired.Task, Task.Delay(10_000));
        await Task.Delay(500); // let any stray duplicate land before asserting
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Ignores_BackupSnapshotFolder()
    {
        using var watcher = new DebouncedWorldWatcher(_dir, TimeSpan.FromMilliseconds(150));
        var fired = false;
        watcher.WorldChanged += _ => fired = true;

        var backupDir = Path.Combine(_dir, "World_backup_auto-20260101-000000");
        Directory.CreateDirectory(backupDir);
        File.WriteAllText(Path.Combine(backupDir, "_main.0.fwl2"), "snapshot");

        await Task.Delay(700);
        Assert.False(fired);
    }

    [Fact]
    public async Task Ignores_FilesNestedMoreThanOneLevelDeep()
    {
        using var watcher = new DebouncedWorldWatcher(_dir, TimeSpan.FromMilliseconds(150));
        var fired = false;
        watcher.WorldChanged += _ => fired = true;

        var nested = Path.Combine(_dir, "Delta", "sub");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "_main.1.db2"), "unexpected nesting");

        await Task.Delay(700);
        Assert.False(fired);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
