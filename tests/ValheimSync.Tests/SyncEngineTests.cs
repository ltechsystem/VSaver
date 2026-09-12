using ValheimSync.Core;
using ValheimSync.Core.Models;
using ValheimSync.Core.Sync;
using ValheimSync.Tests.Fakes;
using Xunit;

namespace ValheimSync.Tests;

/// <summary>
/// Exercises the whole sync decision logic through the ICloudStorageProvider seam.
///
/// Notes:
///  - Each test uses a unique world name so the engine's best-effort mirror into the real
///    Valheim LocalLow folder (if this machine has one) can be cleaned up safely in Dispose.
///  - Tests early-return if Valheim is actually running on this machine, because the engine
///    deliberately refuses to transfer while the game is up (static ValheimProcess seam).
///  - A world's whole save now travels as one deterministic zip (see WorldZip), so a
///    "remote world" in these tests is built by zipping the same files SyncEngine would
///    zip locally — see BuildZipBytesAsync/SeedRemoteWorldAsync — and inspected by
///    unzipping it back (ExtractRemoteZipAsync) rather than reading a named file directly.
/// </summary>
public sealed class SyncEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vstests-" + Guid.NewGuid().ToString("N"));
    private readonly string _world = "VSTestW" + Guid.NewGuid().ToString("N")[..8];
    private readonly FakeCloudProvider _cloud = new();
    private readonly AppSettings _settings;
    private readonly SyncEngine _engine;
    private readonly List<SyncStatus> _statuses = new();

    private string WorldDir => Path.Combine(_dir, _world);
    private string SessionStatePath => Path.Combine(_dir, "sessionstate.json");
    private string ZipName => WorldArchive.ZipName(_world);
    private string BackupZipName => WorldArchive.BackupZipName(_world);

    public SyncEngineTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new AppSettings { WorldsPathOverride = _dir, PlayerName = "me" };
        _settings.SelectedWorlds.Add(_world);
        _engine = new SyncEngine(_settings, _cloud, null, SessionStatePath);
        _engine.WorldStatusChanged += (_, s) => _statuses.Add(s);
    }

    private static bool GameIsRunning => ValheimProcess.IsRunning();

    /// <summary>Writes a complete local revision: db2/fwl2/chunks-index/ok plus the given
    /// chunk files (name -&gt; content).</summary>
    private void WriteLocal(int revision, string db2 = "db2", string fwl2 = "fwl2",
        IDictionary<string, string>? chunks = null)
    {
        Directory.CreateDirectory(WorldDir);
        File.WriteAllText(Path.Combine(WorldDir, $"_main.{revision}.db2"), db2);
        File.WriteAllText(Path.Combine(WorldDir, $"_main.{revision}.fwl2"), fwl2);
        File.WriteAllText(Path.Combine(WorldDir, $"_main.{revision}.chunks"), "idx");
        File.WriteAllText(Path.Combine(WorldDir, $"_main.{revision}.ok"), "1");
        foreach (var (name, content) in chunks ?? new Dictionary<string, string>())
            File.WriteAllText(Path.Combine(WorldDir, name), content);
    }

    private void AgeLocalFiles()
    {
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var f in Directory.EnumerateFiles(WorldDir))
            File.SetLastWriteTimeUtc(f, old);
    }

    private static IReadOnlyDictionary<string, string> OneRevision(string db2 = "db2", string fwl2 = "fwl2") =>
        new Dictionary<string, string>
        {
            ["_main.1.db2"] = db2,
            ["_main.1.fwl2"] = fwl2,
            ["_main.1.chunks"] = "idx",
            ["_main.1.ok"] = "1",
            ["1e_1e__1_1.chunk"] = "chunkA",
        };

    /// <summary>Builds the exact deterministic zip bytes WorldZip would build from these
    /// files, so a seeded "remote" can be made to byte-for-byte match (or intentionally
    /// differ from) a local save.</summary>
    private static async Task<byte[]> BuildZipBytesAsync(IReadOnlyDictionary<string, string> files)
    {
        var srcDir = Path.Combine(Path.GetTempPath(), "vszip-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDir);
        try
        {
            var paths = new List<string>();
            foreach (var (name, content) in files)
            {
                var path = Path.Combine(srcDir, name);
                await File.WriteAllTextAsync(path, content);
                paths.Add(path);
            }
            var zipPath = Path.Combine(Path.GetTempPath(), "vszip-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                await WorldZip.CreateAsync(paths, zipPath);
                return await File.ReadAllBytesAsync(zipPath);
            }
            finally { if (File.Exists(zipPath)) File.Delete(zipPath); }
        }
        finally { Directory.Delete(srcDir, recursive: true); }
    }

    private Task SeedRemoteWorldAsync(IReadOnlyDictionary<string, string> files, DateTimeOffset? modified = null) =>
        SeedRemoteWorldAsync(_world, files, modified);

    private async Task SeedRemoteWorldAsync(string world, IReadOnlyDictionary<string, string> files,
        DateTimeOffset? modified = null)
    {
        var bytes = await BuildZipBytesAsync(files);
        _cloud.SeedBytes(WorldArchive.ZipName(world), bytes, modified);
    }

    /// <summary>Unzips a seeded/uploaded remote archive back into a name→content map for
    /// assertions.</summary>
    private async Task<Dictionary<string, string>> ExtractRemoteZipAsync(string zipName)
    {
        var tmpZip = Path.Combine(Path.GetTempPath(), "vsx-" + Guid.NewGuid().ToString("N") + ".zip");
        var tmpDir = Path.Combine(Path.GetTempPath(), "vsx-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(tmpZip, _cloud.Files[zipName].Content);
            await WorldZip.ExtractAsync(tmpZip, tmpDir);
            var result = new Dictionary<string, string>();
            foreach (var f in Directory.EnumerateFiles(tmpDir))
                result[Path.GetFileName(f)] = await File.ReadAllTextAsync(f);
            return result;
        }
        finally
        {
            if (File.Exists(tmpZip)) File.Delete(tmpZip);
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ---- download paths --------------------------------------------------

    [Fact]
    public async Task FirstTimeDownload_WhenLocalMissingAndRemoteComplete()
    {
        if (GameIsRunning) return;
        await SeedRemoteWorldAsync(OneRevision());

        await _engine.SyncNowAsync();

        Assert.Equal("db2", File.ReadAllText(Path.Combine(WorldDir, "_main.1.db2")));
        Assert.Equal("chunkA", File.ReadAllText(Path.Combine(WorldDir, "1e_1e__1_1.chunk")));
        Assert.Equal(SyncStatus.InSync, _statuses.Last());
    }

    [Fact]
    public async Task StalledDownload_TimesOut_AndFreesTheGateForOtherWorlds()
    {
        if (GameIsRunning) return;
        // A world whose download never returns (dead connection, huge first-time
        // transfer) must not hang this world's sync forever — since every world shares
        // one sync gate, that would starve every other world's sync behind it too. Verify
        // it times out to Error instead, and that the gate is free again afterward.
        await SeedRemoteWorldAsync(OneRevision());
        _cloud.HangDownloads = true;
        await using var timeoutEngine = new SyncEngine(_settings, _cloud, null, SessionStatePath,
            syncTimeout: TimeSpan.FromMilliseconds(200));
        var statuses = new List<SyncStatus>();
        timeoutEngine.WorldStatusChanged += (_, s) => statuses.Add(s);

        await timeoutEngine.SyncNowAsync();

        Assert.Equal(SyncStatus.Error, statuses.Last());
        Assert.False(Directory.Exists(WorldDir)); // nothing partial landed locally

        // The gate must be free again — a second world's sync must not be blocked by it.
        _cloud.HangDownloads = false;
        var secondWorld = "VSTestW2" + Guid.NewGuid().ToString("N")[..8];
        _settings.SelectedWorlds.Add(secondWorld);
        await SeedRemoteWorldAsync(secondWorld, OneRevision());

        await timeoutEngine.SyncNowAsync();

        Assert.True(Directory.Exists(Path.Combine(_dir, secondWorld)));
        try { Directory.Delete(Path.Combine(_dir, secondWorld), recursive: true); } catch { }
        try
        {
            var localLow = ValheimSaveLocations.ResolveLocalLowWorldsFolder();
            if (localLow is not null)
                foreach (var suffix in new[] { "", ".synbak" })
                {
                    var p = Path.Combine(localLow, secondWorld + suffix);
                    if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
                }
        }
        catch { }
    }

    [Fact]
    public async Task Download_WhenOtherHoldsLock_AndKeepsSynbak()
    {
        if (GameIsRunning) return;
        WriteLocal(1, db2: "localdb2", chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "localChunk" });
        AgeLocalFiles();
        await SeedRemoteWorldAsync(new Dictionary<string, string>
        {
            ["_main.1.db2"] = "remotedb2",
            ["_main.1.fwl2"] = "remotefwl2",
            ["_main.1.chunks"] = "remoteidx",
            ["_main.1.ok"] = "1",
            ["1e_1e__1_1.chunk"] = "remoteChunk",
        });
        _cloud.Locks[_world] = new WorldLock("Bob", DateTimeOffset.UtcNow);

        await _engine.SyncNowAsync();

        Assert.Equal("remotedb2", File.ReadAllText(Path.Combine(WorldDir, "_main.1.db2")));
        // The replaced local save must survive as a whole-folder .synbak safety copy.
        Assert.Equal("localdb2", File.ReadAllText(Path.Combine(WorldDir + ".synbak", "_main.1.db2")));
    }

    [Fact]
    public async Task Download_ReplacesWholeFolder_AndKeepsSynbak()
    {
        if (GameIsRunning) return;
        WriteLocal(1, chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "localChunk" });
        AgeLocalFiles();
        // Remote is a newer revision with a different chunk filename (zone was edited).
        await SeedRemoteWorldAsync(new Dictionary<string, string>
        {
            ["_main.2.db2"] = "remoteDb2",
            ["_main.2.fwl2"] = "remoteFwl2",
            ["_main.2.chunks"] = "remoteIdx",
            ["_main.2.ok"] = "1",
            ["1e_1e__1_2.chunk"] = "remoteChunk",
        });

        await _engine.SyncNowAsync();

        Assert.Equal("remoteDb2", File.ReadAllText(Path.Combine(WorldDir, "_main.2.db2")));
        // The old revision's files must not linger next to the new one.
        Assert.False(File.Exists(Path.Combine(WorldDir, "_main.1.db2")));
        Assert.False(File.Exists(Path.Combine(WorldDir, "1e_1e__1_1.chunk")));
        // The whole replaced folder survives as a single .synbak safety copy.
        var backupDb2 = Path.Combine(WorldDir + ".synbak", "_main.1.db2");
        Assert.Equal("db2", File.ReadAllText(backupDb2));
    }

    [Fact]
    public async Task CorruptDownload_Aborts_LeavesLocalUntouched()
    {
        if (GameIsRunning) return;
        await SeedRemoteWorldAsync(OneRevision());
        _cloud.CorruptDownloads = true;

        await _engine.SyncNowAsync();

        Assert.Equal(SyncStatus.Error, _statuses.Last());
        Assert.False(Directory.Exists(WorldDir));
    }

    [Fact]
    public async Task SyncFailure_UploadsDetailedErrorLog_ToTheSharedFolder()
    {
        if (GameIsRunning) return;
        // A real failure (here: hash verification failing on a corrupted download) must
        // leave a diagnosable trail in the shared Drive folder, not just this machine's
        // local log — so anyone in the group can see what went wrong.
        await SeedRemoteWorldAsync(OneRevision());
        _cloud.CorruptDownloads = true;

        await _engine.SyncNowAsync();

        var errorLogName = _cloud.Files.Keys.SingleOrDefault(n =>
            n.StartsWith("errorlog_", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(errorLogName);
        Assert.Contains($"errorlog_{_world}_me_", errorLogName, StringComparison.OrdinalIgnoreCase);

        var content = _cloud.ContentOf(errorLogName!);
        Assert.Contains(_world, content);
        Assert.Contains("me", content); // player name
        Assert.Contains("hash verification", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StalledDownload_UploadsErrorLog_WithTimeoutDetails()
    {
        if (GameIsRunning) return;
        await SeedRemoteWorldAsync(OneRevision());
        _cloud.HangDownloads = true;
        await using var timeoutEngine = new SyncEngine(_settings, _cloud, null, SessionStatePath,
            syncTimeout: TimeSpan.FromMilliseconds(200));

        await timeoutEngine.SyncNowAsync();

        var errorLogName = _cloud.Files.Keys.SingleOrDefault(n =>
            n.StartsWith("errorlog_", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(errorLogName);
        Assert.Contains("timed out", _cloud.ContentOf(errorLogName!), StringComparison.OrdinalIgnoreCase);
    }

    // ---- upload paths ------------------------------------------------------

    [Fact]
    public async Task Upload_WhenRemoteMissing_UploadsWholeWorldAsOneZip()
    {
        if (GameIsRunning) return;
        WriteLocal(1, chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "chunkA" });

        await _engine.SyncNowAsync();

        // The whole point of zipping: one Drive write moves the entire world, no matter
        // how many local files make it up.
        Assert.Equal(new[] { $"upload:{ZipName}" }, _cloud.Calls);
        var remoteFiles = await ExtractRemoteZipAsync(ZipName);
        Assert.Equal("db2", remoteFiles["_main.1.db2"]);
        Assert.Equal("chunkA", remoteFiles["1e_1e__1_1.chunk"]);
        Assert.Equal(SyncStatus.InSync, _statuses.Last());
    }

    [Fact]
    public async Task Upload_WhenIHoldLock_EvenIfRemoteIsNewer()
    {
        if (GameIsRunning) return;
        WriteLocal(1, db2: "mydb2", chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "myChunk" });
        AgeLocalFiles(); // remote looks newer by timestamp…
        await SeedRemoteWorldAsync(new Dictionary<string, string>
        {
            ["_main.1.db2"] = "remotedb2",
            ["_main.1.fwl2"] = "remotefwl2",
            ["_main.1.chunks"] = "remoteidx",
            ["_main.1.ok"] = "1",
            ["1e_1e__1_1.chunk"] = "remoteChunk",
        });
        _cloud.Locks[_world] = new WorldLock("me", DateTimeOffset.UtcNow); // …but the lock is mine

        await _engine.SyncNowAsync();

        var remoteFiles = await ExtractRemoteZipAsync(ZipName);
        Assert.Equal("mydb2", remoteFiles["_main.1.db2"]);
    }

    [Fact]
    public async Task Upload_TransfersWholeZip_EvenWhenOnlyOneChunkChanged()
    {
        if (GameIsRunning) return;
        // Whole-world zipping trades the old per-chunk "skip unchanged" optimization for
        // far fewer API calls: an upload always re-sends the entire archive — unchanged
        // chunks included — in exactly one call.
        await SeedRemoteWorldAsync(new Dictionary<string, string>
        {
            ["_main.1.db2"] = "db2v1",
            ["_main.1.fwl2"] = "fwl2v1",
            ["_main.1.chunks"] = "idxv1",
            ["_main.1.ok"] = "1",
            ["1e_1e__1_1.chunk"] = "unchanged",
        }, DateTimeOffset.UtcNow.AddDays(-1));

        WriteLocal(2, db2: "db2v2", fwl2: "fwl2v2",
            chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "unchanged" });

        await _engine.SyncNowAsync();

        // One backup copy (first upload of the session) plus exactly one upload call —
        // still far fewer API calls than one per file.
        Assert.Equal(new[] { $"copy:{ZipName}->{BackupZipName}", $"upload:{ZipName}" }, _cloud.Calls);
        var remoteFiles = await ExtractRemoteZipAsync(ZipName);
        Assert.Equal("unchanged", remoteFiles["1e_1e__1_1.chunk"]); // carried along despite not changing
        Assert.Equal("db2v2", remoteFiles["_main.2.db2"]);
        Assert.False(remoteFiles.ContainsKey("_main.1.db2")); // superseded revision is gone
    }

    [Fact]
    public async Task OpportunisticUpload_ReleasesLockAfterwards()
    {
        if (GameIsRunning) return;
        // No one holds the lock and nothing is remote yet — this is the "opportunistic"
        // background-upload branch (not an active play session), which must still take
        // the world lock for the duration of the upload so it can't race another idle
        // machine doing the same thing. It must let go afterwards, or a later Play would
        // be wrongly blocked.
        WriteLocal(1, chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "chunkA" });

        await _engine.SyncNowAsync();

        Assert.True(_cloud.Files.ContainsKey(ZipName));
        Assert.False(_cloud.Locks.ContainsKey(_world));
        Assert.Equal(SyncStatus.InSync, _statuses.Last());
    }

    [Fact]
    public async Task OpportunisticUpload_BacksOffIfAnotherMachineGrabsTheLockMidRace()
    {
        if (GameIsRunning) return;
        // Two idle clients can both observe "no one holds the lock" via GetLockAsync at
        // the same moment and both decide to upload. Simulate the race: right as this
        // engine calls TryAcquireLockAsync, another machine's lock appears underneath it.
        // It must back off untouched rather than overwrite the other machine's upload.
        WriteLocal(1, chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "chunkA" });
        await SeedRemoteWorldAsync(new Dictionary<string, string>
        {
            ["_main.9.db2"] = "othermachinedb2",
            ["_main.9.fwl2"] = "othermachinefwl2",
            ["_main.9.chunks"] = "otheridx",
            ["_main.9.ok"] = "1",
        }, DateTimeOffset.UtcNow.AddMinutes(-10)); // older than local, so this client would otherwise upload
        _cloud.OnTryAcquireLock = () => _cloud.Locks[_world] = new WorldLock("Bob", DateTimeOffset.UtcNow);

        await _engine.SyncNowAsync();

        // The other machine's just-uploaded revision must survive untouched.
        var remoteFiles = await ExtractRemoteZipAsync(ZipName);
        Assert.Equal("othermachinedb2", remoteFiles["_main.9.db2"]);
        Assert.Equal(SyncStatus.LockedByOther, _statuses.Last());
    }

    [Fact]
    public async Task Upload_WithExistingRemote_BacksUpBeforeOverwriting()
    {
        if (GameIsRunning) return;
        WriteLocal(2, db2: "newdb2");
        await SeedRemoteWorldAsync(new Dictionary<string, string>
        {
            ["_main.1.db2"] = "olddb2",
            ["_main.1.fwl2"] = "oldfwl2",
            ["_main.1.chunks"] = "oldidx",
            ["_main.1.ok"] = "1",
        }, DateTimeOffset.UtcNow.AddDays(-1));

        await _engine.SyncNowAsync();

        var backupFiles = await ExtractRemoteZipAsync(BackupZipName);
        Assert.Equal("olddb2", backupFiles["_main.1.db2"]);
        var currentFiles = await ExtractRemoteZipAsync(ZipName);
        Assert.Equal("newdb2", currentFiles["_main.2.db2"]);
        // Backup strictly precedes the overwrite.
        var backupIndex = _cloud.Calls.ToList().FindLastIndex(c => c.StartsWith("copy:"));
        var uploadIndex = _cloud.Calls.ToList().FindIndex(c => c.StartsWith("upload:"));
        Assert.True(backupIndex < uploadIndex);
    }

    [Fact]
    public async Task Backup_OnlyOncePerSession()
    {
        if (GameIsRunning) return;
        WriteLocal(2, db2: "v2");
        await SeedRemoteWorldAsync(new Dictionary<string, string>
        {
            ["_main.1.db2"] = "v1",
            ["_main.1.fwl2"] = "fwl1",
            ["_main.1.chunks"] = "idx1",
            ["_main.1.ok"] = "1",
        }, DateTimeOffset.UtcNow.AddDays(-1));

        await _engine.SyncNowAsync();
        var db2Path = Path.Combine(WorldDir, "_main.2.db2");
        File.WriteAllText(db2Path, "v3"); // played some more…
        // NTFS can cache a file's last-write time and occasionally serve it stale to an
        // immediate re-read under I/O load, which would make this write look older than
        // the remote's just-set upload timestamp and flip the next sync to a (wrong)
        // download. Nudge it forward so the ordering is unambiguous either way.
        File.SetLastWriteTimeUtc(db2Path, DateTime.UtcNow.AddSeconds(2));
        await _engine.SyncNowAsync();     // must NOT back up again

        var currentFiles = await ExtractRemoteZipAsync(ZipName);
        Assert.Equal("v3", currentFiles["_main.2.db2"]);
        var backupFiles = await ExtractRemoteZipAsync(BackupZipName);
        Assert.Equal("v1", backupFiles["_main.1.db2"]);
    }

    [Fact]
    public async Task Backup_NotRepeated_AfterAppRestart_MidSession()
    {
        if (GameIsRunning) return;
        WriteLocal(2, db2: "v2");
        await SeedRemoteWorldAsync(new Dictionary<string, string>
        {
            ["_main.1.db2"] = "v1",
            ["_main.1.fwl2"] = "fwl1",
            ["_main.1.chunks"] = "idx1",
            ["_main.1.ok"] = "1",
        }, DateTimeOffset.UtcNow.AddDays(-1));

        await _engine.SyncNowAsync(); // backs up the pre-session v1
        Assert.Equal("v1", (await ExtractRemoteZipAsync(BackupZipName))["_main.1.db2"]);

        // Simulate the app restarting mid-session: a brand-new engine, same persisted
        // session state file. Before persistence this re-backed-up and destroyed the
        // pre-session snapshot with mid-session state.
        await _engine.DisposeAsync();
        var engine2 = new SyncEngine(_settings, _cloud, null, SessionStatePath);
        var db2Path = Path.Combine(WorldDir, "_main.2.db2");
        File.WriteAllText(db2Path, "v3");
        File.SetLastWriteTimeUtc(db2Path, DateTime.UtcNow.AddSeconds(2)); // see Backup_OnlyOncePerSession
        await engine2.SyncNowAsync();
        await engine2.DisposeAsync();

        Assert.Equal("v3", (await ExtractRemoteZipAsync(ZipName))["_main.2.db2"]);
        Assert.Equal("v1", (await ExtractRemoteZipAsync(BackupZipName))["_main.1.db2"]); // snapshot survived the restart
    }

    // ---- no-op path ---------------------------------------------------------

    [Fact]
    public async Task NoTransfer_WhenLocalZipMatchesRemote()
    {
        if (GameIsRunning) return;
        // Same file names and content locally and remotely must hash identically — the
        // whole basis for skipping a transfer depends on WorldZip being deterministic.
        WriteLocal(1, chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "chunkA" });
        await SeedRemoteWorldAsync(OneRevision());

        await _engine.SyncNowAsync();

        Assert.Empty(_cloud.Calls);
        Assert.Equal(SyncStatus.InSync, _statuses.Last());
    }

    [Fact]
    public async Task Fwl2OnlyChange_TriggersUpload()
    {
        if (GameIsRunning) return;
        // Direction is decided by comparing the whole archive's hash, so a metadata-only
        // change (fwl2 diverges while db2 matches) is not ignored.
        WriteLocal(1, db2: "same", fwl2: "DIFFERENT-local-fwl2",
            chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "chunkA" });
        await SeedRemoteWorldAsync(new Dictionary<string, string>
        {
            ["_main.1.db2"] = "same",
            ["_main.1.fwl2"] = "remote-fwl2",
            ["_main.1.chunks"] = "idx",
            ["_main.1.ok"] = "1",
            ["1e_1e__1_1.chunk"] = "chunkA",
        }, DateTimeOffset.UtcNow.AddDays(-1)); // older than local, so local wins deterministically

        await _engine.SyncNowAsync();

        var remoteFiles = await ExtractRemoteZipAsync(ZipName);
        Assert.Equal("DIFFERENT-local-fwl2", remoteFiles["_main.1.fwl2"]);
    }

    [Fact]
    public async Task UnselectedWorld_IsNeverTouched()
    {
        if (GameIsRunning) return;
        _settings.SelectedWorlds.Clear();
        WriteLocal(1, chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "chunkA" });
        _cloud.Seed(ZipName, "remotedb2");

        await _engine.SyncNowAsync();

        Assert.Empty(_cloud.Calls);
        Assert.Empty(_statuses);
    }

    // ---- LocalLow mirroring -------------------------------------------------

    [Fact]
    public async Task Download_MirrorsWholeFolderIntoLocalLow_ForInGameVisibility()
    {
        if (GameIsRunning) return;
        await SeedRemoteWorldAsync(OneRevision());

        await _engine.SyncNowAsync();

        var localLow = ValheimSaveLocations.ResolveLocalLowWorldsFolder();
        if (localLow is null) return; // this machine has no LocalLow folder — nothing to check
        var mirrored = Path.Combine(localLow, _world);
        Assert.True(Directory.Exists(mirrored));
        Assert.Equal("db2", File.ReadAllText(Path.Combine(mirrored, "_main.1.db2")));
        Assert.Equal("chunkA", File.ReadAllText(Path.Combine(mirrored, "1e_1e__1_1.chunk")));
    }

    // -----------------------------------------------------------------------

    public void Dispose()
    {
        _engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_dir, recursive: true); } catch { }

        // Downloads best-effort mirror the whole world folder into the machine's real
        // Valheim LocalLow folder; remove this test's uniquely-named world if it landed there.
        try
        {
            var localLow = ValheimSaveLocations.ResolveLocalLowWorldsFolder();
            if (localLow is not null)
                foreach (var suffix in new[] { "", ".synbak" })
                {
                    var p = Path.Combine(localLow, _world + suffix);
                    if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
                }
        }
        catch { }
    }
}
