using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ValheimSync.Core;
using ValheimSync.Core.Models;
using ValheimSync.Core.Storage;
using ValheimSync.Core.Sync;
using ValheimSync.Core.Update;

namespace ValheimSync.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>Window title, including the running version — e.g. "VSaver v2.0.1".</summary>
    public string WindowTitle => $"VSaver v{Updater.CurrentVersion.ToString(3)}";

    private readonly AppSettings _settings;
    private SyncEngine? _engine;
    private ICloudStorageProvider? _cloud;
    private readonly HashSet<string> _watchedWorlds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When the idle countdown started. Reset to "now" whenever the window is backgrounded
    /// or Valheim is running, so the 15-minute exit timer measures time spent idle in the
    /// background after the game has closed.
    /// </summary>
    private DateTime _idleSinceUtc = DateTime.UtcNow;
    private bool _exiting;

    /// <summary>Raised when the app should shut itself down (idle auto-finalize). App wires this to Shutdown.</summary>
    public event Action? ExitRequested;

    /// <summary>True while the window is hidden (running only in the system tray / background).</summary>
    [ObservableProperty]
    private bool _isInBackground;

    /// <summary>The main list: worlds ("servers") that exist in the shared cloud folder.</summary>
    public ObservableCollection<WorldItemViewModel> Worlds { get; } = new();

    /// <summary>Local worlds not yet published to the cloud — choices for "Add server".</summary>
    public ObservableCollection<string> AddableWorlds { get; } = new();

    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasName))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _playerName;

    /// <summary>True once a username has been set. Nothing in the app is usable until then.</summary>
    public bool HasName => !string.IsNullOrWhiteSpace(PlayerName);

    /// <summary>
    /// The shared Google Drive folder id, pasted by the user and persisted to settings.json.
    /// Required before we can connect — it's never hardcoded in the app (it's private to the
    /// group), so on a fresh install the user pastes the id their organizer sent them.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolderId))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _driveFolderId = "";

    /// <summary>True once a shared folder id has been provided — a prerequisite for connecting.</summary>
    public bool HasFolderId => !string.IsNullOrWhiteSpace(DriveFolderId);

    /// <summary>The name being typed while editing (first-time setup or the pen icon).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveNameCommand))]
    private string _nameDraft = "";

    /// <summary>True while the name is shown as an editable text box rather than plain text.</summary>
    [ObservableProperty]
    private bool _isEditingName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddServerCommand))]
    private string? _selectedAddableWorld;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    [NotifyCanExecuteChangedFor(nameof(LogoutCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddServerCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlayButtons))]
    private bool _userHoldsAnyLock;

    /// <summary>Play is only shown while the user holds no lock on any world.</summary>
    public bool ShowPlayButtons => !UserHoldsAnyLock;

    [ObservableProperty]
    private string _statusText = "Starting up…";

    public MainWindowViewModel()
    {
        _settings = AppSettings.Load();
        _playerName = _settings.PlayerName;
        _driveFolderId = _settings.DriveFolderId;
        _nameDraft = _playerName;
        _isEditingName = false;
        RefreshAddableWorlds();

        if (!HasName)
            _statusText = "Signing in to Google to get you set up…";

        // Always auto-connect: for first-run users this signs them in and derives their
        // name from their Google email; for returning users it just resumes syncing.
        _ = AutoConnectAfterDelayAsync();

        // Idle auto-finalize: quit the background app a while after Valheim closes.
        _ = RunIdleWatchdogAsync();
    }

    /// <summary>Start the idle countdown the moment the window is hidden to the tray.</summary>
    partial void OnIsInBackgroundChanged(bool value)
    {
        if (value) _idleSinceUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// 15 minutes after the window is backgrounded (with Valheim not running), do a final
    /// sync and exit cleanly — so the app doesn't linger in the tray forever after play.
    /// The countdown restarts if Valheim launches or the window is reopened.
    /// </summary>
    private async Task RunIdleWatchdogAsync()
    {
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync())
        {
            if (_exiting) continue;

            // Countdown only runs while idle in the background: reset it whenever the
            // window is open or Valheim is running.
            if (!IsInBackground || ValheimProcess.IsRunning())
            {
                _idleSinceUtc = DateTime.UtcNow;
                continue;
            }

            if (DateTime.UtcNow - _idleSinceUtc >= TimeSpan.FromMinutes(15))
            {
                _exiting = true;
                await AutoFinalizeAndExitAsync();
                return;
            }
        }
    }

    /// <summary>Final sync (wait for it to finish), then ask the app to shut down.</summary>
    private async Task AutoFinalizeAndExitAsync()
    {
        try
        {
            if (_engine is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    LogLines.Insert(0, "Idle 15 min after Valheim closed — syncing, then exiting."));
                await _engine.SyncNowAsync(); // wait for the upload/download to complete
            }
        }
        catch { /* best effort — exit regardless */ }

        // Trigger the app's normal shutdown path (releases held locks, disposes engine).
        await Dispatcher.UIThread.InvokeAsync(() => ExitRequested?.Invoke());
    }

    private bool CanSaveName() => !string.IsNullOrWhiteSpace(NameDraft);

    /// <summary>Commit a name change from the pen icon. Persisted so it sticks.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveName))]
    private void SaveName()
    {
        var name = NameDraft.Trim();
        if (name.Length == 0) return;

        PlayerName = name;
        _settings.PlayerName = name;
        _settings.Save();
        IsEditingName = false;
        StatusText = $"Name set to {name}.";
    }

    /// <summary>Show the name text box (pen icon).</summary>
    [RelayCommand]
    private void EditName()
    {
        NameDraft = PlayerName;
        IsEditingName = true;
    }

    /// <summary>Discard an edit and go back to plain text — not allowed during first-time setup.</summary>
    [RelayCommand]
    private void CancelNameEdit()
    {
        if (!HasName) return;               // can't cancel out of the very first setup
        NameDraft = PlayerName;
        IsEditingName = false;
    }

    /// <summary>
    /// Auto-connect to Google Drive a few seconds after launch so the user
    /// doesn't have to click Connect every time. A browser may open on first
    /// run for the one-time Google sign-in.
    /// </summary>
    private async Task AutoConnectAfterDelayAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (ConnectCommand.CanExecute(null))
                await ConnectCommand.ExecuteAsync(null);
        });
    }

    /// <summary>
    /// Load the main server list straight from the cloud folder and kick a sync
    /// so any cloud-only saves get downloaded locally.
    /// </summary>
    private async Task RefreshServersAsync()
    {
        if (_cloud is null) return;

        var files = await _cloud.ListFilesAsync();
        // A server is any file under a "<world>/" prefix — a world's save is many files
        // (see CommitMarker), and the "/" grouping is what makes its name recoverable
        // even though no single file is literally named "<world>.something".
        var serverNames = files
            .Where(f => f.Name.Contains('/'))
            .Select(f => f.Name[..f.Name.IndexOf('/')])
            // "<world>.bak" is the rolling remote backup prefix (see SyncEngine.BackupRemoteAsync)
            // — it groups under '/' just like a real world, but it's not one; never list it.
            .Where(n => !n.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Worlds.Clear();
        foreach (var name in serverNames)
        {
            var size = files.Where(f => f.Name.StartsWith($"{name}/", StringComparison.OrdinalIgnoreCase))
                .Sum(f => f.SizeBytes);

            var item = new WorldItemViewModel(name, size) { IsSelected = true };
            item.SelectionChanged += OnWorldSelectionChanged;

            var heldLock = await _cloud.GetLockAsync(name);
            var active = heldLock is not null && !heldLock.IsStale;
            item.LockHolder = active ? heldLock!.PlayerName : null;
            item.IsLockedByMe = active &&
                string.Equals(heldLock!.PlayerName, _settings.PlayerName, StringComparison.OrdinalIgnoreCase);

            Worlds.Add(item);
            _settings.SelectedWorlds.Add(name); // servers are synced by default
        }
        _settings.Save();

        // Recompute the global "do I hold a lock?" flag from the loaded rows.
        UserHoldsAnyLock = Worlds.Any(w => w.IsLockedByMe);

        RefreshAddableWorlds();

        // Pull down any server whose save we don't have locally yet.
        if (_engine is not null) await _engine.SyncNowAsync();

        StatusText = Worlds.Count == 0
            ? "No servers in the cloud yet. Pick a local world and click 'Add server' to publish one."
            : $"{Worlds.Count} server(s) synced from the cloud.";
    }

    /// <summary>Local worlds that aren't already published as servers — the "Add server" choices.</summary>
    private void RefreshAddableWorlds()
    {
        var alreadyServers = Worlds.Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddableWorlds.Clear();
        foreach (var world in WorldScanner.Scan(_settings.WorldsPath))
            if (!alreadyServers.Contains(world.Name))
                AddableWorlds.Add(world.Name);
    }

    private void OnWorldSelectionChanged(WorldItemViewModel item)
    {
        if (item.IsSelected) _settings.SelectedWorlds.Add(item.Name);
        else _settings.SelectedWorlds.Remove(item.Name);
        _settings.Save();
    }

    /// <summary>
    /// Set whether the current user holds a world's lock, then recompute the
    /// global flag from the rows. Deriving <see cref="UserHoldsAnyLock"/> purely
    /// from the per-row flags guarantees Play (hidden globally) and Done (shown
    /// per row) can never both be hidden on a world you actually hold.
    /// </summary>
    private void SetLockedByMe(WorldItemViewModel item, bool mine)
    {
        item.IsLockedByMe = mine;
        UserHoldsAnyLock = Worlds.Any(w => w.IsLockedByMe);
    }

    /// <summary>
    /// Extracts a bare Google Drive folder id from whatever the user pasted.
    /// Handles a plain id as well as full share links such as
    /// "https://drive.google.com/drive/folders/&lt;id&gt;?hl=da".
    /// </summary>
    internal static string NormalizeFolderId(string input)
    {
        var value = (input ?? "").Trim();
        if (value.Length == 0) return "";

        // "…/open?id=<id>"-style links carry the id in the query string instead of the path.
        var byQuery = System.Text.RegularExpressions.Regex.Match(value, @"[?&]id=([^&#/?]+)");
        if (byQuery.Success) return byQuery.Groups[1].Value.Trim();

        // If it's a URL, pull the segment after ".../folders/" (or "/d/" for file links).
        foreach (var marker in new[] { "/folders/", "/d/" })
        {
            var idx = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                value = value.Substring(idx + marker.Length);
                break;
            }
        }

        // Drop any trailing path, query string, or fragment (?hl=da, /view, #...).
        var end = value.IndexOfAny(new[] { '/', '?', '#' });
        if (end >= 0) value = value.Substring(0, end);

        return value.Trim();
    }

    private bool CanConnect() => !IsConnected && !IsBusy && HasFolderId;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        IsBusy = true;
        StatusText = "Connecting to Google Drive (a browser window may open)...";
        try
        {
            // Persist the folder id the user provided, then connect against it.
            // Accepts either a bare id or a full Drive URL, e.g.
            // https://drive.google.com/drive/folders/<id>?hl=da
            _settings.DriveFolderId = NormalizeFolderId(DriveFolderId);
            DriveFolderId = _settings.DriveFolderId;
            _settings.Save();
            _cloud = new GoogleDriveStorageProvider(_settings.DriveFolderId);
            _engine = new SyncEngine(_settings, _cloud);
            _engine.Log += line => Dispatcher.UIThread.Post(() =>
            {
                LogLines.Insert(0, line);
                while (LogLines.Count > 200) LogLines.RemoveAt(LogLines.Count - 1);
            });
            _engine.WorldStatusChanged += (world, status) => Dispatcher.UIThread.Post(() =>
            {
                var item = Worlds.FirstOrDefault(w =>
                    string.Equals(w.Name, world, StringComparison.OrdinalIgnoreCase));
                if (item is not null) item.Status = status;
            });

            await _engine.StartAsync();

            // Derive the player's identity from their Google email on first connect
            // (the local part, before the @). They can override it with the pen icon.
            if (!HasName)
            {
                var derived = DeriveNameFromEmail(await _cloud.GetAccountEmailAsync());
                if (!string.IsNullOrWhiteSpace(derived))
                {
                    PlayerName = derived;
                    _settings.PlayerName = derived;
                    _settings.Save();
                }
                else
                {
                    // Couldn't read the email — fall back to letting them type a name.
                    IsEditingName = true;
                }
            }

            IsConnected = true;
            await RefreshServersAsync(); // load the cloud server list + download saves

            if (!HasName)
                StatusText = "Signed in, but couldn't read your Google email — please enter a name.";
        }
        catch (Exception ex)
        {
            StatusText = $"Connection failed: {ex.Message}";
            _engine = null;
            _cloud = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The identity we show/use = the part of the Google email before the @.</summary>
    private static string? DeriveNameFromEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.IndexOf('@');
        return (at > 0 ? email[..at] : email).Trim();
    }

    private bool CanUseEngine() => IsConnected;

    /// <summary>Disconnect and forget the cached Google token so a different account can sign in.</summary>
    [RelayCommand(CanExecute = nameof(CanUseEngine))]
    private async Task LogoutAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
            _engine = null;
        }
        _cloud = null;

        GoogleDriveStorageProvider.ClearCachedToken();

        Worlds.Clear();          // the server list came from the cloud we just left
        UserHoldsAnyLock = false;
        RefreshAddableWorlds();  // local worlds are all "addable" again once reconnected

        IsConnected = false;
        StatusText = "Signed out. Click Connect to sign in with a different Google account.";
    }

    [RelayCommand(CanExecute = nameof(CanUseEngine))]
    private async Task SyncNowAsync()
    {
        if (_engine is null) return;
        IsBusy = true;
        try { await _engine.SyncNowAsync(); }
        finally { IsBusy = false; }
    }

    /// <summary>Reload the server list from the cloud and re-scan local worlds.</summary>
    [RelayCommand]
    private async Task RescanWorldsAsync()
    {
        if (IsConnected) await RefreshServersAsync();
        else RefreshAddableWorlds();
    }

    private bool CanAddServer() => IsConnected && !string.IsNullOrWhiteSpace(SelectedAddableWorld);

    /// <summary>Publish the chosen local world to the cloud so it becomes a shared server.</summary>
    [RelayCommand(CanExecute = nameof(CanAddServer))]
    private async Task AddServerAsync()
    {
        var name = SelectedAddableWorld;
        if (name is null || _cloud is null || _engine is null) return;

        bool existsLocally = WorldScanner.Scan(_settings.WorldsPath)
            .Any(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!existsLocally)
        {
            StatusText = $"Local world '{name}' not found.";
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = $"Publishing '{name}' to the cloud...";
            _settings.SelectedWorlds.Add(name);
            _settings.Save();

            // SyncEngine already knows how to publish a brand-new world — it's the same
            // "nothing remote yet" upload path it uses for every other sync, so there's
            // nothing to hand-roll here. The engine never throws on a failed sync (it logs
            // and sets a per-world error status instead), so success is judged by whether
            // the world actually shows up remotely afterward — and rolled back from
            // SelectedWorlds if not.
            await _engine.SyncNowAsync();
            await RefreshServersAsync();

            if (Worlds.Any(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText = $"'{name}' is now a server.";
            }
            else
            {
                _settings.SelectedWorlds.Remove(name);
                _settings.Save();
                StatusText = $"Couldn't add '{name}' — check the log for details.";
            }
        }
        catch (Exception ex)
        {
            _settings.SelectedWorlds.Remove(name);
            _settings.Save();
            StatusText = $"Couldn't add '{name}': {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Launch the game via Steam, pulling the latest shared save down first.</summary>
    [RelayCommand]
    private async Task LaunchValheimAsync()
    {
        if (ValheimProcess.IsRunning())
        {
            StatusText = "Valheim is already running.";
            return;
        }

        // Always sync with Drive and wait for it to finish before opening the game,
        // so we never start on a stale local save.
        if (!await SyncBeforeLaunchAsync())
        {
            StatusText += "  Fix the connection and press Play again — the game wasn't launched.";
            return;
        }

        try
        {
            ValheimProcess.Launch();
            StatusText = "Launching Valheim via Steam...";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't launch Valheim (is Steam installed?): {ex.Message}";
        }
    }

    /// <summary>
    /// Runs a full sync against Drive and waits for it to complete, so the game always
    /// opens on the latest shared save. Returns true when it's safe to launch — either the
    /// sync succeeded or there's nothing connected to sync against; false only when a sync
    /// was attempted and failed, in which case the launch should be held back.
    /// </summary>
    private async Task<bool> SyncBeforeLaunchAsync()
    {
        if (_engine is null || !IsConnected)
            return true; // not connected — nothing to sync, let the launch proceed

        IsBusy = true;
        StatusText = "Syncing with Drive before launching…";
        try
        {
            await _engine.SyncNowAsync();
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Sync before launch failed: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// "I want to play this world now" — take the cloud lock, make sure the
    /// world is ticked for syncing, and launch Valheim.
    /// </summary>
    [RelayCommand]
    private async Task AcquireLockAsync(WorldItemViewModel? item)
    {
        if (item is null || _cloud is null) return;

        // Tick this world for syncing, then pull the latest shared save down and wait
        // for it to finish — before taking the lock, so the engine downloads the newest
        // version rather than uploading a stale local copy once the lock is ours.
        item.IsSelected = true;
        if (!await SyncBeforeLaunchAsync())
        {
            StatusText += "  Fix the connection and press Play again.";
            return;
        }

        var got = await _cloud.TryAcquireLockAsync(item.Name, _settings.PlayerName);
        if (!got)
        {
            var holder = await _cloud.GetLockAsync(item.Name);
            item.LockHolder = holder?.PlayerName;
            SetLockedByMe(item, false);
            StatusText = $"'{item.Name}' is currently locked by {holder?.PlayerName ?? "someone else"}.";
            return;
        }

        // The lock is ours — switch this row to Done immediately, before we touch Steam,
        // so it's clear we hold the world no matter how long the game takes to come up.
        item.LockHolder = _settings.PlayerName;
        SetLockedByMe(item, true);       // hides Play everywhere, shows Done on this world

        if (ValheimProcess.IsRunning())
        {
            StatusText = $"Locked '{item.Name}' — Valheim is already running. " +
                         "It'll upload and release automatically when you quit.";
        }
        else
        {
            try
            {
                ValheimProcess.Launch();
                StatusText = $"Locked '{item.Name}'. Starting Valheim…";
            }
            catch (Exception ex)
            {
                StatusText = $"Locked '{item.Name}', but couldn't launch Valheim " +
                             $"(is Steam installed?): {ex.Message}. Click Done when you're finished.";
                return; // no game to watch — leave the lock for a manual Done
            }
        }

        // Wait for the game to actually appear (Steam can be slow), then auto-release
        // the lock when it closes. Runs in the background so the UI stays responsive.
        StartValheimSession(item);
    }

    /// <summary>
    /// After Play: wait until Valheim is actually running (with status feedback), then
    /// watch for it to close and auto-release the lock — uploading the final save first,
    /// exactly like clicking Done.
    /// </summary>
    private void StartValheimSession(WorldItemViewModel item)
    {
        lock (_watchedWorlds)
        {
            if (!_watchedWorlds.Add(item.Name)) return; // already watching this world
        }

        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                // Poll until the game process comes up. Give Steam a few minutes.
                var started = await ValheimProcess.WaitForStartAsync(TimeSpan.FromMinutes(3));
                if (!started)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        StatusText = $"Valheim didn't start. '{item.Name}' is still locked to you — " +
                                     "click Done when you're finished, or press Play to try again.");
                    return; // leave the lock; user can Done manually
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                    StatusText = $"Valheim is running — playing '{item.Name}'. " +
                                 "It'll upload and release automatically when you quit.");

                // Now wait for the game to close, then hand the world back.
                await ValheimProcess.WaitUntilExitAsync();
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await ReleaseWorldAsync(item,
                        $"Valheim closed — uploaded final save and released '{item.Name}'."));
            }
            catch { /* app closing / cancellation — ignore */ }
            finally
            {
                lock (_watchedWorlds) { _watchedWorlds.Remove(item.Name); }
            }
        }
    }

    /// <summary>"I'm done playing" — sync up, then release the lock.</summary>
    [RelayCommand]
    private async Task ReleaseLockAsync(WorldItemViewModel? item)
    {
        if (item is null) return;
        await ReleaseWorldAsync(item, $"Lock for '{item.Name}' released.");
    }

    /// <summary>Push the final save, release the lock, and clear the UI lock badge.</summary>
    private async Task ReleaseWorldAsync(WorldItemViewModel item, string doneStatus)
    {
        if (_cloud is null || _engine is null) return;
        await _engine.SyncNowAsync(); // push the final save before handing over
        await _cloud.ReleaseLockAsync(item.Name, _settings.PlayerName);
        item.LockHolder = null;
        SetLockedByMe(item, false);   // hides Done, brings Play back once no lock is held
        StatusText = doneStatus;
    }

    /// <summary>
    /// Called once when the app is really exiting. Releases any lock this user still
    /// holds (with a final upload) so closing the app mid-session never leaves the world
    /// blocked for the group — the Valheim-exit watcher can't do this once the app is gone.
    /// Must be fully awaited before the process exits, or the Drive calls get cut off.
    /// </summary>
    public async Task ShutdownAsync()
    {
        try
        {
            if (_cloud is not null && _engine is not null)
            {
                foreach (var world in Worlds.Where(w => w.IsLockedByMe).ToArray())
                {
                    try
                    {
                        await ReleaseWorldAsync(world,
                            $"Released the lock on '{world.Name}' while closing.");
                    }
                    catch (Exception ex)
                    {
                        // Best effort — if this fails the 12 h stale timeout still frees it.
                        LogLines.Insert(0, $"Could not release '{world.Name}' on exit: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            if (_engine is not null) await _engine.DisposeAsync();
        }
    }
}
