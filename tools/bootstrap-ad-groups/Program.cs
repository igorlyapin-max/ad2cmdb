using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AdGroups2Cmdbuild.Secrets;
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
ValidateOptions(adOptions, cmdbOptions, options);

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
ldap.Bind();

var plan = new List<GroupPlanItem>();
foreach (var role in selectedRoles.OrderBy(role => role.Name, StringComparer.OrdinalIgnoreCase))
{
    var existingDn = FindExistingGroupDn(ldap, adOptions, role.Name);
    plan.Add(new GroupPlanItem(role.Name, existingDn, BuildGroupDn(role.Name, options.TargetOuDn), existingDn is null));
}

PrintPlan(plan, options.Apply);
if (!options.Apply)
{
    Console.WriteLine("Dry-run only. Re-run with --apply to create missing AD groups.");
    return 0;
}

foreach (var item in plan.Where(item => item.ShouldCreate))
{
    CreateGroup(ldap, item.Name, item.TargetDn, options);
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
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{options.BaseUrl.TrimEnd('/')}/roles?limit={options.RolesPageSize}&offset={offset}&detailed=true");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("CMDBuild-View", "admin");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"CMDBuild roles request failed with HTTP {(int)response.StatusCode}: {text}");
        }

        using var document = JsonDocument.Parse(text);
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
    IEnumerable<CmdbuildRole> selected = roles;
    if (!options.All)
    {
        if (options.IncludeRoleNames.Count > 0)
        {
            var include = options.IncludeRoleNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected = selected.Where(role => include.Contains(role.Name));
        }
        else if (!string.IsNullOrWhiteSpace(options.IncludeNamePrefix))
        {
            selected = selected.Where(role => role.Name.StartsWith(options.IncludeNamePrefix, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var configuredGroups = options.FallbackGroupNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected = selected.Where(role => configuredGroups.Contains(role.Name));
        }
    }

    if (options.ExcludeRoleNames.Count > 0)
    {
        var exclude = options.ExcludeRoleNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        selected = selected.Where(role => !exclude.Contains(role.Name));
    }

    return selected.ToList();
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

static string? FindExistingGroupDn(LdapConnection connection, ActiveDirectoryOptions options, string groupName)
{
    var filter = $"(&(objectClass=group)({options.GroupNameAttribute}={EscapeFilterValue(groupName)}))";
    var request = new SearchRequest(options.GroupSearchBaseDn, filter, SearchScope.Subtree, "distinguishedName");
    var response = (SearchResponse)connection.SendRequest(request);
    return response.Entries.Count > 0 ? response.Entries[0].DistinguishedName : null;
}

static void CreateGroup(LdapConnection connection, string name, string dn, BootstrapOptions options)
{
    var request = new AddRequest(dn, new DirectoryAttribute("objectClass", "top", "group"));
    request.Attributes.Add(new DirectoryAttribute("cn", name));
    request.Attributes.Add(new DirectoryAttribute("sAMAccountName", BuildSamAccountName(name)));
    request.Attributes.Add(new DirectoryAttribute("groupType", BuildGroupType(options).ToString()));
    if (!string.IsNullOrWhiteSpace(options.DescriptionTemplate))
    {
        request.Attributes.Add(new DirectoryAttribute(
            "description",
            options.DescriptionTemplate.Replace("{group}", name, StringComparison.OrdinalIgnoreCase)));
    }

    connection.SendRequest(request);
}

static int BuildGroupType(BootstrapOptions options)
{
    var scope = options.GroupScope.ToLowerInvariant() switch
    {
        "global" => 0x00000002,
        "domainlocal" or "domain-local" => 0x00000004,
        "universal" => 0x00000008,
        _ => throw new InvalidOperationException($"Unsupported group scope: {options.GroupScope}")
    };

    return options.SecurityEnabled ? unchecked((int)0x80000000) | scope : scope;
}

static string BuildSamAccountName(string name)
{
    var value = name.Trim();
    if (value.Length > 256)
    {
        throw new InvalidOperationException($"Role name is too long for AD sAMAccountName: {name}");
    }

    if (value.IndexOfAny(['"', '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>']) >= 0)
    {
        throw new InvalidOperationException($"Role name contains characters unsafe for AD sAMAccountName: {name}");
    }

    return value;
}

static string BuildGroupDn(string groupName, string targetOuDn)
{
    return $"CN={EscapeDnValue(groupName)},{targetOuDn}";
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
        IntValue(configuration, "ActiveDirectory:RequestTimeoutMs", 15000));
}

static CmdbuildOptions ReadCmdbuildOptions(IConfiguration configuration)
{
    return new CmdbuildOptions(
        Required(configuration, "Cmdbuild:BaseUrl"),
        Required(configuration, "Cmdbuild:Username"),
        Required(configuration, "Cmdbuild:Password"),
        IntValue(configuration, "Cmdbuild:RequestTimeoutMs", 15000),
        IntValue(configuration, "Cmdbuild:RolesPageSize", 1000),
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

static void ValidateOptions(ActiveDirectoryOptions ad, CmdbuildOptions cmdb, BootstrapOptions options)
{
    if (string.IsNullOrWhiteSpace(options.TargetOuDn))
    {
        throw new InvalidOperationException("BootstrapAdGroups:TargetOuDn is required, or ActiveDirectory:GroupSearchBaseDn must be set.");
    }

    if (cmdb.RolesPageSize <= 0 || ad.RequestTimeoutMs <= 0 || options.TimeoutMs <= 0)
    {
        throw new InvalidOperationException("Timeout/page size settings must be positive.");
    }
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

static string EscapeDnValue(string value)
{
    var escaped = value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("+", "\\+", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("<", "\\<", StringComparison.Ordinal)
        .Replace(">", "\\>", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace("=", "\\=", StringComparison.Ordinal);
    if (escaped.StartsWith(' ') || escaped.StartsWith('#'))
    {
        escaped = $"\\{escaped}";
    }

    if (escaped.EndsWith(' '))
    {
        escaped = $"{escaped[..^1]}\\ ";
    }

    return escaped;
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
    int RequestTimeoutMs);

internal sealed record CmdbuildOptions(
    string BaseUrl,
    string Username,
    string Password,
    int RequestTimeoutMs,
    int RolesPageSize,
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
        All || !string.IsNullOrWhiteSpace(IncludeNamePrefix) || IncludeRoleNames.Count > 0;
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
