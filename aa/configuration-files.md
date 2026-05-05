# Конфигурационные файлы

## Runtime Config

| Файл | Назначение | Коммитить |
| --- | --- | --- |
| `src/adgroups2cmdbuild/appsettings.json` | Base config сервиса и bootstrap tool | Да |
| `src/adgroups2cmdbuild/appsettings.Development.json` | Dev overrides без production secrets | Да |
| `appsettings.Production.json` | Production overrides с секретами или локальными адресами | Нет |
| `state/adgroups2cmdbuild-state.json` | State управляемых логинов и время последней успешной sync | Нет |

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
| `Cmdbuild` | CMDBuild REST v3 URL, service account, user fields, role name fields |
| `Sync` | Периодичность, dry-run, immediate start, state file |
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

Cmdbuild__BaseUrl=https://cmdbuild.example/cmdbuild/services/rest/v3
Cmdbuild__Username=cmdbuild-admin
Cmdbuild__PasswordSecret='AAA.LOCAL/PROD/cmdbuild-admin'

Sync__DryRun=false
Sync__IntervalSeconds=300

ElkLogging__Enabled=true
ElkLogging__Endpoint=https://elastic.example.local:9200
ElkLogging__ApiKey=secret://AAA.LOCAL/PROD/elk-api-key
```

## Bootstrap Tool Config

`scripts/bootstrap-ad-groups.sh` использует те же AD, CMDBuild, Secrets sections.

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
