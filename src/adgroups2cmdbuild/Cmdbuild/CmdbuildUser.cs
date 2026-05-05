namespace AdGroups2Cmdbuild.Cmdbuild;

public sealed class CmdbuildUser
{
    public required string Id { get; init; }

    public required string Username { get; init; }

    public string? DisplayName { get; init; }

    public string? Email { get; init; }

    public bool Active { get; init; }

    public HashSet<string> RoleIds { get; } = new(StringComparer.OrdinalIgnoreCase);
}
