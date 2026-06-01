# Развертывание через локальный Docker registry

Документ описывает, как собрать образы микросервисов и UI, опубликовать их в локальный Docker registry и какие конфигурационные файлы использовать при запуске.

## Образы

| Образ | Dockerfile | Внутренний порт | Назначение |
| --- | --- | --- | --- |
| `cmdbwebhooks2kafka` | `deploy/dockerfiles/cmdbwebhooks2kafka.Dockerfile` | `8080` | Прием CMDBuild webhooks |
| `cmdbkafka2zabbix` | `deploy/dockerfiles/cmdbkafka2zabbix.Dockerfile` | `8080` | Конвертация CMDBuild events в Zabbix requests |
| `zabbixrequests2api` | `deploy/dockerfiles/zabbixrequests2api.Dockerfile` | `8080` | Вызов Zabbix JSON-RPC |
| `zabbixbindings2cmdbuild` | `deploy/dockerfiles/zabbixbindings2cmdbuild.Dockerfile` | `8080` | Обратная запись Zabbix hostid в CMDBuild |
| `monitoring-ui-api` | `deploy/dockerfiles/monitoring-ui-api.Dockerfile` | `5090` | UI/BFF |

Внешние dev-порты обычно мапятся так: `5080:8080`, `5081:8080`, `5082:8080`, `5083:8080`, `5090:5090`.

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

## Сборка и push

Основной вариант:

```bash
REGISTRY=localhost:5000 VERSION=0.8.0 ./scripts/build-local-registry-images.sh
```

Скрипт собирает и публикует:

```text
localhost:5000/cmdb2monitoring/cmdbwebhooks2kafka:0.8.0
localhost:5000/cmdb2monitoring/cmdbkafka2zabbix:0.8.0
localhost:5000/cmdb2monitoring/zabbixrequests2api:0.8.0
localhost:5000/cmdb2monitoring/zabbixbindings2cmdbuild:0.8.0
localhost:5000/cmdb2monitoring/monitoring-ui-api:0.8.0
```

Дополнительно ставится тег `latest`. Для локальной проверки без push:

```bash
PUSH=false VERSION=0.8.0 ./scripts/build-local-registry-images.sh
```

Ручная сборка одного образа:

```bash
docker build \
  -f deploy/dockerfiles/cmdbkafka2zabbix.Dockerfile \
  -t localhost:5000/cmdb2monitoring/cmdbkafka2zabbix:0.8.0 \
  .

docker push localhost:5000/cmdb2monitoring/cmdbkafka2zabbix:0.8.0
```

Сборка требует доступа к `mcr.microsoft.com` для .NET runtime/sdk images, к `docker.io` для Node.js image и к NuGet/npm registry для restore/install зависимостей.

## Конфигурационные файлы

Образы включают base config из репозитория. Для реального запуска не меняйте файлы внутри образа; используйте mounted config, env overrides или secret storage.

| Компонент | Base config | Dev config | Production/local override |
| --- | --- | --- | --- |
| `cmdbwebhooks2kafka` | `src/cmdbwebhooks2kafka/appsettings.json` | `src/cmdbwebhooks2kafka/appsettings.Development.json` | mounted `/app/appsettings.Production.json` или env `Kafka__...`, `CmdbWebhook__...` |
| `cmdbkafka2zabbix` | `src/cmdbkafka2zabbix/appsettings.json` | `src/cmdbkafka2zabbix/appsettings.Development.json` | mounted `/app/appsettings.Production.json` или env `Kafka__...`, `ConversionRules__...`, `Cmdbuild__...` |
| `zabbixrequests2api` | `src/zabbixrequests2api/appsettings.json` | `src/zabbixrequests2api/appsettings.Development.json` | mounted `/app/appsettings.Production.json` или env `Kafka__...`, `Zabbix__...` |
| `zabbixbindings2cmdbuild` | `src/zabbixbindings2cmdbuild/appsettings.json` | `src/zabbixbindings2cmdbuild/appsettings.Development.json` | mounted `/app/appsettings.Production.json` или env `Kafka__...`, `Cmdbuild__...` |
| `monitoring-ui-api` | `src/monitoring-ui-api/config/appsettings.json` | `src/monitoring-ui-api/config/appsettings.Development.json` | mounted `/app/config/appsettings.Production.json`, env `CMDBUILD_BASE_URL`, `ZABBIX_API_ENDPOINT`, `RULES_*`, `MONITORING_UI_*` |

