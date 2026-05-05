namespace AdGroups2Cmdbuild.ActiveDirectory;

public sealed class ActiveDirectoryOptions
{
    public const string SectionName = "ActiveDirectory";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 389;

    public bool UseSsl { get; set; }

    public bool IgnoreCertificateErrors { get; set; }

    public string BindDn { get; set; } = "";

    public string BindPassword { get; set; } = "";

    public string GroupSearchBaseDn { get; set; } = "";

    public string UserSearchBaseDn { get; set; } = "";

    public List<string> GroupNames { get; set; } = [];

    public string ProvisioningGroupName { get; set; } = "";

    public string GroupNameAttribute { get; set; } = "cn";

    public string MemberAttribute { get; set; } = "member";

    public string UserLoginAttribute { get; set; } = "sAMAccountName";

    public string UserDisplayNameAttribute { get; set; } = "displayName";

    public string UserEmailAttribute { get; set; } = "mail";

    public bool RecursiveGroups { get; set; }

    public bool IgnoreDisabledUsers { get; set; } = true;

    public int PageSize { get; set; } = 500;

    public int RangeStep { get; set; } = 1500;

    public int RequestTimeoutMs { get; set; } = 15000;
}
