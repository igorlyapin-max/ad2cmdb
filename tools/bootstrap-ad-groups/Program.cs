using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AdGroups2Cmdbuild.Configuration;
using AdGroups2Cmdbuild.Resilience;
using AdGroups2Cmdbuild.Secrets;
using BootstrapAdGroups;
using Microsoft.Extensions.Configuration;

var cli = BootstrapCli.Parse(args);
if (cli.ShowHelp)
{
    PrintHelp();
    return 0;
}

var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
    ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
    ?? "Production";
var configuration = new ConfigurationManager();
configuration.AddJsonFile("src/adgroups2cmdbuild/appsettings.json", optional: false, reloadOnChange: false);
configuration.AddJsonFile($"src/adgroups2cmdbuild/appsettings.{environment}.json", optional: true, reloadOnChange: false);
configuration.AddEnvironmentVariables();
configuration.AddInMemoryCollection(cli.ToConfiguration());
await configuration.ResolveSecretReferencesAsync("bootstrap-ad-groups");

var adOptions = ReadAdOptions(configuration);
var cmdbOptions = ReadCmdbuildOptions(configuration);
var options = ReadBootstrapOptions(configuration);
ValidateOptions(adOptions, cmdbOptions, options, environment);

using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(options.TimeoutMs));
var roles = await ReadCmdbuildRolesAsync(cmdbOptions, cancellation.Token);
var selectedRoles = SelectRoles(roles, options);
if (selectedRoles.Count == 0)
{
    Console.Error.WriteLine("No CMDBuild roles matched the bootstrap selection.");
    return 2;
}

if (options.Apply && options.RequireExplicitSelectionForApply && !options.All && !options.HasExplicitSelection)
{
    Console.Error.WriteLine("Refusing --apply without explicit selection. Use --all, --prefix, --include, or BootstrapAdGroups:IncludeRoleNames.");
    return 2;
}

using var ldap = CreateLdapConnection(adOptions);
await ExecuteAdWithRetryAsync(
    adOptions,
    "LDAP bind",
    token => Task.Run(() =>
    {
        ldap.Bind();
        return true;
    }, token),
    cancellation.Token);

var plan = new List<GroupPlanItem>();
foreach (var role in selectedRoles.OrderBy(role => role.Name, StringComparer.OrdinalIgnoreCase))
{
    var existingDn = await FindExistingGroupDnAsync(ldap, adOptions, role.Name, cancellation.Token);
    plan.Add(new GroupPlanItem(
        role.Name,
        existingDn,
        BootstrapAdGroupsLogic.BuildGroupDn(role.Name, options.TargetOuDn),
        existingDn is null));
}

PrintPlan(plan, options.Apply);
if (!options.Apply)
{
    Console.WriteLine("Dry-run only. Re-run with --apply to create missing AD groups.");
    return 0;
}

foreach (var item in plan.Where(item => item.ShouldCreate))
{
    await CreateGroupAsync(ldap, adOptions, item.Name, item.TargetDn, options, cancellation.Token);
    Console.WriteLine($"created: {item.Name} -> {item.TargetDn}");
}

Console.WriteLine($"Done. Created {plan.Count(item => item.ShouldCreate)} AD group(s).");
return 0;