`appsettings.Development.json` предназначены для локального стенда разработки. В контейнерах `localhost` внутри сервиса означает сам контейнер, поэтому для Kafka/CMDBuild/Zabbix чаще нужны Docker-network имена или host gateway URL.

.NET-сервисы используют стандартные env overrides с `__`:

```bash
Kafka__Input__BootstrapServers=kafka:29092
Kafka__Output__BootstrapServers=kafka:29092
Cmdbuild__BaseUrl=http://cmdbuild:8080/cmdbuild/services/rest/v3
Zabbix__ApiEndpoint=http://zabbix-web:8080/api_jsonrpc.php
```

UI/BFF использует `config/appsettings.json`, затем `config/appsettings.${NODE_ENV}.json`, затем `state/ui-settings.json`, затем env overrides. В Dockerfile для UI задано `NODE_ENV=Production`; если нужен файл override, монтируйте его как `/app/config/appsettings.Production.json`.

## Indeed PAM/AAPM secret provider

Все микросервисы и `monitoring-ui-api` поддерживают корпоративное хранилище сервисных секретов через секцию `Secrets`.
По умолчанию provider выключен:

```json
"Secrets": {
  "Provider": "None",
  "References": {},
  "IndeedPamAapm": {
    "BaseUrl": "",
    "PasswordEndpointPath": "/sc_aapm_ui/rest/aapm/password",
    "ApplicationToken": "",
    "ApplicationTokenFile": "",
    "ApplicationUsername": "",
    "ApplicationPassword": "",
    "DefaultAccountPath": "",
    "SendApplicationCredentialsInQuery": false,
    "ResponseType": "json",
    "ValueJsonPath": "password",
    "PasswordExpirationInMinute": "30",
    "PasswordChangeRequired": false,
    "Comment": "cmdb2monitoring {service} {secretId}",
    "TenantId": "",
    "TimeoutMs": 10000
  }
}
```

Чтобы использовать Indeed PAM/AAPM:

```json
"Secrets": {
  "Provider": "IndeedPamAapm",
  "References": {
    "cmdbuild-resolver-password": {
      "AccountPath": "/cmdb2monitoring/cmdbuild",
      "AccountName": "cmdbuild-resolver",
      "ValueJsonPath": "password"
    },
    "zabbix-api-token": {
      "AccountPath": "/cmdb2monitoring/zabbix",
      "AccountName": "zabbix-api-token",
      "ValueJsonPath": "password"
    }
  },
  "IndeedPamAapm": {
    "BaseUrl": "https://pam.example.org",
    "PasswordEndpointPath": "/sc_aapm_ui/rest/aapm/password",
    "ApplicationTokenFile": "/run/secrets/indeed-pam-aapm-token",
    "ApplicationUsername": "",
    "ApplicationPassword": "",
    "DefaultAccountPath": "",
    "SendApplicationCredentialsInQuery": false,
    "ResponseType": "json",
    "ValueJsonPath": "password",
    "PasswordExpirationInMinute": "30",
    "PasswordChangeRequired": false,
    "Comment": "cmdb2monitoring {service} {secretId}",
    "TenantId": "",
    "TimeoutMs": 10000
  }
}
```

В чувствительном поле вместо значения пишется ссылка:

```json
"Cmdbuild": {
  "Username": "cmdbuild-resolver",
  "Password": "secret://cmdbuild-resolver-password"
}
```

Поддерживаются ссылки `secret://id` и `aapm://id`; `id` может быть описан в `Secrets:References` или задан как `AccountPath.AccountName`/`AccountPath/AccountName`.
На старте сервиса значение запрашивается из AAPM и подставляется только в память процесса. В Docker image и production config остается ссылка, а не пароль.

