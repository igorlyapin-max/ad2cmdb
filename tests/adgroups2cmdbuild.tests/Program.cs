using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AdGroups2Cmdbuild.ActiveDirectory;
using AdGroups2Cmdbuild.Cmdbuild;
using AdGroups2Cmdbuild.Configuration;
using AdGroups2Cmdbuild.Logging;
using AdGroups2Cmdbuild.Resilience;
using AdGroups2Cmdbuild.Secrets;
using AdGroups2Cmdbuild.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var tests = new (string Name, Func<Task> Run)[]
{
    ("per-user failure continues batch", PerUserFailureContinuesBatch),
    ("state store recovers from backup", StateStoreRecoversFromBackup),
    ("partial failure status is visible", PartialFailureStatusIsVisible),
    ("debug sensitive values are masked by default", DebugSensitiveValuesAreMaskedByDefault),
    ("production guards enforce secure runtime", ProductionGuardsEnforceSecureRuntime),
    ("production startup rejects unsafe runtime config", ProductionStartupRejectsUnsafeRuntimeConfig),
    ("production startup accepts safe runtime config", ProductionStartupAcceptsSafeRuntimeConfig),
    ("development health and readiness endpoints work without dependencies", DevelopmentHealthAndReadinessEndpointsWorkWithoutDependencies),
    ("runtime endpoint responses match OpenAPI required fields", RuntimeEndpointResponsesMatchOpenApiRequiredFields),
    ("operational OpenAPI contract describes runtime endpoints", OperationalOpenApiContractDescribesRuntimeEndpoints),
    ("Dockerfile enforces non-root runtime policy", DockerfileEnforcesNonRootRuntimePolicy),
    ("retry backoff uses exponential cap", RetryBackoffUsesExponentialCap),
    ("CMDBuild retry retries transient status", CmdbuildRetryRetriesTransientStatus),
    ("CMDBuild retry skips authorization status", CmdbuildRetrySkipsAuthorizationStatus),
    ("CMDBuild create request uses expected REST contract", CmdbuildCreateRequestUsesExpectedRestContract),
    ("CMDBuild update preserves unmanaged groups", CmdbuildUpdatePreservesUnmanagedGroups),
    ("CMDBuild disable request revokes groups", CmdbuildDisableRequestRevokesGroups),
    ("sync fails when AD group is missing", SyncFailsWhenAdGroupIsMissing),
    ("sync fails when CMDBuild role is missing", SyncFailsWhenCmdbuildRoleIsMissing),
    ("sync skips missing CMDBuild users when creation disabled", SyncSkipsMissingCmdbuildUsersWhenCreationDisabled),
    ("dry-run does not save sync state", DryRunDoesNotSaveSyncState),
    ("sync deprovisions managed user outside provisioning group", SyncDeprovisionsManagedUserOutsideProvisioningGroup),
    ("ELK logging options validate active sink", ElkLoggingOptionsValidateActiveSink),
    ("ELK logger sends structured HTTP event", ElkLoggerSendsStructuredHttpEvent),
    ("secret resolver handles companion references and PAM compatibility", SecretResolverHandlesCompanionReferencesAndPamCompatibility),
    ("secret resolver resolves Indeed PAM AAPM HTTP response", SecretResolverResolvesIndeedPamAapmHttpResponse),
    ("bootstrap tool help works", BootstrapToolHelpWorks),
    ("bootstrap role selection uses explicit precedence", BootstrapRoleSelectionUsesExplicitPrecedence),
    ("bootstrap naming logic escapes and rejects unsafe values", BootstrapNamingLogicEscapesAndRejectsUnsafeValues),
    ("monitoring artifacts cover Zabbix and Prometheus Grafana", MonitoringArtifactsCoverZabbixAndPrometheusGrafana),
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

static Task ProductionGuardsEnforceSecureRuntime()
{
    AssertEqual(true, ProductionGuards.HasWildcardAllowedHost("*"), "single wildcard host");
    AssertEqual(true, ProductionGuards.HasWildcardAllowedHost("api.example.local;*"), "listed wildcard host");
    AssertEqual(false, ProductionGuards.HasWildcardAllowedHost("api.example.local;localhost"), "explicit hosts");

    AssertEqual(false, ProductionGuards.ActiveDirectoryUsesSecureTransport(false), "AD cleartext transport");
    AssertEqual(true, ProductionGuards.ActiveDirectoryUsesSecureTransport(true), "AD secure transport");
    AssertEqual(true, ProductionGuards.AllowsActiveDirectoryCertificateBypass(true), "AD certificate bypass");

    AssertEqual(true, ProductionGuards.CmdbuildBaseUrlUsesHttps("https://cmdbuild.example/cmdbuild/services/rest/v3"), "CMDBuild https URL");
    AssertEqual(false, ProductionGuards.CmdbuildBaseUrlUsesHttps("http://cmdbuild.example/cmdbuild/services/rest/v3"), "CMDBuild http URL");
    AssertEqual(false, ProductionGuards.CmdbuildBaseUrlUsesHttps("not-a-url"), "CMDBuild invalid URL");

    AssertEqual(true, ProductionGuards.ReadinessChecksDependencies(true, true), "dependency readiness");
    AssertEqual(false, ProductionGuards.ReadinessChecksDependencies(true, false), "shallow readiness");
    AssertEqual(false, ProductionGuards.ReadinessChecksDependencies(false, true), "disabled readiness");
    return Task.CompletedTask;
}

static async Task ProductionStartupRejectsUnsafeRuntimeConfig()
{
    var scenarios = new (string Name, Dictionary<string, string> Overrides, string ExpectedText)[]
    {
        ("wildcard allowed hosts", new Dictionary<string, string> { ["AllowedHosts"] = "*" }, "AllowedHosts must not contain '*'"),
        ("AD cleartext transport", new Dictionary<string, string> { ["ActiveDirectory__UseSsl"] = "false" }, "ActiveDirectory:UseSsl must be true"),
        ("AD certificate bypass", new Dictionary<string, string> { ["ActiveDirectory__IgnoreCertificateErrors"] = "true" }, "ActiveDirectory:IgnoreCertificateErrors is not allowed"),
        ("CMDBuild http URL", new Dictionary<string, string> { ["Cmdbuild__BaseUrl"] = "http://cmdbuild.example.local/cmdbuild/services/rest/v3" }, "Cmdbuild:BaseUrl must use https"),
        ("readiness disabled", new Dictionary<string, string> { ["Readiness__Enabled"] = "false" }, "Readiness:Enabled and Readiness:CheckDependencies must be true"),
        ("readiness without dependencies", new Dictionary<string, string> { ["Readiness__CheckDependencies"] = "false" }, "Readiness:Enabled and Readiness:CheckDependencies must be true")
    };

    foreach (var scenario in scenarios)
    {
        var env = NewServiceEnvironment("Production", GetFreeTcpPort());
        foreach (var item in scenario.Overrides)
        {
            env[item.Key] = item.Value;
        }

        await using var service = ServiceProcessHandle.Start(env);
        await service.WaitForExitAsync(TimeSpan.FromSeconds(8), scenario.Name);
        AssertTrue(service.Process.ExitCode != 0, $"{scenario.Name}: expected non-zero exit code");
        AssertTextContains(service.Output, scenario.ExpectedText, scenario.Name);
    }
}

static async Task ProductionStartupAcceptsSafeRuntimeConfig()
{
    var port = GetFreeTcpPort();
    await using var service = ServiceProcessHandle.Start(NewServiceEnvironment("Production", port));
    await WaitForHttpOkAsync(service, $"http://127.0.0.1:{port}/health", TimeSpan.FromSeconds(8), "production /health");

    AssertTrue(!service.Process.HasExited, "production service should keep running after safe startup");
    AssertTextContains(service.Output, "Now listening", "production startup log");
}

static async Task DevelopmentHealthAndReadinessEndpointsWorkWithoutDependencies()
{
    var port = GetFreeTcpPort();
    var env = NewServiceEnvironment("Development", port);
    env["Readiness__CheckDependencies"] = "false";

    await using var service = ServiceProcessHandle.Start(env);
    await WaitForHttpOkAsync(service, $"http://127.0.0.1:{port}/health", TimeSpan.FromSeconds(8), "development /health");

    using (var health = await GetServiceJsonAsync($"http://127.0.0.1:{port}/health"))
    {
        var root = health.RootElement;
        AssertEqual("adgroups2cmdbuild", root.GetProperty("service").GetString(), "health service");
        AssertEqual("ok", root.GetProperty("status").GetString(), "health status");
        AssertEqual(JsonValueKind.Object, root.GetProperty("sync").ValueKind, "health sync object");
    }

    using (var readiness = await GetServiceJsonAsync($"http://127.0.0.1:{port}/ready"))
    {
        var root = readiness.RootElement;
        AssertEqual("ready", root.GetProperty("status").GetString(), "readiness status");
        AssertEqual(false, root.GetProperty("dependenciesChecked").GetBoolean(), "readiness dependencies");
    }

    using (var status = await GetServiceJsonAsync($"http://127.0.0.1:{port}/sync/status"))
    {
        AssertEqual(JsonValueKind.Object, status.RootElement.ValueKind, "sync status object");
    }
}

static async Task RuntimeEndpointResponsesMatchOpenApiRequiredFields()
{
    var port = GetFreeTcpPort();
    var env = NewServiceEnvironment("Development", port);
    env["Readiness__CheckDependencies"] = "false";

    await using var service = ServiceProcessHandle.Start(env);
    await WaitForHttpOkAsync(service, $"http://127.0.0.1:{port}/health", TimeSpan.FromSeconds(8), "OpenAPI runtime /health");

    using var contract = JsonDocument.Parse(File.ReadAllText(OperationalOpenApiContractPath()));
    var schemas = contract.RootElement.GetProperty("components").GetProperty("schemas");

    using var health = await GetServiceJsonAsync($"http://127.0.0.1:{port}/health");
    AssertJsonHasRequiredProperties(health.RootElement, schemas.GetProperty("HealthResponse"), "/health response");
    AssertJsonHasRequiredProperties(health.RootElement.GetProperty("sync"), schemas.GetProperty("SyncStatus"), "/health sync response");

    using var readiness = await GetServiceJsonAsync($"http://127.0.0.1:{port}/ready");
    AssertJsonHasRequiredProperties(readiness.RootElement, schemas.GetProperty("ReadinessResponse"), "/ready response");

    using var status = await GetServiceJsonAsync($"http://127.0.0.1:{port}/sync/status");
    AssertJsonHasRequiredProperties(status.RootElement, schemas.GetProperty("SyncStatus"), "/sync/status response");
}

static Task OperationalOpenApiContractDescribesRuntimeEndpoints()
{
    var contractPath = OperationalOpenApiContractPath();
    AssertTrue(File.Exists(contractPath), "operational OpenAPI contract should exist");

    using var contract = JsonDocument.Parse(File.ReadAllText(contractPath));
    var root = contract.RootElement;
    AssertEqual("3.1.0", root.GetProperty("openapi").GetString(), "OpenAPI version");

    var paths = root.GetProperty("paths");
    AssertPathResponse(paths, "/health", "200");
    AssertPathResponse(paths, "/ready", "200");
    AssertPathResponse(paths, "/ready", "503");
    AssertPathResponse(paths, "/sync/status", "200");

    var schemas = root.GetProperty("components").GetProperty("schemas");
    AssertRequiredProperties(schemas.GetProperty("HealthResponse"), ["service", "status", "sync"], "HealthResponse");
    AssertRequiredProperties(schemas.GetProperty("ReadinessResponse"), ["service", "status", "dependenciesChecked"], "ReadinessResponse");
    AssertRequiredProperties(schemas.GetProperty("SyncStatus"), ["isRunning", "lastStartedUtc", "lastCompletedUtc", "lastSucceeded", "lastError", "lastSummary"], "SyncStatus");
    AssertRequiredProperties(schemas.GetProperty("SyncRunSummary"), ["adUsers", "provisionedUsers", "createdUsers", "updatedUsers", "disabledUsers", "skippedUsers", "failedUsers", "dryRun", "hasFailures"], "SyncRunSummary");
    return Task.CompletedTask;
}

static Task DockerfileEnforcesNonRootRuntimePolicy()
{
    var dockerfile = File.ReadAllText(Path.Combine(FindRepoRoot(), "deploy", "dockerfiles", "adgroups2cmdbuild.Dockerfile"));
    AssertTextContains(dockerfile, "groupadd --system --gid 64100 ad2cmdb", "Docker group policy");
    AssertTextContains(dockerfile, "useradd --system --uid 64100 --gid ad2cmdb", "Docker user policy");
    AssertTextContains(dockerfile, "mkdir -p /app/state", "Docker state directory");
    AssertTextContains(dockerfile, "COPY --from=build --chown=ad2cmdb:ad2cmdb", "Docker copy ownership");
    AssertTextContains(dockerfile, "USER ad2cmdb", "Docker non-root user");
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

static async Task CmdbuildCreateRequestUsesExpectedRestContract()
{
    var handler = new SequenceHttpHandler(_ => JsonResponse("{}"));
    var client = NewCmdbuildClient(
        handler,
        retryAttempts: 1,
        options =>
        {
            options.NewUserPassword = "generated-by-test";
            options.DefaultLanguage = "en";
        });

    await client.CreateUserAsync(
        new UserUpsertRequest(
            "alice",
            "Alice Smith",
            "alice@example.local",
            [new CmdbuildRole("1", "CMDBuildUsers")],
            ["1"]),
        CancellationToken.None);

    var request = handler.CapturedRequests.Single();
    AssertEqual("POST", request.Method, "create method");
    AssertTextContains(request.Uri, "/users", "create path");
    AssertEqual("Basic", request.AuthorizationScheme, "authorization scheme");
    AssertEqual(
        Convert.ToBase64String(Encoding.UTF8.GetBytes("cmdbuild-sync:secret")),
        request.AuthorizationParameter,
        "authorization parameter");
    AssertEqual("admin", request.CmdbuildView, "CMDBuild view header");

    using var document = JsonDocument.Parse(request.Body ?? "{}");
    var root = document.RootElement;
    AssertEqual("alice", root.GetProperty("username").GetString(), "create username");
    AssertEqual(true, root.GetProperty("active").GetBoolean(), "create active");
    AssertEqual(false, root.GetProperty("service").GetBoolean(), "create service flag");
    AssertEqual(true, root.GetProperty("multiGroup").GetBoolean(), "create multigroup");
    AssertEqual("generated-by-test", root.GetProperty("password").GetString(), "create password");
    AssertEqual("Alice Smith", root.GetProperty("description").GetString(), "create display name");
    AssertEqual("alice@example.local", root.GetProperty("email").GetString(), "create email");
    AssertEqual("en", root.GetProperty("language").GetString(), "create language");
    AssertContains("1", ReadRoleIds(root), "create role ids");
}

static async Task CmdbuildUpdatePreservesUnmanagedGroups()
{
    var handler = new SequenceHttpHandler(_ => JsonResponse("{}"));
    var client = NewCmdbuildClient(
        handler,
        retryAttempts: 1,
        options => options.PreserveUnmanagedGroups = true);
    var existingUser = new CmdbuildUser
    {
        Id = "user-1",
        Username = "alice",
        Active = true
    };
    existingUser.RoleIds.Add("managed-old");
    existingUser.RoleIds.Add("external-role");

    await client.UpdateUserAsync(
        existingUser,
        new UserUpsertRequest(
            "alice",
            "Alice Smith",
            "alice@example.local",
            [new CmdbuildRole("managed-new", "CMDBuildEditors")],
            ["managed-old", "managed-new"]),
        CancellationToken.None);

    var request = handler.CapturedRequests.Single();
    AssertEqual("PUT", request.Method, "update method");
    AssertTextContains(request.Uri, "/users/user-1", "update path");

    using var document = JsonDocument.Parse(request.Body ?? "{}");
    var roleIds = ReadRoleIds(document.RootElement);
    AssertContains("managed-new", roleIds, "update role ids");
    AssertContains("external-role", roleIds, "update role ids");
    AssertDoesNotContain("managed-old", roleIds, "update role ids");
}

static async Task CmdbuildDisableRequestRevokesGroups()
{
    var handler = new SequenceHttpHandler(_ => JsonResponse("{}"));
    var client = NewCmdbuildClient(handler, retryAttempts: 1);
    var existingUser = new CmdbuildUser
    {
        Id = "user-1",
        Username = "alice",
        Active = true
    };
    existingUser.RoleIds.Add("1");

    await client.DisableUserAsync(existingUser, CancellationToken.None);

    var request = handler.CapturedRequests.Single();
    AssertEqual("PUT", request.Method, "disable method");
    AssertTextContains(request.Uri, "/users/user-1", "disable path");

    using var document = JsonDocument.Parse(request.Body ?? "{}");
    var root = document.RootElement;
    AssertEqual("alice", root.GetProperty("username").GetString(), "disable username");
    AssertEqual(false, root.GetProperty("active").GetBoolean(), "disable active");
    AssertEqual(0, root.GetProperty("userGroups").GetArrayLength(), "disable role count");
    AssertEqual(JsonValueKind.Null, root.GetProperty("defaultUserGroup").ValueKind, "disable default group");
}

static async Task SyncFailsWhenAdGroupIsMissing()
{
    var adSnapshot = new AdGroupSnapshot();
    adSnapshot.Groups["CMDBuildUsers"] = new Dictionary<string, AdUserRecord>(StringComparer.OrdinalIgnoreCase);

    var service = NewSynchronizationService(
        adSnapshot,
        NewCmdbSnapshot("CMDBuildUsers"),
        new InMemoryStateStore(),
        new SyncOptions());

    await AssertThrowsAsync<InvalidOperationException>(
        () => service.RunOnceAsync(CancellationToken.None),
        "AD groups are missing");
}

static async Task SyncFailsWhenCmdbuildRoleIsMissing()
{
    var service = NewSynchronizationService(
        NewAdSnapshot("CMDBuildUsers", "alice"),
        new CmdbuildSnapshot(),
        new InMemoryStateStore(),
        new SyncOptions());

    await AssertThrowsAsync<InvalidOperationException>(
        () => service.RunOnceAsync(CancellationToken.None),
        "CMDBuild roles are missing");
}

static async Task SyncSkipsMissingCmdbuildUsersWhenCreationDisabled()
{
    var stateStore = new InMemoryStateStore();
    var cmdbuildClient = new FakeCmdbuildClient(NewCmdbSnapshot("CMDBuildUsers"));
    var service = NewSynchronizationServiceWithClient(
        NewAdSnapshot("CMDBuildUsers", "alice"),
        cmdbuildClient,
        stateStore,
        new SyncOptions { DryRun = false },
        cmdbuildOptions: new CmdbuildOptions { CreateMissingUsers = false });

    var summary = await service.RunOnceAsync(CancellationToken.None);

    AssertEqual(0, summary.CreatedUsers, "created users");
    AssertEqual(1, summary.SkippedUsers, "skipped users");
    AssertEqual(0, cmdbuildClient.CreatedLogins.Count, "created logins");
    AssertEqual(1, stateStore.SaveCount, "save count");
    AssertDoesNotContain("alice", stateStore.SavedState.ManagedLogins, "saved managed logins");
}

static async Task DryRunDoesNotSaveSyncState()
{
    var stateStore = new InMemoryStateStore();
    var cmdbuildClient = new FakeCmdbuildClient(NewCmdbSnapshot("CMDBuildUsers"));
    var service = NewSynchronizationServiceWithClient(
        NewAdSnapshot("CMDBuildUsers", "alice"),
        cmdbuildClient,
        stateStore,
        new SyncOptions { DryRun = true });

    var summary = await service.RunOnceAsync(CancellationToken.None);

    AssertEqual(1, summary.CreatedUsers, "dry-run planned created users");
    AssertEqual(0, cmdbuildClient.CreatedLogins.Count, "created logins");
    AssertEqual(0, stateStore.SaveCount, "save count");
}

static async Task SyncDeprovisionsManagedUserOutsideProvisioningGroup()
{
    var stateStore = new InMemoryStateStore();
    stateStore.State.ManagedLogins.Add("old-user");
    var cmdbSnapshot = NewCmdbSnapshot("CMDBuildUsers");
    var existingUser = new CmdbuildUser
    {
        Id = "user-1",
        Username = "old-user",
        Active = true
    };
    existingUser.RoleIds.Add("1");
    cmdbSnapshot.UsersByLogin["old-user"] = existingUser;
    var cmdbuildClient = new FakeCmdbuildClient(cmdbSnapshot);
    var service = NewSynchronizationServiceWithClient(
        NewAdSnapshot("CMDBuildUsers"),
        cmdbuildClient,
        stateStore,
        new SyncOptions { DryRun = false });

    var summary = await service.RunOnceAsync(CancellationToken.None);

    AssertEqual(1, summary.DisabledUsers, "disabled users");
    AssertContains("old-user", cmdbuildClient.DisabledLogins, "disabled logins");
    AssertEqual(1, stateStore.SaveCount, "save count");
}

static Task ElkLoggingOptionsValidateActiveSink()
{
    var options = new ElkLoggingOptions();
    AssertEqual(false, options.IsActive(), "inactive by default");
    AssertEqual(true, options.HasValidEndpoint(), "inactive endpoint validation");

    options.Enabled = true;
    AssertEqual(false, options.IsActive(), "enabled without endpoint");
    AssertEqual(true, options.HasValidEndpoint(), "enabled without endpoint validation");

    options.Endpoint = "not-a-url";
    AssertEqual(true, options.IsActive(), "active invalid endpoint");
    AssertEqual(false, options.HasValidEndpoint(), "invalid active endpoint");

    options.Endpoint = "https://elk.example.local";
    options.MinimumLevel = "Warning";
    AssertEqual(true, options.HasValidEndpoint(), "valid active endpoint");
    AssertEqual(true, options.HasValidMinimumLevel(), "valid minimum level");
    AssertEqual(LogLevel.Warning, options.GetMinimumLevel(), "minimum level");

    options.MinimumLevel = "DefinitelyNotALevel";
    AssertEqual(false, options.HasValidMinimumLevel(), "invalid minimum level");
    return Task.CompletedTask;
}

static async Task ElkLoggerSendsStructuredHttpEvent()
{
    await using var server = await SingleRequestHttpServer.StartAsync(_ => """{"result":"created"}""");
    using (var provider = new ElkLoggerProvider(Options.Create(new ElkLoggingOptions
    {
        Enabled = true,
        Endpoint = server.BaseUrl,
        Index = "adgroups2cmdbuild-test",
        ApiKey = "test-api-key",
        MinimumLevel = "Information",
        ServiceName = "adgroups2cmdbuild-test",
        Environment = "Test",
        TimeoutMs = 2000,
        QueueCapacity = 10,
        FlushTimeoutMs = 3000
    })))
    {
        var logger = provider.CreateLogger("test.category");
        logger.LogInformation(new EventId(7, "SyncStarted"), "hello {Login}", "alice");
    }

    var request = await server.WaitForRequestAsync(TimeSpan.FromSeconds(3), "ELK log HTTP request");
    AssertEqual("POST", request.Method, "ELK method");
    AssertEqual("/adgroups2cmdbuild-test/_doc", request.Path, "ELK endpoint path");
    AssertTextContains(request.Headers.GetValueOrDefault("authorization") ?? "", "ApiKey test-api-key", "ELK authorization");

    using var document = JsonDocument.Parse(request.Body);
    var root = document.RootElement;
    AssertEqual("Information", root.GetProperty("level").GetString(), "ELK level");
    AssertEqual("test.category", root.GetProperty("category").GetString(), "ELK category");
    AssertEqual(7, root.GetProperty("eventId").GetInt32(), "ELK event id");
    AssertEqual("SyncStarted", root.GetProperty("eventName").GetString(), "ELK event name");
    AssertEqual("hello alice", root.GetProperty("message").GetString(), "ELK message");
    AssertEqual("adgroups2cmdbuild-test", root.GetProperty("service").GetString(), "ELK service");
    AssertEqual("Test", root.GetProperty("environment").GetString(), "ELK environment");
}

static async Task SecretResolverHandlesCompanionReferencesAndPamCompatibility()
{
    await WithEnvironmentAsync(
        new Dictionary<string, string?>
        {
            ["PAMURL"] = null,
            ["PAMTOKEN"] = null,
            ["PAMUSERNAME"] = null,
            ["PAMPASSWORD"] = null,
            ["PAMDEFAULTACCOUNTPATH"] = null
        },
        async () =>
        {
            var companionConfig = new ConfigurationManager();
            companionConfig.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Secrets:Provider"] = "None",
                ["Cmdbuild:Password"] = "",
                ["Cmdbuild:PasswordSecret"] = "AAA.LOCAL/PROD/cmdbuild-sync"
            });

            await AssertThrowsAsync<InvalidOperationException>(
                () => companionConfig.ResolveSecretReferencesAsync("adgroups2cmdbuild-test"),
                "Secrets:Provider is 'None'");
            AssertEqual("secret://AAA.LOCAL/PROD/cmdbuild-sync", companionConfig["Cmdbuild:Password"], "companion secret reference");
        });

    await WithEnvironmentAsync(
        new Dictionary<string, string?>
        {
            ["PAMURL"] = "https://pam.example.local",
            ["PAMTOKEN"] = "compat-token",
            ["PAMDEFAULTACCOUNTPATH"] = "AAA.LOCAL/PROD"
        },
        async () =>
        {
            var pamConfig = new ConfigurationManager();
            pamConfig.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Secrets:Provider"] = "None"
            });
            await pamConfig.ResolveSecretReferencesAsync("adgroups2cmdbuild-test");
            AssertEqual("IndeedPamAapm", pamConfig["Secrets:Provider"], "PAM provider auto-detect");
            AssertEqual("https://pam.example.local", pamConfig["Secrets:IndeedPamAapm:BaseUrl"], "PAM base URL");
            AssertEqual("compat-token", pamConfig["Secrets:IndeedPamAapm:ApplicationToken"], "PAM token");
            AssertEqual("AAA.LOCAL/PROD", pamConfig["Secrets:IndeedPamAapm:DefaultAccountPath"], "PAM default account path");
        });
}

