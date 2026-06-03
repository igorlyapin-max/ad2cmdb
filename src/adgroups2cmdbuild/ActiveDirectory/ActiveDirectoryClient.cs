using System.DirectoryServices.Protocols;
using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using AdGroups2Cmdbuild.Configuration;
using AdGroups2Cmdbuild.Resilience;
using Microsoft.Extensions.Options;

namespace AdGroups2Cmdbuild.ActiveDirectory;

public sealed class ActiveDirectoryClient(
    IOptions<ActiveDirectoryOptions> options,
    IOptions<DebugOptions> debugOptions,
    ILogger<ActiveDirectoryClient> logger) : IActiveDirectoryClient
{
    public async Task CheckConnectionAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var connection = CreateConnection(settings);
        await ExecuteWithRetryAsync(
            "LDAP bind",
            token => Task.Run(() => connection.Bind(), token),
            cancellationToken);
    }

    public async Task<AdGroupSnapshot> ReadGroupsAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var connection = CreateConnection(settings);

        logger.LogInformation("Reading {GroupCount} AD groups from {Host}:{Port}", settings.GroupNames.Count, settings.Host, settings.Port);
        await ExecuteWithRetryAsync(
            "LDAP bind",
            token => Task.Run(() => connection.Bind(), token),
            cancellationToken);
        if (debugOptions.Value.IsBasicEnabled())
        {
            logger.LogInformation(
                "Debug {DebugLevel}: LDAP bind succeeded: host={Host}, port={Port}, ssl={UseSsl}, groupSearchBaseDn={GroupSearchBaseDn}, recursiveGroups={RecursiveGroups}",
                debugOptions.Value.NormalizedLevel(),
                settings.Host,
                settings.Port,
                settings.UseSsl,
                settings.GroupSearchBaseDn,
                settings.RecursiveGroups);
        }

        var groupEntries = await SearchGroupsAsync(connection, settings, cancellationToken);
        if (debugOptions.Value.IsBasicEnabled())
        {
            logger.LogInformation(
                "Debug {DebugLevel}: LDAP group search returned {GroupEntryCount} group entrie(s)",
                debugOptions.Value.NormalizedLevel(),
                groupEntries.Count);
        }

        var snapshot = new AdGroupSnapshot();
        var userByDn = new Dictionary<string, AdUserRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var groupName in settings.GroupNames)
        {
            snapshot.Groups[groupName] = new Dictionary<string, AdUserRecord>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var entry in groupEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groupName = ReadFirst(entry, settings.GroupNameAttribute);
            if (string.IsNullOrWhiteSpace(groupName))
            {
                logger.LogWarning("Skipping AD group {Dn}: attribute {Attribute} is empty", entry.DistinguishedName, settings.GroupNameAttribute);
                continue;
            }

            if (!snapshot.Groups.TryGetValue(groupName, out var groupUsers))
            {
                continue;
            }

            snapshot.FoundGroupNames.Add(groupName);
            var visitedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry.DistinguishedName };
            var memberDns = await ReadMemberDnsAsync(connection, settings, entry, cancellationToken);
            if (debugOptions.Value.IsBasicEnabled())
            {
                logger.LogInformation(
                    "Debug {DebugLevel}: AD group {GroupName} has {RawMemberCount} raw member DN(s)",
                    debugOptions.Value.NormalizedLevel(),
                    groupName,
                    memberDns.Count);
            }

            foreach (var memberDn in memberDns)
            {
                await AddMemberAsync(connection, settings, memberDn, groupUsers, snapshot.Users, userByDn, visitedGroups, cancellationToken);
            }

            if (debugOptions.Value.IsVerboseEnabled())
            {
                logger.LogInformation(
                    "Debug Verbose: AD group {GroupName} resolved login(s): {Logins}",
                    groupName,
                    string.Join(", ", groupUsers.Keys
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .Select(debugOptions.Value.FormatSensitive)));
            }
        }

        logger.LogInformation(
            "Read {UserCount} distinct AD users from configured groups; provisioning group {ProvisioningGroup} has {ProvisioningCount} users",
            snapshot.Users.Count,
            settings.ProvisioningGroupName,
            snapshot.Groups.TryGetValue(settings.ProvisioningGroupName, out var provisioningUsers) ? provisioningUsers.Count : 0);

        return snapshot;
    }

    private static LdapConnection CreateConnection(ActiveDirectoryOptions settings)
    {
        var identifier = new LdapDirectoryIdentifier(settings.Host, settings.Port, fullyQualifiedDnsHostName: true, connectionless: false);
        var connection = new LdapConnection(identifier)
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(settings.BindDn, settings.BindPassword),
            Timeout = TimeSpan.FromMilliseconds(settings.RequestTimeoutMs)
        };

        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = settings.UseSsl;
        if (settings.UseSsl && settings.IgnoreCertificateErrors)
        {
            connection.SessionOptions.VerifyServerCertificate = (_, _) => true;
        }

        return connection;
    }

    private Task<List<SearchResultEntry>> SearchGroupsAsync(
        LdapConnection connection,
        ActiveDirectoryOptions settings,
        CancellationToken cancellationToken)
    {
        var groupFilter = BuildGroupFilter(settings);
        return SearchAllAsync(
            connection,
            () => new SearchRequest(
                settings.GroupSearchBaseDn,
                groupFilter,
                SearchScope.Subtree,
                settings.GroupNameAttribute,
                settings.MemberAttribute,
                "distinguishedName"),
            settings.PageSize,
            "LDAP group search",
            cancellationToken);
    }

    private static string BuildGroupFilter(ActiveDirectoryOptions settings)
    {
        var nameFilters = settings.GroupNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => $"({settings.GroupNameAttribute}={EscapeFilterValue(name)})")
            .ToArray();

        var groupSelector = nameFilters.Length == 1
            ? nameFilters[0]
            : $"(|{string.Concat(nameFilters)})";

        return $"(&(objectClass=group){groupSelector})";
    }

    private async Task AddMemberAsync(
        LdapConnection connection,
        ActiveDirectoryOptions settings,
        string memberDn,
        Dictionary<string, AdUserRecord> groupUsers,
        Dictionary<string, AdUserRecord> snapshotUsers,
        Dictionary<string, AdUserRecord> userByDn,
        HashSet<string> visitedGroups,
        CancellationToken cancellationToken)
    {
        if (userByDn.TryGetValue(memberDn, out var cachedUser))
        {
            groupUsers[cachedUser.Login] = cachedUser;
            snapshotUsers[cachedUser.Login] = cachedUser;
            return;
        }

        var entry = await ReadEntryByDnAsync(connection, settings, memberDn, cancellationToken);
        if (entry is null)
        {
            logger.LogWarning("AD member {MemberDn} was referenced by a group but could not be read", memberDn);
            return;
        }

        var objectClasses = ReadValues(entry, "objectClass");
        if (objectClasses.Any(value => string.Equals(value, "group", StringComparison.OrdinalIgnoreCase)))
        {
            if (!settings.RecursiveGroups)
            {
                logger.LogDebug("Skipping nested AD group {GroupDn}; RecursiveGroups is disabled", memberDn);
                return;
            }

            if (!visitedGroups.Add(memberDn))
            {
                logger.LogWarning("Skipping cyclic nested AD group reference {GroupDn}", memberDn);
                return;
            }

            var nestedMembers = await ReadMemberDnsAsync(connection, settings, entry, cancellationToken);
            foreach (var nestedMemberDn in nestedMembers)
            {
                await AddMemberAsync(connection, settings, nestedMemberDn, groupUsers, snapshotUsers, userByDn, visitedGroups, cancellationToken);
            }

            return;
        }

        if (!objectClasses.Any(value => string.Equals(value, "user", StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogDebug("Skipping AD member {MemberDn}: objectClass is not user", memberDn);
            return;
        }

        if (settings.IgnoreDisabledUsers && IsDisabled(entry))
        {
            logger.LogInformation("Skipping disabled AD user {MemberDn}", memberDn);
            return;
        }

        var login = ReadFirst(entry, settings.UserLoginAttribute);
        if (string.IsNullOrWhiteSpace(login))
        {
            logger.LogWarning("Skipping AD user {UserDn}: attribute {Attribute} is empty", memberDn, settings.UserLoginAttribute);
            return;
        }

        var user = new AdUserRecord(
            login.Trim(),
            memberDn,
            TrimToNull(ReadFirst(entry, settings.UserDisplayNameAttribute)),
            TrimToNull(ReadFirst(entry, settings.UserEmailAttribute)));

        if (debugOptions.Value.IsVerboseEnabled())
        {
            logger.LogInformation(
                "Debug Verbose: resolved AD user {Login}; displayNamePresent={DisplayNamePresent}; emailPresent={EmailPresent}",
                debugOptions.Value.FormatSensitive(user.Login),
                !string.IsNullOrWhiteSpace(user.DisplayName),
                !string.IsNullOrWhiteSpace(user.Email));
        }

        userByDn[memberDn] = user;
        groupUsers[user.Login] = user;
        snapshotUsers[user.Login] = user;
    }

    private async Task<SearchResultEntry?> ReadEntryByDnAsync(
        LdapConnection connection,
        ActiveDirectoryOptions settings,
        string dn,
        CancellationToken cancellationToken)
    {
        var entries = await SearchAllAsync(
            connection,
            () => new SearchRequest(
                dn,
                "(objectClass=*)",
                SearchScope.Base,
                "objectClass",
                settings.MemberAttribute,
                settings.UserLoginAttribute,
                settings.UserDisplayNameAttribute,
                settings.UserEmailAttribute,
                "userAccountControl"),
            settings.PageSize,
            "LDAP member entry read",
            cancellationToken);
        return entries.Count > 0 ? entries[0] : null;
    }

    private async Task<List<string>> ReadMemberDnsAsync(
        LdapConnection connection,
        ActiveDirectoryOptions settings,
        SearchResultEntry groupEntry,
        CancellationToken cancellationToken)
    {
        var members = new List<string>();
        var range = ReadMemberValues(groupEntry, settings.MemberAttribute, members);
        while (range is { IsTerminal: false })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextStart = range.Value.End + 1;
            var nextEnd = nextStart + Math.Max(1, settings.RangeStep) - 1;
            var attributeName = $"{settings.MemberAttribute};range={nextStart}-{nextEnd}";
            var entries = await SearchAllAsync(
                connection,
                () => new SearchRequest(groupEntry.DistinguishedName, "(objectClass=*)", SearchScope.Base, attributeName),
                settings.PageSize,
                "LDAP group member range read",
                cancellationToken);
            if (entries.Count == 0)
            {
                break;
            }

            range = ReadMemberValues(entries[0], settings.MemberAttribute, members);
            if (range is null)
            {
                break;
            }
        }

        return members
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static MemberRange? ReadMemberValues(SearchResultEntry entry, string memberAttribute, List<string> members)
    {
        MemberRange? lastRange = null;
        foreach (string attributeName in entry.Attributes.AttributeNames)
        {
            if (!string.Equals(attributeName, memberAttribute, StringComparison.OrdinalIgnoreCase)
                && !attributeName.StartsWith($"{memberAttribute};range=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in ReadValues(entry, attributeName))
            {
                members.Add(value);
            }

            if (TryParseRange(attributeName, memberAttribute, out var range))
            {
                lastRange = range;
            }
            else
            {
                lastRange = new MemberRange(0, members.Count, IsTerminal: true);
            }
        }

        return lastRange;
    }

    private Task<List<SearchResultEntry>> SearchAllAsync(
        LdapConnection connection,
        Func<SearchRequest> requestFactory,
        int pageSize,
        string operationName,
        CancellationToken cancellationToken)
    {
        return ExecuteWithRetryAsync(
            operationName,
            token => SearchAllOnceAsync(connection, requestFactory(), pageSize, token),
            cancellationToken);
    }

    private static Task<List<SearchResultEntry>> SearchAllOnceAsync(
        LdapConnection connection,
        SearchRequest request,
        int pageSize,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var entries = new List<SearchResultEntry>();
            var pageControl = new PageResultRequestControl(Math.Max(1, pageSize));
            request.Controls.Add(pageControl);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = (SearchResponse)connection.SendRequest(request);
                foreach (SearchResultEntry entry in response.Entries)
                {
                    entries.Add(entry);
                }

                var responseControl = response.Controls
                    .OfType<PageResultResponseControl>()
                    .FirstOrDefault();
                if (responseControl is null || responseControl.Cookie.Length == 0)
                {
                    break;
                }

                pageControl.Cookie = responseControl.Cookie;
            }

            return entries;
        }, cancellationToken);
    }

    private async Task ExecuteWithRetryAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(
            operationName,
            async token =>
            {
                await operation(token);
                return true;
            },
            cancellationToken);
    }

    private async Task<T> ExecuteWithRetryAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var attempts = Math.Max(1, settings.RetryAttempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception exception) when (ShouldRetry(exception, attempt, attempts, cancellationToken))
            {
                var delay = RetryBackoff.CalculateDelay(
                    attempt,
                    settings.RetryBaseDelayMs,
                    settings.RetryMaxDelayMs,
                    settings.RetryJitterPercent);
                logger.LogWarning(
                    exception,
                    "Transient AD {Operation} failure on attempt {Attempt}/{Attempts}; ldapCode={LdapCode}; retrying in {DelayMs}ms",
                    operationName,
                    attempt,
                    attempts,
                    LdapCode(exception),
                    (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("AD retry loop exhausted unexpectedly.");
    }

    private static bool ShouldRetry(Exception exception, int attempt, int attempts, CancellationToken cancellationToken)
    {
        if (attempt >= attempts || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception switch
        {
            LdapException ldapException => IsTransientLdapCode(ldapException.ErrorCode),
            DirectoryOperationException directoryException => IsTransientDirectoryResult(directoryException),
            IOException => true,
            SocketException => true,
            TimeoutException => true,
            TaskCanceledException => true,
            _ => exception.InnerException is not null && ShouldRetry(exception.InnerException, attempt, attempts, cancellationToken)
        };
    }

    private static bool IsTransientLdapCode(int code)
    {
        return code is 51 or 52 or 80 or 81 or 85 or 91;
    }

    private static bool IsTransientDirectoryResult(DirectoryOperationException exception)
    {
        var code = (int?)exception.Response?.ResultCode;
        return code is 3 or 51 or 52 or 80;
    }

    private static string LdapCode(Exception exception)
    {
        return exception switch
        {
            LdapException ldapException => ldapException.ErrorCode.ToString(),
            DirectoryOperationException directoryException => ((int?)directoryException.Response?.ResultCode)?.ToString() ?? "none",
            _ => "none"
        };
    }

    private static IReadOnlyList<string> ReadValues(SearchResultEntry entry, string attributeName)
    {
        if (!entry.Attributes.Contains(attributeName))
        {
            return [];
        }

        return entry.Attributes[attributeName]
            .GetValues(typeof(string))
            .OfType<string>()
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static string? ReadFirst(SearchResultEntry entry, string attributeName)
    {
        return ReadValues(entry, attributeName).FirstOrDefault();
    }

    private static bool IsDisabled(SearchResultEntry entry)
    {
        var value = ReadFirst(entry, "userAccountControl");
        return int.TryParse(value, out var flags) && (flags & 0x2) == 0x2;
    }

    private static bool TryParseRange(string attributeName, string memberAttribute, out MemberRange range)
    {
        range = default;
        var prefix = $"{memberAttribute};range=";
        if (!attributeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rangeText = attributeName[prefix.Length..];
        var parts = rangeText.Split('-', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var start))
        {
            return false;
        }

        var terminal = parts[1] == "*";
        var end = terminal ? start : int.Parse(parts[1]);
        range = new MemberRange(start, end, terminal);
        return true;
    }

    private static string EscapeFilterValue(string value)
    {
        return value
            .Replace("\\", "\\5c", StringComparison.Ordinal)
            .Replace("*", "\\2a", StringComparison.Ordinal)
            .Replace("(", "\\28", StringComparison.Ordinal)
            .Replace(")", "\\29", StringComparison.Ordinal)
            .Replace("\0", "\\00", StringComparison.Ordinal);
    }

    private static string? TrimToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private readonly record struct MemberRange(int Start, int End, bool IsTerminal);
}
