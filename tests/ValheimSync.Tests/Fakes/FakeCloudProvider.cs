using System.Security.Cryptography;
using System.Text;
using ValheimSync.Core.Models;
using ValheimSync.Core.Storage;

namespace ValheimSync.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ICloudStorageProvider"/> for exercising <c>SyncEngine</c> without
/// Google Drive. Mirrors the real provider's semantics: files keyed by name
/// (case-insensitive), real MD5s so the engine's hash comparison works, locks in a dict.
/// Every mutating call is appended to <see cref="Calls"/> so tests can assert ordering
/// (e.g. backup-before-upload, .db-before-.fwl).
/// </summary>
internal sealed class FakeCloudProvider : ICloudStorageProvider
{
    public readonly Dictionary<string, (byte[] Content, DateTimeOffset Modified)> Files =
        new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, WorldLock> Locks = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Calls = new();

    public bool Initialized;

    /// <summary>When true, downloads write garbage so hash verification must fail.</summary>
    public bool CorruptDownloads;

    /// <summary>Test hook fired at the top of every <see cref="TryAcquireLockAsync"/> call,
    /// before it checks the current lock — lets a test simulate another machine grabbing
    /// the lock in the race window between the caller's own GetLockAsync check and its
    /// TryAcquireLockAsync call.</summary>
    public Action? OnTryAcquireLock;

    /// <summary>When true, <see cref="DownloadAsync"/> hangs until its CancellationToken
    /// fires, like a stalled real network call — used to test SyncEngine's sync timeout.</summary>
    public bool HangDownloads;

    public string ProviderName => "Fake";

    public Task InitializeAsync(CancellationToken ct = default)
    {
        Initialized = true;
        return Task.CompletedTask;
    }

    public Task<string?> GetAccountEmailAsync(CancellationToken ct = default) =>
        Task.FromResult<string?>("fake@example.com");

    public void Seed(string name, string content, DateTimeOffset? modified = null) =>
        Files[name] = (Encoding.UTF8.GetBytes(content), modified ?? DateTimeOffset.UtcNow);

    public void SeedBytes(string name, byte[] content, DateTimeOffset? modified = null) =>
        Files[name] = (content, modified ?? DateTimeOffset.UtcNow);

    public string ContentOf(string name) => Encoding.UTF8.GetString(Files[name].Content);

    private static string Md5(byte[] bytes) =>
        Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();

    public Task<IReadOnlyList<RemoteFile>> ListFilesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<RemoteFile> list = Files
            .Select(kv => new RemoteFile(kv.Key, kv.Key, Md5(kv.Value.Content),
                kv.Value.Content.LongLength, kv.Value.Modified))
            .ToList();
        return Task.FromResult(list);
    }

    public async Task UploadAsync(string localPath, string remoteName,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(localPath, ct);
        Files[remoteName] = (bytes, DateTimeOffset.UtcNow);
        Calls.Add($"upload:{remoteName}");
    }

    public async Task DownloadAsync(string remoteName, string localPath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (HangDownloads)
            await Task.Delay(Timeout.Infinite, ct); // stalled "network call" — only ct ends this
        if (!Files.TryGetValue(remoteName, out var f))
            throw new FileNotFoundException($"'{remoteName}' not found in fake cloud.");
        var bytes = CorruptDownloads ? Encoding.UTF8.GetBytes("corrupted!!") : f.Content;
        await File.WriteAllBytesAsync(localPath, bytes, ct);
        Calls.Add($"download:{remoteName}");
    }

    public Task<bool> CopyAsync(string sourceRemoteName, string destRemoteName,
        CancellationToken ct = default)
    {
        if (!Files.TryGetValue(sourceRemoteName, out var src))
            return Task.FromResult(false);
        Files[destRemoteName] = (src.Content.ToArray(), DateTimeOffset.UtcNow);
        Calls.Add($"copy:{sourceRemoteName}->{destRemoteName}");
        return Task.FromResult(true);
    }

    public Task DeleteAsync(string remoteName, CancellationToken ct = default)
    {
        Files.Remove(remoteName);
        Calls.Add($"delete:{remoteName}");
        return Task.CompletedTask;
    }

    public Task<WorldLock?> GetLockAsync(string worldName, CancellationToken ct = default) =>
        Task.FromResult(Locks.TryGetValue(worldName, out var l) ? l : null);

    public Task<bool> TryAcquireLockAsync(string worldName, string playerName,
        CancellationToken ct = default)
    {
        OnTryAcquireLock?.Invoke();
        if (Locks.TryGetValue(worldName, out var cur) && !cur.IsStale &&
            !string.Equals(cur.PlayerName, playerName, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);
        Locks[worldName] = new WorldLock(playerName, DateTimeOffset.UtcNow);
        return Task.FromResult(true);
    }

    public Task ReleaseLockAsync(string worldName, string playerName, CancellationToken ct = default)
    {
        if (Locks.TryGetValue(worldName, out var cur) &&
            string.Equals(cur.PlayerName, playerName, StringComparison.OrdinalIgnoreCase))
            Locks.Remove(worldName);
        return Task.CompletedTask;
    }
}