static async Task SecretResolverResolvesIndeedPamAapmHttpResponse()
{
    await using var server = await SingleRequestHttpServer.StartAsync(_ => """{"password":"resolved-secret"}""");
    var config = new ConfigurationManager();
    config.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Secrets:Provider"] = "IndeedPamAapm",
        ["Secrets:IndeedPamAapm:BaseUrl"] = server.BaseUrl,
        ["Secrets:IndeedPamAapm:PasswordEndpointPath"] = "/aapm/password",
        ["Secrets:IndeedPamAapm:ApplicationToken"] = "app-token",
        ["Secrets:IndeedPamAapm:ResponseType"] = "json",
        ["Secrets:IndeedPamAapm:ValueJsonPath"] = "password",
        ["Secrets:IndeedPamAapm:Comment"] = "ad2cmdb {service} {secretId}",
        ["Cmdbuild:Password"] = "secret://AAA_LOCAL/PROD/cmdbuild-sync"
    });

    await config.ResolveSecretReferencesAsync("adgroups2cmdbuild-test");
    var request = await server.WaitForRequestAsync(TimeSpan.FromSeconds(3), "PAM secret HTTP request");

    AssertEqual("resolved-secret", config["Cmdbuild:Password"], "resolved CMDBuild secret");
    AssertEqual("GET", request.Method, "PAM method");
    AssertEqual("/aapm/password", request.Path, "PAM endpoint path");
    AssertTextContains(request.Query, "token=app-token", "PAM token query");
    AssertTextContains(Uri.UnescapeDataString(request.Query), "sapmaccountpath=AAA_LOCAL/PROD", "PAM account path query");
    AssertTextContains(request.Query, "sapmaccountname=cmdbuild-sync", "PAM account name query");
    AssertTextContains(Uri.UnescapeDataString(request.Query), "comment=ad2cmdb adgroups2cmdbuild-test AAA_LOCAL/PROD/cmdbuild-sync", "PAM comment query");
}

