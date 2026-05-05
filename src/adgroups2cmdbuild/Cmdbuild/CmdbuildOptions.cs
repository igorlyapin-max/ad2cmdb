namespace AdGroups2Cmdbuild.Cmdbuild;

public sealed class CmdbuildOptions
{
    public const string SectionName = "Cmdbuild";

    public string BaseUrl { get; set; } = "http://localhost:8090/cmdbuild/services/rest/v3";

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public int RequestTimeoutMs { get; set; } = 15000;

    public int UsersPageSize { get; set; } = 500;

    public int RolesPageSize { get; set; } = 1000;

    public string UserDisplayNameField { get; set; } = "description";

    public string UserEmailField { get; set; } = "email";

    public bool PreserveUnmanagedGroups { get; set; } = true;

    public bool CreateMissingUsers { get; set; } = true;

    public string NewUserPassword { get; set; } = "";

    public string DefaultLanguage { get; set; } = "";

    public List<string> RoleNameFields { get; set; } = ["name", "code", "description"];
}
