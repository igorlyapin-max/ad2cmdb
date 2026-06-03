# Карта observability

## Каналы Логирования

| ID | Канал | Кто отправляет | Когда используется | Примечание |
| --- | --- | --- | --- | --- |
| OBS-001 | Console stdout/stderr | `adgroups2cmdbuild` через стандартный .NET logging | Всегда | Базовый канал для Docker/Kubernetes/platform logs |
| OBS-002 | ELK / Elasticsearch HTTP | `ElkLoggerProvider` внутри сервиса | Только если `ElkLogging:Enabled=true` и `ElkLogging:Endpoint` не пустой | Ошибки отправки не ломают sync-flow |
| OBS-003 | Docker syslog driver | Docker daemon | Если контейнер запущен с `--log-driver=syslog` | Приложение ничего не знает о syslog, Docker пересылает stdout/stderr |
| OBS-004 | Host log agent | Filebeat/Vector/Fluent Bit | Если ELK/центральный collector собирает Docker container logs с узла | Рекомендуемый вариант для платформенной эксплуатации |
| OBS-005 | Sidecar collector | Отдельный контейнер | Если нужна изоляция сборщика логов рядом с сервисом | Полезно в orchestrator-сценариях |

## Monitoring Artifacts

| Platform | Artifact | Signals |
| --- | --- | --- |
| Zabbix | `aa/monitoring/zabbix-adgroups2cmdbuild-template.yaml` | `/health`, `/ready`, `failedUsers`, `lastSucceeded`, stale `lastCompletedUtc` |
| Prometheus | `aa/monitoring/prometheus-json-exporter-adgroups2cmdbuild.yaml`; `aa/monitoring/prometheus-adgroups2cmdbuild-rules.yaml` | blackbox `/health`/`/ready`, json_exporter `/sync/status`, stale/failed/partial alerts |
| Grafana | `aa/monitoring/grafana-adgroups2cmdbuild-dashboard.json` | Health, readiness, last sync age, failed users, last sync success |

## Debug Levels

| Level | Alias | Содержимое |
| --- | --- | --- |
| `Basic` | `1` | Старт worker, старт sync-run, AD/CMDBuild snapshot counts, group counts, CMDBuild page counts, deprovision candidate count, state save decision |
| `Verbose` | `2` | Все из Basic плюс per-user planned create/update/disable, resolved AD login lists по группам и признаки наличия ФИО/email; sensitive values редактируются по умолчанию |

Debug-события пишутся через `ILogger` на уровне `Information`.
Это сделано намеренно: включение `Debug:Enabled=true` не требует менять глобальный `Logging:LogLevel`, а события попадают в console/ELK/syslog тем же путем, что и обычные информационные логи.

## Ключевые Operational Events

| Событие | Уровень | Назначение |
| --- | --- | --- |
| Старт/завершение sync-run | Information | Связать действия сервиса с интервалом синхронизации |
| Transient AD retry | Warning | Видимость LDAP/LDAPS timeout/server down/busy/unavailable |
| Transient CMDBuild retry | Warning | Видимость HTTP `408`, `429`, `5xx`, timeout и network errors |
| Shutdown requested | Information | Понять, что процесс получил SIGTERM/SIGINT и остановил scheduling |
| Shutdown grace expired | Warning | Понять, что активный run был отменен из-за `Sync:ShutdownGracePeriodSeconds` |

## ELK Contract

Если `ElkLogging:Endpoint=https://elastic.example.local:9200` и `ElkLogging:Index=adgroups2cmdbuild-logs`, документ отправляется в:

```text
https://elastic.example.local:9200/adgroups2cmdbuild-logs/_doc
```

Если endpoint уже заканчивается на `/_doc` или `/_bulk`, он используется как есть.

Поля события:

| Field | Значение |
| --- | --- |
| `timestamp` | UTC timestamp события |
| `level` | .NET log level |
| `category` | Logger category |
| `eventId` / `eventName` | Event metadata из `ILogger` |
| `message` | Форматированное сообщение |
| `exception` | Stack trace, если есть |
| `service` | `ElkLogging:ServiceName` |
| `environment` | `ElkLogging:Environment` |

## Syslog Через Docker

Приложение не реализует syslog protocol самостоятельно.
Для syslog используется Docker logging driver:

```bash
docker run \
  --log-driver=syslog \
  --log-opt syslog-address=udp://syslog.example.local:514 \
  --log-opt tag='adgroups2cmdbuild/{{.Name}}' \
  ...
```

Для TCP/TLS:

```bash
--log-opt syslog-address=tcp://syslog.example.local:514
--log-opt syslog-address=tcp+tls://syslog.example.local:6514
```

Ограничение: syslog driver пересылает только stdout/stderr контейнера. Если нужен структурированный JSON в syslog, это решается настройкой logging pipeline или отдельного collector-а.

## Риски

- `Debug:Level=Verbose` вместе с `Debug:LogSensitiveValues=true` пишет логины и состав групп. Не держать постоянно включенным в production.
- ELK endpoint/API key errors не попадают в `lastError`, потому что logging не должен ломать синхронизацию.
- Если нужен гарантированный audit trail, его нужно проектировать отдельно; текущие логи являются operational telemetry.
