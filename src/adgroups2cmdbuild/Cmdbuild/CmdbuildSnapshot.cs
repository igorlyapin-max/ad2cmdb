namespace AdGroups2Cmdbuild.Cmdbuild;

public sealed class CmdbuildSnapshot
{
    public Dictionary<string, CmdbuildRole> RolesByName { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, CmdbuildRole> RolesById { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, CmdbuildUser> UsersByLogin { get; } = new(StringComparer.OrdinalIgnoreCase);
}
