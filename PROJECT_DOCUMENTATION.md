# Документация проекта ad2cmdb

Версия документации: `0.2.0`.
Дата актуализации: 2026-05-31.

## Назначение

`ad2cmdb` синхронизирует членство выбранных групп Microsoft Active Directory с ролями CMDBuild.
Основной сервис `adgroups2cmdbuild` периодически читает AD-группы, сверяет их с CMDBuild roles и создает, обновляет или блокирует CMDBuild users.
Отдельный инструмент `bootstrap-ad-groups` помогает один раз создать AD-группы по существующим CMDBuild roles.

MS AD является источником истины для членства в группах.
CMDBuild является целевой системой.

## Состав Репозитория

| Путь | Назначение |
| --- | --- |
| `src/adgroups2cmdbuild` | ASP.NET Core worker/API синхронизации AD groups -> CMDBuild users/roles |
| `tools/bootstrap-ad-groups` | Консольный deployment tool для создания AD-групп по CMDBuild roles |
| `deploy/dockerfiles/adgroups2cmdbuild.Dockerfile` | Docker image сервиса |
| `aa/` | Архитектурные артефакты, карты конфигурации, health, secrets и observability |
| `tests/adgroups2cmdbuild.tests` | Легкий test harness без внешних NuGet test packages |
| `state/` | Runtime state, не коммитится |

## Runtime Поведение

- Синхронизация запускается при старте и далее по `Sync:IntervalSeconds`.
- `ActiveDirectory:ProvisioningGroupName` определяет, должен ли пользователь существовать активным в CMDBuild.
- Каждая настроенная AD-группа должна иметь одноименную CMDBuild role.
- Missing AD group или CMDBuild role останавливает sync-run до любых изменений.
- Ошибка CMDBuild по одному пользователю не останавливает batch: остальные пользователи продолжают обрабатываться, а `/sync/status` показывает partial failure.
- State сохраняется только после apply и только для успешно примененных операций.
- `Sync:DryRun=true` ничего не пишет в CMDBuild и не сохраняет state.

## Эксплуатационные Ограничения

Сервис рассчитан на одну активную реплику.
Active-active не поддерживается, потому что state хранится в локальном JSON-файле.
Для защиты от случайного второго процесса на том же хосте используется `Sync:InstanceLockPath`.
Для HA используйте active-passive модель средствами оркестратора, где одновременно работает только одна реплика.

State-файл:

- основной файл: `state/adgroups2cmdbuild-state.json`;
- backup: `state/adgroups2cmdbuild-state.json.bak`;
- lock: `state/adgroups2cmdbuild.lock`.

Если основной state поврежден, сервис пробует backup.
Если повреждены оба файла, sync-run завершается ошибкой и требует ручного восстановления.

## API И Health

| Endpoint | Назначение |
| --- | --- |
| `GET /health` | Процесс жив и возвращает текущий sync status |
| `GET /ready` | Readiness; shallow по умолчанию, dependency checks включаются через `Readiness:CheckDependencies=true` |
| `GET /sync/status` | Детальный статус последнего sync-run |

Поля partial failure отражаются в `lastSummary.failedUsers`, а `lastSucceeded=false` означает, что последний run завершился с ошибкой или частичными ошибками.

Для `/health`, `/ready` и `/sync/status` включен fixed-window rate limit через `EndpointRateLimiting`.

## Security Baseline

- В `Production` запрещен `ActiveDirectory:IgnoreCertificateErrors=true`.
- В `Production` `Cmdbuild:BaseUrl` должен использовать HTTPS.
- В `Production` `AllowedHosts` не должен быть `*`; задавайте явные DNS names через env/config.
- Секреты задаются через env, `appsettings.Production.json` вне git или PAM/AAPM references.
- `Debug:Level=Verbose` по умолчанию редактирует sensitive login values; реальные значения включаются только через `Debug:LogSensitiveValues=true`.

## CI И Проверки

GitLab CI описан в `.gitlab-ci.yml`:

- build сервиса и bootstrap tool;
- запуск `tests/adgroups2cmdbuild.tests`;
- non-blocking проверка vulnerable NuGet packages.

Локальные обязательные проверки:

```bash
./scripts/dotnet build src/adgroups2cmdbuild/adgroups2cmdbuild.csproj -v minimal
./scripts/dotnet build tools/bootstrap-ad-groups/bootstrap-ad-groups.csproj -v minimal
./scripts/dotnet run --project tests/adgroups2cmdbuild.tests/adgroups2cmdbuild.tests.csproj
git diff --check
```

## Runbook Кратко

Первое включение:

1. Настроить AD/CMDBuild accounts и secrets.
2. Запустить `bootstrap-ad-groups` в dry-run.
3. Запустить сервис с `Sync:DryRun=true`.
4. Проверить `/health`, `/ready`, `/sync/status` и logs.
5. Включить `Sync:DryRun=false`.

Инцидент partial failure:

1. Проверить `lastSummary.failedUsers`.
2. Найти error log по login и CMDBuild operation.
3. Исправить причину в CMDBuild/AD/сети.
4. Дождаться следующего run или перезапустить сервис в dry-run для проверки.

Повреждение state:

1. Остановить сервис.
2. Проверить основной state и `.bak`.
3. Восстановить state из backup платформы или `.bak`.
4. Запустить с `Sync:DryRun=true` и проверить план.

Rollback:

1. Остановить сервис или задать `Sync:Enabled=false`.
2. Исправить AD membership и конфигурацию.
3. Запустить dry-run.
4. Вернуть apply только после проверки.
