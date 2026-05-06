# Карта healthcheck

## Endpoints

| ID | Endpoint | Method | Назначение |
| --- | --- | --- | --- |
| HLT-001 | `/health` | GET | Проверка процесса и публикация текущего sync status |
| HLT-002 | `/sync/status` | GET | Детальный статус последнего sync-run |

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
      "dryRun": false
    }
  }
}
```

## Операционные Алерты

Рекомендуемые условия алертов:

| Условие | Severity | Комментарий |
| --- | --- | --- |
| HTTP `/health` недоступен | Critical | Процесс не отвечает |
| `lastSucceeded=false` | Warning/Critical | Последний sync-run завершился ошибкой |
| `lastCompletedUtc` старше 2 интервалов sync | Warning | Worker завис или не запускается |
| `lastSummary.dryRun=true` в production | Warning | Изменения не применяются |
| Debug `Verbose` включен дольше диагностического окна | Info/Warning | Может писать логины и состав групп |

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
