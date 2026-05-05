using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AdGroups2Cmdbuild.Secrets;

public static class SecretConfigurationResolver
{
    private const string SecretPrefix = "secret://";
    private const string AapmPrefix = "aapm://";

    public static async Task ResolveSecretReferencesAsync(
        this ConfigurationManager configuration,
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        configuration.ApplyPamCompatibilityEnvironment();
        configuration.ApplySecretCompanionReferences();

        var references = configuration.AsEnumerable()
            .Where(item => !string.IsNullOrWhiteSpace(item.Value)
                && !item.Key.StartsWith("Secrets:", StringComparison.OrdinalIgnoreCase)
                && TryReadSecretId(item.Value, out _))
            .ToArray();
        if (references.Length == 0)
        {
            return;
        }

        var provider = configuration["Secrets:Provider"] ?? "None";
        if (!string.Equals(provider, "IndeedPamAapm", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Configuration contains secret:// references, but Secrets:Provider is '{provider}'.");
        }

        var client = new IndeedPamAapmSecretClient(configuration, serviceName);
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            if (TryReadSecretId(reference.Value, out var secretId))
            {
                resolved[reference.Key] = await client.GetSecretAsync(secretId, cancellationToken);
            }
        }

        if (resolved.Count > 0)
        {
            configuration.AddInMemoryCollection(resolved);
        }
    }

    private static bool TryReadSecretId(string? value, out string secretId)
    {
        secretId = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith(SecretPrefix, StringComparison.OrdinalIgnoreCase))
        {
            secretId = trimmed[SecretPrefix.Length..].Trim();
        }
        else if (trimmed.StartsWith(AapmPrefix, StringComparison.OrdinalIgnoreCase))
        {
            secretId = trimmed[AapmPrefix.Length..].Trim();
        }

        return !string.IsNullOrWhiteSpace(secretId);
    }
}

internal sealed class IndeedPamAapmSecretClient(IConfiguration configuration, string serviceName)
{
    public async Task<string> GetSecretAsync(string secretId, CancellationToken cancellationToken)
    {
        var baseUrl = Required("Secrets:IndeedPamAapm:BaseUrl");
        var endpointPath = Value("Secrets:IndeedPamAapm:PasswordEndpointPath") ?? "/sc_aapm_ui/rest/aapm/password";
        var (parsedAccountPath, parsedAccountName) = ParseSecretId(secretId);
        var accountPath = Value($"Secrets:References:{secretId}:AccountPath")
            ?? parsedAccountPath
            ?? Value("Secrets:IndeedPamAapm:DefaultAccountPath")
            ?? throw new InvalidOperationException(
                $"Required Indeed PAM AAPM configuration value is missing: Secrets:References:{secretId}:AccountPath.");
        var accountName = Value($"Secrets:References:{secretId}:AccountName")
            ?? parsedAccountName
            ?? throw new InvalidOperationException(
                $"Required Indeed PAM AAPM configuration value is missing: Secrets:References:{secretId}:AccountName.");
        var responseType = Value($"Secrets:References:{secretId}:ResponseType")
            ?? Value("Secrets:IndeedPamAapm:ResponseType")
            ?? "json";
        var valueJsonPath = Value($"Secrets:References:{secretId}:ValueJsonPath")
            ?? Value("Secrets:IndeedPamAapm:ValueJsonPath")
            ?? "password";
        var timeoutMs = IntValue("Secrets:IndeedPamAapm:TimeoutMs", 10000);

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(timeoutMs)
        };

