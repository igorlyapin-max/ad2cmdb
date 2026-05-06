using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdGroups2Cmdbuild.Configuration;
using Microsoft.Extensions.Options;

namespace AdGroups2Cmdbuild.Cmdbuild;

public sealed class CmdbuildClient(
    HttpClient httpClient,
    IOptions<CmdbuildOptions> options,
    IOptions<DebugOptions> debugOptions,
    ILogger<CmdbuildClient> logger) : ICmdbuildClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CmdbuildSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = new CmdbuildSnapshot();
        if (debugOptions.Value.IsBasicEnabled())
        {
            logger.LogInformation(
                "Debug {DebugLevel}: reading CMDBuild snapshot from {BaseUrl}",
                debugOptions.Value.NormalizedLevel(),
                options.Value.BaseUrl);
        }

        foreach (var role in await ReadRolesAsync(cancellationToken))
        {
            snapshot.RolesByName[role.Name] = role;
            snapshot.RolesById[role.Id] = role;
        }

        foreach (var user in await ReadUsersAsync(snapshot, cancellationToken))
        {
            snapshot.UsersByLogin[user.Username] = user;
        }

        logger.LogInformation("Read {RoleCount} CMDBuild roles and {UserCount} users", snapshot.RolesByName.Count, snapshot.UsersByLogin.Count);
        if (debugOptions.Value.IsVerboseEnabled())
        {
            logger.LogInformation(
                "Debug Verbose: CMDBuild role names: {RoleNames}",
                string.Join(", ", snapshot.RolesByName.Keys.Order(StringComparer.OrdinalIgnoreCase)));
        }

        return snapshot;
    }

    public async Task CreateUserAsync(UserUpsertRequest request, CancellationToken cancellationToken)
    {
        var body = BuildUserBody(request, existingUser: null, active: true);
        var password = options.Value.NewUserPassword;
        body["password"] = string.IsNullOrWhiteSpace(password) ? GeneratePassword() : password;

        using var document = await SendAsync(HttpMethod.Post, "/users", body, cancellationToken);
        logger.LogInformation("Created CMDBuild user {Login} with {RoleCount} groups", request.Login, request.DesiredRoles.Count);
    }

    public async Task UpdateUserAsync(CmdbuildUser existingUser, UserUpsertRequest request, CancellationToken cancellationToken)
    {
        var body = BuildUserBody(request, existingUser, active: true);
        using var document = await SendAsync(HttpMethod.Put, $"/users/{Uri.EscapeDataString(existingUser.Id)}", body, cancellationToken);
        logger.LogInformation("Updated CMDBuild user {Login}: active=true, groups={RoleCount}", request.Login, request.DesiredRoles.Count);
    }

    public async Task DisableUserAsync(CmdbuildUser existingUser, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["username"] = existingUser.Username,
            ["active"] = false,
            ["multiGroup"] = true,
            ["userGroups"] = new JsonArray(),
            ["defaultUserGroup"] = null
        };

        using var document = await SendAsync(HttpMethod.Put, $"/users/{Uri.EscapeDataString(existingUser.Id)}", body, cancellationToken);
        logger.LogInformation("Disabled CMDBuild user {Login} and revoked all groups", existingUser.Username);
    }

    private async Task<IReadOnlyList<CmdbuildRole>> ReadRolesAsync(CancellationToken cancellationToken)
    {
        var roles = new List<CmdbuildRole>();
        var offset = 0;
        while (true)
        {
            using var document = await SendAsync(
                HttpMethod.Get,
                $"/roles?limit={options.Value.RolesPageSize}&offset={offset}&detailed=true",
                null,
                cancellationToken);
            var page = ReadDataArray(document?.RootElement).ToArray();
            if (debugOptions.Value.IsBasicEnabled())
            {
                logger.LogInformation(
                    "Debug {DebugLevel}: CMDBuild roles page offset={Offset} returned {Count} item(s)",
                    debugOptions.Value.NormalizedLevel(),
                    offset,
                    page.Length);
            }

            foreach (var item in page)
            {
                var id = ReadString(item, "_id") ?? ReadString(item, "id");
                var name = ReadRoleName(item);
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                {
                    roles.Add(new CmdbuildRole(id, name));
                }
            }

            if (page.Length < options.Value.RolesPageSize)
            {
                break;
            }

            offset += page.Length;
        }

        return roles;
    }

    private async Task<IReadOnlyList<CmdbuildUser>> ReadUsersAsync(CmdbuildSnapshot snapshot, CancellationToken cancellationToken)
    {
        var users = new List<CmdbuildUser>();
        var start = 0;
        while (true)
        {
            using var document = await SendAsync(
                HttpMethod.Get,
                $"/users?limit={options.Value.UsersPageSize}&start={start}&detailed=true",
                null,
                cancellationToken);
            var page = ReadDataArray(document?.RootElement).ToArray();
            if (debugOptions.Value.IsBasicEnabled())
            {
                logger.LogInformation(
                    "Debug {DebugLevel}: CMDBuild users page start={Start} returned {Count} item(s)",
                    debugOptions.Value.NormalizedLevel(),
                    start,
                    page.Length);
            }

            foreach (var item in page)
            {
                var id = ReadString(item, "_id") ?? ReadString(item, "id");
                var username = ReadString(item, "username");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(username))
                {
                    continue;
                }

                var user = new CmdbuildUser
                {
                    Id = id,
                    Username = username,
                    DisplayName = ReadString(item, options.Value.UserDisplayNameField) ?? ReadString(item, "description") ?? ReadString(item, "name"),
                    Email = ReadString(item, options.Value.UserEmailField) ?? ReadString(item, "email"),
                    Active = ReadBool(item, "active") ?? true
                };
                foreach (var roleId in ReadUserRoleIds(item, snapshot))
                {
                    user.RoleIds.Add(roleId);
                }

                users.Add(user);
            }

            if (page.Length < options.Value.UsersPageSize)
            {
                break;
            }

            start += page.Length;
        }

        return users;
    }

    private JsonObject BuildUserBody(UserUpsertRequest request, CmdbuildUser? existingUser, bool active)
    {
        var roles = request.DesiredRoles.ToList();
        if (existingUser is not null && options.Value.PreserveUnmanagedGroups)
        {
            var managedRoleIds = request.ManagedRoleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var existingRoleId in existingUser.RoleIds)
            {
                if (!managedRoleIds.Contains(existingRoleId))
                {
                    roles.Add(new CmdbuildRole(existingRoleId, ""));
                }
            }
        }

        var body = new JsonObject
        {
            ["username"] = request.Login,
            ["active"] = active,
            ["service"] = false,
            ["multiGroup"] = true,
            ["changePasswordRequired"] = false,
            ["userGroups"] = BuildRoleArray(roles),
            ["defaultUserGroup"] = roles.Count > 0 ? JsonValueFromId(roles[0].Id) : null
        };

        if (!string.IsNullOrWhiteSpace(options.Value.DefaultLanguage))
        {
            body["language"] = options.Value.DefaultLanguage;
        }

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
        {
            body[options.Value.UserDisplayNameField] = request.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            body[options.Value.UserEmailField] = request.Email;
        }

        return body;
    }

    private static JsonArray BuildRoleArray(IEnumerable<CmdbuildRole> roles)
    {
        var array = new JsonArray();
        foreach (var role in roles.DistinctBy(role => role.Id, StringComparer.OrdinalIgnoreCase))
        {
            var item = new JsonObject
            {
                ["_id"] = JsonValueFromId(role.Id)
            };
            if (!string.IsNullOrWhiteSpace(role.Name))
            {
                item["name"] = role.Name;
            }

            array.Add(item);
        }

        return array;
    }

    private async Task<JsonDocument?> SendAsync(
        HttpMethod method,
        string path,
        JsonObject? body,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(options.Value.RequestTimeoutMs));

        using var request = new HttpRequestMessage(method, $"{BaseUrl()}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Value.Username}:{options.Value.Password}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("CMDBuild-View", "admin");

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json");
        }

        using var response = await httpClient.SendAsync(request, timeout.Token);
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"CMDBuild {method} {path} failed with {(int)response.StatusCode} {response.ReasonPhrase}: {text}",
                null,
                response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return JsonDocument.Parse(text);
    }

    private string BaseUrl()
    {
        return options.Value.BaseUrl.TrimEnd('/');
    }

    private string? ReadRoleName(JsonElement element)
    {
        foreach (var field in options.Value.RoleNameFields)
        {
            var value = ReadString(element, field);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static IEnumerable<string> ReadUserRoleIds(JsonElement item, CmdbuildSnapshot snapshot)
    {
        if (!TryGetProperty(item, "userGroups", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var group in groups.EnumerateArray())
        {
            var id = ReadString(group, "_id") ?? ReadString(group, "id") ?? ReadString(group, "roleId");
            if (!string.IsNullOrWhiteSpace(id))
            {
                yield return id;
                continue;
            }

            var name = ReadString(group, "name") ?? ReadString(group, "code") ?? ReadString(group, "description");
            if (!string.IsNullOrWhiteSpace(name) && snapshot.RolesByName.TryGetValue(name, out var role))
            {
                yield return role.Id;
            }
        }
    }

    private static IEnumerable<JsonElement> ReadDataArray(JsonElement? root)
    {
        if (root is null)
        {
            yield break;
        }

        if (root.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.Value.EnumerateArray())
            {
                yield return item.Clone();
            }

            yield break;
        }

        if (root.Value.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var propertyName in new[] { "data", "items" })
        {
            if (TryGetProperty(root.Value, propertyName, out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    yield return item.Clone();
                }

                yield break;
            }
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
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

    private static bool? ReadBool(JsonElement element, string propertyName)
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

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
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

    private static JsonNode JsonValueFromId(string id)
    {
        if (long.TryParse(id, out var number))
        {
            return JsonValue.Create(number)!;
        }

        return JsonValue.Create(id)!;
    }

    private static string GeneratePassword()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }
}
