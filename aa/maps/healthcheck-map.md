# Карта healthcheck

## Endpoints

| ID | Endpoint | Method | Назначение |
| --- | --- | --- | --- |
| HLT-001 | `/health` | GET | Проверка процесса и публикация текущего sync status |
| HLT-002 | `/ready` | GET | Readiness; shallow или dependency checks по настройке |
| HLT-003 | `/sync/status` | GET | Детальный статус последнего sync-run |

## `/health`

Ответ содержит:
- `service`;
- `status`;
- `sync`.

Пример:

```json
{
  "service": "adgroups2cmdbuild",
  "status": "ok",
  "sync": {
    "isRunning": false,
    "lastStartedUtc": "2026-05-05T12:00:00Z",
    "lastCompletedUtc": "2026-05-05T12:00:02Z",
    "lastSucceeded": true,
    "lastError": null,
    "lastSummary": {
      "adUsers": 120,
      "provisionedUsers": 118,
      "createdUsers": 1,
      "updatedUsers": 117,
      "disabledUsers": 2,
      "skippedUsers": 0,
      "failedUsers": 0,
      "dryRun": false
    }
  }
}
```

## `/ready`

По умолчанию возвращает shallow readiness без обращения к AD/CMDBuild.
Если `Readiness:CheckDependencies=true`, endpoint выполняет LDAP bind и lightweight CMDBuild REST call с timeout `Readiness:TimeoutMs`.
При ошибке dependency возвращается HTTP `503`.

Все status endpoints могут вернуть HTTP `429`, если превышен `EndpointRateLimiting`.

## Операционные Алерты

Рекомендуемые условия алертов:

| Условие | Severity | Комментарий |
| --- | --- | --- |
| HTTP `/health` недоступен | Critical | Процесс не отвечает |
| `lastSucceeded=false` | Warning/Critical | Последний sync-run завершился ошибкой |
| `lastSummary.failedUsers > 0` | Warning | Batch завершился с partial failure по пользователям |
| `lastError` содержит cancellation после остановки | Info/Warning | Активный run был отменен после истечения shutdown grace-period |
| `lastCompletedUtc` старше 2 интервалов sync | Warning | Worker завис или не запускается |
| `lastSummary.dryRun=true` в production | Warning | Изменения не применяются |
| Debug `Verbose` + `LogSensitiveValues=true` включен дольше диагностического окна | Info/Warning | Может писать логины и состав групп |

## Bootstrap Tool

`bootstrap-ad-groups` не имеет health endpoint, так как это one-shot deployment tool.
Успешность определяется exit code:

| Exit code | Значение |
| --- | --- |
| `0` | План построен или apply выполнен |
| `2` | Нет выбранных roles или нарушена защита apply |
| другое | Ошибка конфигурации, CMDBuild или AD |

## Observability

Подробная схема логирования описана в [observability-map.md](observability-map.md).
Health endpoints не проверяют доставку логов в ELK или syslog. Это намеренно: проблемы telemetry не должны делать сервис неготовым к синхронизации.
