namespace AdGroups2Cmdbuild.Cmdbuild;

public interface ICmdbuildClient
{
    Task<CmdbuildSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken);

    Task CheckConnectionAsync(CancellationToken cancellationToken);

    Task CreateUserAsync(UserUpsertRequest request, CancellationToken cancellationToken);

    Task UpdateUserAsync(CmdbuildUser existingUser, UserUpsertRequest request, CancellationToken cancellationToken);

    Task DisableUserAsync(CmdbuildUser existingUser, CancellationToken cancellationToken);
}
