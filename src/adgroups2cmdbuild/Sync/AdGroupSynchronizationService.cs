using AdGroups2Cmdbuild.ActiveDirectory;
using AdGroups2Cmdbuild.Cmdbuild;
using AdGroups2Cmdbuild.Configuration;
using Microsoft.Extensions.Options;

namespace AdGroups2Cmdbuild.Sync;

public sealed class AdGroupSynchronizationService(
    IActiveDirectoryClient activeDirectoryClient,
    ICmdbuildClient cmdbuildClient,
    ISyncStateStore stateStore,
    IOptions<ActiveDirectoryOptions> adOptions,
    IOptions<CmdbuildOptions> cmdbuildOptions,
    IOptions<SyncOptions> syncOptions,
    IOptions<DebugOptions> debugOptions,
    ILogger<AdGroupSynchronizationService> logger)
{
    public async Task<SyncRunSummary> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (debugOptions.Value.IsBasicEnabled())
        {
            logger.LogInformation(
                "Debug {DebugLevel}: loading AD snapshot for groups {Groups}; provisioningGroup={ProvisioningGroup}; dryRun={DryRun}",
                debugOptions.Value.NormalizedLevel(),
                string.Join(", ", adOptions.Value.GroupNames),
                adOptions.Value.ProvisioningGroupName,
                syncOptions.Value.DryRun);
        }

        var adSnapshot = await activeDirectoryClient.ReadGroupsAsync(cancellationToken);
        var cmdbSnapshot = await cmdbuildClient.ReadSnapshotAsync(cancellationToken);
        var state = await stateStore.LoadAsync(cancellationToken);

        ValidateAdGroups(adSnapshot);
        ValidateCmdbuildRoles(cmdbSnapshot);

        var provisioningGroup = adOptions.Value.ProvisioningGroupName;
        var provisioningUsers = adSnapshot.Groups.GetValueOrDefault(provisioningGroup)
            ?? new Dictionary<string, AdUserRecord>(StringComparer.OrdinalIgnoreCase);
        var managedRoleIds = adOptions.Value.GroupNames
            .Select(groupName => cmdbSnapshot.RolesByName[groupName].Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (debugOptions.Value.IsBasicEnabled())
        {
            logger.LogInformation(
                "Debug {DebugLevel}: snapshots validated: adUsers={AdUsers}, cmdbUsers={CmdbUsers}, cmdbRoles={CmdbRoles}, managedStateLogins={ManagedStateLogins}",
                debugOptions.Value.NormalizedLevel(),
                adSnapshot.Users.Count,
                cmdbSnapshot.UsersByLogin.Count,
                cmdbSnapshot.RolesByName.Count,
                state.ManagedLogins.Count);

            foreach (var (groupName, members) in adSnapshot.Groups.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Debug {DebugLevel}: AD group {GroupName} has {MemberCount} resolved user member(s)",
                    debugOptions.Value.NormalizedLevel(),
                    groupName,
                    members.Count);
            }
        }

        var created = 0;
        var updated = 0;
        var disabled = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var user in provisioningUsers.Values.OrderBy(user => user.Login, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var desiredRoles = DesiredRolesFor(user.Login, adSnapshot, cmdbSnapshot);
            var request = new UserUpsertRequest(user.Login, user.DisplayName, user.Email, desiredRoles, managedRoleIds);

            if (!cmdbSnapshot.UsersByLogin.TryGetValue(user.Login, out var existingUser))
            {
                if (!cmdbuildOptions.Value.CreateMissingUsers)
                {
                    logger.LogWarning("CMDBuild user {Login} is missing and CreateMissingUsers=false", user.Login);
                    skipped++;
                    continue;
                }

                if (!await ApplyOrLogAsync(
                    $"create user {user.Login} with groups {string.Join(", ", desiredRoles.Select(role => role.Name))}",
                    user.Login,
                    () => cmdbuildClient.CreateUserAsync(request, cancellationToken)))
                {
                    failed++;
                    continue;
                }

                state.ManagedLogins.Add(user.Login);
                created++;
                LogVerboseUserAction("create", user.Login, desiredRoles);
                continue;
            }

            if (!await ApplyOrLogAsync(
                $"update user {user.Login} with groups {string.Join(", ", desiredRoles.Select(role => role.Name))}",
                user.Login,
                () => cmdbuildClient.UpdateUserAsync(existingUser, request, cancellationToken)))
            {
                failed++;
                continue;
            }

            state.ManagedLogins.Add(user.Login);
            updated++;
            LogVerboseUserAction("update", user.Login, desiredRoles);
        }

        var deprovisionCandidates = BuildDeprovisionCandidates(state, adSnapshot, cmdbSnapshot, managedRoleIds, provisioningUsers);
        if (debugOptions.Value.IsBasicEnabled())
        {
            logger.LogInformation(
                "Debug {DebugLevel}: calculated {CandidateCount} deprovision candidate(s)",
                debugOptions.Value.NormalizedLevel(),
                deprovisionCandidates.Count);
        }

        foreach (var login in deprovisionCandidates.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!cmdbSnapshot.UsersByLogin.TryGetValue(login, out var existingUser))
            {
                logger.LogDebug("Managed login {Login} is absent in CMDBuild; no deprovision action needed", login);
                continue;
            }

            if (!await ApplyOrLogAsync(
                $"disable user {login} and revoke all groups",
                login,
                () => cmdbuildClient.DisableUserAsync(existingUser, cancellationToken)))
            {
                failed++;
                continue;
            }

            state.ManagedLogins.Add(login);
            disabled++;
            if (debugOptions.Value.IsVerboseEnabled())
            {
                logger.LogInformation("Debug Verbose: planned disable for login {Login}", debugOptions.Value.FormatSensitive(login));
            }
        }

        if (!syncOptions.Value.DryRun)
        {
            await stateStore.SaveAsync(state, cancellationToken);
            if (debugOptions.Value.IsBasicEnabled())
            {
                logger.LogInformation(
                    "Debug {DebugLevel}: sync state saved with {ManagedLoginCount} managed login(s)",
                    debugOptions.Value.NormalizedLevel(),
                    state.ManagedLogins.Count);
            }
        }
        else if (debugOptions.Value.IsBasicEnabled())
        {
            logger.LogInformation("Debug {DebugLevel}: sync state was not saved because dryRun=true", debugOptions.Value.NormalizedLevel());
        }

        return new SyncRunSummary(
            AdUsers: adSnapshot.Users.Count,
            ProvisionedUsers: provisioningUsers.Count,
            CreatedUsers: created,
            UpdatedUsers: updated,
            DisabledUsers: disabled,
            SkippedUsers: skipped,
            FailedUsers: failed,
            DryRun: syncOptions.Value.DryRun);
    }

    private void ValidateAdGroups(AdGroupSnapshot adSnapshot)
    {
        var missing = adOptions.Value.GroupNames
            .Where(groupName => !adSnapshot.FoundGroupNames.Contains(groupName))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"AD groups are missing for configured sync groups: {string.Join(", ", missing)}");
        }
    }

    private void ValidateCmdbuildRoles(CmdbuildSnapshot cmdbSnapshot)
    {
        var missing = adOptions.Value.GroupNames
            .Where(groupName => !cmdbSnapshot.RolesByName.ContainsKey(groupName))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"CMDBuild roles are missing for configured AD groups: {string.Join(", ", missing)}");
        }
    }

    private static HashSet<string> BuildDeprovisionCandidates(
        SyncState state,
        AdGroupSnapshot adSnapshot,
        CmdbuildSnapshot cmdbSnapshot,
        HashSet<string> managedRoleIds,
        Dictionary<string, AdUserRecord> provisioningUsers)
    {
        var candidates = new HashSet<string>(state.ManagedLogins, StringComparer.OrdinalIgnoreCase);
        foreach (var login in adSnapshot.Users.Keys)
        {
            candidates.Add(login);
        }

        foreach (var user in cmdbSnapshot.UsersByLogin.Values)
        {
            if (user.RoleIds.Any(roleId => managedRoleIds.Contains(roleId)))
            {
                candidates.Add(user.Username);
            }
        }

        candidates.ExceptWith(provisioningUsers.Keys);
        return candidates;
    }

    private IReadOnlyCollection<CmdbuildRole> DesiredRolesFor(
        string login,
        AdGroupSnapshot adSnapshot,
        CmdbuildSnapshot cmdbSnapshot)
    {
        var roles = new List<CmdbuildRole>();
        foreach (var groupName in adOptions.Value.GroupNames)
        {
            if (adSnapshot.Groups.TryGetValue(groupName, out var members) && members.ContainsKey(login))
            {
                roles.Add(cmdbSnapshot.RolesByName[groupName]);
            }
        }

        return roles
            .DistinctBy(role => role.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(role => role.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<bool> ApplyOrLogAsync(string operation, string login, Func<Task> action)
    {
        if (syncOptions.Value.DryRun)
        {
            logger.LogInformation("Dry-run: would {Operation}", operation);
            return true;
        }

        try
        {
            await action();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "CMDBuild operation failed for login {Login}: {Operation}", login, operation);
            return false;
        }
    }

    private void LogVerboseUserAction(string action, string login, IReadOnlyCollection<CmdbuildRole> desiredRoles)
    {
        if (!debugOptions.Value.IsVerboseEnabled())
        {
            return;
        }

        logger.LogInformation(
            "Debug Verbose: planned {Action} for login {Login}; desiredRoles={DesiredRoles}",
            action,
            debugOptions.Value.FormatSensitive(login),
            string.Join(", ", desiredRoles.Select(role => role.Name)));
    }
}