static async Task BootstrapToolHelpWorks()
{
    var result = await RunProcessAsync(
        Path.Combine(FindRepoRoot(), "scripts", "bootstrap-ad-groups.sh"),
        "--help",
        FindRepoRoot(),
        TimeSpan.FromSeconds(8));

    AssertEqual(0, result.ExitCode, "bootstrap help exit code");
    AssertTextContains(result.Output, "bootstrap-ad-groups creates missing AD groups", "bootstrap help output");
}

static Task BootstrapRoleSelectionUsesExplicitPrecedence()
{
    var roleNames = new[] { "CMDBuildUsers", "CMDBuildEditors", "OtherRole", "CMDBuildAdmins" };

    AssertSequenceEqual(
        ["CMDBuildUsers"],
        BootstrapAdGroups.BootstrapAdGroupsLogic.SelectRoleNames(
            roleNames,
            new BootstrapAdGroups.BootstrapRoleSelection(
                All: false,
                IncludeNamePrefix: "",
                IncludeRoleNames: [],
                ExcludeRoleNames: [],
                FallbackGroupNames: ["CMDBuildUsers"])),
        "fallback selection");

    AssertSequenceEqual(
        ["OtherRole"],
        BootstrapAdGroups.BootstrapAdGroupsLogic.SelectRoleNames(
            roleNames,
            new BootstrapAdGroups.BootstrapRoleSelection(
                All: false,
                IncludeNamePrefix: "CMDBuild",
                IncludeRoleNames: ["OtherRole"],
                ExcludeRoleNames: [],
                FallbackGroupNames: ["CMDBuildUsers"])),
        "include selection precedence");

    AssertSequenceEqual(
        ["CMDBuildEditors", "CMDBuildAdmins"],
        BootstrapAdGroups.BootstrapAdGroupsLogic.SelectRoleNames(
            roleNames,
            new BootstrapAdGroups.BootstrapRoleSelection(
                All: false,
                IncludeNamePrefix: "CMDBuild",
                IncludeRoleNames: [],
                ExcludeRoleNames: ["CMDBuildUsers"],
                FallbackGroupNames: [])),
        "prefix selection with exclude");

    AssertSequenceEqual(
        ["CMDBuildUsers", "CMDBuildEditors", "CMDBuildAdmins"],
        BootstrapAdGroups.BootstrapAdGroupsLogic.SelectRoleNames(
            roleNames,
            new BootstrapAdGroups.BootstrapRoleSelection(
                All: true,
                IncludeNamePrefix: "",
                IncludeRoleNames: [],
                ExcludeRoleNames: ["OtherRole"],
                FallbackGroupNames: [])),
        "all selection with exclude");

    AssertEqual(true, BootstrapAdGroups.BootstrapAdGroupsLogic.HasExplicitSelection(true, "", []), "all explicit selection");
    AssertEqual(true, BootstrapAdGroups.BootstrapAdGroupsLogic.HasExplicitSelection(false, "CMDBuild", []), "prefix explicit selection");
    AssertEqual(true, BootstrapAdGroups.BootstrapAdGroupsLogic.HasExplicitSelection(false, "", ["CMDBuildUsers"]), "include explicit selection");
    AssertEqual(false, BootstrapAdGroups.BootstrapAdGroupsLogic.HasExplicitSelection(false, "", []), "fallback is not explicit selection");
    return Task.CompletedTask;
}

