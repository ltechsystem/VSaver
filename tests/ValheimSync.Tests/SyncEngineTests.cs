using ValheimSync.Core;
using ValheimSync.Core.Models;
using ValheimSync.Core.Sync;
using ValheimSync.Core.Util;
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
    private string Prefix => _world + "/";
    private string ManifestName => CommitMarker.Name(_world);

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

    /// <summary>Seeds a complete, internally consistent remote world.</summary>
    private void SeedRemoteWorld(IReadOnlyDictionary<string, string> files,
        DateTimeOffset? modified = null, bool withMarker = true)
    {
        foreach (var (name, content) in files)
            _cloud.Seed(Prefix + name, content, modified);
        if (withMarker)
        {
            var entries = files.Select(kv => (Prefix + kv.Key, Hashing.Md5Text(kv.Value)));
            _cloud.Seed(ManifestName, CommitMarker.Content(entries), modified);
        }
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

    // ---- download paths --------------------------------------------------

    [Fact]
    public async Task FirstTimeDownload_WhenLocalMissingAndRemoteComplete()
    {
        if (GameIsRunning) return;
        SeedRemoteWorld(OneRevision());

        await _engine.SyncNowAsync();

        Assert.Equal("db2", File.ReadAllText(Path.Combine(WorldDir, "_main.1.db2")));
        Assert.Equal("chunkA", File.ReadAllText(Path.Combine(WorldDir, "1e_1e__1_1.chunk")));
        Assert.Equal(SyncStatus.InSync, _statuses.Last());
    }

    [Fact]
    public async Task TornRemote_MissingMarker_IsNotDownloaded()
    {
        if (GameIsRunning) return;
        // There's no pre-marker era for this format — files with no marker at all are
        // always an interrupted upload, never trusted.
        SeedRemoteWorld(OneRevision(), withMarker: false);

        await _engine.SyncNowAsync();

        Assert.False(Directory.Exists(WorldDir));
        Assert.Equal(SyncStatus.Error, _statuses.Last());
        Assert.DoesNotContain(_cloud.Calls, c => c.StartsWith("download:"));
    }

    [Fact]
    public async Task TornRemote_MarkerMismatch_IsNotDownloaded()
    {
        if (GameIsRunning) return;
        var files = OneRevision();
        foreach (var (name, content) in files) _cloud.Seed(Prefix + name, content);
        // Marker describes a db2 that doesn't match what's actually up there.
        _cloud.Seed(ManifestName, CommitMarker.Content(new[]
        {
            (Prefix + "_main.1.db2", Hashing.Md5Text("some-other-content")),
            (Prefix + "_main.1.fwl2", Hashing.Md5Text(files["_main.1.fwl2"])),
            (Prefix + "_main.1.chunks", Hashing.Md5Text(files["_main.1.chunks"])),
            (Prefix + "_main.1.ok", Hashing.Md5Text(files["_main.1.ok"])),
            (Prefix + "1e_1e__1_1.chunk", Hashing.Md5Text(files["1e_1e__1_1.chunk"])),
        }));

        await _engine.SyncNowAsync();

        Assert.False(Directory.Exists(WorldDir));
        Assert.Equal(SyncStatus.Error, _statuses.Last());
    }

    [Fact]
    public async Task PartialRemoteFiles_WithNoMarker_IsTreatedAsTorn()
    {
        if (GameIsRunning) return;
        // Any file under this world's prefix makes it a download candidate — and with no
        // marker at all, that's unambiguously an interrupted upload.
        _cloud.Seed(Prefix + "_main.1.db2", "halfuploaded");

        await _engine.SyncNowAsync();

        Assert.False(Directory.Exists(WorldDir));
        Assert.Equal(SyncStatus.Error, _statuses.Last());
        Assert.DoesNotContain(_cloud.Calls, c => c.StartsWith("download:"));
    }

    [Fact]
    public async Task Download_WhenOtherHoldsLock_AndKeepsSynbak()
    {
        if (GameIsRunning) return;
        WriteLocal(1, db2: "localdb2", chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "localChunk" });
        AgeLocalFiles();
        SeedRemoteWorld(new Dictionary<string, string>
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
    public async Task TornRemote_BlocksDivergenceDownload_Too()
    {
        if (GameIsRunning) return;
        WriteLocal(1, db2: "localdb2", chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "localChunk" });
        AgeLocalFiles();
        // Remote is newer but its marker certifies a different db2 — refuse to download.
        var files = new Dictionary<string, string>
        {
            ["_main.1.db2"] = "remotedb2",
            ["_main.1.fwl2"] = "remotefwl2",
            ["_main.1.chunks"] = "remoteidx",
            ["_main.1.ok"] = "1",
            ["1e_1e__1_1.chunk"] = "remoteChunk",
        };
        foreach (var (name, content) in files) _cloud.Seed(Prefix + name, content);
        _cloud.Seed(ManifestName, CommitMarker.Content(new[]
        {
            (Prefix + "_main.1.db2", Hashing.Md5Text("some-other-content")),
            (Prefix + "_main.1.fwl2", Hashing.Md5Text(files["_main.1.fwl2"])),
            (Prefix + "_main.1.chunks", Hashing.Md5Text(files["_main.1.chunks"])),
            (Prefix + "_main.1.ok", Hashing.Md5Text(files["_main.1.ok"])),
            (Prefix + "1e_1e__1_1.chunk", Hashing.Md5Text(files["1e_1e__1_1.chunk"])),
        }));

        await _engine.SyncNowAsync();

        Assert.Equal("localdb2", File.ReadAllText(Path.Combine(WorldDir, "_main.1.db2"))); // untouched
        Assert.Equal(SyncStatus.Error, _statuses.Last());
    }

    [Fact]
    public async Task Download_ReplacesWholeFolder_AndKeepsSynbak()
    {
        if (GameIsRunning) return;
        WriteLocal(1, chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "localChunk" });
        AgeLocalFiles();
        // Remote is a newer revision with a different chunk filename (zone was edited).
        SeedRemoteWorld(new Dictionary<string, string>
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
        SeedRemoteWorld(OneRevision());
        _cloud.CorruptDownloads = true;

        await _engine.SyncNowAsync();

        Assert.Equal(SyncStatus.Error, _statuses.Last());
        Assert.False(Directory.Exists(WorldDir));
    }

    // ---- upload paths ------------------------------------------------------

    [Fact]
    public async Task Upload_WhenRemoteMissing_UploadsEveryFile_MarkerLast()
    {
        if (GameIsRunning) return;
        WriteLocal(1, chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "chunkA" });

        await _engine.SyncNowAsync();

        Assert.Equal("db2", _cloud.ContentOf(Prefix + "_main.1.db2"));
        Assert.Equal("chunkA", _cloud.ContentOf(Prefix + "1e_1e__1_1.chunk"));
        Assert.Equal(ManifestName, _cloud.Calls.Last().Split(':')[1]); // marker uploaded last
        Assert.DoesNotContain(_cloud.Calls, c => c.StartsWith("copy:")); // nothing remote to back up yet
        Assert.Equal(SyncStatus.InSync, _statuses.Last());
    }

    [Fact]
    public async Task Upload_WhenIHoldLock_EvenIfRemoteIsNewer()
    {
        if (GameIsRunning) return;
        WriteLocal(1, db2: "mydb2", chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "myChunk" });
        AgeLocalFiles(); // remote looks newer by timestamp…
        SeedRemoteWorld(new Dictionary<string, string>
        {
            ["_main.1.db2"] = "remotedb2",
            ["_main.1.fwl2"] = "remotefwl2",
            ["_main.1.chunks"] = "remoteidx",
            ["_main.1.ok"] = "1",
            ["1e_1e__1_1.chunk"] = "remoteChunk",
        });
        _cloud.Locks[_world] = new WorldLock("me", DateTimeOffset.UtcNow); // …but the lock is mine

        await _engine.SyncNowAsync();

        Assert.Equal("mydb2", _cloud.ContentOf(Prefix + "_main.1.db2"));
    }

    [Fact]
    public async Task Upload_SkipsUnchangedChunks_ButUploadsChangedOnes()
    {
        if (GameIsRunning) return;
        // Remote already has revision 1 with an untouched chunk; local has moved on to
        // revision 2 where only that chunk's zone changed (new filename, new content) —
        // the world's other file (a second, unmodified chunk) keeps its exact name.
        SeedRemoteWorld(new Dictionary<string, string>
        {
            ["_main.1.db2"] = "db2v1",
            ["_main.1.fwl2"] = "fwl2v1",
            ["_main.1.chunks"] = "idxv1",
            ["_main.1.ok"] = "1",
            ["1e_1e__1_1.chunk"] = "unchanged",
            ["20_20__1_1.chunk"] = "willChange",
        }, DateTimeOffset.UtcNow.AddDays(-1));

        Directory.CreateDirectory(WorldDir);
        File.WriteAllText(Path.Combine(WorldDir, "_main.2.db2"), "db2v2");
        File.WriteAllText(Path.Combine(WorldDir, "_main.2.fwl2"), "fwl2v2");
        File.WriteAllText(Path.Combine(WorldDir, "_main.2.chunks"), "idxv2");
        File.WriteAllText(Path.Combine(WorldDir, "_main.2.ok"), "1");
        File.WriteAllText(Path.Combine(WorldDir, "1e_1e__1_1.chunk"), "unchanged"); // same content, same name
        File.WriteAllText(Path.Combine(WorldDir, "20_20__1_2.chunk"), "changed"); // new name+content

        await _engine.SyncNowAsync();

        Assert.DoesNotContain(_cloud.Calls, c => c == $"upload:{Prefix}1e_1e__1_1.chunk");
        Assert.Contains(_cloud.Calls, c => c == $"upload:{Prefix}20_20__1_2.chunk");
        // Superseded revision-1 main files and the old chunk name are gone from the remote.
        Assert.False(_cloud.Files.ContainsKey(Prefix + "_main.1.db2"));
        Assert.False(_cloud.Files.ContainsKey(Prefix + "20_20__1_1.chunk"));
        Assert.True(_cloud.Files.ContainsKey(Prefix + "1e_1e__1_1.chunk"));
    }

    [Fact]
    public async Task Upload_WithExistingRemote_BacksUpBeforeOverwriting()
    {
        if (GameIsRunning) return;
        WriteLocal(2, db2: "newdb2");
        SeedRemoteWorld(new Dictionary<string, string>
        {
            ["_main.1.db2"] = "olddb2",
            ["_main.1.fwl2"] = "oldfwl2",
            ["_main.1.chunks"] = "oldidx",
            ["_main.1.ok"] = "1",
        }, DateTimeOffset.UtcNow.AddDays(-1));

        await _engine.SyncNowAsync();

        Assert.Equal("olddb2", _cloud.ContentOf(_world + ".bak/_main.1.db2"));
        Assert.Equal("newdb2", _cloud.ContentOf(Prefix + "_main.2.db2"));
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
        SeedRemoteWorld(new Dictionary<string, string>
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

        Assert.Equal("v3", _cloud.ContentOf(Prefix + "_main.2.db2"));
        Assert.Equal("v1", _cloud.ContentOf(_world + ".bak/_main.1.db2"));
    }

    [Fact]
    public async Task Backup_NotRepeated_AfterAppRestart_MidSession()
    {
        if (GameIsRunning) return;
        WriteLocal(2, db2: "v2");
        SeedRemoteWorld(new Dictionary<string, string>
        {
            ["_main.1.db2"] = "v1",
            ["_main.1.fwl2"] = "fwl1",
            ["_main.1.chunks"] = "idx1",
            ["_main.1.ok"] = "1",
        }, DateTimeOffset.UtcNow.AddDays(-1));

        await _engine.SyncNowAsync(); // backs up the pre-session v1
        Assert.Equal("v1", _cloud.ContentOf(_world + ".bak/_main.1.db2"));

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

        Assert.Equal("v3", _cloud.ContentOf(Prefix + "_main.2.db2"));
        Assert.Equal("v1", _cloud.ContentOf(_world + ".bak/_main.1.db2")); // snapshot survived the restart
    }

    // ---- no-op / repair paths -----------------------------------------------

    [Fact]
    public async Task NoTransfer_WhenFileSetMatches()
    {
        if (GameIsRunning) return;
        WriteLocal(1, chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "chunkA" });
        SeedRemoteWorld(OneRevision());

        await _engine.SyncNowAsync();

        Assert.Empty(_cloud.Calls);
        Assert.Equal(SyncStatus.InSync, _statuses.Last());
    }

    [Fact]
    public async Task Fwl2OnlyChange_TriggersUpload()
    {
        if (GameIsRunning) return;
        // Direction is decided by comparing the whole file set, so a metadata-only
        // change (fwl2 diverges while db2 matches) is not ignored.
        WriteLocal(1, db2: "same", fwl2: "DIFFERENT-local-fwl2",
            chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "chunkA" });
        SeedRemoteWorld(new Dictionary<string, string>
        {
            ["_main.1.db2"] = "same",
            ["_main.1.fwl2"] = "remote-fwl2",
            ["_main.1.chunks"] = "idx",
            ["_main.1.ok"] = "1",
            ["1e_1e__1_1.chunk"] = "chunkA",
        }, DateTimeOffset.UtcNow.AddDays(-1)); // older than local, so local wins deterministically

        await _engine.SyncNowAsync();

        Assert.Equal("DIFFERENT-local-fwl2", _cloud.ContentOf(Prefix + "_main.1.fwl2"));
    }

    [Fact]
    public async Task MissingRemoteFile_IsCompleted_ViaNormalUpload_NotRepairPath()
    {
        if (GameIsRunning) return;
        // A file set with anything missing simply isn't a "match" — it's resolved
        // through the normal upload path, which fills the gap as a side effect (there's
        // no separate partial-completion / repair path for a missing file).
        WriteLocal(1, db2: "same", chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "chunkA" });
        // The db2 made it up but the rest of the upload died, and it predates local.
        _cloud.Seed(Prefix + "_main.1.db2", "same", DateTimeOffset.UtcNow.AddDays(-1));

        await _engine.SyncNowAsync();

        Assert.Equal("fwl2", _cloud.ContentOf(Prefix + "_main.1.fwl2"));
        Assert.Equal(ManifestName, _cloud.Calls.Last().Split(':')[1]); // marker uploaded last, as always
    }

    [Fact]
    public async Task UnrelatedRemoteFiles_AreInert()
    {
        if (GameIsRunning) return;
        WriteLocal(1, chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "chunkA" });
        SeedRemoteWorld(OneRevision());
        // Another world's files, and this world's lock file — neither should be mistaken
        // for part of this world's manifest.
        _cloud.Seed("SomeOtherWorld/_main.1.db2", "unrelated");
        _cloud.Seed(_world + ".lock", "{}");

        await _engine.SyncNowAsync();

        Assert.Empty(_cloud.Calls); // nothing mistaken for this world's files
        Assert.Equal(SyncStatus.InSync, _statuses.Last());
    }

    [Fact]
    public async Task StaleMarker_IsRepaired_WhenLocalMatches()
    {
        if (GameIsRunning) return;
        WriteLocal(1, chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "chunkA" });
        var files = OneRevision();
        foreach (var (name, content) in files) _cloud.Seed(Prefix + name, content);
        _cloud.Seed(ManifestName, "garbage-from-a-failed-upload");

        await _engine.SyncNowAsync();

        Assert.Equal(new[] { $"upload:{ManifestName}" }, _cloud.Calls);
        Assert.Equal(SyncStatus.InSync, _statuses.Last());
    }

    [Fact]
    public async Task NoRepair_WhileSomeoneElseHoldsTheLock()
    {
        if (GameIsRunning) return;
        WriteLocal(1, chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "chunkA" });
        SeedRemoteWorld(OneRevision(), withMarker: false);
        _cloud.Locks[_world] = new WorldLock("Bob", DateTimeOffset.UtcNow);

        await _engine.SyncNowAsync();

        Assert.Empty(_cloud.Calls);
        Assert.Equal(SyncStatus.LockedByOther, _statuses.Last());
    }

    [Fact]
    public async Task UnselectedWorld_IsNeverTouched()
    {
        if (GameIsRunning) return;
        _settings.SelectedWorlds.Clear();
        WriteLocal(1, chunks: new Dictionary<string, string> { ["1e_1e__1_1.chunk"] = "chunkA" });
        _cloud.Seed(Prefix + "_main.1.db2", "remotedb2");

        await _engine.SyncNowAsync();

        Assert.Empty(_cloud.Calls);
        Assert.Empty(_statuses);
    }

    // ---- LocalLow mirroring -------------------------------------------------

    [Fact]
    public async Task Download_MirrorsWholeFolderIntoLocalLow_ForInGameVisibility()
    {
        if (GameIsRunning) return;
        SeedRemoteWorld(OneRevision());

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
