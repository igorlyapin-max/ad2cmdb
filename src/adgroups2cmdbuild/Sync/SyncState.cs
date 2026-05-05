namespace AdGroups2Cmdbuild.Sync;

public sealed class SyncState
{
    public HashSet<string> ManagedLogins { get; } = new(StringComparer.OrdinalIgnoreCase);
}
