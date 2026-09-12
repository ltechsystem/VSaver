namespace ValheimSync.Core.Sync;

/// <summary>
/// Naming convention for a world's cloud representation: the whole save lives as one file,
/// "&lt;world&gt;.zip" (see <see cref="WorldZip"/>), replacing the old scattered-files-plus-
/// manifest scheme. One file means one MD5 and one atomic Drive write — Drive never exposes
/// a partially-written file to a reader, so there's no "torn" state to detect any more.
/// </summary>
public static class WorldArchive
{
    public static string ZipName(string worldName) => worldName + ".zip";

    /// <summary>The single rolling remote backup for a world (see SyncEngine.BackupRemoteAsync).
    /// Deliberately doesn't end in ".zip" so a plain "*.zip" listing never mistakes it for a
    /// real world.</summary>
    public static string BackupZipName(string worldName) => worldName + ".zip.bak";
}
