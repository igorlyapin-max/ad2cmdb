using AdGroups2Cmdbuild.ActiveDirectory;
using AdGroups2Cmdbuild.Cmdbuild;
using AdGroups2Cmdbuild.Configuration;
using AdGroups2Cmdbuild.Logging;
using AdGroups2Cmdbuild.Secrets;
using AdGroups2Cmdbuild.Sync;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
await builder.Configuration.ResolveSecretReferencesAsync("adgroups2cmdbuild");
var isProduction = builder.Environment.IsProduction();
var rateLimitSettings = builder.Configuration
    .GetSection(EndpointRateLimitOptions.SectionName)
    .Get<EndpointRateLimitOptions>() ?? new EndpointRateLimitOptions();

if (isProduction && string.Equals(builder.Configuration["AllowedHosts"]?.Trim(), "*", StringComparison.Ordinal))
{
    throw new InvalidOperationException("AllowedHosts must not be '*' in Production. Configure explicit host names.");
}

builder.Services.AddOptions<ServiceOptions>()
    .Bind(builder.Configuration.GetSection(ServiceOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Name), "Service name is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.HealthRoute), "Service health route is required.")
    .ValidateOnStart();

builder.Services.AddOptions<ActiveDirectoryOptions>()
    .Bind(builder.Configuration.GetSection(ActiveDirectoryOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Host), "ActiveDirectory:Host is required.")
    .Validate(options => options.Port > 0, "ActiveDirectory:Port must be greater than zero.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.BindDn), "ActiveDirectory:BindDn is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.BindPassword), "ActiveDirectory:BindPassword is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.GroupSearchBaseDn), "ActiveDirectory:GroupSearchBaseDn is required.")
    .Validate(options => options.GroupNames.Count > 0, "ActiveDirectory:GroupNames must contain at least one group.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ProvisioningGroupName), "ActiveDirectory:ProvisioningGroupName is required.")
    .Validate(options => options.GroupNames.Contains(options.ProvisioningGroupName, StringComparer.OrdinalIgnoreCase), "Provisioning group must be listed in ActiveDirectory:GroupNames.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.GroupNameAttribute), "ActiveDirectory:GroupNameAttribute is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.MemberAttribute), "ActiveDirectory:MemberAttribute is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.UserLoginAttribute), "ActiveDirectory:UserLoginAttribute is required.")
    .Validate(options => options.PageSize > 0, "ActiveDirectory:PageSize must be greater than zero.")
    .Validate(options => options.RequestTimeoutMs > 0, "ActiveDirectory:RequestTimeoutMs must be greater than zero.")
    .Validate(options => options.RetryAttempts > 0, "ActiveDirectory:RetryAttempts must be greater than zero.")
    .Validate(options => options.RetryBaseDelayMs > 0, "ActiveDirectory:RetryBaseDelayMs must be greater than zero.")
    .Validate(options => options.RetryMaxDelayMs >= options.RetryBaseDelayMs, "ActiveDirectory:RetryMaxDelayMs must be greater than or equal to RetryBaseDelayMs.")
    .Validate(options => options.RetryJitterPercent is >= 0 and <= 100, "ActiveDirectory:RetryJitterPercent must be between 0 and 100.")
    .Validate(options => !(isProduction && options.UseSsl && options.IgnoreCertificateErrors), "ActiveDirectory:IgnoreCertificateErrors is not allowed in Production.")
    .ValidateOnStart();

builder.Services.AddOptions<CmdbuildOptions>()
    .Bind(builder.Configuration.GetSection(CmdbuildOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BaseUrl), "Cmdbuild:BaseUrl is required.")
    .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _), "Cmdbuild:BaseUrl must be an absolute URL.")
    .Validate(options => !isProduction || Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps, "Cmdbuild:BaseUrl must use https in Production.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Username), "Cmdbuild:Username is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Password), "Cmdbuild:Password is required.")
    .Validate(options => options.RequestTimeoutMs > 0, "Cmdbuild:RequestTimeoutMs must be greater than zero.")
    .Validate(options => options.UsersPageSize > 0, "Cmdbuild:UsersPageSize must be greater than zero.")
    .Validate(options => options.RolesPageSize > 0, "Cmdbuild:RolesPageSize must be greater than zero.")
    .Validate(options => options.RetryAttempts > 0, "Cmdbuild:RetryAttempts must be greater than zero.")
    .Validate(options => options.RetryBaseDelayMs > 0, "Cmdbuild:RetryBaseDelayMs must be greater than zero.")
    .Validate(options => options.RetryMaxDelayMs >= options.RetryBaseDelayMs, "Cmdbuild:RetryMaxDelayMs must be greater than or equal to RetryBaseDelayMs.")
    .Validate(options => options.RetryJitterPercent is >= 0 and <= 100, "Cmdbuild:RetryJitterPercent must be between 0 and 100.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.UserDisplayNameField), "Cmdbuild:UserDisplayNameField is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.UserEmailField), "Cmdbuild:UserEmailField is required.")
    .Validate(options => options.RoleNameFields.Count > 0, "Cmdbuild:RoleNameFields must contain at least one field.")
    .ValidateOnStart();

