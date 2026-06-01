# adgroups2cmdbuild

`adgroups2cmdbuild` synchronizes selected Microsoft Active Directory groups into CMDBuild roles.

Architecture artifacts are stored in [aa/](aa/README.md).
Russian deployment instructions are in [DEPLOYMENT.md](DEPLOYMENT.md).

Behavior:
- every configured AD group is expected to have the same name as a CMDBuild role;
- `ActiveDirectory:ProvisioningGroupName` is the authoritative group for account existence;
- a user present in the provisioning group is created or updated in CMDBuild;
- a managed user absent from the provisioning group is disabled in CMDBuild and all CMDBuild groups are revoked;
- CMDBuild username equals AD login (`sAMAccountName` by default);
- AD `displayName` is written to CMDBuild `description` by default, configurable as `Cmdbuild:UserDisplayNameField`;
- AD `mail` is written to CMDBuild `email`;
- synchronization runs every 300 seconds by default;
- the service is designed for one active replica. Active-active operation is not supported with the file-based state store.

The default config is `DryRun=true`; set `Sync__DryRun=false` only after checking logs against a test CMDBuild instance.
Dry-run does not write CMDBuild and does not persist the local managed-login state file.

The service fails a sync run without changes when any configured AD group or CMDBuild role is missing. This prevents accidental mass revocation after a typo in a group name or search DN.

## Configuration

Important environment overrides:

```bash
ActiveDirectory__Host=ad.example.local
ActiveDirectory__Port=636
ActiveDirectory__UseSsl=true
ActiveDirectory__BindDn='CN=svc-cmdb,OU=Service Accounts,DC=example,DC=local'
ActiveDirectory__BindPassword='<secret>'
ActiveDirectory__GroupSearchBaseDn='OU=Groups,DC=example,DC=local'
ActiveDirectory__GroupNames__0=CMDBuildUsers
ActiveDirectory__GroupNames__1=CMDBuildEditors
ActiveDirectory__ProvisioningGroupName=CMDBuildUsers
ActiveDirectory__RetryAttempts=3
ActiveDirectory__RetryJitterPercent=20

Cmdbuild__BaseUrl=https://cmdbuild.example/cmdbuild/services/rest/v3
Cmdbuild__Username='<secret>'
Cmdbuild__Password='<secret>'
Cmdbuild__UserDisplayNameField=description
Cmdbuild__UserEmailField=email
Cmdbuild__RetryAttempts=3
Cmdbuild__RetryJitterPercent=20

Sync__DryRun=false
Sync__IntervalSeconds=300
Sync__FailureBackoffSeconds=30
Sync__ShutdownGracePeriodSeconds=60
```

In `Production`, `Cmdbuild:BaseUrl` must use HTTPS, `AllowedHosts` must not be `*`, and `ActiveDirectory:IgnoreCertificateErrors=true` is rejected.
For production containers set `AllowedHosts` to the external DNS names accepted by the service.

Secrets can be stored as plain env/config values for development or as PAM/AAPM references:

```bash
PAMURL=https://pam.example.local
PAMUSERNAME=APP_ACCOUNT
PAMPASSWORD='<bootstrap-secret>'

ActiveDirectory__BindPassword=secret://AAA.LOCAL/PROD/ad-bind
Cmdbuild__Password=secret://AAA.LOCAL/PROD/cmdbuild-admin
```

The companion form is also supported when the target field is empty:

```bash
ActiveDirectory__BindPassword=
ActiveDirectory__BindPasswordSecret=AAA.LOCAL/PROD/ad-bind
Cmdbuild__Password=
Cmdbuild__PasswordSecret=AAA.LOCAL/PROD/cmdbuild-admin
```

If `PAMURL` plus `PAMTOKEN` or `PAMUSERNAME`/`PAMPASSWORD` are present, `Secrets:Provider` is treated as `IndeedPamAapm`.

Use `RecursiveGroups=true` only if nested AD group membership should grant CMDBuild groups.
With `Cmdbuild:PreserveUnmanagedGroups=true`, only the configured AD/CMDBuild groups are authoritative; unrelated CMDBuild groups already assigned to a user are preserved while the user remains provisioned. When a user leaves the provisioning group, all CMDBuild groups are revoked regardless of this flag.

If `Cmdbuild:NewUserPassword` is empty, the service sends a generated random password when creating a CMDBuild user. This is intended for installations where login is handled by external authentication.

## Retry And Shutdown

AD LDAP/LDAPS operations and CMDBuild REST calls use exponential backoff with optional jitter.
AD retry covers bind/search/range reads for transient LDAP/network failures such as timeout, server down, busy, or unavailable.
CMDBuild retry covers HTTP `408`, `429`, `5xx`, timeout, and network failures.
Authentication, authorization, invalid DN/filter, and other permanent errors are not retried.

