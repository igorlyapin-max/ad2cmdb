using Microsoft.Extensions.Options;

namespace AdGroups2Cmdbuild.Sync;

public sealed class SyncRunLock(IOptions<SyncOptions> options, ILogger<SyncRunLock> logger)
{
    public async Task<SyncRunLease?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        var path = options.Value.InstanceLockPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous);

            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, leaveOpen: true);
            await writer.WriteLineAsync($"pid={Environment.ProcessId}");
            await writer.WriteLineAsync($"acquiredUtc={DateTimeOffset.UtcNow:O}");
            await writer.FlushAsync(cancellationToken);
            stream.Position = 0;
            return new SyncRunLease(stream);
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Could not acquire sync instance lock {LockPath}; another process may be running", path);
            return null;
        }
    }
}

public sealed class SyncRunLease(FileStream stream) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        return stream.DisposeAsync();
    }
}
