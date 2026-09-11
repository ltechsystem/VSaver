using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ValheimSync.Core.Models;
using ValheimSync.Core.Storage;
using ValheimSync.Core.Util;

namespace ValheimSync.Core.Sync;

/// <summary>
/// Coordinates the whole sync lifecycle for the selected worlds:
///
///   * FileSystemWatcher (debounced) → upload after Valheim finishes a save
///   * Periodic poll (default 15 min) → download remote changes / fallback upload check
///   * MD5 comparison against Drive's md5Checksum → no duplicate transfers, ever
///   * A world's "manifest.commit" marker is uploaded LAST and certifies that the whole
///     file set currently under its "&lt;world&gt;/" prefix was uploaded together
///     (see CommitMarker)
///   * Downloads go to a temp file, are hash-verified, then moved atomically
///   * Nothing is downloaded while Valheim is running
///
/// Only the modern chunked-folder save format (see <see cref="WorldSave"/>) is
/// supported — Valheim's old flat ".db"/".fwl" pair is not recognized.
/// </summary>
public sealed class SyncEngine : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly ICloudStorageProvider _cloud;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _syncGate = new(1, 1); // one sync at a time

    // A stalled Drive call (dead connection, huge first-time download, an API request
    // that never completes) must not hang a world's sync forever — since every sync
    // shares _syncGate, an unbounded hang for one world would starve every other
    // world's sync behind it too. This bounds a single world's sync pass so it always
    // eventually fails and frees the gate for a retry on the next poll instead.
    // Overridable only so tests can verify the timeout path without waiting 15 minutes.
    private readonly TimeSpan _syncTimeout;
    private DebouncedWorldWatcher? _watcher;
    private PeriodicTimer? _timer;
    private PeriodicTimer? _inGameTimer;
    private CancellationTokenSource? _cts;

    // Backup-once-per-session bookkeeping. A "session" is one run of Valheim; the state
    // records which worlds were already backed up this session and is cleared when a new
    // session starts. Persisted to disk so an app restart mid-session doesn't re-backup
    // and overwrite the pre-session snapshot with mid-session state. All access is
    // serialized under _syncGate.
    private readonly BackupSessionState _session;
    private readonly string _sessionStatePath;

    public event Action<string, SyncStatus>? WorldStatusChanged;
    public event Action<string>? Log;

    public SyncEngine(AppSettings settings, ICloudStorageProvider cloud, ILogger? log = null,
        string? sessionStatePath = null, TimeSpan? syncTimeout = null)
    {
        _settings = settings;
        _cloud = cloud;
        _log = log ?? NullLogger.Instance;
        _syncTimeout = syncTimeout ?? TimeSpan.FromMinutes(15);
        _sessionStatePath = sessionStatePath ?? BackupSessionState.DefaultPath;
        _session = BackupSessionState.Load(_sessionStatePath);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _cloud.InitializeAsync(ct);
        Info($"Connected to {_cloud.ProviderName}.");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (Directory.Exists(_settings.WorldsPath))
        {
            _watcher = new DebouncedWorldWatcher(
                _settings.WorldsPath, TimeSpan.FromSeconds(_settings.DebounceSeconds));
            _watcher.WorldChanged += world =>
                _ = SafeSyncWorldAsync(world, _cts.Token);
        }

        // Fallback / download poll.
        _timer = new PeriodicTimer(TimeSpan.FromMinutes(_settings.PollIntervalMinutes));
        _ = Task.Run(async () =>
        {
            // Initial pass on startup, then on every tick.
            await SafeSyncAllAsync(_cts.Token);
            while (await _timer.WaitForNextTickAsync(_cts.Token))
                await SafeSyncAllAsync(_cts.Token);
        }, _cts.Token);

        // In-game auto-save uploader: while Valheim is open, push the in-progress save
        // on this interval — but only if it changed and has settled (never mid-write).
        _inGameTimer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _settings.InGameUploadMinutes)));
        _ = Task.Run(async () =>
        {
            while (await _inGameTimer.WaitForNextTickAsync(_cts.Token))
                if (ValheimProcess.IsRunning())
                    await SafeSyncAllAsync(_cts.Token, allowUploadWhileRunning: true);
        }, _cts.Token);
    }

    public Task SyncNowAsync() => SafeSyncAllAsync(_cts?.Token ?? default);

    // ---------------------------------------------------------------------

    private async Task SafeSyncAllAsync(CancellationToken ct, bool allowUploadWhileRunning = false)
    {
        foreach (var world in _settings.SelectedWorlds.ToArray())
            await SafeSyncWorldAsync(world, ct, allowUploadWhileRunning);
    }

    private async Task SafeSyncWorldAsync(string worldName, CancellationToken ct,
        bool allowUploadWhileRunning = false)
    {
        if (!_settings.SelectedWorlds.Contains(worldName)) return;

        await _syncGate.WaitAsync(ct);
        try
        {
            UpdateSessionState();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_syncTimeout);
            await SyncWorldAsync(worldName, timeoutCts.Token, allowUploadWhileRunning);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The linked token fired from CancelAfter, not from the caller — a stalled
            // transfer, not a real shutdown/cancellation.
            _log.LogError("Sync timed out for {World}", worldName);
            Info($"[{worldName}] Sync timed out after {_syncTimeout.TotalMinutes:F0} minutes " +
                 "(a file transfer likely stalled) — will retry on the next sync.");
            WorldStatusChanged?.Invoke(worldName, SyncStatus.Error);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sync failed for {World}", worldName);
            Info($"[{worldName}] Sync failed: {ex.Message}");
            WorldStatusChanged?.Invoke(worldName, SyncStatus.Error);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task SyncWorldAsync(string worldName, CancellationToken ct,
        bool allowUploadWhileRunning = false)
    {
        var local = WorldScanner.Scan(_settings.WorldsPath)
            .FirstOrDefault(w => string.Equals(w.Name, worldName, StringComparison.OrdinalIgnoreCase));

        var remote = await _cloud.ListFilesAsync(ct);
        var remoteFiles = RemoteWorldFiles(remote, worldName);
        var remoteManifest = remote.FirstOrDefault(f =>
            f.Name.Equals(CommitMarker.Name(worldName), StringComparison.OrdinalIgnoreCase));

        var whoHasLock = await _cloud.GetLockAsync(worldName, ct);
        bool iHoldLock = whoHasLock is not null &&
            whoHasLock.PlayerName.Equals(_settings.PlayerName, StringComparison.OrdinalIgnoreCase);
        bool otherHoldsLock = whoHasLock is not null && !whoHasLock.IsStale && !iHoldLock;

        // ---- Case 1: nothing local, remote exists → first-time download ----
        if (local is null && remoteFiles.Count > 0)
        {
            if (!CommitState(remoteFiles, remoteManifest))
            {
                Info($"[{worldName}] Remote save looks torn (an upload was interrupted) — " +
                     "waiting for the next complete upload before downloading.");
                WorldStatusChanged?.Invoke(worldName, SyncStatus.Error);
                return;
            }
            await DownloadWorldAsync(worldName, remoteFiles, ct);
            return;
        }

        if (local is null)
        {
            WorldStatusChanged?.Invoke(worldName, SyncStatus.Unknown);
            return;
        }

        // ---- Compare: does the local file set exactly match what's remote? ----
        var localHashes = await HashLocalManifestAsync(worldName, local, ct);
        bool setMatches = FileSetMatches(localHashes, remoteFiles);

        if (setMatches)
        {
            // A matching set has nothing partial left to complete — the only repairable
            // defect is a missing/stale manifest marker.
            if (!otherHoldsLock)
            {
                try { await RepairRemoteManifestAsync(worldName, localHashes, remoteFiles, remoteManifest, ct); }
                catch (Exception ex) { Info($"[{worldName}] Couldn't repair remote manifest: {ex.Message}"); }
            }
            WorldStatusChanged?.Invoke(worldName, otherHoldsLock ? SyncStatus.LockedByOther : SyncStatus.InSync);
            return;
        }

        // ---- Divergence: decide direction ----
        // If I hold the lock (or no one does and my file is newer), upload.
        // If someone else holds the lock, or the remote is newer, download —
        // but never while Valheim is running locally.
        bool remoteIsNewer = remoteFiles.Count > 0 &&
            remoteFiles.Max(f => f.ModifiedTime.UtcDateTime) > local.LastWriteUtc;

        if (iHoldLock || remoteFiles.Count == 0 || (!otherHoldsLock && !remoteIsNewer))
        {
            if (ValheimProcess.IsRunning())
            {
                // Only push mid-session when the in-game timer asked us to AND the save
                // has been quiet long enough that Valheim isn't part-way through writing it.
                var settled = (DateTime.UtcNow - local.LastWriteUtc)
                    >= TimeSpan.FromSeconds(_settings.DebounceSeconds);
                if (!allowUploadWhileRunning || !settled)
                {
                    Info($"[{worldName}] Valheim is running — will upload after the next completed save.");
                    return;
                }
                Info($"[{worldName}] Valheim running — pushing in-progress save.");
            }

            // If I don't already hold the lock (this is an opportunistic background
            // upload — "no one holds the lock and my file isn't older" — rather than an
            // active play session), grab it for the duration of the upload. Without this,
            // two idle clients that both satisfy that condition at the same moment can
            // race: their per-file uploads and stale-file cleanup interleave on the same
            // "<world>/" prefix and corrupt the set, leaving a remote whose files don't
            // hash to the marker (torn). Whoever loses the race sees the lock on its next
            // pass and correctly downloads/skips instead of stepping on the winner.
            bool tookLock = false;
            if (!iHoldLock)
            {
                tookLock = await _cloud.TryAcquireLockAsync(worldName, _settings.PlayerName, ct);
                if (!tookLock)
                {
                    Info($"[{worldName}] Another machine grabbed the lock just now — skipping this upload.");
                    WorldStatusChanged?.Invoke(worldName, SyncStatus.LockedByOther);
                    return;
                }
            }
            try
            {
                await UploadWorldAsync(worldName, local, localHashes, remote, ct);
            }
            finally
            {
                if (tookLock)
                    await _cloud.ReleaseLockAsync(worldName, _settings.PlayerName, ct);
            }
        }
        else
        {
            if (ValheimProcess.IsRunning())
            {
                Info($"[{worldName}] Remote is newer but Valheim is running — skipping download.");
                WorldStatusChanged?.Invoke(worldName, SyncStatus.RemoteNewer);
                return;
            }
            if (!CommitState(remoteFiles, remoteManifest))
            {
                Info($"[{worldName}] Remote save is incomplete or torn — skipping download " +
                     "until it's re-uploaded.");
                WorldStatusChanged?.Invoke(worldName, SyncStatus.Error);
                return;
            }
            await DownloadWorldAsync(worldName, remoteFiles, ct);
        }
    }

    private async Task UploadWorldAsync(string worldName, WorldSave local,
        IReadOnlyDictionary<string, string> localHashes, IReadOnlyList<RemoteFile> remote, CancellationToken ct)
    {
        WorldStatusChanged?.Invoke(worldName, SyncStatus.Syncing);

        // Snapshot once per play session: the first upload of a session rolls the
        // pre-session remote save into a single backup per world. Later uploads in the
        // same session — in-game auto-saves and the final save on exit — leave that
        // backup untouched, so it always holds the state from before the session began.
        // The set is persisted so an app restart mid-session can't retrigger it.
        if (_session.BackedUpWorlds.Add(worldName))
        {
            await BackupRemoteAsync(worldName, remote, ct);
            _session.Save(_sessionStatePath);
        }

        Info($"[{worldName}] Uploading ({local.SizeBytes / (1024.0 * 1024):F1} MB)...");

        // Skip any file whose remote copy already matches by MD5 — untouched chunk files
        // (the common case: most zones don't change between saves) cost nothing here.
        var remoteByName = remote.ToDictionary(f => f.Name, f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var path in local.AllFilePaths)
        {
            var remoteName = RemoteName(worldName, path);
            if (remoteByName.TryGetValue(remoteName, out var existing) &&
                string.Equals(existing.Md5Checksum, localHashes[remoteName], StringComparison.OrdinalIgnoreCase))
                continue;
            await _cloud.UploadAsync(path, remoteName, null, ct);
        }

        // Drop any remote file under this world's prefix that isn't part of the current
        // revision any more (a superseded chunk, or the previous revision's main files) —
        // otherwise a stale leftover could be mistaken for part of a future revision.
        var currentNames = localHashes.Keys;
        var prefix = CommitMarker.RemotePrefix(worldName);
        var markerName = CommitMarker.Name(worldName);
        foreach (var f in remote.Where(f =>
                     f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                     !f.Name.Equals(markerName, StringComparison.OrdinalIgnoreCase) &&
                     !currentNames.Contains(f.Name)))
            await _cloud.DeleteAsync(f.Name, ct);

        // The manifest marker is uploaded LAST. Only a marker whose content matches every
        // file's MD5 certifies the set as uploaded together, so an interrupt anywhere
        // before the marker leaves the remote detectably torn instead of silently
        // downloadable.
        await UploadCommitMarkerAsync(worldName, localHashes, ct);

        Info($"[{worldName}] Upload complete.");
        WorldStatusChanged?.Invoke(worldName, SyncStatus.InSync);
    }

    /// <summary>
    /// Detects the start of a new play session (Valheim transitioning from not-running to
    /// running) and clears the per-session backup set so the next upload snapshots again.
    /// Resetting on session <em>start</em> — not exit — keeps the final save-on-exit upload
    /// (which happens after Valheim has already closed) from re-triggering a backup.
    /// </summary>
    private void UpdateSessionState()
    {
        bool running = ValheimProcess.IsRunning();
        if (running == _session.ValheimWasRunning) return;

        if (running) _session.BackedUpWorlds.Clear();
        _session.ValheimWasRunning = running;
        _session.Save(_sessionStatePath);
    }

    /// <summary>
    /// Server-side-copies the world's whole current "&lt;world&gt;/" prefix to a
    /// "&lt;world&gt;.bak/" prefix before it's overwritten, then removes any leftover
    /// .bak file that isn't part of this backup (a previous revision may have had a
    /// different set of chunk files). Best-effort: a failed backup is logged but never
    /// blocks the upload that follows.
    /// </summary>
    private async Task BackupRemoteAsync(string worldName, IReadOnlyList<RemoteFile> remote, CancellationToken ct)
    {
        try
        {
            var prefix = CommitMarker.RemotePrefix(worldName);
            var backupPrefix = worldName + ".bak/";
            var toBackup = remote.Where(f => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();

            var newBackupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var any = false;
            foreach (var f in toBackup)
            {
                var dest = backupPrefix + f.Name[prefix.Length..];
                newBackupNames.Add(dest);
                if (await _cloud.CopyAsync(f.Name, dest, ct)) any = true;
            }

            foreach (var f in remote.Where(f =>
                         f.Name.StartsWith(backupPrefix, StringComparison.OrdinalIgnoreCase) &&
                         !newBackupNames.Contains(f.Name)))
                await _cloud.DeleteAsync(f.Name, ct);

            if (any) Info($"[{worldName}] Backed up previous remote save.");
        }
        catch (Exception ex)
        {
            Info($"[{worldName}] Couldn't back up previous remote save: {ex.Message}");
        }
    }

    /// <summary>
    /// Consistency of a world's remote file set against its manifest marker, judged
    /// purely from the folder listing — the marker's canonical content makes its MD5
    /// predictable from every file's MD5, so no download is needed. There is no legacy/
    /// pre-marker era for this format, so a file set with no marker (or a stale one) is
    /// always torn.
    /// </summary>
    private static bool CommitState(IReadOnlyList<RemoteFile> remoteFiles, RemoteFile? manifest)
    {
        if (remoteFiles.Count == 0) return true;
        if (manifest?.Md5Checksum is null) return false;
        if (remoteFiles.Any(f => f.Md5Checksum is null)) return false;
        var expected = Hashing.Md5Text(
            CommitMarker.Content(remoteFiles.Select(f => (f.Name, f.Md5Checksum!))));
        return string.Equals(manifest.Md5Checksum, expected, StringComparison.OrdinalIgnoreCase);
    }

    private async Task UploadCommitMarkerAsync(string worldName,
        IReadOnlyDictionary<string, string> fileHashes, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"vs-{Guid.NewGuid():N}.manifest");
        var content = CommitMarker.Content(fileHashes.Select(kv => (kv.Key, kv.Value)));
        await File.WriteAllTextAsync(tmp, content, ct);
        try { await _cloud.UploadAsync(tmp, CommitMarker.Name(worldName), null, ct); }
        finally { File.Delete(tmp); }
    }

    /// <summary>
    /// Runs when the local and remote file sets already match exactly: writes/refreshes
    /// the manifest marker when it's missing or stale (its own upload failed after the
    /// rest of the set went up). Safe for any client — it only describes content that
    /// already matches what it has locally.
    /// </summary>
    private async Task RepairRemoteManifestAsync(string worldName,
        IReadOnlyDictionary<string, string> localHashes, IReadOnlyList<RemoteFile> remoteFiles,
        RemoteFile? manifest, CancellationToken ct)
    {
        if (CommitState(remoteFiles, manifest)) return;
        await UploadCommitMarkerAsync(worldName, localHashes, ct);
        Info($"[{worldName}] Wrote manifest marker for the remote save.");
    }

    private async Task DownloadWorldAsync(string worldName, IReadOnlyList<RemoteFile> remoteFiles,
        CancellationToken ct)
    {
        WorldStatusChanged?.Invoke(worldName, SyncStatus.Syncing);
        Info($"[{worldName}] Downloading...");

        Directory.CreateDirectory(_settings.WorldsPath);
        var worldDir = Path.Combine(_settings.WorldsPath, worldName);

        var prefix = CommitMarker.RemotePrefix(worldName);
        var tmpDir = Path.Combine(Path.GetTempPath(), $"{worldName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var downloaded = new List<(string RemoteName, string TmpPath)>();
            foreach (var f in remoteFiles)
            {
                var tmpPath = Path.Combine(tmpDir, f.Name[prefix.Length..]);
                await _cloud.DownloadAsync(f.Name, tmpPath, null, ct);
                downloaded.Add((f.Name, tmpPath));
            }

            // Verify every file against the listing's hash before touching the live folder.
            var byName = remoteFiles.ToDictionary(f => f.Name, f => f, StringComparer.OrdinalIgnoreCase);
            foreach (var (remoteName, tmpPath) in downloaded)
            {
                var expected = byName[remoteName].Md5Checksum;
                if (expected is not null && expected != await Hashing.Md5Async(tmpPath, ct))
                    throw new IOException($"Downloaded '{remoteName}' failed hash verification — aborting.");
            }

            // Swap the whole folder: keep one safety copy of what we're replacing, then
            // replace it entirely with exactly the new revision's files. A full swap
            // (rather than overwriting file-by-file) is what prunes the previous
            // revision's main files and any now-superseded chunks — the same end state
            // Valheim's own save leaves. Nothing touches the live folder until every
            // download above has verified — a failed/corrupt download leaves it as-is.
            if (Directory.Exists(worldDir) && Directory.EnumerateFileSystemEntries(worldDir).Any())
            {
                var backupDir = worldDir + ".synbak";
                if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
                Directory.Move(worldDir, backupDir);
            }
            Directory.CreateDirectory(worldDir);

            foreach (var (remoteName, tmpPath) in downloaded)
                File.Move(tmpPath, Path.Combine(worldDir, remoteName[prefix.Length..]), overwrite: true);

            // Also drop it into Valheim's LocalLow folder so it actually shows up in the
            // in-game world list (see MirrorToLocalLow). Never fatal to the download.
            MirrorToLocalLow(worldName, worldDir);

            Info($"[{worldName}] Download complete.");
            WorldStatusChanged?.Invoke(worldName, SyncStatus.InSync);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>
    /// Copies a freshly downloaded world (every file, flat — a world folder never has
    /// subfolders of its own) into Valheim's LocalLow "local storage" folder, replacing
    /// anything already there (with a safety copy kept, same as the main download).
    /// Valheim only adds a world to the in-game list once it imports it from there and
    /// registers it with Steam Cloud — a world written solely into the Steam userdata\…\
    /// remote folder never appears. This automates the manual "copy into LocalLow, then
    /// open Valheim" step. It is best-effort: any failure is logged, never thrown, so a
    /// download is still considered complete even if this copy can't happen.
    /// </summary>
    private void MirrorToLocalLow(string worldName, string worldDir)
    {
        try
        {
            var folder = ResolveLocalLowMirrorFolder();
            if (folder is null) return;

            var dest = Path.Combine(folder, worldName);

            if (Directory.Exists(dest))
            {
                var backupDir = dest + ".synbak";
                if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
                Directory.Move(dest, backupDir);
            }

            Directory.CreateDirectory(dest);
            foreach (var file in Directory.EnumerateFiles(worldDir))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);

            Info($"[{worldName}] Copied into Valheim's local folder — it will appear in game.");
        }
        catch (Exception ex)
        {
            Info($"[{worldName}] Couldn't copy into Valheim's local folder: {ex.Message}");
        }
    }

    /// <summary>Resolves Valheim's LocalLow worlds folder to mirror a download into, or
    /// null if it can't be found, or if it's the same folder we already sync out of (the
    /// files are already there — nothing to mirror).</summary>
    private string? ResolveLocalLowMirrorFolder()
    {
        var folder = ValheimSaveLocations.ResolveLocalLowWorldsFolder();
        if (folder is null) return null;

        if (string.Equals(
                Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(_settings.WorldsPath).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return null;

        Directory.CreateDirectory(folder);
        return folder;
    }

    private static List<RemoteFile> RemoteWorldFiles(IReadOnlyList<RemoteFile> remote, string worldName)
    {
        var prefix = CommitMarker.RemotePrefix(worldName);
        var markerName = CommitMarker.Name(worldName);
        return remote.Where(f => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                  !f.Name.Equals(markerName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static async Task<Dictionary<string, string>> HashLocalManifestAsync(
        string worldName, WorldSave local, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in local.AllFilePaths)
            result[RemoteName(worldName, path)] = await Hashing.Md5Async(path, ct);
        return result;
    }

    private static string RemoteName(string worldName, string localPath) =>
        CommitMarker.RemotePrefix(worldName) + Path.GetFileName(localPath);

    private static bool FileSetMatches(IReadOnlyDictionary<string, string> localHashes,
        IReadOnlyList<RemoteFile> remoteFiles)
    {
        if (remoteFiles.Count != localHashes.Count) return false;
        foreach (var f in remoteFiles)
        {
            if (!localHashes.TryGetValue(f.Name, out var md5)) return false;
            if (!string.Equals(f.Md5Checksum, md5, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private void Info(string message)
    {
        _log.LogInformation("{Message}", message);
        Log?.Invoke($"{DateTime.Now:HH:mm:ss}  {message}");
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _inGameTimer?.Dispose();
        _watcher?.Dispose();
        await Task.CompletedTask;
    }
}
