namespace AdGroups2Cmdbuild.Configuration;

public sealed class EndpointRateLimitOptions
{
    public const string SectionName = "EndpointRateLimiting";

    public bool Enabled { get; set; } = true;

    public int PermitLimit { get; set; } = 120;

    public int WindowSeconds { get; set; } = 60;
}
