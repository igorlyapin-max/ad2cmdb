using Microsoft.Extensions.Logging;

namespace AdGroups2Cmdbuild.Configuration;

public sealed class ElkLoggingOptions
{
    public const string SectionName = "ElkLogging";

    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = "";

    public string Index { get; set; } = "adgroups2cmdbuild-logs";

    public string ApiKey { get; set; } = "";

    public string MinimumLevel { get; set; } = "Information";

    public string ServiceName { get; set; } = "adgroups2cmdbuild";

    public string Environment { get; set; } = "Production";

    public int TimeoutMs { get; set; } = 5000;

    public int QueueCapacity { get; set; } = 1000;

    public int FlushTimeoutMs { get; set; } = 5000;

    public bool IsActive()
    {
        return Enabled && !string.IsNullOrWhiteSpace(Endpoint);
    }

    public bool HasValidMinimumLevel()
    {
        return Enum.TryParse<LogLevel>(MinimumLevel, ignoreCase: true, out _);
    }

    public LogLevel GetMinimumLevel()
    {
        return Enum.Parse<LogLevel>(MinimumLevel, ignoreCase: true);
    }

    public bool HasValidEndpoint()
    {
        return !IsActive() || Uri.TryCreate(Endpoint, UriKind.Absolute, out _);
    }
}
