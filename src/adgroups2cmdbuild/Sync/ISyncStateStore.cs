namespace AdGroups2Cmdbuild.Sync;

public interface ISyncStateStore
{
    Task<SyncState> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(SyncState state, CancellationToken cancellationToken);
}