Практические правила:
- `ApplicationToken`/`ApplicationTokenFile` или `ApplicationUsername`/`ApplicationPassword` - bootstrap-секрет доступа приложения к AAPM; его надо передавать через Docker/Kubernetes secret, PAM env или другой защищенный mount;
- `PasswordEndpointPath` оставлен настраиваемым, потому что конкретный URL AAPM может отличаться между версиями/публикациями Indeed PAM;
- если AAPM возвращает plain text, задайте `ResponseType` не равным `json`;
- если AAPM возвращает JSON с другим полем, задайте `ValueJsonPath`, например `result.password`;
- `monitoring-ui-api` при показе runtime settings возвращает `secret://id`, а не фактическое значение секрета;
- изменение `secret://id` через UI применяется сразу для UI/BFF, для .NET-микросервисов - после перезапуска или перечитывания их конфигурации deployment-слоем.

Корпоративный формат env aliases поддержан для всех .NET-микросервисов и `monitoring-ui-api`:

```bash
PAMURL=https://pam.localhost
PAMUSERNAME=MS_PRO
PAMPASSWORD='*****'

SASLUSERNAME=MS_SUN
SASLPASSWORD=
SASLPASSWORDSECRET=AAA.LOCAL\PROD.contractorProfiles
```

Если задан `PAMURL` вместе с `PAMUSERNAME`/`PAMPASSWORD` или `PAMTOKEN`, а `Secrets:Provider=None`, provider автоматически считается `IndeedPamAapm`. `SASLUSERNAME` заполняет пустые Kafka SASL username-поля, `SASLPASSWORD` - пустые password-поля, а `SASLPASSWORDSECRET` превращается в `secret://...`. В примере `AAA.LOCAL\PROD.contractorProfiles` разбирается по последней точке как `AccountPath=AAA.LOCAL\PROD` и `AccountName=contractorProfiles`. Явно заданные поля вида `Kafka__Input__Password` имеют приоритет и не перезаписываются alias-переменными.

Для любого чувствительного поля есть два равнозначных способа указать PAM/AAPM secret.

Прямая ссылка в целевом поле:

```bash
Kafka__Input__Password=secret://AAA.LOCAL\PROD.contractorProfiles
Zabbix__ApiToken=secret://AAA.LOCAL\PROD.zabbixApiToken
Service__RulesReloadToken=secret://AAA.LOCAL\PROD.rulesReloadToken
AuditStorage__ConnectionString=secret://AAA.LOCAL\PROD.auditStorageConnection
```

Companion-поле с суффиксом `Secret`, если целевое поле существует и пустое:

```bash
Kafka__Input__Password=
Kafka__Input__PasswordSecret=AAA.LOCAL\PROD.contractorProfiles

Zabbix__ApiToken=
Zabbix__ApiTokenSecret=AAA.LOCAL\PROD.zabbixApiToken

Service__RulesReloadToken=
Service__RulesReloadTokenSecret=AAA.LOCAL\PROD.rulesReloadToken

AuditStorage__ConnectionString=
AuditStorage__ConnectionStringSecret=AAA.LOCAL\PROD.auditStorageConnection
```

Общее правило: `<ИмяПоля>Secret` заполняет пустое `<ИмяПоля>` значением `secret://...`. Например, `PasswordSecret` заполняет `Password`, `ApiTokenSecret` заполняет `ApiToken`, `RulesReloadTokenSecret` заполняет `RulesReloadToken`, `ConnectionStringSecret` заполняет `ConnectionString`. Companion-поле применяется только если рядом уже есть целевое поле без `Secret`; это защищает обычные поля вроде `OAuth2:ClientSecret` от ошибочной интерпретации как ссылка на несуществующее поле `OAuth2:Client`.

Типовые поля, которые можно заменить на `secret://id`:

