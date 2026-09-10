namespace ValheimSync.Core.Sync;

/// <summary>
/// Watches the worlds folder and raises <see cref="WorldChanged"/> only after a
/// world's files have been quiet for the debounce window. This is what makes
/// "sync every 15 minutes" safe: we never upload a save Valheim is still writing.
///
/// A world writes many files (".db2"/".fwl2"/".chunks"/".ok"/"*.chunk") inside its own
/// "&lt;World&gt;\" subfolder, so watching has to recurse — see <see cref="WorldNameFor"/>
/// for how a changed path maps back to a world.
/// </summary>
public sealed class DebouncedWorldWatcher : IDisposable
{
    private static readonly string[] SaveFileExtensions = { ".db2", ".fwl2", ".chunks", ".ok", ".chunk" };

    private readonly FileSystemWatcher _watcher;
    private readonly TimeSpan _debounce;
    private readonly Dictionary<string, CancellationTokenSource> _pending = new();
    private readonly object _gate = new();

    /// <summary>Fired with the world name once its files have settled.</summary>
    public event Action<string>? WorldChanged;

    public DebouncedWorldWatcher(string worldsPath, TimeSpan debounce)
    {
        _debounce = debounce;
        _watcher = new FileSystemWatcher(worldsPath)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = true, // worlds live one folder down
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFsEvent;
        _watcher.Created += OnFsEvent;
        _watcher.Renamed += OnFsEvent;
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        var world = WorldNameFor(e.Name);
        if (world is null) return;

        lock (_gate)
        {
            // Restart the timer for this world on every write.
            if (_pending.TryGetValue(world, out var old))
                old.Cancel();

            var cts = new CancellationTokenSource();
            _pending[world] = cts;

            _ = Task.Delay(_debounce, cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                lock (_gate) _pending.Remove(world);
                WorldChanged?.Invoke(world);
            }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Maps a changed path (relative to the watched worlds folder, as FileSystemWatcher
    /// reports it) to the world it belongs to, or null if the change is irrelevant.
    ///
    /// A world's files sit exactly one folder down — the containing folder's name is the
    /// world name, since the filename itself could be any of "_main.&lt;N&gt;.db2/.fwl2/
    /// .chunks/.ok" or an arbitrary "*.chunk" zone file. Anything at the top level,
    /// nested deeper than one level, or under a "&lt;World&gt;_backup_auto-…" snapshot
    /// folder, is ignored — never real save traffic.
    /// </summary>
    private static string? WorldNameFor(string? relativePath)
    {
        if (relativePath is null) return null;

        var separator = relativePath.IndexOfAny(new[] { '\\', '/' });
        if (separator < 0) return null; // top-level file, not inside any world's folder

        var world = relativePath[..separator];
        var rest = relativePath[(separator + 1)..];
        if (rest.IndexOfAny(new[] { '\\', '/' }) >= 0) return null; // nested deeper than one level
        if (world.Contains("_backup_", StringComparison.OrdinalIgnoreCase)) return null;

        var ext = Path.GetExtension(rest);
        return SaveFileExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) ? world : null;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        lock (_gate)
        {
            foreach (var cts in _pending.Values) cts.Cancel();
            _pending.Clear();
        }
    }
}