static Task BootstrapNamingLogicEscapesAndRejectsUnsafeValues()
{
    AssertEqual("CMDBuildUsers", BootstrapAdGroups.BootstrapAdGroupsLogic.BuildSamAccountName(" CMDBuildUsers "), "sAMAccountName trim");
    AssertThrows<InvalidOperationException>(
        () => BootstrapAdGroups.BootstrapAdGroupsLogic.BuildSamAccountName("CMDBuild/Users"),
        "unsafe");
    AssertThrows<InvalidOperationException>(
        () => BootstrapAdGroups.BootstrapAdGroupsLogic.BuildSamAccountName(new string('a', 257)),
        "too long");

    AssertEqual(
        unchecked((int)0x80000000) | 0x00000002,
        BootstrapAdGroups.BootstrapAdGroupsLogic.BuildGroupType("Global", securityEnabled: true),
        "security global group type");
    AssertEqual(0x00000008, BootstrapAdGroups.BootstrapAdGroupsLogic.BuildGroupType("Universal", securityEnabled: false), "distribution universal group type");
    AssertThrows<InvalidOperationException>(
        () => BootstrapAdGroups.BootstrapAdGroupsLogic.BuildGroupType("Unsupported", securityEnabled: true),
        "Unsupported group scope");

    AssertEqual(
        @"CN=\#Role\,One\ ,OU=Groups,DC=example,DC=local",
        BootstrapAdGroups.BootstrapAdGroupsLogic.BuildGroupDn("#Role,One ", "OU=Groups,DC=example,DC=local"),
        "escaped group DN");
    return Task.CompletedTask;
}