static async Task<IReadOnlyList<CmdbuildRole>> ReadCmdbuildRolesAsync(
    CmdbuildOptions options,
    CancellationToken cancellationToken)
{
    using var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMilliseconds(options.RequestTimeoutMs)
    };

    var roles = new List<CmdbuildRole>();
    var offset = 0;
    while (true)
    {
        using var document = await ExecuteCmdbuildWithRetryAsync(
            options,
            $"CMDBuild roles page offset={offset}",
            async token =>
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{options.BaseUrl.TrimEnd('/')}/roles?limit={options.RolesPageSize}&offset={offset}&detailed=true");
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}")));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Add("CMDBuild-View", "admin");

                using var response = await httpClient.SendAsync(request, token);
                var text = await response.Content.ReadAsStringAsync(token);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"CMDBuild roles request failed with HTTP {(int)response.StatusCode}: {text}",
                        null,
                        response.StatusCode);
                }

                return JsonDocument.Parse(text);
            },
            cancellationToken);
        var page = ReadDataArray(document.RootElement).ToArray();
        foreach (var item in page)
        {
            if (!options.IncludeInactiveRoles && ReadBool(item, "active") == false)
            {
                continue;
            }

            var id = ReadString(item, "_id") ?? ReadString(item, "id");
            var name = ReadFirstString(item, options.RoleNameFields);
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            {
                roles.Add(new CmdbuildRole(id, name.Trim()));
            }
        }

        if (page.Length < options.RolesPageSize)
        {
            break;
        }

        offset += page.Length;
    }

    return roles
        .DistinctBy(role => role.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static List<CmdbuildRole> SelectRoles(IReadOnlyList<CmdbuildRole> roles, BootstrapOptions options)
{
    var selectedNames = BootstrapAdGroupsLogic.SelectRoleNames(
            roles.Select(role => role.Name),
            new BootstrapRoleSelection(
                options.All,
                options.IncludeNamePrefix,
                options.IncludeRoleNames,
                options.ExcludeRoleNames,
                options.FallbackGroupNames))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    return roles
        .Where(role => selectedNames.Contains(role.Name))
        .ToList();
}

static LdapConnection CreateLdapConnection(ActiveDirectoryOptions options)
{
    var identifier = new LdapDirectoryIdentifier(options.Host, options.Port, fullyQualifiedDnsHostName: true, connectionless: false);
    var connection = new LdapConnection(identifier)
    {
        AuthType = AuthType.Basic,
        Credential = new NetworkCredential(options.BindDn, options.BindPassword),
        Timeout = TimeSpan.FromMilliseconds(options.RequestTimeoutMs)
    };
    connection.SessionOptions.ProtocolVersion = 3;
    connection.SessionOptions.SecureSocketLayer = options.UseSsl;
    if (options.UseSsl && options.IgnoreCertificateErrors)
    {
        connection.SessionOptions.VerifyServerCertificate = (_, _) => true;
    }

    return connection;
}

static Task<string?> FindExistingGroupDnAsync(
    LdapConnection connection,
    ActiveDirectoryOptions options,
    string groupName,
    CancellationToken cancellationToken)
{
    return ExecuteAdWithRetryAsync(
        options,
        $"LDAP group search for {groupName}",
        token => Task.Run(() => FindExistingGroupDnOnce(connection, options, groupName), token),
        cancellationToken);
}

static string? FindExistingGroupDnOnce(LdapConnection connection, ActiveDirectoryOptions options, string groupName)
{
    var filter = $"(&(objectClass=group)({options.GroupNameAttribute}={EscapeFilterValue(groupName)}))";
    var request = new SearchRequest(options.GroupSearchBaseDn, filter, SearchScope.Subtree, "distinguishedName");
    var response = (SearchResponse)connection.SendRequest(request);
    return response.Entries.Count > 0 ? response.Entries[0].DistinguishedName : null;
}

static Task CreateGroupAsync(
    LdapConnection connection,
    ActiveDirectoryOptions adOptions,
    string name,
    string dn,
    BootstrapOptions options,
    CancellationToken cancellationToken)
{
    return ExecuteAdWithRetryAsync(
        adOptions,
        $"LDAP group create for {name}",
        token => Task.Run(() =>
        {
            CreateGroupOnce(connection, name, dn, options);
            return true;
        }, token),
        cancellationToken);
}

static void CreateGroupOnce(LdapConnection connection, string name, string dn, BootstrapOptions options)
{
    var request = new AddRequest(dn, new DirectoryAttribute("objectClass", "top", "group"));
    request.Attributes.Add(new DirectoryAttribute("cn", name));
    request.Attributes.Add(new DirectoryAttribute("sAMAccountName", BootstrapAdGroupsLogic.BuildSamAccountName(name)));
    request.Attributes.Add(new DirectoryAttribute("groupType", BootstrapAdGroupsLogic.BuildGroupType(options.GroupScope, options.SecurityEnabled).ToString()));
    if (!string.IsNullOrWhiteSpace(options.DescriptionTemplate))
    {
        request.Attributes.Add(new DirectoryAttribute(
            "description",
            options.DescriptionTemplate.Replace("{group}", name, StringComparison.OrdinalIgnoreCase)));
    }

    connection.SendRequest(request);
}

static void PrintPlan(IReadOnlyList<GroupPlanItem> plan, bool apply)
{
    Console.WriteLine(apply ? "AD group bootstrap apply plan:" : "AD group bootstrap dry-run plan:");
    foreach (var item in plan)
    {
        if (item.ShouldCreate)
        {
            Console.WriteLine($"  create  {item.Name} -> {item.TargetDn}");
        }
        else
        {
            Console.WriteLine($"  exists  {item.Name} -> {item.ExistingDn}");
        }
    }
}

static ActiveDirectoryOptions ReadAdOptions(IConfiguration configuration)
{
    return new ActiveDirectoryOptions(
        Required(configuration, "ActiveDirectory:Host"),
        IntValue(configuration, "ActiveDirectory:Port", 389),
        BoolValue(configuration, "ActiveDirectory:UseSsl"),
        BoolValue(configuration, "ActiveDirectory:IgnoreCertificateErrors"),
        Required(configuration, "ActiveDirectory:BindDn"),
        Required(configuration, "ActiveDirectory:BindPassword"),
        Required(configuration, "ActiveDirectory:GroupSearchBaseDn"),
        Value(configuration, "ActiveDirectory:GroupNameAttribute") ?? "cn",
        IntValue(configuration, "ActiveDirectory:RequestTimeoutMs", 15000),
        IntValue(configuration, "ActiveDirectory:RetryAttempts", 3),
        IntValue(configuration, "ActiveDirectory:RetryBaseDelayMs", 250),
        IntValue(configuration, "ActiveDirectory:RetryMaxDelayMs", 2000),
        IntValue(configuration, "ActiveDirectory:RetryJitterPercent", 20));
}

static CmdbuildOptions ReadCmdbuildOptions(IConfiguration configuration)
{
    return new CmdbuildOptions(
        Required(configuration, "Cmdbuild:BaseUrl"),
        Required(configuration, "Cmdbuild:Username"),
        Required(configuration, "Cmdbuild:Password"),
        IntValue(configuration, "Cmdbuild:RequestTimeoutMs", 15000),
        IntValue(configuration, "Cmdbuild:RolesPageSize", 1000),
        IntValue(configuration, "Cmdbuild:RetryAttempts", 3),
        IntValue(configuration, "Cmdbuild:RetryBaseDelayMs", 250),
        IntValue(configuration, "Cmdbuild:RetryMaxDelayMs", 2000),
        IntValue(configuration, "Cmdbuild:RetryJitterPercent", 20),
        StringList(configuration, "Cmdbuild:RoleNameFields", ["name", "code", "description"]),
        BoolValue(configuration, "BootstrapAdGroups:IncludeInactiveRoles"));
}

static BootstrapOptions ReadBootstrapOptions(IConfiguration configuration)
{
    var fallbackGroups = StringList(configuration, "ActiveDirectory:GroupNames", []);
    var include = StringList(configuration, "BootstrapAdGroups:IncludeRoleNames", []);
    return new BootstrapOptions(
        Apply: BoolValue(configuration, "BootstrapAdGroups:Apply"),
        All: BoolValue(configuration, "BootstrapAdGroups:All"),
        TargetOuDn: Value(configuration, "BootstrapAdGroups:TargetOuDn")
            ?? Value(configuration, "ActiveDirectory:GroupSearchBaseDn")
            ?? "",
        IncludeNamePrefix: Value(configuration, "BootstrapAdGroups:IncludeNamePrefix") ?? "",
        IncludeRoleNames: include,
        ExcludeRoleNames: StringList(configuration, "BootstrapAdGroups:ExcludeRoleNames", []),
        FallbackGroupNames: fallbackGroups,
        GroupScope: Value(configuration, "BootstrapAdGroups:GroupScope") ?? "Global",
        SecurityEnabled: BoolValue(configuration, "BootstrapAdGroups:SecurityEnabled", defaultValue: true),
        DescriptionTemplate: Value(configuration, "BootstrapAdGroups:DescriptionTemplate")
            ?? "Created by adgroups2cmdbuild bootstrap from CMDBuild role {group}",
        RequireExplicitSelectionForApply: BoolValue(configuration, "BootstrapAdGroups:RequireExplicitSelectionForApply", defaultValue: true),
        IncludeInactiveRoles: BoolValue(configuration, "BootstrapAdGroups:IncludeInactiveRoles"),
        TimeoutMs: IntValue(configuration, "BootstrapAdGroups:TimeoutMs", 120000));
}

static void ValidateOptions(ActiveDirectoryOptions ad, CmdbuildOptions cmdb, BootstrapOptions options, string environment)
{
    if (string.IsNullOrWhiteSpace(options.TargetOuDn))
    {
        throw new InvalidOperationException("BootstrapAdGroups:TargetOuDn is required, or ActiveDirectory:GroupSearchBaseDn must be set.");
    }

    if (cmdb.RolesPageSize <= 0 || ad.RequestTimeoutMs <= 0 || cmdb.RequestTimeoutMs <= 0 || options.TimeoutMs <= 0)
    {
        throw new InvalidOperationException("Timeout/page size settings must be positive.");
    }

    if (ad.RetryAttempts <= 0 || cmdb.RetryAttempts <= 0
        || ad.RetryBaseDelayMs <= 0 || cmdb.RetryBaseDelayMs <= 0
        || ad.RetryMaxDelayMs < ad.RetryBaseDelayMs || cmdb.RetryMaxDelayMs < cmdb.RetryBaseDelayMs
        || ad.RetryJitterPercent is < 0 or > 100 || cmdb.RetryJitterPercent is < 0 or > 100)
    {
        throw new InvalidOperationException("Retry settings must be positive, bounded, and use jitter between 0 and 100.");
    }

    if (!string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    if (!ProductionGuards.ActiveDirectoryUsesSecureTransport(ad.UseSsl))
    {
        throw new InvalidOperationException("ActiveDirectory:UseSsl must be true in Production because LDAP simple bind sends credentials.");
    }

    if (ProductionGuards.AllowsActiveDirectoryCertificateBypass(ad.IgnoreCertificateErrors))
    {
        throw new InvalidOperationException("ActiveDirectory:IgnoreCertificateErrors is not allowed in Production.");
    }

    if (!ProductionGuards.CmdbuildBaseUrlUsesHttps(cmdb.BaseUrl))
    {
        throw new InvalidOperationException("Cmdbuild:BaseUrl must use https in Production.");
    }
}

static async Task<T> ExecuteCmdbuildWithRetryAsync<T>(
    CmdbuildOptions options,
    string operationName,
    Func<CancellationToken, Task<T>> operation,
    CancellationToken cancellationToken)
{
    var attempts = Math.Max(1, options.RetryAttempts);
    for (var attempt = 1; attempt <= attempts; attempt++)
    {
        try
        {
            return await operation(cancellationToken);
        }
        catch (Exception exception) when (ShouldRetryCmdbuild(exception, attempt, attempts, cancellationToken))
        {
            var delay = RetryBackoff.CalculateDelay(
                attempt,
                options.RetryBaseDelayMs,
                options.RetryMaxDelayMs,
                options.RetryJitterPercent);
            Console.Error.WriteLine(
                $"Transient {operationName} failure on attempt {attempt}/{attempts}; status={RetryStatus(exception)}; retrying in {(int)delay.TotalMilliseconds}ms.");
            await Task.Delay(delay, cancellationToken);
        }
    }

    throw new InvalidOperationException($"{operationName} retry loop exhausted unexpectedly.");
}

static async Task<T> ExecuteAdWithRetryAsync<T>(
    ActiveDirectoryOptions options,
    string operationName,
    Func<CancellationToken, Task<T>> operation,
    CancellationToken cancellationToken)
{
    var attempts = Math.Max(1, options.RetryAttempts);
    for (var attempt = 1; attempt <= attempts; attempt++)
    {
        try
        {
            return await operation(cancellationToken);
        }
        catch (Exception exception) when (ShouldRetryAd(exception, attempt, attempts, cancellationToken))
        {
            var delay = RetryBackoff.CalculateDelay(
                attempt,
                options.RetryBaseDelayMs,
                options.RetryMaxDelayMs,
                options.RetryJitterPercent);
            Console.Error.WriteLine(
                $"Transient {operationName} failure on attempt {attempt}/{attempts}; ldapCode={LdapCode(exception)}; retrying in {(int)delay.TotalMilliseconds}ms.");
            await Task.Delay(delay, cancellationToken);
        }
    }

    throw new InvalidOperationException($"{operationName} retry loop exhausted unexpectedly.");
}

static bool ShouldRetryCmdbuild(Exception exception, int attempt, int attempts, CancellationToken cancellationToken)
{
    if (attempt >= attempts || cancellationToken.IsCancellationRequested)
    {
        return false;
    }

    return exception switch
    {
        HttpRequestException httpRequestException => IsTransientHttpStatus(httpRequestException.StatusCode),
        TaskCanceledException => true,
        TimeoutException => true,
        _ => false
    };
}

static bool ShouldRetryAd(Exception exception, int attempt, int attempts, CancellationToken cancellationToken)
{
    if (attempt >= attempts || cancellationToken.IsCancellationRequested)
    {
        return false;
    }

    return exception switch
    {
        LdapException ldapException => IsTransientLdapCode(ldapException.ErrorCode),
        DirectoryOperationException directoryException => IsTransientDirectoryResult(directoryException),
        IOException => true,
        SocketException => true,
        TimeoutException => true,
        TaskCanceledException => true,
        _ => exception.InnerException is not null && ShouldRetryAd(exception.InnerException, attempt, attempts, cancellationToken)
    };
}

static bool IsTransientHttpStatus(HttpStatusCode? statusCode)
{
    var code = (int?)statusCode;
    return statusCode is HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        || code is >= 500 and <= 599;
}

static bool IsTransientLdapCode(int code)
{
    return code is 51 or 52 or 80 or 81 or 85 or 91;
}

static bool IsTransientDirectoryResult(DirectoryOperationException exception)
{
    var code = (int?)exception.Response?.ResultCode;
    return code is 3 or 51 or 52 or 80;
}

static string RetryStatus(Exception exception)
{
    if (exception is HttpRequestException { StatusCode: not null } httpRequestException)
    {
        return ((int)httpRequestException.StatusCode.Value).ToString();
    }

    return "none";
}

static string LdapCode(Exception exception)
{
    return exception switch
    {
        LdapException ldapException => ldapException.ErrorCode.ToString(),
        DirectoryOperationException directoryException => ((int?)directoryException.Response?.ResultCode)?.ToString() ?? "none",
        _ => "none"
    };
}

static IEnumerable<JsonElement> ReadDataArray(JsonElement root)
{
    if (root.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in root.EnumerateArray())
        {
            yield return item.Clone();
        }

        yield break;
    }

    if (root.ValueKind != JsonValueKind.Object)
    {
        yield break;
    }

    foreach (var propertyName in new[] { "data", "items" })
    {
        if (TryGetProperty(root, propertyName, out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                yield return item.Clone();
            }

            yield break;
        }
    }
}

static string? ReadFirstString(JsonElement element, IReadOnlyCollection<string> propertyNames)
{
    foreach (var propertyName in propertyNames)
    {
        var value = ReadString(element, propertyName);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
    }

    return null;
}

static string? ReadString(JsonElement element, string propertyName)
{
    if (!TryGetProperty(element, propertyName, out var value))
    {
        return null;
    }

    return value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => bool.TrueString,
        JsonValueKind.False => bool.FalseString,
        _ => null
    };
}

