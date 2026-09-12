using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ValheimSync.Core.Models;
using ValheimSync.Core.Storage;
using ValheimSync.Core.Update;
using ValheimSync.Core.Util;

namespace ValheimSync.Core.Sync;

/// <summary>
/// Coordinates the whole sync lifecycle for the selected worlds:
///
///   * FileSystemWatcher (debounced) → upload after Valheim finishes a save
///   * Periodic poll (default 15 min) → download remote changes / fallback upload check
///   * A world's whole file set is packed into one deterministic zip archive
///     (see <see cref="WorldZip"/>) and transferred as a single "&lt;world&gt;.zip" —
///     one Drive object, one MD5, one API call each way instead of one per file
///   * MD5 comparison against Drive's md5Checksum → no duplicate transfers, ever
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
            var timeoutEx = new TimeoutException(
                $"Sync timed out after {_syncTimeout.TotalMinutes:F0} minutes (a file transfer likely stalled).");
            _log.LogError("Sync timed out for {World}", worldName);
            Info($"[{worldName}] Sync timed out after {_syncTimeout.TotalMinutes:F0} minutes " +
                 "(a file transfer likely stalled) — will retry on the next sync.");
            WorldStatusChanged?.Invoke(worldName, SyncStatus.Error);
            await TryUploadErrorLogAsync(worldName, timeoutEx, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sync failed for {World}", worldName);
            Info($"[{worldName}] Sync failed: {ex.Message}");
            WorldStatusChanged?.Invoke(worldName, SyncStatus.Error);
            await TryUploadErrorLogAsync(worldName, ex, ct);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>
    /// Best-effort: writes a detailed .txt report of a sync failure (a stalled transfer, a
    /// corrupt/incomplete download, any other exception) and uploads it to the shared Drive
    /// folder so it's diagnosable from any machine, not just this one's local log. Never
    /// throws, and never fires for a real shutdown/cancellation — only for genuine failures.
    /// </summary>
    private async Task TryUploadErrorLogAsync(string worldName, Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException || ct.IsCancellationRequested) return;

        string? tmp = null;
        try
        {
            var content =
                $"""
                ValheimSync error log
                Time (UTC):   {DateTime.UtcNow:O}
                Player:       {_settings.PlayerName}
                World:        {worldName}
                Provider:     {_cloud.ProviderName}
                App version:  {Updater.CurrentVersion}

                {ex}
                """;

            tmp = Path.Combine(Path.GetTempPath(), $"vs-errorlog-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(tmp, content, ct);

            var remoteName = $"errorlog_{SanitizeForFileName(worldName)}_" +
                $"{SanitizeForFileName(_settings.PlayerName)}_{DateTime.UtcNow:yyyyMMdd_HHmmssZ}.txt";
            await _cloud.UploadAsync(tmp, remoteName, null, ct);
            Info($"[{worldName}] Uploaded a detailed error log ({remoteName}) to the shared folder.");
        }
        catch (Exception uploadEx)
        {
            // Never let a failed log upload mask the original error, or throw out of a
            // catch block.
            _log.LogWarning(uploadEx, "Couldn't upload error log for {World}", worldName);
        }
        finally
        {
            if (tmp is not null && File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static string SanitizeForFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) || c == '/' ? '_' : c).ToArray());
    }

    private async Task SyncWorldAsync(string worldName, CancellationToken ct,
        bool allowUploadWhileRunning = false)
    {
        var local = WorldScanner.Scan(_settings.WorldsPath)
            .FirstOrDefault(w => string.Equals(w.Name, worldName, StringComparison.OrdinalIgnoreCase));

        var remote = await _cloud.ListFilesAsync(ct);
        var remoteZip = remote.FirstOrDefault(f =>
            f.Name.Equals(WorldArchive.ZipName(worldName), StringComparison.OrdinalIgnoreCase));

        var whoHasLock = await _cloud.GetLockAsync(worldName, ct);
        bool iHoldLock = whoHasLock is not null &&
            whoHasLock.PlayerName.Equals(_settings.PlayerName, StringComparison.OrdinalIgnoreCase);
        bool otherHoldsLock = whoHasLock is not null && !whoHasLock.IsStale && !iHoldLock;

        // ---- Case 1: nothing local, remote exists → first-time download ----
        if (local is null && remoteZip is not null)
        {
            await DownloadWorldAsync(worldName, remoteZip, ct);
            return;
        }

        if (local is null)
        {
            WorldStatusChanged?.Invoke(worldName, SyncStatus.Unknown);
            return;
        }

        // Pack the local save once per sync pass — used both for the MD5 comparison
        // below and, if this pass ends up uploading, as the exact bytes sent.
        var localZipPath = Path.Combine(Path.GetTempPath(), $"vs-{Guid.NewGuid():N}.zip");
        try
        {
            await WorldZip.CreateAsync(local.AllFilePaths, localZipPath, ct);
            var localMd5 = await Hashing.Md5Async(localZipPath, ct);

            // ---- Compare: does the local save exactly match what's remote? ----
            bool setMatches = remoteZip is not null &&
                string.Equals(remoteZip.Md5Checksum, localMd5, StringComparison.OrdinalIgnoreCase);

            if (setMatches)
            {
                WorldStatusChanged?.Invoke(worldName, otherHoldsLock ? SyncStatus.LockedByOther : SyncStatus.InSync);
                return;
            }

            // ---- Divergence: decide direction ----
            // If I hold the lock (or no one does and my file is newer), upload.
            // If someone else holds the lock, or the remote is newer, download —
            // but never while Valheim is running locally.
            bool remoteIsNewer = remoteZip is not null &&
                remoteZip.ModifiedTime.UtcDateTime > local.LastWriteUtc;

            if (iHoldLock || remoteZip is null || (!otherHoldsLock && !remoteIsNewer))
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
                // race and overwrite each other's upload. Whoever loses the race sees the
                // lock on its next pass and correctly downloads/skips instead of stepping on
                // the winner.
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
                    await UploadWorldAsync(worldName, local, localZipPath, ct);
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
                await DownloadWorldAsync(worldName, remoteZip!, ct);
            }
        }
        finally
        {
            if (File.Exists(localZipPath)) File.Delete(localZipPath);
        }
    }

    private async Task UploadWorldAsync(string worldName, WorldSave local, string localZipPath,
        CancellationToken ct)
    {
        WorldStatusChanged?.Invoke(worldName, SyncStatus.Syncing);

        // Snapshot once per play session: the first upload of a session rolls the
        // pre-session remote save into a single backup per world. Later uploads in the
        // same session — in-game auto-saves and the final save on exit — leave that
        // backup untouched, so it always holds the state from before the session began.
        // The set is persisted so an app restart mid-session can't retrigger it.
        if (_session.BackedUpWorlds.Add(worldName))
        {
            await BackupRemoteAsync(worldName, ct);
            _session.Save(_sessionStatePath);
        }

        Info($"[{worldName}] Uploading ({local.SizeBytes / (1024.0 * 1024):F1} MB)...");
        await _cloud.UploadAsync(localZipPath, WorldArchive.ZipName(worldName), null, ct);

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
    /// Server-side-copies the world's current "&lt;world&gt;.zip" to "&lt;world&gt;.zip.bak"
    /// before it's overwritten, keeping exactly one rolling backup. Best-effort: a failed
    /// backup is logged but never blocks the upload that follows.
    /// </summary>
    private async Task BackupRemoteAsync(string worldName, CancellationToken ct)
    {
        try
        {
            var copied = await _cloud.CopyAsync(
                WorldArchive.ZipName(worldName), WorldArchive.BackupZipName(worldName), ct);
            if (copied) Info($"[{worldName}] Backed up previous remote save.");
        }
        catch (Exception ex)
        {
            Info($"[{worldName}] Couldn't back up previous remote save: {ex.Message}");
        }
    }

    private async Task DownloadWorldAsync(string worldName, RemoteFile remoteZip, CancellationToken ct)
    {
        WorldStatusChanged?.Invoke(worldName, SyncStatus.Syncing);
        Info($"[{worldName}] Downloading...");

        Directory.CreateDirectory(_settings.WorldsPath);
        var worldDir = Path.Combine(_settings.WorldsPath, worldName);

        var tmpZip = Path.Combine(Path.GetTempPath(), $"vs-{Guid.NewGuid():N}.zip");
        var tmpDir = Path.Combine(Path.GetTempPath(), $"{worldName}-{Guid.NewGuid():N}");
        try
        {
            await _cloud.DownloadAsync(remoteZip.Name, tmpZip, null, ct);

            // Verify the archive against the listing's hash before touching the live folder.
            if (remoteZip.Md5Checksum is not null &&
                remoteZip.Md5Checksum != await Hashing.Md5Async(tmpZip, ct))
                throw new IOException($"Downloaded '{remoteZip.Name}' failed hash verification — aborting.");

            await WorldZip.ExtractAsync(tmpZip, tmpDir, ct);

            // Swap the whole folder: keep one safety copy of what we're replacing, then
            // replace it entirely with exactly the new revision's files. A full swap
            // (rather than overwriting file-by-file) is what prunes the previous
            // revision's main files and any now-superseded chunks locally too — the same
            // end state Valheim's own save leaves. Nothing touches the live folder until
            // the download above has verified — a failed/corrupt download leaves it as-is.
            if (Directory.Exists(worldDir) && Directory.EnumerateFileSystemEntries(worldDir).Any())
            {
                var backupDir = worldDir + ".synbak";
                if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
                Directory.Move(worldDir, backupDir);
            }
            Directory.CreateDirectory(worldDir);

            // File-by-file (not a directory move) because the temp folder and the worlds
            // path can live on different volumes — File.Move falls back to copy+delete
            // across volumes, Directory.Move does not.
            foreach (var file in Directory.EnumerateFiles(tmpDir))
                File.Move(file, Path.Combine(worldDir, Path.GetFileName(file)), overwrite: true);

            // Also drop it into Valheim's LocalLow folder so it actually shows up in the
            // in-game world list (see MirrorToLocalLow). Never fatal to the download.
            MirrorToLocalLow(worldName, worldDir);

            Info($"[{worldName}] Download complete.");
            WorldStatusChanged?.Invoke(worldName, SyncStatus.InSync);
        }
        finally
        {
            if (File.Exists(tmpZip)) File.Delete(tmpZip);
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
