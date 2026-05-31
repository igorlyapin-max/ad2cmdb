namespace AdGroups2Cmdbuild.Sync;

public sealed class SyncStatusStore
{
    private readonly object gate = new();
    private SyncStatus status = new(false, null, null, null, null, null);

    public SyncStatus Current
    {
        get
        {
            lock (gate)
            {
                return status;
            }
        }
    }

    public void MarkStarted()
    {
        lock (gate)
        {
            status = status with
            {
                IsRunning = true,
                LastStartedUtc = DateTimeOffset.UtcNow,
                LastError = null
            };
        }
    }

    public void MarkCompleted(SyncRunSummary summary)
    {
        var hasFailures = summary.HasFailures;
        lock (gate)
        {
            status = status with
            {
                IsRunning = false,
                LastCompletedUtc = DateTimeOffset.UtcNow,
                LastSucceeded = !hasFailures,
                LastError = hasFailures ? $"{summary.FailedUsers} user operation(s) failed during sync run" : null,
                LastSummary = summary
            };
        }
    }

    public void MarkFailed(Exception exception)
    {
        lock (gate)
        {
            status = status with
            {
                IsRunning = false,
                LastCompletedUtc = DateTimeOffset.UtcNow,
                LastSucceeded = false,
                LastError = exception.Message
            };
        }
    }
}