        var applicationCredentials = await ReadApplicationCredentialsAsync(cancellationToken);
        var url = BuildUrl(baseUrl, endpointPath, new Dictionary<string, string?>
        {
            ["token"] = applicationCredentials.Token,
            ["sapmaccountpath"] = accountPath,
            ["sapmaccountname"] = accountName,
            ["responsetype"] = responseType,
            ["passwordexpirationinminute"] = Value($"Secrets:References:{secretId}:PasswordExpirationInMinute")
                ?? Value("Secrets:IndeedPamAapm:PasswordExpirationInMinute"),
            ["passwordchangerequired"] = BoolText($"Secrets:References:{secretId}:PasswordChangeRequired")
                ?? BoolText("Secrets:IndeedPamAapm:PasswordChangeRequired"),
            ["comment"] = FormatComment(
                Value($"Secrets:References:{secretId}:Comment")
                    ?? Value("Secrets:IndeedPamAapm:Comment")
                    ?? $"ad2cmdb {serviceName} {secretId}",
                secretId),
            ["tenantid"] = Value($"Secrets:References:{secretId}:TenantId")
                ?? Value("Secrets:IndeedPamAapm:TenantId"),
            ["pin"] = Value($"Secrets:References:{secretId}:Pin")
                ?? Value("Secrets:IndeedPamAapm:Pin")
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(applicationCredentials.Username)
            && !string.IsNullOrWhiteSpace(applicationCredentials.Password))
        {
            var basicToken = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{applicationCredentials.Username}:{applicationCredentials.Password}"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basicToken);
            if (BoolValue("Secrets:IndeedPamAapm:SendApplicationCredentialsInQuery"))
            {
                var separator = url.Contains('?') ? '&' : '?';
                url = $"{url}{separator}username={Uri.EscapeDataString(applicationCredentials.Username)}&password={Uri.EscapeDataString(applicationCredentials.Password)}";
                request.RequestUri = new Uri(url);
            }
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Indeed PAM AAPM secret '{secretId}' request failed with HTTP {(int)response.StatusCode}.");
        }

        var secret = ExtractSecretValue(body, responseType, valueJsonPath);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException($"Indeed PAM AAPM secret '{secretId}' returned an empty value.");
        }

