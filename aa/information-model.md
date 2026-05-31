# Информационная модель

## Сущности

| ID | Сущность | Источник | Ключ | Назначение |
| --- | --- | --- | --- | --- |
| IM-001 | AD group | MS AD | `cn` или `ActiveDirectory:GroupNameAttribute` | Source of truth для CMDBuild role membership |
| IM-002 | AD user | MS AD | `sAMAccountName` или `ActiveDirectory:UserLoginAttribute` | Source of truth для активной УЗ, ФИО и email |
| IM-003 | CMDBuild role | CMDBuild REST | `_id`, name/code/description | Target group для CMDBuild user |
| IM-004 | CMDBuild user | CMDBuild REST | `username` | Target учетная запись |
| IM-005 | Sync state | Local FS | `ManagedLogins[]` | Память сервиса о ранее управляемых пользователях |
| IM-006 | Sync state backup | Local FS | `.bak` | Последний backup state перед заменой |
| IM-007 | Sync run lock | Local FS | lock file | Защита от двух sync-run на одном хосте |
| IM-008 | Secret reference | Config/PAM | `secret://id` или `aapm://id` | Ссылка на секрет без хранения значения в git |
| IM-009 | ELK log event | Runtime | timestamp/category/eventId | Structured log для optional ELK |
| IM-010 | Debug diagnostic event | Runtime | log category + message template | Diagnostic событие Basic/Verbose, отправляется через обычный logging pipeline |

## AD User Mapping

| AD attribute | CMDBuild field | Config |
| --- | --- | --- |
| login | `username` | `ActiveDirectory:UserLoginAttribute`, default `sAMAccountName` |
| ФИО | `description` | `ActiveDirectory:UserDisplayNameAttribute` -> `Cmdbuild:UserDisplayNameField` |
| email | `email` | `ActiveDirectory:UserEmailAttribute` -> `Cmdbuild:UserEmailField` |

## Group Mapping

| Источник | Цель | Правило |
| --- | --- | --- |
| AD group name | CMDBuild role name | Строковое совпадение без учета регистра |
| AD membership | CMDBuild `userGroups[]` | Пользователь получает role, если он состоит в одноименной AD group |
| `ProvisioningGroupName` | CMDBuild `active=true` | Только пользователи этой group имеют активную УЗ |

## State Ownership

`state/adgroups2cmdbuild-state.json` принадлежит сервису.
Оператор не должен редактировать его вручную, кроме аварийного восстановления по отдельному плану.

State не является источником прав. Он нужен, чтобы безопасно определить ранее управляемых пользователей, которые исчезли из AD groups.
Перед заменой state сервис сохраняет `state/adgroups2cmdbuild-state.json.bak`.
`state/adgroups2cmdbuild.lock` удерживается во время sync-run и защищает только от параллельных процессов на одном хосте; active-active deployment не поддерживается.

## Идемпотентность

- Повторный sync с теми же входными данными не должен менять результат.
- Missing AD group или missing CMDBuild role останавливают весь run до write-операций.
- Ошибка отдельной CMDBuild user operation не останавливает batch; успешные операции сохраняются в state, failed operations отражаются в `failedUsers`.
- Bootstrap tool не изменяет существующие AD groups и не добавляет members.
- Dry-run не пишет CMDBuild, AD и state.

## Чувствительность Логов

| Тип данных | Где может появиться | Уровень |
| --- | --- | --- |
| Login пользователя | create/update/disable план, resolved group members | `Debug:Level=Verbose`, raw values только при `Debug:LogSensitiveValues=true` |
| Состав групп | resolved AD login lists | `Debug:Level=Verbose`, raw values только при `Debug:LogSensitiveValues=true` |
| Количество пользователей/групп | snapshot/group counters | `Debug:Level=Basic` |
| Секреты | Не должны логироваться | Никогда |

Секреты не пишутся в logs. Для проверки наличия ФИО/email verbose logs пишут только boolean-признаки, не значения атрибутов.