static bool? ReadBool(JsonElement element, string propertyName)
{
    if (!TryGetProperty(element, propertyName, out var value))
    {
        return null;
    }

    return value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
        _ => null
    };
}

static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
{
    value = default;
    if (element.ValueKind != JsonValueKind.Object)
    {
        return false;
    }

    foreach (var property in element.EnumerateObject())
    {
        if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
        {
            value = property.Value;
            return true;
        }
    }

    return false;
}

static string? Value(IConfiguration configuration, string path)
{
    var value = configuration[path];
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

static string Required(IConfiguration configuration, string path)
{
    return Value(configuration, path) ?? throw new InvalidOperationException($"Required configuration value is missing: {path}");
}

static int IntValue(IConfiguration configuration, string path, int defaultValue)
{
    return int.TryParse(configuration[path], out var value) ? value : defaultValue;
}

static bool BoolValue(IConfiguration configuration, string path, bool defaultValue = false)
{
    return bool.TryParse(configuration[path], out var value) ? value : defaultValue;
}

static IReadOnlyCollection<string> StringList(IConfiguration configuration, string path, IReadOnlyCollection<string> defaultValue)
{
    var section = configuration.GetSection(path);
    var children = section.GetChildren()
        .Select(child => child.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim())
        .ToArray();
    if (children.Length > 0)
    {
        return children;
    }

    var direct = configuration[path];
    if (!string.IsNullOrWhiteSpace(direct))
    {
        return direct.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    return defaultValue;
}

static string EscapeFilterValue(string value)
{
    return value
        .Replace("\\", "\\5c", StringComparison.Ordinal)
        .Replace("*", "\\2a", StringComparison.Ordinal)
        .Replace("(", "\\28", StringComparison.Ordinal)
        .Replace(")", "\\29", StringComparison.Ordinal)
        .Replace("\0", "\\00", StringComparison.Ordinal);
}

static void PrintHelp()
{
    Console.WriteLine("""
    bootstrap-ad-groups creates missing AD groups from existing CMDBuild roles.

    Default mode is dry-run. Use --apply to create groups.

    Options:
      --target-ou <DN>       AD OU/container DN for new groups. Defaults to ActiveDirectory:GroupSearchBaseDn.
      --prefix <text>        Select CMDBuild roles with this name prefix.
      --include <a,b,c>      Select exact CMDBuild role names.
      --exclude <a,b,c>      Exclude exact CMDBuild role names.
      --all                  Select all CMDBuild roles.
      --scope <scope>        Global, Universal, or DomainLocal. Default: Global.
      --apply                Apply the plan. Without it only prints the plan.
      --help                 Show this help.
    """);
}

internal sealed record CmdbuildRole(string Id, string Name);

internal sealed record ActiveDirectoryOptions(
    string Host,
    int Port,
    bool UseSsl,
    bool IgnoreCertificateErrors,
    string BindDn,
    string BindPassword,
    string GroupSearchBaseDn,
    string GroupNameAttribute,
    int RequestTimeoutMs,
    int RetryAttempts,
    int RetryBaseDelayMs,
    int RetryMaxDelayMs,
    int RetryJitterPercent);

internal sealed record CmdbuildOptions(
    string BaseUrl,
    string Username,
    string Password,
    int RequestTimeoutMs,
    int RolesPageSize,
    int RetryAttempts,
    int RetryBaseDelayMs,
    int RetryMaxDelayMs,
    int RetryJitterPercent,
    IReadOnlyCollection<string> RoleNameFields,
    bool IncludeInactiveRoles);

internal sealed record BootstrapOptions(
    bool Apply,
    bool All,
    string TargetOuDn,
    string IncludeNamePrefix,
    IReadOnlyCollection<string> IncludeRoleNames,
    IReadOnlyCollection<string> ExcludeRoleNames,
    IReadOnlyCollection<string> FallbackGroupNames,
    string GroupScope,
    bool SecurityEnabled,
    string DescriptionTemplate,
    bool RequireExplicitSelectionForApply,
    bool IncludeInactiveRoles,
    int TimeoutMs)
{
    public bool HasExplicitSelection =>
        BootstrapAdGroupsLogic.HasExplicitSelection(All, IncludeNamePrefix, IncludeRoleNames);
}

internal sealed record GroupPlanItem(string Name, string? ExistingDn, string TargetDn, bool ShouldCreate);

internal sealed class BootstrapCli
{
    private readonly Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);

    public bool ShowHelp { get; private init; }

    public static BootstrapCli Parse(string[] args)
    {
        var cli = new BootstrapCli();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--help" or "-h":
                    return new BootstrapCli { ShowHelp = true };
                case "--apply":
                    cli.values["BootstrapAdGroups:Apply"] = "true";
                    break;
                case "--all":
                    cli.values["BootstrapAdGroups:All"] = "true";
                    break;
                case "--target-ou":
                    cli.values["BootstrapAdGroups:TargetOuDn"] = RequiredArgument(args, ref index, arg);
                    break;
                case "--prefix":
                    cli.values["BootstrapAdGroups:IncludeNamePrefix"] = RequiredArgument(args, ref index, arg);
                    break;
                case "--include":
                    cli.values["BootstrapAdGroups:IncludeRoleNames"] = RequiredArgument(args, ref index, arg);
                    break;
                case "--exclude":
                    cli.values["BootstrapAdGroups:ExcludeRoleNames"] = RequiredArgument(args, ref index, arg);
                    break;
                case "--scope":
                    cli.values["BootstrapAdGroups:GroupScope"] = RequiredArgument(args, ref index, arg);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown argument: {arg}");
            }
        }

        return cli;
    }

    public IEnumerable<KeyValuePair<string, string?>> ToConfiguration()
    {
        return values;
    }

    private static string RequiredArgument(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Argument {name} requires a value.");
        }

        index++;
        return args[index];
    }
}