static Task MonitoringArtifactsCoverZabbixAndPrometheusGrafana()
{
    var root = FindRepoRoot();
    var monitoringDirectory = Path.Combine(root, "aa", "monitoring");
    var expectedFiles = new[]
    {
        "README.md",
        "zabbix-adgroups2cmdbuild-template.yaml",
        "prometheus-json-exporter-adgroups2cmdbuild.yaml",
        "prometheus-adgroups2cmdbuild-rules.yaml",
        "grafana-adgroups2cmdbuild-dashboard.json"
    };

    foreach (var fileName in expectedFiles)
    {
        AssertTrue(File.Exists(Path.Combine(monitoringDirectory, fileName)), $"monitoring artifact should exist: {fileName}");
    }

    var zabbix = File.ReadAllText(Path.Combine(monitoringDirectory, "zabbix-adgroups2cmdbuild-template.yaml"));
    AssertTextContains(zabbix, "{$AD2CMDB.URL}", "Zabbix service URL macro");
    AssertTextContains(zabbix, "ad2cmdb.ready.body", "Zabbix readiness item");
    AssertTextContains(zabbix, "ad2cmdb.sync.failed_users", "Zabbix failed users item");

    var prometheusRules = File.ReadAllText(Path.Combine(monitoringDirectory, "prometheus-adgroups2cmdbuild-rules.yaml"));
    AssertTextContains(prometheusRules, "Ad2CmdbReadinessDown", "Prometheus readiness alert");
    AssertTextContains(prometheusRules, "Ad2CmdbSyncStale", "Prometheus stale sync alert");
    AssertTextContains(prometheusRules, "Ad2CmdbPartialFailures", "Prometheus partial failure alert");

    using var dashboard = JsonDocument.Parse(File.ReadAllText(Path.Combine(monitoringDirectory, "grafana-adgroups2cmdbuild-dashboard.json")));
    AssertEqual("adgroups2cmdbuild operations", dashboard.RootElement.GetProperty("title").GetString(), "Grafana dashboard title");
    AssertTrue(dashboard.RootElement.GetProperty("panels").GetArrayLength() >= 4, "Grafana dashboard panels");
    return Task.CompletedTask;
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

static CmdbuildClient NewCmdbuildClient(
    SequenceHttpHandler handler,
    int retryAttempts,
    Action<CmdbuildOptions>? configure = null)
{
    var options = new CmdbuildOptions
    {
        BaseUrl = "http://cmdbuild.example.local/cmdbuild/services/rest/v3",
        Username = "cmdbuild-sync",
        Password = "secret",
        RetryAttempts = retryAttempts,
        RetryBaseDelayMs = 1,
        RetryMaxDelayMs = 1,
        RetryJitterPercent = 0
    };
    configure?.Invoke(options);

    return new CmdbuildClient(
        new HttpClient(handler),
        Options.Create(options),
        Options.Create(new DebugOptions()),
        NullLogger<CmdbuildClient>.Instance);
}

static AdGroupSynchronizationService NewSynchronizationService(
    AdGroupSnapshot adSnapshot,
    CmdbuildSnapshot cmdbSnapshot,
    InMemoryStateStore stateStore,
    SyncOptions syncOptions,
    CmdbuildOptions? cmdbuildOptions = null)
{
    return NewSynchronizationServiceWithClient(
        adSnapshot,
        new FakeCmdbuildClient(cmdbSnapshot),
        stateStore,
        syncOptions,
        cmdbuildOptions);
}

static AdGroupSynchronizationService NewSynchronizationServiceWithClient(
    AdGroupSnapshot adSnapshot,
    FakeCmdbuildClient cmdbuildClient,
    InMemoryStateStore stateStore,
    SyncOptions syncOptions,
    CmdbuildOptions? cmdbuildOptions = null)
{
    return new AdGroupSynchronizationService(
        new FakeActiveDirectoryClient(adSnapshot),
        cmdbuildClient,
        stateStore,
        Options.Create(new ActiveDirectoryOptions
        {
            GroupNames = ["CMDBuildUsers"],
            ProvisioningGroupName = "CMDBuildUsers"
        }),
        Options.Create(cmdbuildOptions ?? new CmdbuildOptions()),
        Options.Create(syncOptions),
        Options.Create(new DebugOptions()),
        NullLogger<AdGroupSynchronizationService>.Instance);
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

static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
{
    return new HttpResponseMessage(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

static IReadOnlyCollection<string> ReadRoleIds(JsonElement root)
{
    var values = new List<string>();
    foreach (var item in root.GetProperty("userGroups").EnumerateArray())
    {
        if (!item.TryGetProperty("_id", out var id))
        {
            continue;
        }

        values.Add(id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString() ?? "");
    }

    return values;
}

static Dictionary<string, string> NewServiceEnvironment(string environment, int port)
{
    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ASPNETCORE_ENVIRONMENT"] = environment,
        ["DOTNET_ENVIRONMENT"] = environment,
        ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
        ["AllowedHosts"] = "127.0.0.1;localhost",
        ["Secrets__Provider"] = "None",
        ["ActiveDirectory__Host"] = "ad.example.local",
        ["ActiveDirectory__Port"] = "636",
        ["ActiveDirectory__UseSsl"] = "true",
        ["ActiveDirectory__IgnoreCertificateErrors"] = "false",
        ["ActiveDirectory__BindDn"] = "CN=svc-adgroups2cmdbuild,OU=Service Accounts,DC=example,DC=local",
        ["ActiveDirectory__BindPassword"] = "secret",
        ["ActiveDirectory__GroupSearchBaseDn"] = "OU=Groups,DC=example,DC=local",
        ["ActiveDirectory__GroupNames__0"] = "CMDBuildUsers",
        ["ActiveDirectory__ProvisioningGroupName"] = "CMDBuildUsers",
        ["Cmdbuild__BaseUrl"] = "https://cmdbuild.example.local/cmdbuild/services/rest/v3",
        ["Cmdbuild__Username"] = "cmdbuild-sync",
        ["Cmdbuild__Password"] = "secret",
        ["Sync__Enabled"] = "false",
        ["Readiness__Enabled"] = "true",
        ["Readiness__CheckDependencies"] = environment.Equals("Production", StringComparison.OrdinalIgnoreCase) ? "true" : "false",
        ["Readiness__Route"] = "/ready",
        ["Readiness__TimeoutMs"] = "200",
        ["EndpointRateLimiting__Enabled"] = "false",
        ["ElkLogging__Enabled"] = "false"
    };
}

static int GetFreeTcpPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    finally
    {
        listener.Stop();
    }
}

static async Task WaitForHttpOkAsync(
    ServiceProcessHandle service,
    string url,
    TimeSpan timeout,
    string operation)
{
    using var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMilliseconds(300)
    };
    var deadline = DateTimeOffset.UtcNow + timeout;
    Exception? lastException = null;

    while (DateTimeOffset.UtcNow < deadline)
    {
        if (service.Process.HasExited)
        {
            throw new InvalidOperationException($"{operation}: service exited early with code {service.Process.ExitCode}. Output:{Environment.NewLine}{service.Output}");
        }

        try
        {
            using var response = await httpClient.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            lastException = exception;
        }

        await Task.Delay(100);
    }

    throw new InvalidOperationException($"{operation} timed out. Last error: {lastException?.Message}. Output:{Environment.NewLine}{service.Output}");
}

static async Task<JsonDocument> GetServiceJsonAsync(string url)
{
    using var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(2)
    };
    using var response = await httpClient.GetAsync(url);
    response.EnsureSuccessStatusCode();
    var text = await response.Content.ReadAsStringAsync();
    return JsonDocument.Parse(text);
}

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "scripts", "bootstrap-ad-groups.sh")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new InvalidOperationException("repo root was not found");
}

