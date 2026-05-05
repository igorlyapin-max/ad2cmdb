using Microsoft.Extensions.Options;

namespace AdGroups2Cmdbuild.Sync;

public sealed class AdGroupSyncWorker(
    AdGroupSynchronizationService synchronizationService,
    SyncStatusStore statusStore,
    IOptions<SyncOptions> options,
    ILogger<AdGroupSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogWarning("AD group synchronization is disabled by configuration");
            return;
        }

        if (settings.RunImmediately)
        {
            await RunGuardedAsync(stoppingToken);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.IntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunGuardedAsync(stoppingToken);
        }
    }

    private async Task RunGuardedAsync(CancellationToken stoppingToken)
    {
        statusStore.MarkStarted();
        try
        {
            var summary = await synchronizationService.RunOnceAsync(stoppingToken);
            statusStore.MarkCompleted(summary);
            logger.LogInformation(
                "AD group sync completed: AD users={AdUsers}, provisioned={ProvisionedUsers}, created={CreatedUsers}, updated={UpdatedUsers}, disabled={DisabledUsers}, skipped={SkippedUsers}, dryRun={DryRun}",
                summary.AdUsers,
                summary.ProvisionedUsers,
                summary.CreatedUsers,
                summary.UpdatedUsers,
                summary.DisabledUsers,
                summary.SkippedUsers,
                summary.DryRun);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            statusStore.MarkFailed(exception);
            logger.LogError(exception, "AD group sync failed");
        }
    }
}
