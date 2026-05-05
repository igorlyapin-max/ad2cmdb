using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AdGroups2Cmdbuild.Sync;

public sealed class FileSyncStateStore(IOptions<SyncOptions> options) : ISyncStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<SyncState> LoadAsync(CancellationToken cancellationToken)
    {
        var state = new SyncState();
        var path = options.Value.StateFilePath;
        if (!File.Exists(path))
        {
            return state;
        }

        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<SyncStateDocument>(stream, JsonOptions, cancellationToken)
            ?? new SyncStateDocument();
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
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }
}
