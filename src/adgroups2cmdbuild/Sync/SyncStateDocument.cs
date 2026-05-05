namespace AdGroups2Cmdbuild.Sync;

public sealed class SyncStateDocument
{
    public List<string> ManagedLogins { get; set; } = [];

    public DateTimeOffset? LastSuccessfulSyncUtc { get; set; }
}
