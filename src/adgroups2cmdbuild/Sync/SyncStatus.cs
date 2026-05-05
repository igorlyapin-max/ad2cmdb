namespace AdGroups2Cmdbuild.Sync;

public sealed record SyncStatus(
    bool IsRunning,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastCompletedUtc,
    bool? LastSucceeded,
    string? LastError,
    SyncRunSummary? LastSummary);
