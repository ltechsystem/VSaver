namespace ValheimSync.Core.Models;

/// <summary>A file as it exists in the cloud storage folder.</summary>
public sealed record RemoteFile(
    string Id,
    string Name,
    string? Md5Checksum,
    long SizeBytes,
    DateTimeOffset ModifiedTime,
    int? Revision = null);