| Сервис | Поля |
| --- | --- |
| `cmdbwebhooks2kafka` | `Kafka:Password`, `ElkLogging:Kafka:Password`, `ElkLogging:Elk:ApiKey` |
| `cmdbkafka2zabbix` | `Cmdbuild:Password`, `Service:RulesReloadToken`, `Service:RulesStatusToken`, `Kafka:Input:Password`, `Kafka:Output:Password`, `ElkLogging:Kafka:Password`, `ElkLogging:Elk:ApiKey` |
| `zabbixrequests2api` | `Zabbix:ApiToken`, `Zabbix:Password`, `Kafka:Input:Password`, `Kafka:Output:Password`, `Kafka:BindingOutput:Password`, `ElkLogging:Kafka:Password`, `ElkLogging:Elk:ApiKey` |
| `zabbixbindings2cmdbuild` | `Cmdbuild:Password`, `Kafka:Input:Password`, `ElkLogging:Kafka:Password`, `ElkLogging:Elk:ApiKey` |
| `monitoring-ui-api` | `Zabbix:ApiToken`, `EventBrowser:Password`, `Idp:OAuth2:ClientSecret`, `Idp:Ldap:BindPassword`, `AuditStorage:ConnectionString`, `Services:HealthEndpoints[].RulesReloadToken`, `Services:HealthEndpoints[].RulesStatusToken` |

## Kafka topics и ACL

Topics создаются внешней инфраструктурой до запуска сервисов. Код микросервисов не создает topics при старте.

Основной поток:

| Base/prod topic | Dev topic | Producer | Consumer | Назначение |
| --- | --- | --- | --- | --- |
| `cmdbuild.webhooks` | `cmdbuild.webhooks.dev` | `cmdbwebhooks2kafka` | `cmdbkafka2zabbix` | Нормализованные CMDBuild webhook events |
| `zabbix.host.requests` | `zabbix.host.requests.dev` | `cmdbkafka2zabbix` | `zabbixrequests2api` | Zabbix JSON-RPC requests |
| `zabbix.host.responses` | `zabbix.host.responses.dev` | `zabbixrequests2api` | UI/Event Browser или внешний consumer | Результаты обработки Zabbix API |
| `zabbix.host.bindings` | `zabbix.host.bindings.dev` | `zabbixrequests2api` | `zabbixbindings2cmdbuild` | Reverse binding `CMDBuild card/profile -> Zabbix hostid` |

Log topics нужны, если `ElkLogging:Enabled=true`, `ElkLogging:Mode=Kafka` и `ElkLogging:Kafka:Enabled=true`:

| Base/prod topic | Dev topic | Producer |
| --- | --- | --- |
| `cmdbwebhooks2kafka.logs` | `cmdbwebhooks2kafka.logs.dev` | `cmdbwebhooks2kafka` |
| `cmdbkafka2zabbix.logs` | `cmdbkafka2zabbix.logs.dev` | `cmdbkafka2zabbix` |
| `zabbixrequests2api.logs` | `zabbixrequests2api.logs.dev` | `zabbixrequests2api` |
| `zabbixbindings2cmdbuild.logs` | `zabbixbindings2cmdbuild.logs.dev` | `zabbixbindings2cmdbuild` |

Минимальные Kafka ACL при включенной авторизации:

| Principal/service | Topic ACL | Group ACL |
| --- | --- | --- |
| `cmdbwebhooks2kafka` | `WRITE`, `DESCRIBE` на `cmdbuild.webhooks`; `WRITE`, `DESCRIBE` на log topic при Kafka logging | Не требуется |
| `cmdbkafka2zabbix` | `READ`, `DESCRIBE` на `cmdbuild.webhooks`; `WRITE`, `DESCRIBE` на `zabbix.host.requests`; `WRITE`, `DESCRIBE` на log topic | `READ` на group `cmdbkafka2zabbix` |
| `zabbixrequests2api` | `READ`, `DESCRIBE` на `zabbix.host.requests`; `WRITE`, `DESCRIBE` на `zabbix.host.responses` и `zabbix.host.bindings`; `WRITE`, `DESCRIBE` на log topic | `READ` на group `zabbixrequests2api` |
| `zabbixbindings2cmdbuild` | `READ`, `DESCRIBE` на `zabbix.host.bindings`; `WRITE`, `DESCRIBE` на log topic | `READ` на group `zabbixbindings2cmdbuild` |
| `monitoring-ui-api` | При `EventBrowser:Enabled=true`: `READ`, `DESCRIBE` на просматриваемые topics | `READ` на ephemeral groups с prefix `monitoring-ui-api-events-` или на настроенный `EventBrowser:ClientId` prefix |

## Секреты и учетные записи по сервисам

