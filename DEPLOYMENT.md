# Инструкция по развертыванию ad2cmdb

Документ описывает развертывание сервиса `adgroups2cmdbuild` и разового инструмента `bootstrap-ad-groups`.

## 1. Что Развертываем

| Компонент | Назначение |
| --- | --- |
| `adgroups2cmdbuild` | Микросервис, который раз в 5 минут читает группы MS AD и синхронизирует пользователей/группы CMDBuild |
| `bootstrap-ad-groups` | Разовый deployment tool: создает отсутствующие AD-группы по существующим CMDBuild roles |
| `state/adgroups2cmdbuild-state.json` | Локальное состояние управляемых логинов |
| `state/adgroups2cmdbuild-state.json.bak` | Последний backup state перед успешной заменой |
| `state/adgroups2cmdbuild.lock` | Локальный lock-файл, запрещающий два sync-run на одном хосте |
| `ElkLogging` | Optional отправка structured logs в ELK/Elasticsearch |

Основной принцип: MS AD является источником истины для членства в группах, CMDBuild является целевой системой.
Сервис рассчитан на одну активную реплику. Active-active запуск с общим file-based state не поддерживается.

## 2. Предварительные Требования

Нужны:

- доступ сервиса к MS AD по LDAP `389` или LDAPS `636`;
- доступ сервиса к CMDBuild REST API v3;
- сервисная УЗ AD для чтения групп и пользователей;
- сервисная УЗ CMDBuild для управления пользователями;
- при использовании PAM/AAPM: bootstrap-доступ приложения к PAM;
- при отправке логов в ELK: endpoint и API key, если требуется.

Рекомендуемый порядок первого запуска:

1. Подготовить учетные записи и права.
2. Настроить секреты.
3. Выполнить bootstrap AD-групп в dry-run.
4. При необходимости создать AD-группы через `--apply`.
5. Запустить сервис с `Sync:DryRun=true`.
6. Проверить планируемые действия по логам.
7. Включить `Sync:DryRun=false`.

## 3. Права

### MS AD

Для основного сервиса:

- read на OU с группами;
- read на `member` у групп;
- read на user attributes: `sAMAccountName`, `displayName`, `mail`, `userAccountControl`, `objectClass`;
- write-права сервису не нужны.

Для `bootstrap-ad-groups` дополнительно:

- create group в целевой OU;
- write attributes `cn`, `sAMAccountName`, `groupType`, optional `description`.

### CMDBuild

Для основного сервиса:

- read roles;
- read users;
- create users;
- update users: `active`, `userGroups`, `defaultUserGroup`, поле ФИО и поле email.

Для `bootstrap-ad-groups`:

- read roles.

## 4. Конфигурация

Base config лежит в:

```text
src/adgroups2cmdbuild/appsettings.json
```

Production overrides задавайте через env или mounted `appsettings.Production.json`, который не коммитится.

Минимальный набор env:

```bash
ASPNETCORE_ENVIRONMENT=Production

ActiveDirectory__Host=dc01.example.local
ActiveDirectory__Port=636
ActiveDirectory__UseSsl=true
ActiveDirectory__BindDn='CN=svc-cmdb,OU=Service Accounts,DC=example,DC=local'
ActiveDirectory__BindPasswordSecret='AAA.LOCAL/PROD/ad-bind'
ActiveDirectory__GroupSearchBaseDn='OU=CMDBuild,OU=Groups,DC=example,DC=local'
ActiveDirectory__GroupNames__0=CMDBuildUsers
ActiveDirectory__GroupNames__1=CMDBuildEditors
ActiveDirectory__ProvisioningGroupName=CMDBuildUsers

Cmdbuild__BaseUrl='https://cmdbuild.example/cmdbuild/services/rest/v3'
Cmdbuild__Username=cmdbuild-sync
Cmdbuild__PasswordSecret='AAA.LOCAL/PROD/cmdbuild-sync'

Sync__DryRun=true
Sync__IntervalSeconds=300
Sync__FailureBackoffSeconds=30
AllowedHosts=adgroups2cmdbuild.example.local

Debug__Enabled=false
Debug__Level=Basic
```

`ProvisioningGroupName` должен входить в `GroupNames`.
Пользователь получает активную УЗ CMDBuild только если он состоит в provisioning group.
Если пользователь пропал из provisioning group, сервис блокирует его и отзывает все CMDBuild groups.

В `Production` действуют runtime guards:

- `ActiveDirectory:IgnoreCertificateErrors=true` запрещен;
- `Cmdbuild:BaseUrl` должен быть `https://...`;
- `AllowedHosts` не должен быть `*`.

Для локальной разработки используйте `ASPNETCORE_ENVIRONMENT=Development`.

## 5. Секреты и PAM/AAPM

Можно задавать секреты напрямую через env/config, но для production рекомендуется PAM/AAPM.

Пример:

