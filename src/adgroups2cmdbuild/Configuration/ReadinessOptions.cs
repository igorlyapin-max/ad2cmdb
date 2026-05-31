namespace AdGroups2Cmdbuild.Configuration;

public sealed class ReadinessOptions
{
    public const string SectionName = "Readiness";

    public bool Enabled { get; set; } = true;

    public string Route { get; set; } = "/ready";

    public bool CheckDependencies { get; set; }

    public int TimeoutMs { get; set; } = 3000;
}
