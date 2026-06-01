using System.Text.Json;
using AdGroups2Cmdbuild.ActiveDirectory;
using AdGroups2Cmdbuild.Cmdbuild;
using AdGroups2Cmdbuild.Configuration;
using AdGroups2Cmdbuild.Resilience;
using AdGroups2Cmdbuild.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var tests = new (string Name, Func<Task> Run)[]
{
    ("per-user failure continues batch", PerUserFailureContinuesBatch),
    ("state store recovers from backup", StateStoreRecoversFromBackup),
    ("partial failure status is visible", PartialFailureStatusIsVisible),
    ("debug sensitive values are masked by default", DebugSensitiveValuesAreMaskedByDefault),
    ("retry backoff uses exponential cap", RetryBackoffUsesExponentialCap),
    ("CMDBuild retry retries transient status", CmdbuildRetryRetriesTransientStatus),
    ("CMDBuild retry skips authorization status", CmdbuildRetrySkipsAuthorizationStatus),
    ("worker stop waits for active sync run", WorkerStopWaitsForActiveRun),
    ("worker stop cancels after grace period", WorkerStopCancelsAfterGracePeriod)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

if (failed > 0)
{
    Environment.Exit(1);
}

static async Task PerUserFailureContinuesBatch()
{
    var adSnapshot = NewAdSnapshot("CMDBuildUsers", "alice", "bob");
    var cmdbSnapshot = NewCmdbSnapshot("CMDBuildUsers");
    var stateStore = new InMemoryStateStore();
    var cmdbuildClient = new FakeCmdbuildClient(cmdbSnapshot)
    {
        FailCreateLogins = { "alice" }
    };

    var service = new AdGroupSynchronizationService(
        new FakeActiveDirectoryClient(adSnapshot),
        cmdbuildClient,
        stateStore,
        Options.Create(new ActiveDirectoryOptions
        {
            GroupNames = ["CMDBuildUsers"],
            ProvisioningGroupName = "CMDBuildUsers"
        }),
        Options.Create(new CmdbuildOptions()),
        Options.Create(new SyncOptions { DryRun = false }),
        Options.Create(new DebugOptions()),
        NullLogger<AdGroupSynchronizationService>.Instance);

    var summary = await service.RunOnceAsync(CancellationToken.None);

    AssertEqual(1, summary.CreatedUsers, "created users");
    AssertEqual(1, summary.FailedUsers, "failed users");
    AssertContains("bob", cmdbuildClient.CreatedLogins, "created logins");
    AssertDoesNotContain("alice", stateStore.SavedState.ManagedLogins, "saved managed logins");
    AssertContains("bob", stateStore.SavedState.ManagedLogins, "saved managed logins");
}

static async Task StateStoreRecoversFromBackup()
{
    var directory = Path.Combine(Path.GetTempPath(), $"adgroups2cmdbuild-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, "state.json");
        await File.WriteAllTextAsync(path, "{ invalid json");
        await File.WriteAllTextAsync(
            $"{path}.bak",
            JsonSerializer.Serialize(new SyncStateDocument { ManagedLogins = ["backup-user"] }));

        var store = new FileSyncStateStore(
            Options.Create(new SyncOptions { StateFilePath = path }),
            NullLogger<FileSyncStateStore>.Instance);

        var state = await store.LoadAsync(CancellationToken.None);
        AssertContains("backup-user", state.ManagedLogins, "recovered state");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static Task PartialFailureStatusIsVisible()
{
    var store = new SyncStatusStore();
    store.MarkCompleted(new SyncRunSummary(
        AdUsers: 2,
        ProvisionedUsers: 2,
        CreatedUsers: 1,
        UpdatedUsers: 0,
        DisabledUsers: 0,
        SkippedUsers: 0,
        FailedUsers: 1,
        DryRun: false));

    AssertEqual(false, store.Current.LastSucceeded, "last succeeded");
    AssertTrue(!string.IsNullOrWhiteSpace(store.Current.LastError), "last error should be set");
    return Task.CompletedTask;
}

static Task DebugSensitiveValuesAreMaskedByDefault()
{
    var options = new DebugOptions { Enabled = true, Level = "Verbose" };
    AssertEqual("<redacted>", options.FormatSensitive("alice"), "masked value");

    options.LogSensitiveValues = true;
    AssertEqual("alice", options.FormatSensitive("alice"), "unmasked value");
    return Task.CompletedTask;
}

static Task RetryBackoffUsesExponentialCap()
{
    AssertEqual(250, (int)RetryBackoff.CalculateDelay(1, 250, 1000, 0).TotalMilliseconds, "attempt 1 delay");
    AssertEqual(500, (int)RetryBackoff.CalculateDelay(2, 250, 1000, 0).TotalMilliseconds, "attempt 2 delay");
    AssertEqual(1000, (int)RetryBackoff.CalculateDelay(3, 250, 1000, 0).TotalMilliseconds, "attempt 3 delay");
    AssertEqual(1000, (int)RetryBackoff.CalculateDelay(4, 250, 1000, 0).TotalMilliseconds, "attempt 4 delay");
    return Task.CompletedTask;
}

static async Task CmdbuildRetryRetriesTransientStatus()
{
    var handler = new SequenceHttpHandler(
        _ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("temporary")
        },
        _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        });

    var client = NewCmdbuildClient(handler, retryAttempts: 2);
    await client.CheckConnectionAsync(CancellationToken.None);

    AssertEqual(2, handler.RequestCount, "CMDBuild request count");
}

static async Task CmdbuildRetrySkipsAuthorizationStatus()
{
    var handler = new SequenceHttpHandler(
        _ => new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("unauthorized")
        },
        _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        });

    var client = NewCmdbuildClient(handler, retryAttempts: 2);
    try
    {
        await client.CheckConnectionAsync(CancellationToken.None);
    }
    catch (HttpRequestException)
    {
        AssertEqual(1, handler.RequestCount, "CMDBuild request count");
        return;
    }

    throw new InvalidOperationException("expected CMDBuild authorization failure");
}

