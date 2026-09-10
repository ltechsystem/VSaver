using System.Text;

namespace ValheimSync.Core.Sync;

/// <summary>
/// The "&lt;world&gt;/manifest.commit" marker for a world (see <see cref="Models.WorldSave"/>):
/// uploaded LAST after every file in the revision (chunks, .db2, .fwl2, .chunks, .ok), it
/// certifies that the exact file set currently living under the "&lt;world&gt;/" prefix in
/// the shared cloud folder was uploaded together. Its content is canonical (byte-for-byte
/// reproducible from the sorted (name, md5) list), so its own MD5 is predictable straight
/// from the folder listing — no download needed to check consistency.
///
/// A remote whose files' MD5s don't hash to the marker's MD5 is "torn" (an upload was
/// interrupted) and must not be downloaded. There is no legacy/pre-marker era for this
/// format, so a file set with no marker at all (or a stale one) is always torn — unlike
/// a marker-less remote, which would be trusted.
/// </summary>
public static class CommitMarker
{
    /// <summary>The flat-storage prefix every file of this world's save lives under in
    /// the shared cloud folder — Drive has no real subfolders, so this is just a naming
    /// convention (e.g. "test10/_main.1.db2").</summary>
    public static string RemotePrefix(string worldName) => worldName + "/";

    public static string Name(string worldName) => RemotePrefix(worldName) + "manifest.commit";

    /// <summary>
    /// Canonical marker content for a file set: a sorted-by-name JSON array of
    /// {"Name":...,"Md5":...} entries. Sorted so the content — and therefore its MD5 —
    /// doesn't depend on upload order. Never reformat this — consistency checks depend
    /// on its bytes being reproducible.
    /// </summary>
    public static string Content(IEnumerable<(string Name, string Md5)> files)
    {
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var (name, md5) in files.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append($"{{\"Name\":\"{name}\",\"Md5\":\"{md5}\"}}");
        }
        sb.Append(']');
        return sb.ToString();
    }
}
