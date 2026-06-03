namespace BootstrapAdGroups;

public sealed record BootstrapRoleSelection(
    bool All,
    string IncludeNamePrefix,
    IReadOnlyCollection<string> IncludeRoleNames,
    IReadOnlyCollection<string> ExcludeRoleNames,
    IReadOnlyCollection<string> FallbackGroupNames);

public static class BootstrapAdGroupsLogic
{
    public static IReadOnlyList<string> SelectRoleNames(
        IEnumerable<string> roleNames,
        BootstrapRoleSelection selection)
    {
        IEnumerable<string> selected = roleNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim());

        if (!selection.All)
        {
            if (selection.IncludeRoleNames.Count > 0)
            {
                var include = selection.IncludeRoleNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
                selected = selected.Where(include.Contains);
            }
            else if (!string.IsNullOrWhiteSpace(selection.IncludeNamePrefix))
            {
                selected = selected.Where(name => name.StartsWith(selection.IncludeNamePrefix, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                var fallback = selection.FallbackGroupNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
                selected = selected.Where(fallback.Contains);
            }
        }

        if (selection.ExcludeRoleNames.Count > 0)
        {
            var exclude = selection.ExcludeRoleNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected = selected.Where(name => !exclude.Contains(name));
        }

        return selected.ToArray();
    }

    public static bool HasExplicitSelection(
        bool all,
        string includeNamePrefix,
        IReadOnlyCollection<string> includeRoleNames)
    {
        return all || !string.IsNullOrWhiteSpace(includeNamePrefix) || includeRoleNames.Count > 0;
    }

    public static int BuildGroupType(string groupScope, bool securityEnabled)
    {
        var scope = groupScope.ToLowerInvariant() switch
        {
            "global" => 0x00000002,
            "domainlocal" or "domain-local" => 0x00000004,
            "universal" => 0x00000008,
            _ => throw new InvalidOperationException($"Unsupported group scope: {groupScope}")
        };

        return securityEnabled ? unchecked((int)0x80000000) | scope : scope;
    }

    public static string BuildSamAccountName(string name)
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

    public static string BuildGroupDn(string groupName, string targetOuDn)
    {
        return $"CN={EscapeDnValue(groupName)},{targetOuDn}";
    }

    public static string EscapeDnValue(string value)
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
}