static async Task WorkerStopWaitsForActiveRun()
{
    var directory = Path.Combine(Path.GetTempPath(), $"adgroups2cmdbuild-worker-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var groupName = "CMDBuildUsers";
        var adClient = new FakeActiveDirectoryClient(NewAdSnapshot(groupName, "alice"), waitForRelease: true);
        var cmdbuildClient = new FakeCmdbuildClient(NewCmdbSnapshot(groupName));
        var statusStore = new SyncStatusStore();
        var syncOptions = NewWorkerSyncOptions(directory, shutdownGracePeriodSeconds: 2);
        using var worker = NewWorker(adClient, cmdbuildClient, statusStore, syncOptions, groupName);

        await worker.StartAsync(CancellationToken.None);
        await WaitAsync(adClient.ReadStarted.Task, TimeSpan.FromSeconds(2), "AD read start");

        var stopTask = worker.StopAsync(CancellationToken.None);
        await Task.Delay(150);
        AssertEqual(false, stopTask.IsCompleted, "stop completed before run release");

        adClient.ContinueRead.TrySetResult(true);
        await WaitAsync(stopTask, TimeSpan.FromSeconds(3), "worker stop");

        AssertEqual(false, adClient.Cancelled, "AD read canceled");
        AssertEqual(true, statusStore.Current.LastSucceeded, "last succeeded");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task WorkerStopCancelsAfterGracePeriod()
{
    var directory = Path.Combine(Path.GetTempPath(), $"adgroups2cmdbuild-worker-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var groupName = "CMDBuildUsers";
        var adClient = new FakeActiveDirectoryClient(NewAdSnapshot(groupName, "alice"), waitForRelease: true);
        var cmdbuildClient = new FakeCmdbuildClient(NewCmdbSnapshot(groupName));
        var statusStore = new SyncStatusStore();
        var syncOptions = NewWorkerSyncOptions(directory, shutdownGracePeriodSeconds: 0);
        using var worker = NewWorker(adClient, cmdbuildClient, statusStore, syncOptions, groupName);

        await worker.StartAsync(CancellationToken.None);
        await WaitAsync(adClient.ReadStarted.Task, TimeSpan.FromSeconds(2), "AD read start");

        await WaitAsync(worker.StopAsync(CancellationToken.None), TimeSpan.FromSeconds(2), "worker stop");

        AssertEqual(true, adClient.Cancelled, "AD read canceled");
        AssertEqual(false, statusStore.Current.LastSucceeded, "last succeeded");
        AssertTrue(
            statusStore.Current.LastError?.Contains("canceled", StringComparison.OrdinalIgnoreCase) == true,
            "last error should describe cancellation");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static AdGroupSnapshot NewAdSnapshot(string groupName, params string[] logins)
{
    var snapshot = new AdGroupSnapshot();
    snapshot.Groups[groupName] = new Dictionary<string, AdUserRecord>(StringComparer.OrdinalIgnoreCase);
    snapshot.FoundGroupNames.Add(groupName);
    foreach (var login in logins)
    {
        var user = new AdUserRecord(login, $"CN={login},DC=example,DC=local", login, $"{login}@example.local");
        snapshot.Groups[groupName][login] = user;
        snapshot.Users[login] = user;
    }

    return snapshot;
}

static CmdbuildSnapshot NewCmdbSnapshot(string roleName)
{
    var snapshot = new CmdbuildSnapshot();
    var role = new CmdbuildRole("1", roleName);
    snapshot.RolesByName[roleName] = role;
    snapshot.RolesById[role.Id] = role;
    return snapshot;
}

static CmdbuildClient NewCmdbuildClient(SequenceHttpHandler handler, int retryAttempts)
{
    return new CmdbuildClient(
        new HttpClient(handler),
        Options.Create(new CmdbuildOptions
        {
            BaseUrl = "http://cmdbuild.example.local/cmdbuild/services/rest/v3",
            Username = "cmdbuild-sync",
            Password = "secret",
            RetryAttempts = retryAttempts,
            RetryBaseDelayMs = 1,
            RetryMaxDelayMs = 1,
            RetryJitterPercent = 0
        }),
        Options.Create(new DebugOptions()),
        NullLogger<CmdbuildClient>.Instance);
}

static SyncOptions NewWorkerSyncOptions(string directory, int shutdownGracePeriodSeconds)
{
    return new SyncOptions
    {
        DryRun = true,
        RunImmediately = true,
        IntervalSeconds = 300,
        StateFilePath = Path.Combine(directory, "state.json"),
        InstanceLockPath = Path.Combine(directory, "sync.lock"),
        ShutdownGracePeriodSeconds = shutdownGracePeriodSeconds
    };
}

static AdGroupSyncWorker NewWorker(
    FakeActiveDirectoryClient adClient,
    FakeCmdbuildClient cmdbuildClient,
    SyncStatusStore statusStore,
    SyncOptions syncOptions,
    string groupName)
{
    var adOptions = Options.Create(new ActiveDirectoryOptions
    {
        GroupNames = [groupName],
        ProvisioningGroupName = groupName
    });
    var cmdbuildOptions = Options.Create(new CmdbuildOptions());
    var debugOptions = Options.Create(new DebugOptions());
    var syncOptionsAccessor = Options.Create(syncOptions);
    var synchronizationService = new AdGroupSynchronizationService(
        adClient,
        cmdbuildClient,
        new InMemoryStateStore(),
        adOptions,
        cmdbuildOptions,
        syncOptionsAccessor,
        debugOptions,
        NullLogger<AdGroupSynchronizationService>.Instance);

    return new AdGroupSyncWorker(
        synchronizationService,
        new SyncRunLock(syncOptionsAccessor, NullLogger<SyncRunLock>.Instance),
        statusStore,
        syncOptionsAccessor,
        debugOptions,
        NullLogger<AdGroupSyncWorker>.Instance);
}

static async Task WaitAsync(Task task, TimeSpan timeout, string operation)
{
    using var timeoutCancellation = new CancellationTokenSource(timeout);
    try
    {
        await task.WaitAsync(timeoutCancellation.Token);
    }
    catch (OperationCanceledException)
    {
        throw new InvalidOperationException($"{operation} timed out");
    }
}

static void AssertEqual<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertContains(string value, IEnumerable<string> values, string name)
{
    if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"{name}: expected to contain {value}");
    }
}

static void AssertDoesNotContain(string value, IEnumerable<string> values, string name)
{
    if (values.Contains(value, StringComparer.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"{name}: expected not to contain {value}");
    }
}

internal sealed class FakeActiveDirectoryClient(AdGroupSnapshot snapshot, bool waitForRelease = false) : IActiveDirectoryClient
{
    public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> ContinueRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Cancelled { get; private set; }

    public Task<AdGroupSnapshot> ReadGroupsAsync(CancellationToken cancellationToken)
    {
        return ReadGroupsCoreAsync(cancellationToken);
    }

    public Task CheckConnectionAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task<AdGroupSnapshot> ReadGroupsCoreAsync(CancellationToken cancellationToken)
    {
        ReadStarted.TrySetResult(true);
        if (waitForRelease)
        {
            try
            {
                await ContinueRead.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
        }

        return snapshot;
    }
}

internal sealed class FakeCmdbuildClient(CmdbuildSnapshot snapshot) : ICmdbuildClient
{
    public HashSet<string> FailCreateLogins { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> CreatedLogins { get; } = [];

    public Task<CmdbuildSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(snapshot);
    }

    public Task CheckConnectionAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task CreateUserAsync(UserUpsertRequest request, CancellationToken cancellationToken)
    {
        if (FailCreateLogins.Contains(request.Login))
        {
            throw new InvalidOperationException($"create failed for {request.Login}");
        }

        CreatedLogins.Add(request.Login);
        return Task.CompletedTask;
    }

    public Task UpdateUserAsync(CmdbuildUser existingUser, UserUpsertRequest request, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task DisableUserAsync(CmdbuildUser existingUser, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryStateStore : ISyncStateStore
{
    public SyncState State { get; } = new();

    public SyncState SavedState { get; private set; } = new();

    public Task<SyncState> LoadAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(State);
    }

    public Task SaveAsync(SyncState state, CancellationToken cancellationToken)
    {
        SavedState = new SyncState();
        foreach (var login in state.ManagedLogins)
        {
            SavedState.ManagedLogins.Add(login);
        }

        return Task.CompletedTask;
    }
}

internal sealed class SequenceHttpHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private int index;

    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        var responseIndex = Math.Min(index, responses.Length - 1);
        index++;
        return Task.FromResult(responses[responseIndex](request));
    }
}