On SIGTERM/SIGINT the worker stops scheduling new runs.
If a sync run is active, it waits up to `Sync:ShutdownGracePeriodSeconds` for normal completion.
After that timeout the active run is canceled, `/sync/status` is marked failed, and the local sync lock is released.

## Debug Logging

The service has a separate debug flag with two verbosity levels.
Debug messages are written through normal `ILogger` at `Information` level, so they are visible in console logs and are sent to ELK when `ElkLogging` is enabled.

```bash
Debug__Enabled=true
Debug__Level=Basic
```

Levels:
- `Basic` or `1`: sync run boundaries, AD/CMDBuild snapshot counts, group counts, page counts, deprovision candidate count, state save decision;
- `Verbose` or `2`: Basic plus per-user planned create/update/disable operations and resolved AD login lists.

By default verbose diagnostic lists redact sensitive login values. Set `Debug__LogSensitiveValues=true` only for a short diagnostic window if operators need raw logins in debug output.

## ELK Logging

Logs are sent to ELK only when `ElkLogging:Enabled=true` and `ElkLogging:Endpoint` is not empty.
With default empty settings, the provider is no-op and the service writes only normal console logs.

```bash
ElkLogging__Enabled=true
ElkLogging__Endpoint=https://elastic.example.local:9200
ElkLogging__Index=adgroups2cmdbuild-logs
ElkLogging__ApiKey=secret://AAA.LOCAL/PROD/elk-api-key
ElkLogging__MinimumLevel=Information
ElkLogging__Environment=Production
```

When `Index` is set and `Endpoint` is an Elasticsearch base URL, logs are posted to `{Endpoint}/{Index}/_doc`.
If `Endpoint` already ends with `/_doc` or `/_bulk`, it is used as-is.

If ELK is not available, keep console logs and collect Docker stdout/stderr with your platform. Syslog works from Docker via the Docker logging driver, for example `--log-driver=syslog --log-opt syslog-address=udp://syslog.example.local:514`. In that mode the application still writes normal console logs; Docker forwards them to syslog.

## Run

```bash
./scripts/dotnet run --project src/adgroups2cmdbuild/adgroups2cmdbuild.csproj
```

Health and status:

```bash
curl http://localhost:5084/health
curl http://localhost:5084/ready
curl http://localhost:5084/sync/status
```

`/health`, `/ready`, and `/sync/status` use the `EndpointRateLimiting` fixed-window limit. `/ready` is shallow by default; set `Readiness__CheckDependencies=true` to make it check LDAP bind and a lightweight CMDBuild REST call.

If a single CMDBuild user operation fails, the sync run continues with the remaining users. `/sync/status` reports `lastSucceeded=false` and `lastSummary.failedUsers` when a run completed with partial failures.

Docker build:

```bash
docker build -f deploy/dockerfiles/adgroups2cmdbuild.Dockerfile -t adgroups2cmdbuild:dev .
```

## Bootstrap AD Groups

One-time AD group creation is a separate deployment tool, not part of the sync microservice.
It reads CMDBuild roles and creates missing AD groups with the same names.

Dry-run:

```bash
./scripts/bootstrap-ad-groups.sh --target-ou 'OU=CMDBuild,OU=Groups,DC=example,DC=local' --prefix CMDBuild
```

Apply:

```bash
./scripts/bootstrap-ad-groups.sh --target-ou 'OU=CMDBuild,OU=Groups,DC=example,DC=local' --prefix CMDBuild --apply
```

Selection options:
- `--prefix CMDBuild` selects roles by name prefix;
- `--include Role1,Role2` selects exact role names;
- `--all` selects all CMDBuild roles and is required if no explicit filter is wanted;
- without `--prefix`, `--include`, or `--all`, the tool falls back to `ActiveDirectory:GroupNames` from config.

The tool refuses `--apply` without explicit selection unless `BootstrapAdGroups:RequireExplicitSelectionForApply=false`.
It uses the same AD, CMDBuild, env override, and PAM/AAPM settings as the service.

## CMDBuild Contract

The service uses CMDBuild REST v3:
- `GET /roles?detailed=true` to resolve role names to ids;
- `GET /users?detailed=true` to read current accounts and groups;
- `POST /users` to create users;
- `PUT /users/{userId}` to update, enable, disable, and replace `userGroups`.

The service account needs admin/write permissions for CMDBuild user management.
The REST paths are based on CMDBuild REST v3 user and role endpoints.
