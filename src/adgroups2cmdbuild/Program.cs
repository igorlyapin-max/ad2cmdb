using AdGroups2Cmdbuild.ActiveDirectory;
using AdGroups2Cmdbuild.Cmdbuild;
using AdGroups2Cmdbuild.Configuration;
using AdGroups2Cmdbuild.Logging;
using AdGroups2Cmdbuild.Secrets;
using AdGroups2Cmdbuild.Sync;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
await builder.Configuration.ResolveSecretReferencesAsync("adgroups2cmdbuild");

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
    .ValidateOnStart();

builder.Services.AddOptions<CmdbuildOptions>()
    .Bind(builder.Configuration.GetSection(CmdbuildOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BaseUrl), "Cmdbuild:BaseUrl is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Username), "Cmdbuild:Username is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Password), "Cmdbuild:Password is required.")
    .Validate(options => options.RequestTimeoutMs > 0, "Cmdbuild:RequestTimeoutMs must be greater than zero.")
    .Validate(options => options.UsersPageSize > 0, "Cmdbuild:UsersPageSize must be greater than zero.")
    .Validate(options => options.RolesPageSize > 0, "Cmdbuild:RolesPageSize must be greater than zero.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.UserDisplayNameField), "Cmdbuild:UserDisplayNameField is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.UserEmailField), "Cmdbuild:UserEmailField is required.")
    .Validate(options => options.RoleNameFields.Count > 0, "Cmdbuild:RoleNameFields must contain at least one field.")
    .ValidateOnStart();

builder.Services.AddOptions<SyncOptions>()
    .Bind(builder.Configuration.GetSection(SyncOptions.SectionName))
    .Validate(options => options.IntervalSeconds >= 30, "Sync:IntervalSeconds must be at least 30 seconds.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.StateFilePath), "Sync:StateFilePath is required.")
    .ValidateOnStart();

builder.Services.AddOptions<DebugOptions>()
    .Bind(builder.Configuration.GetSection(DebugOptions.SectionName))
    .Validate(options => options.HasValidLevel(), "Debug:Level must be Basic, Verbose, 1, or 2.")
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
builder.Services.AddSingleton<AdGroupSynchronizationService>();
builder.Services.AddHostedService<AdGroupSyncWorker>();

var app = builder.Build();
var serviceOptions = app.Services.GetRequiredService<IOptions<ServiceOptions>>().Value;

app.MapGet(serviceOptions.HealthRoute, (SyncStatusStore statusStore) => Results.Ok(new
{
    service = serviceOptions.Name,
    status = "ok",
    sync = statusStore.Current
}));

app.MapGet("/sync/status", (SyncStatusStore statusStore) => Results.Ok(statusStore.Current));

app.Run();