static string OperationalOpenApiContractPath()
{
    return Path.Combine(FindRepoRoot(), "aa", "contracts", "operational-api.openapi.json");
}

static async Task<ProcessResult> RunProcessAsync(
    string fileName,
    string arguments,
    string workingDirectory,
    TimeSpan timeout)
{
    using var process = new Process();
    var output = new StringBuilder();
    process.StartInfo = new ProcessStartInfo(fileName, arguments)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    process.OutputDataReceived += (_, args) =>
    {
        if (args.Data is not null)
        {
            output.AppendLine(args.Data);
        }
    };
    process.ErrorDataReceived += (_, args) =>
    {
        if (args.Data is not null)
        {
            output.AppendLine(args.Data);
        }
    };

    process.Start();
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    using var cancellation = new CancellationTokenSource(timeout);
    try
    {
        await process.WaitForExitAsync(cancellation.Token);
        process.WaitForExit();
    }
    catch (OperationCanceledException)
    {
        process.Kill(entireProcessTree: true);
        throw new InvalidOperationException($"{fileName} {arguments} timed out. Output:{Environment.NewLine}{output}");
    }

    return new ProcessResult(process.ExitCode, output.ToString());
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

static async Task WithEnvironmentAsync(IReadOnlyDictionary<string, string?> values, Func<Task> action)
{
    var previous = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in values)
    {
        previous[item.Key] = Environment.GetEnvironmentVariable(item.Key);
        Environment.SetEnvironmentVariable(item.Key, item.Value);
    }

    try
    {
        await action();
    }
    finally
    {
        foreach (var item in previous)
        {
            Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string expectedMessage)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException exception)
    {
        AssertTextContains(exception.Message, expectedMessage, typeof(TException).Name);
        return;
    }

    throw new InvalidOperationException($"expected exception {typeof(TException).Name}");
}

