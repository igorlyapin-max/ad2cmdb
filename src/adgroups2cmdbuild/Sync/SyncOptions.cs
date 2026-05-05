namespace AdGroups2Cmdbuild.Sync;

public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    public bool Enabled { get; set; } = true;

    public bool DryRun { get; set; } = true;

    public int IntervalSeconds { get; set; } = 300;

    public bool RunImmediately { get; set; } = true;

    public string StateFilePath { get; set; } = "state/adgroups2cmdbuild-state.json";
}
