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
  adgroups2cmdbuild:dev
```

## Deployment Order

1. Создать или выбрать AD service account.
2. Создать или выбрать CMDBuild service account.
3. Настроить PAM/AAPM references или deployment secrets.
4. При необходимости выполнить bootstrap AD groups в dry-run.
5. Выполнить bootstrap AD groups с `--apply`.
6. Запустить сервис с `Sync:DryRun=true`.
7. Проверить logs и `/sync/status`.
8. При необходимости временно включить `Debug:Enabled=true`, `Debug:Level=Basic`.
9. Включить `Sync:DryRun=false`.
10. Проверить CMDBuild users и groups.

## Smoke

```bash
curl http://localhost:5084/health
curl http://localhost:5084/sync/status
```

Build gates:

```bash
./scripts/dotnet build src/adgroups2cmdbuild/adgroups2cmdbuild.csproj -v minimal
./scripts/dotnet build tools/bootstrap-ad-groups/bootstrap-ad-groups.csproj -v minimal
git diff --check
```

## Rollback

- Если `Sync:DryRun=true`, rollback не нужен: writes не выполнялись.
- Если сервис ошибочно заблокировал пользователей, откат выполняется восстановлением AD provisioning membership и повторным sync.
- Если нужно остановить изменения немедленно, остановить service или задать `Sync:Enabled=false`.
- Bootstrap tool не удаляет AD groups; ошибочно созданные groups удаляются отдельной AD admin процедурой.

## Logging Without ELK

Если ELK недоступен, сервис продолжает писать обычные console logs.
Docker может отправлять stdout/stderr контейнера в syslog через logging driver:

```bash
docker run --log-driver=syslog --log-opt syslog-address=udp://syslog.example.local:514 ...
```

Альтернативы: Docker json-file + log agent, Filebeat/Vector/Fluent Bit на узле или sidecar collector.
