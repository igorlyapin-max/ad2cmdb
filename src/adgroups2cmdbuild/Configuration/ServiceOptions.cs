namespace AdGroups2Cmdbuild.Configuration;

public sealed class ServiceOptions
{
    public const string SectionName = "Service";

    public string Name { get; set; } = "adgroups2cmdbuild";

    public string HealthRoute { get; set; } = "/health";
}
