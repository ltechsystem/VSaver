using ValheimSync.Core.Models;

namespace ValheimSync.Core.Sync;

/// <summary>
/// Finds Valheim worlds saved in the modern per-folder layout: one subfolder of the
/// worlds path per world, named for the world, containing a versioned "_main.&lt;N&gt;.*"
/// record plus its ".chunk" zone files.
/// </summary>
public static class WorldScanner
{
    /// <summary>
    /// Finds every world with a complete revision under <paramref name="worldsPath"/>.
    /// Folders with no complete "_main.&lt;N&gt;.*" revision yet (e.g. a brand-new world
    /// Valheim hasn't finished saving once) and Valheim's own
    /// "&lt;World&gt;_backup_auto-…" snapshot folders are skipped.
    /// </summary>
    public static IReadOnlyList<WorldSave> Scan(string worldsPath)
    {
        if (!Directory.Exists(worldsPath))
            return Array.Empty<WorldSave>();

        var results = new List<WorldSave>();
        foreach (var dir in Directory.EnumerateDirectories(worldsPath))
        {
            var name = Path.GetFileName(dir);
            if (name.Contains("_backup_", StringComparison.OrdinalIgnoreCase)) continue;

            var save = TryReadCurrentRevision(dir, name);
            if (save is not null) results.Add(save);
        }
        return results.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Picks the highest revision whose .db2, .fwl2, .chunks AND .ok all exist, falling
    /// back to the next-highest if the newest one is mid-write (only some of the four
    /// landed yet) — that window is transient but real while Valheim is actively saving.
    /// Returns null if no revision in the folder is complete.
    /// </summary>
    private static WorldSave? TryReadCurrentRevision(string dir, string name)
    {
        var revisions = Directory.EnumerateFiles(dir, "_main.*.fwl2")
            .Select(ParseRevision)
            .Where(n => n is not null)
            .Select(n => n!.Value)
            .OrderByDescending(n => n);

        foreach (var revision in revisions)
        {
            var db2 = Path.Combine(dir, $"_main.{revision}.db2");
            var fwl2 = Path.Combine(dir, $"_main.{revision}.fwl2");
            var chunksIndex = Path.Combine(dir, $"_main.{revision}.chunks");
            var ok = Path.Combine(dir, $"_main.{revision}.ok");
            if (!File.Exists(db2) || !File.Exists(chunksIndex) || !File.Exists(ok)) continue;

            var chunks = Directory.EnumerateFiles(dir)
                .Where(f => string.Equals(Path.GetExtension(f), ".chunk", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new WorldSave(name, dir, revision, db2, fwl2, chunksIndex, ok, chunks);
        }
        return null;
    }

    /// <summary>Extracts N from "_main.&lt;N&gt;.fwl2"; null if the name doesn't match.</summary>
    private static int? ParseRevision(string fwl2Path)
    {
        var stem = Path.GetFileNameWithoutExtension(fwl2Path); // "_main.<N>"
        var parts = stem.Split('.');
        return parts.Length == 2 && parts[0] == "_main" && int.TryParse(parts[1], out var n) ? n : null;
    }
}