Секреты задаются через env/secret storage или mounted config, исключенный из git. Не встраивайте их в Docker image.

| Сервис | Секреты/учетки | Config/env | Для чего используется |
| --- | --- | --- | --- |
| `cmdbwebhooks2kafka` | Kafka SASL user/password | `Kafka__Username`, `Kafka__Password`, `ElkLogging__Kafka__Username`, `ElkLogging__Kafka__Password` | Публикация CMDBuild events и logs в Kafka |
| `cmdbwebhooks2kafka` | ELK API key, если используется прямой ELK sink | `ElkLogging__Elk__ApiKey` | Запись logs в ELK |
| `cmdbwebhooks2kafka` | Встроенный webhook Bearer token отсутствует | Нет встроенного config-поля | Входящий webhook защищается сетью, reverse proxy или внешним gateway, если это требуется политикой безопасности |
| `cmdbkafka2zabbix` | CMDBuild service login/password | `Cmdbuild__Username`, `Cmdbuild__Password` | Чтение карточек, attributes, lookup/reference/domain leaves и `ZabbixHostBinding` |
| `cmdbkafka2zabbix` | Rules reload/status Bearer tokens | `Service__RulesReloadToken`, `Service__RulesStatusToken` | Защита `/admin/reload-rules` и `/admin/rules-status`; эти же значения настраиваются в UI `Services:HealthEndpoints` |
| `cmdbkafka2zabbix` | Kafka SASL user/password | `Kafka__Input__Username`, `Kafka__Input__Password`, `Kafka__Output__Username`, `Kafka__Output__Password`, `ElkLogging__Kafka__Username`, `ElkLogging__Kafka__Password` | Чтение CMDBuild events, публикация Zabbix requests и logs |
| `zabbixrequests2api` | Zabbix API token или login/password | `Zabbix__ApiToken` или `Zabbix__User`, `Zabbix__Password`, `Zabbix__AuthMode` | Вызовы Zabbix JSON-RPC |
| `zabbixrequests2api` | Kafka SASL user/password | `Kafka__Input__Username`, `Kafka__Input__Password`, `Kafka__Output__Username`, `Kafka__Output__Password`, `Kafka__BindingOutput__Username`, `Kafka__BindingOutput__Password`, `ElkLogging__Kafka__Username`, `ElkLogging__Kafka__Password` | Чтение requests, публикация responses/bindings/logs |
| `zabbixbindings2cmdbuild` | CMDBuild service login/password | `Cmdbuild__Username`, `Cmdbuild__Password` | Запись `zabbix_main_hostid` и карточек `ZabbixHostBinding` |
| `zabbixbindings2cmdbuild` | Kafka SASL user/password | `Kafka__Input__Username`, `Kafka__Input__Password`, `ElkLogging__Kafka__Username`, `ElkLogging__Kafka__Password` | Чтение binding events и публикация logs |
| `monitoring-ui-api` | Local UI users file | `MONITORING_UI_USERS_FILE`, `Auth:UsersFilePath` | Локальные пользователи UI; файл содержит PBKDF2 hashes/salts |
| `monitoring-ui-api` | Zabbix API token для UI catalog/audit | `ZABBIX_API_TOKEN`, `Zabbix:ApiToken` | Чтение Zabbix catalog/metadata/audit без запроса login/password у пользователя |
| `monitoring-ui-api` | Session CMDBuild/Zabbix login/password | Вводятся пользователем в UI | Хранятся только в server-side session и используются для операций UI с CMDBuild/Zabbix |
| `monitoring-ui-api` | Kafka Event Browser SASL user/password | `MONITORING_UI_KAFKA_USERNAME`, `MONITORING_UI_KAFKA_PASSWORD` | Read-only просмотр Kafka topics |
| `monitoring-ui-api` | OAuth2 client secret | `OAUTH2_CLIENT_SECRET` | Внешний вход через OAuth2/OIDC |
| `monitoring-ui-api` | LDAP bind password | `LDAP_BIND_PASSWORD` | MS AD/LDAP bind и чтение групп для назначения ролей |
| `monitoring-ui-api` | SAML SP private key/cert и IdP cert | `SAML2_SP_PRIVATE_KEY_PATH`, `SAML2_SP_CERT_PATH`, `SAML2_IDP_CERT_PATH` или inline env | SAML2 login, подпись/расшифровка при включении соответствующих режимов |
| `monitoring-ui-api` | Audit DB password | `AUDIT_STORAGE_CONNECTION_STRING` | PostgreSQL/SQLite audit storage; для SQLite password не нужен |
| `monitoring-ui-api` | Rules reload/status tokens converter-а | `Services:HealthEndpoints[].RulesReloadToken`, `Services:HealthEndpoints[].RulesStatusToken` | Нажатие `Перечитать правила конвертации` и чтение версии rules на `cmdbkafka2zabbix` |

