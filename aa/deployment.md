# Развертывание

## Компоненты

| Компонент | Тип | Назначение |
| --- | --- | --- |
| `adgroups2cmdbuild` | .NET Web/Worker service | Периодическая синхронизация AD groups -> CMDBuild users/roles |
| `bootstrap-ad-groups` | .NET console tool | Разовое создание отсутствующих AD groups из CMDBuild roles |
| `scripts/dotnet` | wrapper | Локальный запуск .NET SDK |
| `deploy/dockerfiles/adgroups2cmdbuild.Dockerfile` | Dockerfile | Сборка runtime image сервиса |

## Ports

| Среда | Port | Назначение |
| --- | --- | --- |
| Local launch profile | `5084` | HTTP health/status сервиса |
| Container internal | `8080` | HTTP health/status сервиса |

## Docker

Build:

```bash
docker build -f deploy/dockerfiles/adgroups2cmdbuild.Dockerfile -t adgroups2cmdbuild:dev .
```

Run example:

```bash
docker run --rm \
  --name adgroups2cmdbuild \
  -p 5084:8080 \
  -v "$PWD/state:/app/state" \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ActiveDirectory__Host=dc01.example.local \
  -e ActiveDirectory__Port=636 \
  -e ActiveDirectory__UseSsl=true \
  -e ActiveDirectory__BindDn='CN=svc-cmdb,OU=Service Accounts,DC=example,DC=local' \
  -e ActiveDirectory__BindPasswordSecret='AAA.LOCAL/PROD/ad-bind' \
  -e Cmdbuild__BaseUrl='https://cmdbuild.example/cmdbuild/services/rest/v3' \
  -e Cmdbuild__Username=cmdbuild-admin \
  -e Cmdbuild__PasswordSecret='AAA.LOCAL/PROD/cmdbuild-admin' \
  -e Sync__DryRun=false \
  -e Readiness__CheckDependencies=true \
  -e AllowedHosts=adgroups2cmdbuild.example.local \
  adgroups2cmdbuild:dev
```

Контейнерный image содержит Docker `HEALTHCHECK`, который вызывает `http://localhost:8080/health`.
Runtime запускается от non-root пользователя `ad2cmdb` с UID/GID `64100`.
Если `state/` монтируется с host, каталог должен быть writable для UID/GID `64100` или предварительно создан с совместимыми правами.
Для production обязательно задайте `ActiveDirectory__UseSsl=true`, HTTPS `Cmdbuild__BaseUrl`, `Readiness__CheckDependencies=true`, явный `AllowedHosts` и не включайте `ActiveDirectory__IgnoreCertificateErrors`.
При остановке контейнера worker прекращает новые sync-run и ждет активный run до `Sync__ShutdownGracePeriodSeconds`, затем отменяет его.

## Deployment Order

1. Создать или выбрать AD service account.
2. Создать или выбрать CMDBuild service account.
3. Настроить PAM/AAPM references или deployment secrets.
4. При необходимости выполнить bootstrap AD groups в dry-run.
5. Выполнить bootstrap AD groups с `--apply`.
6. Запустить сервис с `Sync:DryRun=true`.
7. Проверить logs, `/health`, `/ready` и `/sync/status`.
8. При необходимости временно включить `Debug:Enabled=true`, `Debug:Level=Basic`.
9. Включить `Sync:DryRun=false`.
10. Проверить CMDBuild users и groups.

## Smoke

```bash
curl http://localhost:5084/health
curl http://localhost:5084/ready
curl http://localhost:5084/sync/status
```

Машиночитаемый contract этих endpoint-ов: `aa/contracts/operational-api.openapi.json`.

Build gates:

```bash
./scripts/dotnet build src/adgroups2cmdbuild/adgroups2cmdbuild.csproj -v minimal
./scripts/dotnet build tools/bootstrap-ad-groups/bootstrap-ad-groups.csproj -v minimal
./scripts/dotnet run --project tests/adgroups2cmdbuild.tests/adgroups2cmdbuild.tests.csproj
bash -n scripts/dotnet
bash -n scripts/bootstrap-ad-groups.sh
./scripts/bootstrap-ad-groups.sh --help
git diff --check
```

## Rollback

- Если `Sync:DryRun=true`, rollback не нужен: writes не выполнялись.
- Если сервис ошибочно заблокировал пользователей, откат выполняется восстановлением AD provisioning membership и повторным sync.
- Если нужно остановить изменения немедленно, остановить service или задать `Sync:Enabled=false`.
- Если state поврежден, сервис пробует `.bak`; при повреждении обоих файлов остановите сервис и восстановите state из backup платформы.
- Bootstrap tool не удаляет AD groups; ошибочно созданные groups удаляются отдельной AD admin процедурой.

## Logging Without ELK

Если ELK недоступен, сервис продолжает писать обычные console logs.
Docker может отправлять stdout/stderr контейнера в syslog через logging driver:

```bash
docker run --log-driver=syslog --log-opt syslog-address=udp://syslog.example.local:514 ...
```

Альтернативы: Docker json-file + log agent, Filebeat/Vector/Fluent Bit на узле или sidecar collector.
