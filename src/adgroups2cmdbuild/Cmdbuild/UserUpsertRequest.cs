namespace AdGroups2Cmdbuild.Cmdbuild;

public sealed record UserUpsertRequest(
    string Login,
    string? DisplayName,
    string? Email,
    IReadOnlyCollection<CmdbuildRole> DesiredRoles,
    IReadOnlyCollection<string> ManagedRoleIds);