        return secret;
    }

    private async Task<ApplicationCredentials> ReadApplicationCredentialsAsync(CancellationToken cancellationToken)
    {
        var token = Value("Secrets:IndeedPamAapm:ApplicationToken");
        if (!string.IsNullOrWhiteSpace(token))
        {
            return new ApplicationCredentials(token, null, null);
        }

        var tokenFile = Value("Secrets:IndeedPamAapm:ApplicationTokenFile");
        if (!string.IsNullOrWhiteSpace(tokenFile))
        {
            return new ApplicationCredentials(
                (await File.ReadAllTextAsync(tokenFile, cancellationToken)).Trim(),
                null,
                null);
        }

        var username = Value("Secrets:IndeedPamAapm:ApplicationUsername");
        var password = Value("Secrets:IndeedPamAapm:ApplicationPassword");
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            return new ApplicationCredentials(null, username, password);
        }

        throw new InvalidOperationException(
            "Indeed PAM AAPM credentials are not configured. Set ApplicationToken, ApplicationTokenFile, or ApplicationUsername/ApplicationPassword.");
    }

    private string Required(string path)
    {
        return Value(path) ?? throw new InvalidOperationException($"Required Indeed PAM AAPM configuration value is missing: {path}.");
    }

    private string? Value(string path)
    {
        var value = configuration[path];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private int IntValue(string path, int defaultValue)
    {
        return int.TryParse(configuration[path], out var value) && value > 0 ? value : defaultValue;
    }

    private string? BoolText(string path)
    {
        return bool.TryParse(configuration[path], out var value) ? value.ToString().ToLowerInvariant() : null;
    }

    private bool BoolValue(string path)
    {
        return bool.TryParse(configuration[path], out var value) && value;
    }

    private string FormatComment(string comment, string secretId)
    {
        return comment
            .Replace("{service}", serviceName, StringComparison.OrdinalIgnoreCase)
            .Replace("{secretId}", secretId, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildUrl(string baseUrl, string endpointPath, Dictionary<string, string?> query)
    {
        var builder = new StringBuilder();
        builder.Append(baseUrl.TrimEnd('/'));
        builder.Append('/');
        builder.Append(endpointPath.TrimStart('/'));

        var separator = '?';
        foreach (var (key, value) in query)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            builder.Append(separator);
            builder.Append(Uri.EscapeDataString(key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return builder.ToString();
    }

    private static string ExtractSecretValue(string body, string responseType, string valueJsonPath)
    {
        if (!responseType.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return body.Trim();
        }

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind == JsonValueKind.String)
        {
            return document.RootElement.GetString() ?? string.Empty;
        }

        if (TryReadJsonPath(document.RootElement, valueJsonPath, out var value))
        {
            return value;
        }

        foreach (var fallback in new[] { "password", "value", "secret", "Password" })
        {
            if (TryReadJsonPath(document.RootElement, fallback, out value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static bool TryReadJsonPath(JsonElement element, string path, out string value)
    {
        value = string.Empty;
        var current = element;
        foreach (var part in path.Split(['.', ':'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return false;
            }
        }

        value = current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? string.Empty,
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(value);
    }

    private static (string? AccountPath, string? AccountName) ParseSecretId(string secretId)
    {
        var dot = secretId.LastIndexOf('.');
        if (dot > 0 && dot < secretId.Length - 1)
        {
            return (secretId[..dot], secretId[(dot + 1)..]);
        }

        var slash = secretId.LastIndexOf('/');
        if (slash > 0 && slash < secretId.Length - 1)
        {
            return (secretId[..slash], secretId[(slash + 1)..]);
        }

        return (null, null);
    }

    private sealed record ApplicationCredentials(string? Token, string? Username, string? Password);
}

internal static class SecretConfigurationCompatibility
{
    public static void ApplyPamCompatibilityEnvironment(this ConfigurationManager configuration)
    {
        var updates = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        SetIfPresent(updates, configuration, "PAMURL", "Secrets:IndeedPamAapm:BaseUrl");
        SetIfPresent(updates, configuration, "PAMUSERNAME", "Secrets:IndeedPamAapm:ApplicationUsername");
        SetIfPresent(updates, configuration, "PAMPASSWORD", "Secrets:IndeedPamAapm:ApplicationPassword");
        SetIfPresent(updates, configuration, "PAMTOKEN", "Secrets:IndeedPamAapm:ApplicationToken");
        SetIfPresent(updates, configuration, "PAMDEFAULTACCOUNTPATH", "Secrets:IndeedPamAapm:DefaultAccountPath");

        var hasPamCompatibility =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAMURL"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAMTOKEN"))
            || (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAMUSERNAME"))
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAMPASSWORD")));
        if (hasPamCompatibility
            && string.Equals(configuration["Secrets:Provider"] ?? "None", "None", StringComparison.OrdinalIgnoreCase))
        {
            updates["Secrets:Provider"] = "IndeedPamAapm";
        }

        if (updates.Count > 0)
        {
            configuration.AddInMemoryCollection(updates);
        }
    }

    public static void ApplySecretCompanionReferences(this ConfigurationManager configuration)
    {
        var updates = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var existingKeys = configuration.AsEnumerable()
            .Select(item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in configuration.AsEnumerable())
        {
            if (string.IsNullOrWhiteSpace(item.Value)
                || item.Key.StartsWith("Secrets:", StringComparison.OrdinalIgnoreCase)
                || !item.Key.EndsWith("Secret", StringComparison.OrdinalIgnoreCase)
                || item.Key.Length <= "Secret".Length)
            {
                continue;
            }

            var targetKey = item.Key[..^"Secret".Length];
            if (existingKeys.Contains(targetKey) && string.IsNullOrWhiteSpace(configuration[targetKey]))
            {
                updates[targetKey] = EnsureSecretReference(item.Value);
            }
        }

        if (updates.Count > 0)
        {
            configuration.AddInMemoryCollection(updates);
        }
    }

    private static void SetIfPresent(
        IDictionary<string, string?> updates,
        IConfiguration configuration,
        string environmentName,
        string targetPath)
    {
        var value = Environment.GetEnvironmentVariable(environmentName);
        if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(configuration[targetPath]))
        {
            updates[targetPath] = value;
        }
    }

    private static string EnsureSecretReference(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("secret://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("aapm://", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"secret://{trimmed}";
    }
}
