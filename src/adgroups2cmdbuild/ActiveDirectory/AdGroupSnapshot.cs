namespace AdGroups2Cmdbuild.ActiveDirectory;

public sealed class AdGroupSnapshot
{
    public Dictionary<string, Dictionary<string, AdUserRecord>> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, AdUserRecord> Users { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> FoundGroupNames { get; } = new(StringComparer.OrdinalIgnoreCase);
}
