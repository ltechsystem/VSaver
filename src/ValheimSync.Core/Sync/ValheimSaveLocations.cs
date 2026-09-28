namespace ValheimSync.Core.Sync;

/// <summary>
/// Works out the folder the installed Valheim actually reads/writes worlds in.
///
/// Modern Valheim keeps its worlds under
///   &lt;user&gt;\AppData\LocalLow\IronGate\Valheim\worlds_local (or \worlds)
///
/// This is the folder Valheim itself reads and writes. Steam's own Cloud "remote" cache
/// (under &lt;Steam&gt;\userdata\&lt;accountId&gt;\892970\remote) is a separate copy Steam
/// maintains on its own schedule and is deliberately never touched by VSaver — reading it as
/// the local source risks treating a stale cloud copy as current, and writing to it behind
/// Steam's back makes Manage Saves offer that stale copy against the fresh one, and risks
/// Steam's own cloud reconciliation silently overwriting what was written there.
/// </summary>
public static class ValheimSaveLocations
{
    public const string LocalLowNotFoundMessage =
        "Could not find Valheim's local worlds folder " +
        "(...\\AppData\\LocalLow\\IronGate\\Valheim\\worlds_local).\n\n" +
        "Likely cause: Valheim hasn't been launched on this PC yet (the folder is created on first run).\n\n" +
        "Fix: launch Valheim at least once, or set \"WorldsPathOverride\" in settings.json " +
        "(next to the app) to your worlds folder.";

    /// <summary>
    /// Valheim's LocalLow "local storage" worlds folder — the folder the game itself reads
    /// and writes (…\AppData\LocalLow\IronGate\Valheim\worlds_local or …\worlds).
    /// Returns null if Valheim's LocalLow folder can't be found (Valheim has never run here).
    /// </summary>
    public static string? ResolveLocalLowWorldsFolder()
    {
        var valheim = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "IronGate", "Valheim");
        if (!Directory.Exists(valheim)) return null;

        // worlds_local is the modern (crossplay) folder; worlds is the legacy one.
        // Pick whichever Valheim most recently wrote a real world into; if neither has
        // worlds yet, fall back to the first that exists.
        var candidates = new[]
        {
            Path.Combine(valheim, "worlds_local"),
            Path.Combine(valheim, "worlds"),
        };

        string? best = null;
        var bestTime = DateTime.MinValue;
        foreach (var folder in candidates)
        {
            if (!Directory.Exists(folder)) continue;
            var newest = NewestFwlUtc(folder);
            if (best is null || newest > bestTime) { best = folder; bestTime = newest; }
        }
        return best ?? candidates.FirstOrDefault(Directory.Exists);
    }

    /// <summary>Newest ".fwl2" anywhere under the folder — "*.fwl2" lives one folder down,
    /// inside each world's own subfolder.</summary>
    private static DateTime NewestFwlUtc(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.fwl2", SearchOption.AllDirectories)
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
        }
        catch { return DateTime.MinValue; }
    }
}
