namespace ValheimSync.Core.Models;

/// <summary>
/// A Valheim world saved in the modern per-world-folder layout: a versioned "main"
/// record ("_main.&lt;N&gt;.db2" / ".fwl2" / ".chunks" / ".ok") plus one ".chunk" file per
/// world zone that Valheim rewrites only when that zone actually changed since the last
/// save.
///
/// <see cref="Revision"/> is the highest "_main.&lt;N&gt;.*" set for which .db2, .fwl2,
/// .chunks AND .ok all exist — Valheim writes .ok last, so a revision missing any of them
/// is a save still in progress (or interrupted) and is never surfaced as current.
///
/// The live ".chunk" set is trusted straight from the directory listing rather than by
/// parsing ".chunks": Valheim itself prunes superseded chunk files and older revisions
/// once a save completes, so whatever sits in <see cref="FolderPath"/> alongside the
/// current revision's main files already is the live set. ".chunks" itself still has to
/// travel with the rest of the revision (Valheim reads it back on load), it's just never
/// parsed here.
/// </summary>
public sealed record WorldSave(
    string Name,
    string FolderPath,
    int Revision,
    string Db2Path,
    string Fwl2Path,
    string ChunksIndexPath,
    string OkPath,
    IReadOnlyList<string> ChunkPaths)
{
    /// <summary>Every file that makes up this revision — the unit sync must move together.</summary>
    public IEnumerable<string> AllFilePaths =>
        new[] { Db2Path, Fwl2Path, ChunksIndexPath, OkPath }.Concat(ChunkPaths);

    public DateTime LastWriteUtc => AllFilePaths
        .Where(File.Exists)
        .Select(File.GetLastWriteTimeUtc)
        .DefaultIfEmpty(DateTime.MinValue)
        .Max();

    public long SizeBytes => AllFilePaths
        .Where(File.Exists)
        .Sum(p => new FileInfo(p).Length);
}
