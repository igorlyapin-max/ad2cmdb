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
    private readonly object activeRunGate = new();
    private readonly CancellationTokenSource shutdownRequested = new();
    private CancellationTokenSource? activeRunCancellation;
    private Task<bool>? activeRunTask;

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

        using var stoppingRegistration = stoppingToken.Register(
            static state => ((CancellationTokenSource)state!).Cancel(),
            shutdownRequested);
        var schedulerToken = shutdownRequested.Token;

        try
        {
            if (settings.RunImmediately)
            {
                var succeeded = await RunTrackedAsync();
                if (!succeeded)
                {
                    await DelayAfterFailureAsync(settings, schedulerToken);
                }
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.IntervalSeconds));
            while (await timer.WaitForNextTickAsync(schedulerToken))
            {
                var succeeded = await RunTrackedAsync();
                if (!succeeded)
                {
                    await DelayAfterFailureAsync(settings, schedulerToken);
                }
            }
        }
        catch (OperationCanceledException) when (schedulerToken.IsCancellationRequested || stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("AD group sync worker stopped scheduling new runs");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("AD group sync worker shutdown requested");
        await shutdownRequested.CancelAsync();

        var activeRun = CurrentActiveRun();
        if (activeRun is not null && !activeRun.IsCompleted)
        {
            var gracePeriod = TimeSpan.FromSeconds(Math.Max(0, options.Value.ShutdownGracePeriodSeconds));
            if (gracePeriod > TimeSpan.Zero)
            {
                logger.LogInformation(
                    "Waiting up to {GracePeriodSeconds} second(s) for active AD group sync run to finish",
                    (int)gracePeriod.TotalSeconds);
                var completed = await Task.WhenAny(activeRun, Task.Delay(gracePeriod, cancellationToken)) == activeRun;
                if (completed)
                {
                    logger.LogInformation("Active AD group sync run finished during shutdown grace period");
                }
                else
                {
                    logger.LogWarning("Canceling active AD group sync run after shutdown grace period expired");
                    CancelActiveRun();
                }
            }
            else
            {
                logger.LogWarning("Canceling active AD group sync run immediately because shutdown grace period is zero");
                CancelActiveRun();
            }
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        shutdownRequested.Dispose();
        base.Dispose();
    }

    private async Task<bool> RunTrackedAsync()
    {
        using var runCancellation = new CancellationTokenSource();
        var runCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (activeRunGate)
        {
            activeRunCancellation = runCancellation;
            activeRunTask = runCompletion.Task;
        }

        try
        {
            var succeeded = await RunGuardedAsync(runCancellation.Token);
            runCompletion.TrySetResult(succeeded);
            return succeeded;
        }
        catch (Exception exception)
        {
            runCompletion.TrySetException(exception);
            throw;
        }
        finally
        {
            lock (activeRunGate)
            {
                if (ReferenceEquals(activeRunCancellation, runCancellation))
                {
                    activeRunCancellation = null;
                    activeRunTask = null;
                }
            }
        }
    }

    private Task<bool>? CurrentActiveRun()
    {
        lock (activeRunGate)
        {
            return activeRunTask;
        }
    }

    private void CancelActiveRun()
    {
        lock (activeRunGate)
        {
            activeRunCancellation?.Cancel();
        }
    }

    private async Task<bool> RunGuardedAsync(CancellationToken stoppingToken)
    {
        SyncRunLease? lease = null;

        try
        {
            lease = await syncRunLock.TryAcquireAsync(stoppingToken);
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
            var exception = new OperationCanceledException("AD group sync run was canceled.");
            statusStore.MarkFailed(exception);
            logger.LogWarning(exception, "AD group sync run canceled");
            return false;
        }
        catch (Exception exception)
        {
            statusStore.MarkFailed(exception);
            logger.LogError(exception, "AD group sync failed");
            return false;
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }
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
