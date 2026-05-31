using Microsoft.Extensions.Options;
using AdGroups2Cmdbuild.Configuration;

namespace AdGroups2Cmdbuild.Sync;

public sealed class AdGroupSyncWorker(
    AdGroupSynchronizationService synchronizationService,
    SyncRunLock syncRunLock,
    SyncStatusStore statusStore,
    IOptions<SyncOptions> options,
    IOptions<DebugOptions> debugOptions,
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

        if (debugOptions.Value.IsBasicEnabled())
        {
            logger.LogInformation(
                "Debug {DebugLevel}: sync worker configured: intervalSeconds={IntervalSeconds}, runImmediately={RunImmediately}, dryRun={DryRun}",
                debugOptions.Value.NormalizedLevel(),
                settings.IntervalSeconds,
                settings.RunImmediately,
                settings.DryRun);
        }

        if (settings.RunImmediately)
        {
            var succeeded = await RunGuardedAsync(stoppingToken);
            if (!succeeded)
            {
                await DelayAfterFailureAsync(settings, stoppingToken);
            }
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.IntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var succeeded = await RunGuardedAsync(stoppingToken);
            if (!succeeded)
            {
                await DelayAfterFailureAsync(settings, stoppingToken);
            }
        }
    }

    private async Task<bool> RunGuardedAsync(CancellationToken stoppingToken)
    {
        await using var lease = await syncRunLock.TryAcquireAsync(stoppingToken);
        if (lease is null)
        {
            statusStore.MarkFailed(new InvalidOperationException("Another sync run holds the local instance lock."));
            return false;
        }

        statusStore.MarkStarted();
        if (debugOptions.Value.IsBasicEnabled())
        {
            logger.LogInformation("Debug {DebugLevel}: AD group sync run started", debugOptions.Value.NormalizedLevel());
        }

        try
        {
            var summary = await synchronizationService.RunOnceAsync(stoppingToken);
            statusStore.MarkCompleted(summary);
            logger.LogInformation(
                "AD group sync completed: AD users={AdUsers}, provisioned={ProvisionedUsers}, created={CreatedUsers}, updated={UpdatedUsers}, disabled={DisabledUsers}, skipped={SkippedUsers}, failed={FailedUsers}, dryRun={DryRun}",
                summary.AdUsers,
                summary.ProvisionedUsers,
                summary.CreatedUsers,
                summary.UpdatedUsers,
                summary.DisabledUsers,
                summary.SkippedUsers,
                summary.FailedUsers,
                summary.DryRun);
            return !summary.HasFailures;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            statusStore.MarkFailed(exception);
            logger.LogError(exception, "AD group sync failed");
            return false;
        }
    }

    private async Task DelayAfterFailureAsync(SyncOptions settings, CancellationToken stoppingToken)
    {
        if (settings.FailureBackoffSeconds <= 0)
        {
            return;
        }

        logger.LogWarning("Delaying next sync attempt for {DelaySeconds} second(s) after failure", settings.FailureBackoffSeconds);
        await Task.Delay(TimeSpan.FromSeconds(settings.FailureBackoffSeconds), stoppingToken);
    }
}
