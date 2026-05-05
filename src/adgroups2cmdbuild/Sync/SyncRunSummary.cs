namespace AdGroups2Cmdbuild.Sync;

public sealed record SyncRunSummary(
    int AdUsers,
    int ProvisionedUsers,
    int CreatedUsers,
    int UpdatedUsers,
    int DisabledUsers,
    int SkippedUsers,
    bool DryRun);
