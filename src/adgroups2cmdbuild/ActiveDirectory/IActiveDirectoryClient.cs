namespace AdGroups2Cmdbuild.ActiveDirectory;

public interface IActiveDirectoryClient
{
    Task<AdGroupSnapshot> ReadGroupsAsync(CancellationToken cancellationToken);

    Task CheckConnectionAsync(CancellationToken cancellationToken);
}
