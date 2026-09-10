using System.Text;

namespace ValheimSync.Core.Sync;

/// <summary>
/// Works out the folder the installed Valheim actually reads/writes worlds in.
///
/// Modern Valheim with Steam Cloud saves worlds under
///   &lt;Steam&gt;\userdata\&lt;accountId&gt;\892970\remote\worlds(_local)
///
/// The Steam path, account id, and the "worlds" vs "worlds_local" subfolder vary
/// per machine, so we probe the likely spots and pick the one that holds worlds.
/// There is deliberately NO LocalLow fallback: LocalLow holds legacy/local-only
/// saves, and silently falling back to it hides the real problem. If the Steam
/// Cloud folder can't be found we throw a detailed error instead.
/// </summary>
public static class ValheimSaveLocations
{
    private const string ValheimAppId = "892970";

    /// <summary>Steam Cloud world folders, most-likely-current first.</summary>
    public static IEnumerable<string> Candidates()
    {
        foreach (var remote in SteamValheimRemoteFolders())
        {
            yield return Path.Combine(remote, "worlds_local");
            yield return Path.Combine(remote, "worlds");
        }
    }

    /// <summary>
    /// The folder this machine's Valheim keeps its worlds in.
    /// Throws <see cref="DirectoryNotFoundException"/> with a detailed message if
    /// no Steam Cloud worlds folder can be located.
    /// </summary>
    public static string ResolveWorldsFolder()
    {
        var checkedPaths = new List<string>();
        string? firstExisting = null;

        foreach (var folder in Candidates())
        {
            checkedPaths.Add(folder);
            if (!Directory.Exists(folder)) continue;
            firstExisting ??= folder;

            // Prefer a folder that actually contains a world.
            if (HasWorlds(folder)) return folder;
        }

        // An existing-but-empty Steam folder is still a valid target (worlds will
        // land there); only error when nothing exists at all.
        return firstExisting ?? throw new DirectoryNotFoundException(BuildError(checkedPaths));
    }

    /// <summary>True if any immediate subfolder holds a world (identified by a
    /// "_main.&lt;N&gt;.fwl2" inside it).</summary>
    private static bool HasWorlds(string folder)
    {
        try
        {
            return SafeEnumerateDirectories(folder)
                .Any(dir => Directory.EnumerateFiles(dir, "_main.*.fwl2").Any());
        }
        catch { return false; }
    }

    /// <summary>
    /// Valheim's LocalLow "local storage" worlds folder
    /// (…\AppData\LocalLow\IronGate\Valheim\worlds_local or …\worlds).
    ///
    /// A world only appears in the in-game list once Valheim imports it from here and
    /// registers it with Steam Cloud (remotecache.vdf). A world written straight into the
    /// Steam userdata\…\remote folder is NOT registered, so it never shows up in game.
    /// Copying a download into this folder is what makes it appear — it automates the
    /// manual "drop the files in LocalLow, then open Valheim" step.
    ///
    /// Returns null if Valheim's LocalLow folder can't be found.
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

    // ---- Steam discovery ---------------------------------------------------

    private static IEnumerable<string> SteamValheimRemoteFolders()
    {
        // <steam>\userdata\<accountId>\892970\remote, ordered by most recent world
        // so a machine with multiple Steam accounts picks the active one.
        var found = new List<(string path, DateTime newest)>();
        foreach (var steam in SteamRoots())
        {
            var userdata = Path.Combine(steam, "userdata");
            if (!Directory.Exists(userdata)) continue;

            foreach (var user in SafeEnumerateDirectories(userdata))
            {
                var remote = Path.Combine(user, ValheimAppId, "remote");
                if (Directory.Exists(remote))
                    found.Add((remote, NewestFwlUtc(remote)));
            }
        }
        return found.OrderByDescending(f => f.newest).Select(f => f.path);
    }

    private static IEnumerable<string> SteamRoots()
    {
        // userdata always lives under the main Steam install directory.
        foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles" })
        {
            var pf = Environment.GetEnvironmentVariable(env);
            if (string.IsNullOrEmpty(pf)) continue;
            var steam = Path.Combine(pf, "Steam");
            if (Directory.Exists(steam)) yield return steam;
        }
    }

    private static DateTime NewestFwlUtc(string remote)
    {
        try
        {
            // Recursive — "*.fwl2" lives one folder down, inside each world's own subfolder.
            return Directory.EnumerateFiles(remote, "*.fwl2", SearchOption.AllDirectories)
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
        }
        catch { return DateTime.MinValue; }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return Array.Empty<string>(); }
    }

    private static string BuildError(IReadOnlyList<string> checkedPaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Could not find Valheim's Steam Cloud worlds folder.");
        sb.AppendLine();

        if (checkedPaths.Count == 0)
        {
            sb.AppendLine("No Steam installation was found under Program Files, so there was");
            sb.AppendLine("nowhere to look. Is Steam installed in a custom location?");
        }
        else
        {
            sb.AppendLine("Looked in (none of these exist):");
            foreach (var p in checkedPaths)
                sb.AppendLine("  • " + p);
        }

        sb.AppendLine();
        sb.AppendLine("Likely causes:");
        sb.AppendLine("  - Valheim hasn't been launched on this PC yet (the folder is created on first run).");
        sb.AppendLine("  - Steam Cloud is disabled for Valheim, or Valheim isn't installed through Steam.");
        sb.AppendLine("  - Steam is installed somewhere non-standard.");
        sb.AppendLine();
        sb.AppendLine("Fix: launch Valheim at least once, or set \"WorldsPathOverride\" in");
        sb.AppendLine("settings.json (next to the app) to your worlds folder.");
        return sb.ToString().TrimEnd();
    }
}
