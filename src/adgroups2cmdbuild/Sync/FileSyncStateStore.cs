using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AdGroups2Cmdbuild.Sync;

public sealed class FileSyncStateStore(IOptions<SyncOptions> options, ILogger<FileSyncStateStore> logger) : ISyncStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<SyncState> LoadAsync(CancellationToken cancellationToken)
    {
        var path = options.Value.StateFilePath;
        if (!File.Exists(path))
        {
            return new SyncState();
        }

        try
        {
            return await LoadFromPathAsync(path, cancellationToken);
        }
        catch (JsonException exception) when (File.Exists(BackupPath(path)))
        {
            logger.LogWarning(exception, "Sync state file {StatePath} is invalid; attempting recovery from backup", path);
            try
            {
                return await LoadFromPathAsync(BackupPath(path), cancellationToken);
            }
            catch (JsonException backupException)
            {
                throw new InvalidOperationException($"Sync state file {path} and backup {BackupPath(path)} are invalid.", backupException);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Sync state file {path} is invalid and no valid backup is available.", exception);
        }
    }

    private static async Task<SyncState> LoadFromPathAsync(string path, CancellationToken cancellationToken)
    {
        var state = new SyncState();
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var document = await JsonSerializer.DeserializeAsync<SyncStateDocument>(stream, JsonOptions, cancellationToken)
            ?? new SyncStateDocument();
        if (document.ManagedLogins is null)
        {
            return state;
        }

        foreach (var login in document.ManagedLogins.Where(login => !string.IsNullOrWhiteSpace(login)))
        {
            state.ManagedLogins.Add(login.Trim());
        }

        return state;
    }

    public async Task SaveAsync(SyncState state, CancellationToken cancellationToken)
    {
        var path = options.Value.StateFilePath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new SyncStateDocument
        {
            ManagedLogins = state.ManagedLogins.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            LastSuccessfulSyncUtc = DateTimeOffset.UtcNow
        };
        var tempPath = $"{path}.tmp";
        await using (var stream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
        }

        if (File.Exists(path))
        {
            File.Copy(path, BackupPath(path), overwrite: true);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static string BackupPath(string path)
    {
        return $"{path}.bak";
    }
}
