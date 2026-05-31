using System.Text.Json;
using AdGroups2Cmdbuild.ActiveDirectory;
using AdGroups2Cmdbuild.Cmdbuild;
using AdGroups2Cmdbuild.Configuration;
using AdGroups2Cmdbuild.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var tests = new (string Name, Func<Task> Run)[]
{
    ("per-user failure continues batch", PerUserFailureContinuesBatch),
    ("state store recovers from backup", StateStoreRecoversFromBackup),
    ("partial failure status is visible", PartialFailureStatusIsVisible),
    ("debug sensitive values are masked by default", DebugSensitiveValuesAreMaskedByDefault)
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

internal sealed class FakeActiveDirectoryClient(AdGroupSnapshot snapshot) : IActiveDirectoryClient
{
    public Task<AdGroupSnapshot> ReadGroupsAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(snapshot);
    }

    public Task CheckConnectionAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
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
