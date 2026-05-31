namespace AdGroups2Cmdbuild.Configuration;

public sealed class DebugOptions
{
    public const string SectionName = "Debug";

    public bool Enabled { get; set; }

    public string Level { get; set; } = "Basic";

    public bool LogSensitiveValues { get; set; }

    public bool HasValidLevel()
    {
        return IsLevel("Basic")
            || IsLevel("Verbose")
            || IsLevel("1")
            || IsLevel("2");
    }

    public bool IsBasicEnabled()
    {
        return Enabled;
    }

    public bool IsVerboseEnabled()
    {
        return Enabled && (IsLevel("Verbose") || IsLevel("2"));
    }

    public string NormalizedLevel()
    {
        return IsVerboseEnabled() ? "Verbose" : "Basic";
    }

    public string FormatSensitive(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return LogSensitiveValues ? value : "<redacted>";
    }

    private bool IsLevel(string value)
    {
        return string.Equals(Level, value, StringComparison.OrdinalIgnoreCase);
    }
}
