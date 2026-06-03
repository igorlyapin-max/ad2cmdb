# Развертывание через локальный Docker registry

Документ описывает сборку и публикацию image `adgroups2cmdbuild` в локальный Docker registry.
В репозитории нет Kafka/Zabbix/UI микросервисов; для них используйте документацию соответствующего проекта.

## Image

| Image | Dockerfile | Внутренний port | Назначение |
| --- | --- | --- | --- |
| `adgroups2cmdbuild` | `deploy/dockerfiles/adgroups2cmdbuild.Dockerfile` | `8080` | Синхронизация AD groups -> CMDBuild users/roles |

Runtime image запускает сервис от non-root пользователя `ad2cmdb` с UID/GID `64100`.

## Локальный registry

Если registry еще не запущен:

```bash
docker run -d --restart=always -p 5000:5000 --name registry registry:2
```

Проверка:

```bash
curl http://localhost:5000/v2/_catalog
```

Если registry расположен на другом host и работает без TLS, Docker daemon на узлах запуска должен разрешать этот адрес как `insecure-registries`.

## Build And Push

```bash
VERSION=0.3.0
REGISTRY=localhost:5000

docker build \
  -f deploy/dockerfiles/adgroups2cmdbuild.Dockerfile \
  -t "$REGISTRY/ad2cmdb/adgroups2cmdbuild:$VERSION" \
  -t "$REGISTRY/ad2cmdb/adgroups2cmdbuild:latest" \
  .

docker push "$REGISTRY/ad2cmdb/adgroups2cmdbuild:$VERSION"
docker push "$REGISTRY/ad2cmdb/adgroups2cmdbuild:latest"
```

Сборка требует доступа к `mcr.microsoft.com` для .NET SDK/runtime image и к NuGet registry для restore.

## Runtime Config

Base config находится в `src/adgroups2cmdbuild/appsettings.json`.
Production overrides задавайте через env, mounted `appsettings.Production.json` или secret storage; не меняйте config внутри image.

Минимальный production run:

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
  -e Readiness__CheckDependencies=true \
  -e AllowedHosts=adgroups2cmdbuild.example.local \
  localhost:5000/ad2cmdb/adgroups2cmdbuild:0.3.0
```

Production guards reject:

- `ActiveDirectory__UseSsl=false`;
- `ActiveDirectory__IgnoreCertificateErrors=true`;
- non-HTTPS `Cmdbuild__BaseUrl`;
- `AllowedHosts` containing `*`;
- readiness that does not check AD and CMDBuild dependencies.

## Secrets

Use env, Docker/Kubernetes secrets, mounted config outside git, or PAM/AAPM references.
Supported sensitive fields include:

| Field | Purpose |
| --- | --- |
| `ActiveDirectory__BindPassword` / `ActiveDirectory__BindPasswordSecret` | AD bind account password |
| `Cmdbuild__Password` / `Cmdbuild__PasswordSecret` | CMDBuild service account password |
| `ElkLogging__ApiKey` / `ElkLogging__ApiKeySecret` | Optional ELK API key |

If `PAMURL` plus `PAMTOKEN` or `PAMUSERNAME`/`PAMPASSWORD` are present, `Secrets:Provider` is treated as `IndeedPamAapm`.

## State Volume

Mount `state/` or another writable volume for:

| File | Purpose |
| --- | --- |
| `state/adgroups2cmdbuild-state.json` | Managed login state |
| `state/adgroups2cmdbuild-state.json.bak` | Last valid backup |
| `state/adgroups2cmdbuild.lock` | Local run lock |

The service is designed for one active replica. Use active-passive orchestration if high availability is required.
Mounted state volume must be writable for UID/GID `64100`.

## Smoke

```bash
curl http://localhost:5084/health
curl http://localhost:5084/ready
curl http://localhost:5084/sync/status
```

Before enabling apply mode, inspect logs with `Sync__DryRun=true`.
After validation, restart with `Sync__DryRun=false`.

## Do Not Commit

- `state/`;
- `.nuget/`, `.dotnet/`, `.dotnet_home/`;
- `appsettings.Production.json` with deployment-specific values;
- AD, CMDBuild, PAM, ELK passwords or tokens.