```bash
PAMURL=https://pam.example.local
PAMUSERNAME=APP_ACCOUNT
PAMPASSWORD='<bootstrap-secret>'

ActiveDirectory__BindPasswordSecret='AAA.LOCAL/PROD/ad-bind'
Cmdbuild__PasswordSecret='AAA.LOCAL/PROD/cmdbuild-sync'
ElkLogging__ApiKeySecret='AAA.LOCAL/PROD/elk-api-key'
```

Если `PAMURL` плюс `PAMTOKEN` или `PAMUSERNAME`/`PAMPASSWORD` заданы, provider автоматически считается `IndeedPamAapm`.

Важно:

- `secret://...` без активного PAM provider вызывает ошибку старта;
- фактические значения секретов подставляются только в память процесса;
- production passwords и tokens не коммитить.

## 6. Разовое Создание AD-Групп

Если в CMDBuild roles уже заведены, а одноименных AD-групп еще нет, используйте отдельный tool.

Dry-run:

```bash
./scripts/bootstrap-ad-groups.sh \
  --target-ou 'OU=CMDBuild,OU=Groups,DC=example,DC=local' \
  --prefix CMDBuild
```

Apply:

```bash
./scripts/bootstrap-ad-groups.sh \
  --target-ou 'OU=CMDBuild,OU=Groups,DC=example,DC=local' \
  --prefix CMDBuild \
  --apply
```

Варианты выбора roles:

- `--prefix CMDBuild` - только roles с указанным prefix;
- `--include Role1,Role2` - точный список roles;
- `--all` - все roles CMDBuild;
- без фильтра tool использует `ActiveDirectory:GroupNames`.

Защита: `--apply` без явного выбора запрещен, если не задано `BootstrapAdGroups:RequireExplicitSelectionForApply=false`.

## 7. Локальный Запуск

Сборка:

```bash
./scripts/dotnet build src/adgroups2cmdbuild/adgroups2cmdbuild.csproj -v minimal
./scripts/dotnet build tools/bootstrap-ad-groups/bootstrap-ad-groups.csproj -v minimal
```

Запуск сервиса:

```bash
ASPNETCORE_ENVIRONMENT=Production \
ActiveDirectory__Host=dc01.example.local \
ActiveDirectory__Port=636 \
ActiveDirectory__UseSsl=true \
ActiveDirectory__BindDn='CN=svc-cmdb,OU=Service Accounts,DC=example,DC=local' \
ActiveDirectory__BindPassword='<secret>' \
Cmdbuild__BaseUrl='https://cmdbuild.example/cmdbuild/services/rest/v3' \
Cmdbuild__Username=cmdbuild-sync \
Cmdbuild__Password='<secret>' \
Sync__DryRun=true \
./scripts/dotnet run --project src/adgroups2cmdbuild/adgroups2cmdbuild.csproj
```

Health:

```bash
curl http://localhost:5084/health
curl http://localhost:5084/ready
curl http://localhost:5084/sync/status
```

## 8. Docker

Сборка образа:

```bash
docker build \
  -f deploy/dockerfiles/adgroups2cmdbuild.Dockerfile \
  -t adgroups2cmdbuild:0.1.0 \
  .
```

Запуск:

```bash
docker run -d \
  --name adgroups2cmdbuild \
  --restart unless-stopped \
  -p 5084:8080 \
  -v "$PWD/state:/app/state" \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ActiveDirectory__Host=dc01.example.local \
  -e ActiveDirectory__Port=636 \
  -e ActiveDirectory__UseSsl=true \
  -e ActiveDirectory__BindDn='CN=svc-cmdb,OU=Service Accounts,DC=example,DC=local' \
  -e ActiveDirectory__BindPasswordSecret='AAA.LOCAL/PROD/ad-bind' \
  -e ActiveDirectory__GroupSearchBaseDn='OU=CMDBuild,OU=Groups,DC=example,DC=local' \
  -e ActiveDirectory__GroupNames__0=CMDBuildUsers \
  -e ActiveDirectory__ProvisioningGroupName=CMDBuildUsers \
  -e Cmdbuild__BaseUrl='https://cmdbuild.example/cmdbuild/services/rest/v3' \
  -e Cmdbuild__Username=cmdbuild-sync \
  -e Cmdbuild__PasswordSecret='AAA.LOCAL/PROD/cmdbuild-sync' \
  -e Sync__DryRun=true \
  -e AllowedHosts=adgroups2cmdbuild.example.local \
  adgroups2cmdbuild:0.1.0
```

После проверки dry-run включите применение:

```bash
docker rm -f adgroups2cmdbuild
# повторить docker run, но с:
-e Sync__DryRun=false
```

## 9. ELK Logging

По умолчанию отправка логов в ELK выключена.
Если `ElkLogging:Enabled=false` или `ElkLogging:Endpoint` пустой, provider ничего не отправляет.

Пример включения:

