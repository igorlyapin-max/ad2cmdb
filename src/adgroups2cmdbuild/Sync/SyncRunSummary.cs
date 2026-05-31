namespace AdGroups2Cmdbuild.Sync;

public sealed record SyncRunSummary(
    int AdUsers,
    int ProvisionedUsers,
    int CreatedUsers,
    int UpdatedUsers,
    int DisabledUsers,
    int SkippedUsers,
    int FailedUsers,
    bool DryRun)
{
    public bool HasFailures => FailedUsers > 0;
}