Любой из перечисленных секретов можно оставить как `secret://id`, если `Secrets:Provider=IndeedPamAapm`; `id` может быть описан в `Secrets:References` или разобран как `AccountPath.AccountName`/`AccountPath/AccountName`.
Если `ConversionRules:ReadFromGit=true` и git repository приватный, credentials для git должны передаваться deployment-слоем: mounted SSH key, token в credential helper или read-only deploy key. В appsettings хранится URL/path, а не пароль к git.

## Права во внешних системах

CMDBuild:

| Кто обращается | Минимальные права |
| --- | --- |
| `cmdbkafka2zabbix` | Read-only REST к metadata classes/attributes/domains, lookup types/values, карточкам участвующих классов, reference/domain связанным карточкам, relations; read к `zabbix_main_hostid` и `ZabbixHostBinding` при `HostBindingLookupEnabled=true` |
| `zabbixbindings2cmdbuild` | Read/update карточек участвующих классов для `zabbix_main_hostid`; read/create/update служебного класса `ZabbixHostBinding` |
| `monitoring-ui-api` catalog/rules/audit | Read-only к metadata, lookup values, relations и карточкам выбранных классов; для Quick audit также read к `ZabbixHostBinding` |
| `monitoring-ui-api` Webhook Setup | Read к ETL/webhook records для `Загрузить из CMDB`; create/update/delete к ETL/webhook records для `Загрузить в CMDB` и `Удалить выбранные` |
| `monitoring-ui-api` Audit model preparation | Права администратора модели CMDBuild на создание attributes/classes, включая `zabbix_main_hostid` и `ZabbixHostBinding` |
| CMDBuild webhook caller | Сетевой доступ из CMDBuild к `cmdbwebhooks2kafka` route `/webhooks/cmdbuild`; отдельный CMDBuild login сервису не нужен, потому что CMDBuild сам вызывает webhook |

Zabbix:

| Кто обращается | Минимальные права |
| --- | --- |
| `monitoring-ui-api` catalog/metadata/audit | API read к `hostgroup.get`, `templategroup.get`, `template.get` с subselects, `host.get`, `proxy.get`, `proxygroup.get`, `globalmacro.get`, `usermacro.get`, `maintenance.get`, `valuemap.get`; Quick audit использует `host.get` и `maintenance.get` |
| `zabbixrequests2api` базовый host flow | API read к `host.get`, `hostgroup.get`, `template.get`; write к `host.create`, `host.update`, `host.delete` |
| `zabbixrequests2api` dynamic host groups | Дополнительно `hostgroup.create`, если `Zabbix:AllowDynamicHostGroupCreate=true` и rules используют dynamic host groups из CMDBuild leaf |
| `zabbixrequests2api` расширенные rules | Права должны соответствовать JSON-RPC methods, которые реально генерируют rules/T4, например `maintenance.create/update/delete`, если такие операции включены в rules |

MS AD/LDAP/IdP:

| Кто обращается | Минимальные права |
| --- | --- |
| `monitoring-ui-api` LDAP/MS AD | Bind от имени service account, чтение user attributes и group membership attributes, используемых в `Idp:Ldap:*` и `Idp:RoleMapping` |
| `monitoring-ui-api` SAML2/OAuth2 | Зарегистрированный service provider/client, redirect/ACS URL UI, чтение claims login/email/displayName/groups или возможность lookup групп через LDAP |

## State, rules и volume

Минимально вынесите в volume:

| Компонент | Что вынести |
| --- | --- |
| `cmdbkafka2zabbix` | `/app/state`, rules-файл или git working copy с rules |
| `zabbixrequests2api` | `/app/state` |
| `zabbixbindings2cmdbuild` | `/app/state` |
| `monitoring-ui-api` | `/app/state`, `/app/data`, rules working copy при использовании `Настройка git` |

Для converter в контейнере типовой rules override:

```bash
ConversionRules__RepositoryPath=/app
ConversionRules__RulesFilePath=rules/cmdbuild-to-zabbix-host-create.json
ConversionRules__ReadFromGit=false
```

Если rules читаются из git working copy:

```bash
ConversionRules__ReadFromGit=true
ConversionRules__RepositoryPath=/app/rules-git-working-copy
ConversionRules__RepositoryUrl=https://git.example.org/cmdb2monitoring/conversion-rules.git
ConversionRules__RulesFilePath=rules/cmdbuild-to-zabbix-host-create.json
ConversionRules__PullOnStartup=true
ConversionRules__PullOnReload=true
```

Внутри repository ожидается файл `rules/cmdbuild-to-zabbix-host-create.json`, если `RulesFilePath`/`ConversionRules:RulesFilePath` не переопределен.

## Учетные записи по умолчанию

| Система | Login/password | Назначение |
| --- | --- | --- |
| UI local users | `viewer/viewer`, `editor/editor`, `admin/admin` | Создаются при первом старте, если отсутствует `state/users.json`; пароли хранятся как PBKDF2-SHA256 hash/salt |
| CMDBuild dev стенд | `admin/admin` | Только тестовая среда |
| Zabbix dev стенд | `Admin/zabbix` | Только тестовая среда |
| Kafka dev стенд | без логина/пароля | PLAINTEXT Kafka в локальном Docker |

В production начальные UI-пароли нужно сменить после первого входа или заранее смонтировать подготовленный `state/users.json`. CMDBuild/Zabbix login/password не хранятся в UI runtime state: UI спрашивает их на server-side session при первой операции, а для Zabbix может использовать `Zabbix:ApiToken`. Сервисные учетные записи `cmdbkafka2zabbix`, `zabbixrequests2api`, `zabbixbindings2cmdbuild` задаются через env/secret, а не через стартовые UI-пользователи.

## Пример запуска одного сервиса

```bash
docker run --rm \
  --name cmdbkafka2zabbix \
  -p 5081:8080 \
  -v "$PWD/state/cmdbkafka2zabbix:/app/state" \
  -v "$PWD/rules:/app/rules:ro" \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e Kafka__Input__BootstrapServers=kafka:29092 \
  -e Kafka__Output__BootstrapServers=kafka:29092 \
  -e ConversionRules__RepositoryPath=/app \
  -e ConversionRules__RulesFilePath=rules/cmdbuild-to-zabbix-host-create.json \
  -e Cmdbuild__BaseUrl=http://cmdbuild:8080/cmdbuild/services/rest/v3 \
  -e Cmdbuild__Username='<secret>' \
  -e Cmdbuild__Password='<secret>' \
  localhost:5000/cmdb2monitoring/cmdbkafka2zabbix:0.8.0
```

## Smoke-проверка

После запуска проверьте:

```bash
curl http://localhost:5080/health
curl http://localhost:5081/health
curl http://localhost:5082/health
curl http://localhost:5083/health
curl http://localhost:5090/health
```

Затем в UI:

1. Войдите `admin/admin` и смените пароль.
2. Заполните `Runtime-настройки`: CMDBuild URL, Zabbix API URL/API token, Kafka Events, AuditStorage.
3. Проверьте `Настройка git` или путь к локальному rules-файлу.
4. Синхронизируйте CMDBuild catalog, Zabbix catalog и Zabbix metadata.
5. Нажмите `Перечитать правила конвертации` и сравните версию rules в UI и на converter.

## Что не коммитить и не встраивать в образ

- `state/users.json`;
- `state/ui-settings.json`;
- `data/*-catalog-cache.json`;
- service state-файлы offsets;
- `appsettings.Production.json` с secret-значениями;
- Zabbix API token, CMDBuild/Zabbix passwords, Kafka SASL passwords, LDAP bind password, webhook Bearer tokens.