static void AssertThrows<TException>(Action action, string expectedMessage)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException exception)
    {
        AssertTextContains(exception.Message, expectedMessage, typeof(TException).Name);
        return;
    }

    throw new InvalidOperationException($"expected exception {typeof(TException).Name}");
}

static void AssertPathResponse(JsonElement paths, string path, string statusCode)
{
    AssertTrue(paths.TryGetProperty(path, out var pathItem), $"OpenAPI path {path} should exist");
    AssertTrue(pathItem.TryGetProperty("get", out var get), $"OpenAPI path {path} should define GET");
    var responses = get.GetProperty("responses");
    AssertTrue(responses.TryGetProperty(statusCode, out _), $"OpenAPI path {path} should define response {statusCode}");
}

static void AssertRequiredProperties(JsonElement schema, IReadOnlyCollection<string> expectedProperties, string schemaName)
{
    var required = schema.GetProperty("required")
        .EnumerateArray()
        .Select(item => item.GetString())
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .ToHashSet(StringComparer.Ordinal);

    foreach (var property in expectedProperties)
    {
        AssertTrue(required.Contains(property), $"{schemaName} should require {property}");
    }
}

static void AssertJsonHasRequiredProperties(JsonElement json, JsonElement schema, string name)
{
    var required = schema.GetProperty("required")
        .EnumerateArray()
        .Select(item => item.GetString())
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .ToArray();

    foreach (var property in required)
    {
        AssertTrue(json.TryGetProperty(property!, out _), $"{name} should include required property {property}");
    }
}

