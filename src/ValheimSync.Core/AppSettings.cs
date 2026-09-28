using System.Text.Json;
using System.Text.Json.Serialization;
using ValheimSync.Core.Sync;

namespace ValheimSync.Core;

/// <summary>
/// Persisted next to the executable as settings.json.
/// Kept deliberately simple so non-technical friends can be sent one folder.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// The player's identity, used for world locks. Defaults to empty; on first connect
    /// it's auto-filled from the signed-in Google account's email (local part, before the @).
    /// The user can override it with the pen icon in the UI.
    /// </summary>
    public string PlayerName { get; set; } = "";

    /// <summary>
    /// The shared Google Drive folder id this machine syncs against. Entered by the user in
    /// the app on first run (or pre-seeded in a private settings.json) and persisted here.
    /// Deliberately NOT hardcoded anywhere in source — the folder is private to the group, so
    /// it must never ship in the public repo or a public release. Blank until provided.
    /// </summary>
    public string DriveFolderId { get; set; } = "";

    /// <summary>Override if Valheim saves live somewhere non-standard.</summary>
    public string? WorldsPathOverride { get; set; }

    /// <summary>Fallback poll interval for remote changes.</summary>
    public int PollIntervalMinutes { get; set; } = 5;

    /// <summary>Seconds a save file must be untouched before we trust it is complete.</summary>
    public int DebounceSeconds { get; set; } = 60;

    /// <summary>
    /// While Valheim is open, how often (minutes) to push the in-progress save if it
    /// changed and has settled. Keeps a crash from losing more than this interval and
    /// lets friends see progress without waiting for the game to close.
    /// </summary>
    public int InGameUploadMinutes { get; set; } = 5;

    /// <summary>World names the user has ticked for syncing.</summary>
    public HashSet<string> SelectedWorlds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where this machine's Valheim keeps its worlds. Uses the explicit override if set,
    /// otherwise Valheim's own LocalLow "local storage" folder — the folder the game itself
    /// reads and writes. Steam's Cloud "remote" cache is deliberately never auto-detected
    /// here; see <see cref="ValheimSaveLocations"/>. Not persisted — resolved fresh per
    /// machine.
    /// </summary>
    [JsonIgnore]
    public string WorldsPath =>
        !string.IsNullOrWhiteSpace(WorldsPathOverride)
            ? WorldsPathOverride
            : ValheimSaveLocations.ResolveLocalLowWorldsFolder()
                ?? throw new DirectoryNotFoundException(ValheimSaveLocations.LocalLowNotFoundMessage);

    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (s is not null)
                {
                    // Deserialization replaces the set and silently drops the OrdinalIgnoreCase
                    // comparer — rewrap so world-name lookups stay case-insensitive after a reload.
                    s.SelectedWorlds = new HashSet<string>(s.SelectedWorlds, StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
        }
        catch { /* corrupt settings — start fresh rather than crash */ }
        return new AppSettings();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