builder.Services.AddOptions<SyncOptions>()
    .Bind(builder.Configuration.GetSection(SyncOptions.SectionName))
    .Validate(options => options.IntervalSeconds >= 30, "Sync:IntervalSeconds must be at least 30 seconds.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.StateFilePath), "Sync:StateFilePath is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.InstanceLockPath), "Sync:InstanceLockPath is required.")
    .Validate(options => options.FailureBackoffSeconds >= 0, "Sync:FailureBackoffSeconds must be greater than or equal to zero.")
    .Validate(options => options.ShutdownGracePeriodSeconds >= 0, "Sync:ShutdownGracePeriodSeconds must be greater than or equal to zero.")
    .ValidateOnStart();

builder.Services.AddOptions<DebugOptions>()
    .Bind(builder.Configuration.GetSection(DebugOptions.SectionName))
    .Validate(options => options.HasValidLevel(), "Debug:Level must be Basic, Verbose, 1, or 2.")
    .ValidateOnStart();

builder.Services.AddOptions<ReadinessOptions>()
    .Bind(builder.Configuration.GetSection(ReadinessOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Route), "Readiness:Route is required.")
    .Validate(options => options.Route.StartsWith('/'), "Readiness:Route must start with '/'.")
    .Validate(options => options.TimeoutMs > 0, "Readiness:TimeoutMs must be greater than zero.")
    .ValidateOnStart();

builder.Services.AddOptions<EndpointRateLimitOptions>()
    .Bind(builder.Configuration.GetSection(EndpointRateLimitOptions.SectionName))
    .Validate(options => options.PermitLimit > 0, "EndpointRateLimiting:PermitLimit must be greater than zero.")
    .Validate(options => options.WindowSeconds > 0, "EndpointRateLimiting:WindowSeconds must be greater than zero.")
    .ValidateOnStart();

builder.Services.AddOptions<ElkLoggingOptions>()
    .Bind(builder.Configuration.GetSection(ElkLoggingOptions.SectionName))
    .Validate(options => options.HasValidMinimumLevel(), "ElkLogging:MinimumLevel is invalid.")
    .Validate(options => options.HasValidEndpoint(), "ElkLogging:Endpoint must be an absolute URL when ELK logging is enabled.")
    .Validate(options => options.TimeoutMs > 0, "ElkLogging:TimeoutMs must be greater than zero.")
    .Validate(options => options.QueueCapacity > 0, "ElkLogging:QueueCapacity must be greater than zero.")
    .Validate(options => options.FlushTimeoutMs > 0, "ElkLogging:FlushTimeoutMs must be greater than zero.")
    .ValidateOnStart();

builder.Logging.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, ElkLoggerProvider>());

