# Конфигурационные файлы

## Runtime Config

| Файл | Назначение | Коммитить |
| --- | --- | --- |
| `src/adgroups2cmdbuild/appsettings.json` | Base config сервиса и bootstrap tool | Да |
| `src/adgroups2cmdbuild/appsettings.Development.json` | Dev overrides без production secrets | Да |
| `appsettings.Production.json` | Production overrides с секретами или локальными адресами | Нет |
| `state/adgroups2cmdbuild-state.json` | State управляемых логинов и время последней успешной sync | Нет |
| `state/adgroups2cmdbuild-state.json.bak` | Последний backup state | Нет |
| `state/adgroups2cmdbuild.lock` | Lock-файл активного sync-run на хосте | Нет |

.NET configuration order:

1. `appsettings.json`;
2. `appsettings.{Environment}.json`;
3. environment variables with `__`;
4. command line/in-memory overrides for bootstrap tool;
5. resolved `secret://...` values injected in memory.

## Service Sections

| Секция | Что задает |
| --- | --- |
| `Service` | Имя сервиса и health route |
| `Secrets` | PAM/AAPM provider, references и bootstrap credentials |
| `ActiveDirectory` | LDAP/LDAPS endpoint, bind account, group search, user attributes |
| `Cmdbuild` | CMDBuild REST v3 URL, service account, retry, user fields, role name fields |
| `Sync` | Периодичность, dry-run, immediate start, state file, lock file и failure backoff |
| `Debug` | Diagnostic logging flag, verbosity level `Basic`/`Verbose` и sensitive-value opt-in |
| `Readiness` | `/ready` route, dependency checks и timeout |
| `EndpointRateLimiting` | Fixed-window rate limit для `/health`, `/ready`, `/sync/status` |
| `ElkLogging` | Optional отправка structured logs в ELK |
| `Logging` | Обычный .NET console logging |

## Environment Overrides

Примеры:

```bash
ActiveDirectory__Host=dc01.example.local
ActiveDirectory__Port=636
ActiveDirectory__UseSsl=true
ActiveDirectory__BindDn='CN=svc-cmdb,OU=Service Accounts,DC=example,DC=local'
ActiveDirectory__BindPasswordSecret='AAA.LOCAL/PROD/ad-bind'
ActiveDirectory__RetryAttempts=3
ActiveDirectory__RetryJitterPercent=20

Cmdbuild__BaseUrl=https://cmdbuild.example/cmdbuild/services/rest/v3
Cmdbuild__Username=cmdbuild-admin
Cmdbuild__PasswordSecret='AAA.LOCAL/PROD/cmdbuild-admin'
Cmdbuild__RetryAttempts=3
Cmdbuild__RetryJitterPercent=20

Sync__DryRun=false
Sync__IntervalSeconds=300
Sync__FailureBackoffSeconds=30
Sync__ShutdownGracePeriodSeconds=60

Debug__Enabled=true
Debug__Level=Basic
Debug__LogSensitiveValues=false

Readiness__CheckDependencies=true
AllowedHosts=adgroups2cmdbuild.example.local

ElkLogging__Enabled=true
ElkLogging__Endpoint=https://elastic.example.local:9200
ElkLogging__ApiKey=secret://AAA.LOCAL/PROD/elk-api-key
```

## Retry And Shutdown Settings

| Секция | Параметры | Поведение |
| --- | --- | --- |
| `ActiveDirectory` | `RetryAttempts`, `RetryBaseDelayMs`, `RetryMaxDelayMs`, `RetryJitterPercent` | Retry для transient LDAP/LDAPS bind/search/range-read ошибок |
| `Cmdbuild` | `RetryAttempts`, `RetryBaseDelayMs`, `RetryMaxDelayMs`, `RetryJitterPercent` | Retry для HTTP `408`, `429`, `5xx`, timeout и network errors |
| `Sync` | `ShutdownGracePeriodSeconds` | Время ожидания активного sync-run при SIGTERM/SIGINT перед отменой |

`bootstrap-ad-groups` использует те же `ActiveDirectory` и `Cmdbuild` retry settings для transient role read, bind, search и group create ошибок.

## Bootstrap Tool Config

`scripts/bootstrap-ad-groups.sh` использует те же AD, CMDBuild, Secrets sections.
Retry, timeout и PAM/AAPM настройки читаются из той же configuration chain, что и у сервиса.

Дополнительные параметры задаются CLI или секцией `BootstrapAdGroups`:

| Параметр | Назначение |
| --- | --- |
| `TargetOuDn` / `--target-ou` | OU/container DN для создаваемых групп |
| `IncludeNamePrefix` / `--prefix` | Выбор roles по prefix |
| `IncludeRoleNames` / `--include` | Выбор exact roles |
| `ExcludeRoleNames` / `--exclude` | Исключение roles |
| `All` / `--all` | Выбрать все roles |
| `Apply` / `--apply` | Применить план |
| `GroupScope` / `--scope` | `Global`, `Universal`, `DomainLocal` |
| `SecurityEnabled` | Создавать security group, default `true` |
| `RequireExplicitSelectionForApply` | Защита от apply без фильтра |

## Не Коммитить

- `.dotnet/`, `.dotnet_home/`, `.nuget/`;
- `bin/`, `obj/`;
- `state/`;
- production config с secret values;
- локальные IDE workspace files.