static void AssertSequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string name)
{
    if (expected.Count != actual.Count)
    {
        throw new InvalidOperationException($"{name}: expected {expected.Count} item(s), got {actual.Count}");
    }

    for (var index = 0; index < expected.Count; index++)
    {
        if (!EqualityComparer<T>.Default.Equals(expected[index], actual[index]))
        {
            throw new InvalidOperationException($"{name}: item {index} expected {expected[index]}, got {actual[index]}");
        }
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

static void AssertTextContains(string value, string expected, string name)
{
    if (!value.Contains(expected, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"{name}: expected text to contain '{expected}', got '{value}'");
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

    public List<string> UpdatedLogins { get; } = [];

    public List<string> DisabledLogins { get; } = [];

    public List<UserUpsertRequest> CreateRequests { get; } = [];

    public List<UserUpsertRequest> UpdateRequests { get; } = [];

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
        CreateRequests.Add(request);
        return Task.CompletedTask;
    }

    public Task UpdateUserAsync(CmdbuildUser existingUser, UserUpsertRequest request, CancellationToken cancellationToken)
    {
        UpdatedLogins.Add(request.Login);
        UpdateRequests.Add(request);
        return Task.CompletedTask;
    }

    public Task DisableUserAsync(CmdbuildUser existingUser, CancellationToken cancellationToken)
    {
        DisabledLogins.Add(existingUser.Username);
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryStateStore : ISyncStateStore
{
    public SyncState State { get; } = new();

    public SyncState SavedState { get; private set; } = new();

    public int SaveCount { get; private set; }

    public Task<SyncState> LoadAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(State);
    }

    public Task SaveAsync(SyncState state, CancellationToken cancellationToken)
    {
        SaveCount++;
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

    public List<CapturedRequest> CapturedRequests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        CapturedRequests.Add(new CapturedRequest(
            request.Method.Method,
            request.RequestUri?.ToString() ?? "",
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            request.Headers.TryGetValues("CMDBuild-View", out var view) ? view.FirstOrDefault() : null,
            body));

        var responseIndex = Math.Min(index, responses.Length - 1);
        index++;
        return responses[responseIndex](request);
    }
}

internal sealed class SingleRequestHttpServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly Func<CapturedHttpRequest, string> responseFactory;
    private readonly Task<CapturedHttpRequest> requestTask;

    private SingleRequestHttpServer(TcpListener listener, Func<CapturedHttpRequest, string> responseFactory)
    {
        this.listener = listener;
        this.responseFactory = responseFactory;
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        requestTask = AcceptOnceAsync();
    }

    public string BaseUrl { get; }

    public static Task<SingleRequestHttpServer> StartAsync(Func<CapturedHttpRequest, string> responseFactory)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new SingleRequestHttpServer(listener, responseFactory));
    }

    public async Task<CapturedHttpRequest> WaitForRequestAsync(TimeSpan timeout, string operation)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            return await requestTask.WaitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException($"{operation} timed out");
        }
    }

    public ValueTask DisposeAsync()
    {
        listener.Stop();
        return ValueTask.CompletedTask;
    }

    private async Task<CapturedHttpRequest> AcceptOnceAsync()
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        var requestLine = await reader.ReadLineAsync() ?? "";
        var requestParts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var method = requestParts.Length > 0 ? requestParts[0] : "";
        var rawTarget = requestParts.Length > 1 ? requestParts[1] : "/";
        var target = rawTarget.Split('?', 2);
        var path = target[0];
        var query = target.Length > 1 ? target[1] : "";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        var contentLength = headers.TryGetValue("Content-Length", out var contentLengthText)
            && int.TryParse(contentLengthText, out var parsedContentLength)
            ? parsedContentLength
            : 0;
        var body = "";
        if (contentLength > 0)
        {
            var buffer = new char[contentLength];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(read, buffer.Length - read));
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            body = new string(buffer, 0, read);
        }

        var captured = new CapturedHttpRequest(method, path, query, headers, body);
        var responseBody = responseFactory(captured);
        var responseBytes = Encoding.UTF8.GetBytes(responseBody);
        var responseHeader = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n"
            + "Content-Type: application/json\r\n"
            + $"Content-Length: {responseBytes.Length}\r\n"
            + "Connection: close\r\n\r\n");
        await stream.WriteAsync(responseHeader);
        await stream.WriteAsync(responseBytes);
        return captured;
    }
}

internal sealed class ServiceProcessHandle : IAsyncDisposable
{
    private readonly StringBuilder output = new();
    private readonly object outputGate = new();

    private ServiceProcessHandle(Process process)
    {
        Process = process;
    }

    public Process Process { get; }

    public string Output
    {
        get
        {
            lock (outputGate)
            {
                return output.ToString();
            }
        }
    }

    public static ServiceProcessHandle Start(IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = BuildStartInfo(environment);
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        var handle = new ServiceProcessHandle(process);
        process.OutputDataReceived += (_, args) => handle.AppendOutput(args.Data);
        process.ErrorDataReceived += (_, args) => handle.AppendOutput(args.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return handle;
    }

    public async Task WaitForExitAsync(TimeSpan timeout, string operation)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await Process.WaitForExitAsync(cancellation.Token);
            Process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            await DisposeAsync();
            throw new InvalidOperationException($"{operation}: service did not exit within {timeout.TotalSeconds:0.#}s. Output:{Environment.NewLine}{Output}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
        }

        try
        {
            await Process.WaitForExitAsync();
            Process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // Process may already be gone between HasExited and Kill/WaitForExit.
        }
        finally
        {
            Process.Dispose();
        }
    }

    private static ProcessStartInfo BuildStartInfo(IReadOnlyDictionary<string, string> environment)
    {
        var appHostName = OperatingSystem.IsWindows() ? "adgroups2cmdbuild.exe" : "adgroups2cmdbuild";
        var appHostPath = Path.Combine(AppContext.BaseDirectory, appHostName);
        var dllPath = Path.Combine(AppContext.BaseDirectory, "adgroups2cmdbuild.dll");
        var startInfo = File.Exists(appHostPath)
            ? new ProcessStartInfo(appHostPath)
            : new ProcessStartInfo(FindDotnetHost(), $"\"{dllPath}\"");

        startInfo.WorkingDirectory = AppContext.BaseDirectory;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        foreach (var item in environment)
        {
            startInfo.Environment[item.Key] = item.Value;
        }

        return startInfo;
    }

    private static string FindDotnetHost()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            var candidate = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath)
            && Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.ProcessPath;
        }

        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }

    private void AppendOutput(string? value)
    {
        if (value is null)
        {
            return;
        }

        lock (outputGate)
        {
            output.AppendLine(value);
        }
    }
}

internal sealed record CapturedRequest(
    string Method,
    string Uri,
    string? AuthorizationScheme,
    string? AuthorizationParameter,
    string? CmdbuildView,
    string? Body);

internal sealed record CapturedHttpRequest(
    string Method,
    string Path,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    string Body);

internal sealed record ProcessResult(int ExitCode, string Output);