builder.Services.AddSingleton<IActiveDirectoryClient, ActiveDirectoryClient>();
builder.Services.AddHttpClient<ICmdbuildClient, CmdbuildClient>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<CmdbuildOptions>>().Value;
    client.Timeout = TimeSpan.FromMilliseconds(options.RequestTimeoutMs);
});
builder.Services.AddSingleton<ISyncStateStore, FileSyncStateStore>();
builder.Services.AddSingleton<SyncStatusStore>();
builder.Services.AddSingleton<SyncRunLock>();
builder.Services.AddSingleton<AdGroupSynchronizationService>();
builder.Services.AddHostedService<AdGroupSyncWorker>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("status", limiter =>
    {
        limiter.PermitLimit = rateLimitSettings.PermitLimit;
        limiter.Window = TimeSpan.FromSeconds(rateLimitSettings.WindowSeconds);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});

var app = builder.Build();
var serviceOptions = app.Services.GetRequiredService<IOptions<ServiceOptions>>().Value;
var readinessOptions = app.Services.GetRequiredService<IOptions<ReadinessOptions>>().Value;
var endpointRateLimitOptions = app.Services.GetRequiredService<IOptions<EndpointRateLimitOptions>>().Value;

if (endpointRateLimitOptions.Enabled)
{
    app.UseRateLimiter();
}

var healthEndpoint = app.MapGet(serviceOptions.HealthRoute, (SyncStatusStore statusStore) => Results.Ok(new
{
    service = serviceOptions.Name,
    status = "ok",
    sync = statusStore.Current
}));
ApplyRateLimit(healthEndpoint, endpointRateLimitOptions);

var syncStatusEndpoint = app.MapGet("/sync/status", (SyncStatusStore statusStore) => Results.Ok(statusStore.Current));
ApplyRateLimit(syncStatusEndpoint, endpointRateLimitOptions);

if (readinessOptions.Enabled)
{
    var readyEndpoint = app.MapGet(
        readinessOptions.Route,
        async (
            IOptions<ReadinessOptions> options,
            IOptions<ServiceOptions> service,
            IActiveDirectoryClient activeDirectoryClient,
            ICmdbuildClient cmdbuildClient,
            CancellationToken cancellationToken) =>
        {
            var settings = options.Value;
            if (!settings.CheckDependencies)
            {
                IResult shallowReady = Results.Ok(new
                {
                    service = service.Value.Name,
                    status = "ready",
                    dependenciesChecked = false
                });
                return shallowReady;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(settings.TimeoutMs));

            var ad = await CheckDependencyAsync("activeDirectory", activeDirectoryClient.CheckConnectionAsync, timeout.Token);
            var cmdbuild = await CheckDependencyAsync("cmdbuild", cmdbuildClient.CheckConnectionAsync, timeout.Token);
            var ready = ad.Ok && cmdbuild.Ok;
            var response = new
            {
                service = service.Value.Name,
                status = ready ? "ready" : "not_ready",
                dependenciesChecked = true,
                checks = new[] { ad, cmdbuild }
            };

            IResult result = ready
                ? Results.Ok(response)
                : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
            return result;
        });
    ApplyRateLimit(readyEndpoint, endpointRateLimitOptions);
}

app.Run();

static void ApplyRateLimit(RouteHandlerBuilder endpoint, EndpointRateLimitOptions options)
{
    if (options.Enabled)
    {
        endpoint.RequireRateLimiting("status");
    }
}

static async Task<DependencyCheck> CheckDependencyAsync(
    string name,
    Func<CancellationToken, Task> check,
    CancellationToken cancellationToken)
{
    try
    {
        await check(cancellationToken);
        return new DependencyCheck(name, true, null);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return new DependencyCheck(name, false, "timeout");
    }
    catch (Exception exception)
    {
        return new DependencyCheck(name, false, exception.Message);
    }
}

internal sealed record DependencyCheck(string Name, bool Ok, string? Error);
