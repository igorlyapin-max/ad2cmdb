namespace AdGroups2Cmdbuild.ActiveDirectory;

public sealed record AdUserRecord(
    string Login,
    string DistinguishedName,
    string? DisplayName,
    string? Email);