```bash
ElkLogging__Enabled=true
ElkLogging__Endpoint=https://elastic.example.local:9200
ElkLogging__Index=adgroups2cmdbuild-logs
ElkLogging__ApiKeySecret='AAA.LOCAL/PROD/elk-api-key'
ElkLogging__MinimumLevel=Information
ElkLogging__Environment=Production
```

Если `Endpoint` указывает на base URL Elasticsearch, сервис отправляет документы в:

```text
{Endpoint}/{Index}/_doc
```

Если `Endpoint` уже заканчивается на `/_doc` или `/_bulk`, URL используется как есть.

## 10. Debug Logging

Для диагностики есть отдельный флаг `Debug`.
Debug-события пишутся через стандартный `ILogger` на уровне `Information`, поэтому они видны в Docker stdout и попадают в ELK при включенном `ElkLogging`.

```bash
Debug__Enabled=true
Debug__Level=Basic
```

Уровни:

- `Basic` или `1`: границы sync-run, счетчики AD/CMDBuild snapshot, количество пользователей в группах, страницы CMDBuild, количество кандидатов на блокировку, запись state;
- `Verbose` или `2`: все из Basic плюс per-user действия create/update/disable и resolved login lists по AD-группам.

По умолчанию sensitive values в verbose diagnostic lists редактируются. Если нужны реальные логины и состав групп, задайте `Debug__LogSensitiveValues=true` только на короткое диагностическое окно.

## 11. Если ELK Нет

Варианты сбора логов:

1. Docker stdout/stderr: оставить приложение как есть и собирать `docker logs` средствами платформы.
2. Docker syslog logging driver: приложение пишет в stdout, Docker пересылает в syslog.
3. Агент на узле: Filebeat/Vector/Fluent Bit читает Docker container logs и отправляет в нужное хранилище.
4. Sidecar collector: отдельный контейнер-агент рядом с сервисом.

Syslog из Docker работает без изменения приложения через logging driver:

```bash
docker run -d \
  --name adgroups2cmdbuild \
  --log-driver=syslog \
  --log-opt syslog-address=udp://syslog.example.local:514 \
  --log-opt tag='adgroups2cmdbuild/{{.Name}}' \
  -p 5084:8080 \
  -v "$PWD/state:/app/state" \
  ... \
  adgroups2cmdbuild:0.1.0
```

Для TCP/TLS syslog используйте адреса вида:

```bash
--log-opt syslog-address=tcp://syslog.example.local:514
--log-opt syslog-address=tcp+tls://syslog.example.local:6514
```

Ограничение: Docker syslog driver пересылает stdout/stderr контейнера. Он не использует `ElkLogging` и не требует отдельной syslog-библиотеки в приложении.

## 12. Проверка После Запуска

1. Проверить `/health`.
2. Проверить `/ready`.
3. Проверить `/sync/status`.
4. Проверить logs на отсутствие ошибок missing AD group или missing CMDBuild role.
5. При `Sync:DryRun=true` проверить, какие действия сервис планирует.
6. При `Sync:DryRun=false` проверить несколько пользователей в CMDBuild:
   - `active`;
   - `description` или выбранное поле ФИО;
   - `email`;
   - `userGroups`;
   - блокировку пользователя, удаленного из provisioning group.

Если один пользователь не применился в CMDBuild, run продолжается. В `/sync/status` это видно как `lastSucceeded=false` и `lastSummary.failedUsers > 0`; смотрите error log по конкретному login и повторите sync после исправления причины.

## 13. Rollback

Если сервис еще в dry-run, rollback не нужен.

Если сервис применил неверные изменения:

1. Остановить сервис или задать `Sync__Enabled=false`.
2. Исправить membership в AD.
3. Проверить `GroupNames` и `ProvisioningGroupName`.
4. Запустить сервис в `Sync__DryRun=true`.
5. После проверки вернуть `Sync__DryRun=false`.

Если поврежден `state/adgroups2cmdbuild-state.json`, сервис пробует прочитать `state/adgroups2cmdbuild-state.json.bak`. Если оба файла повреждены, остановите сервис, восстановите state из backup платформы или удалите state только после ручной оценки последствий повторного deprovision/provision.

При переименовании AD group или удалении CMDBuild role сначала обновите конфигурацию и проверьте dry-run. Missing configured group/role считается hard error, чтобы не выполнить массовые неверные изменения.

Bootstrap tool не удаляет AD groups. Если группа создана ошибочно, удаление выполняется отдельной AD admin процедурой.

## 14. Обязательные Проверки Перед Релизом

```bash
./scripts/dotnet build src/adgroups2cmdbuild/adgroups2cmdbuild.csproj -v minimal
./scripts/dotnet build tools/bootstrap-ad-groups/bootstrap-ad-groups.csproj -v minimal
./scripts/dotnet run --project tests/adgroups2cmdbuild.tests/adgroups2cmdbuild.tests.csproj
./scripts/bootstrap-ad-groups.sh --help
git diff --check
```
