namespace AdGroups2Cmdbuild.Configuration;

public static class ProductionGuards
{
    public static bool HasWildcardAllowedHost(string? allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(allowedHosts))
        {
            return false;
        }

        return allowedHosts
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(host => string.Equals(host, "*", StringComparison.Ordinal));
    }

    public static bool ActiveDirectoryUsesSecureTransport(bool useSsl)
    {
        return useSsl;
    }

    public static bool AllowsActiveDirectoryCertificateBypass(bool ignoreCertificateErrors)
    {
        return ignoreCertificateErrors;
    }

    public static bool CmdbuildBaseUrlUsesHttps(string? baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps;
    }

    public static bool ReadinessChecksDependencies(bool enabled, bool checkDependencies)
    {
        return enabled && checkDependencies;
    }
}
