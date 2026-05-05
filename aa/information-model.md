# Информационная модель

## Сущности

| ID | Сущность | Источник | Ключ | Назначение |
| --- | --- | --- | --- | --- |
| IM-001 | AD group | MS AD | `cn` или `ActiveDirectory:GroupNameAttribute` | Source of truth для CMDBuild role membership |
| IM-002 | AD user | MS AD | `sAMAccountName` или `ActiveDirectory:UserLoginAttribute` | Source of truth для активной УЗ, ФИО и email |
| IM-003 | CMDBuild role | CMDBuild REST | `_id`, name/code/description | Target group для CMDBuild user |
| IM-004 | CMDBuild user | CMDBuild REST | `username` | Target учетная запись |
| IM-005 | Sync state | Local FS | `ManagedLogins[]` | Память сервиса о ранее управляемых пользователях |
| IM-006 | Secret reference | Config/PAM | `secret://id` или `aapm://id` | Ссылка на секрет без хранения значения в git |
| IM-007 | ELK log event | Runtime | timestamp/category/eventId | Structured log для optional ELK |

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

## Идемпотентность

- Повторный sync с теми же входными данными не должен менять результат.
- Missing AD group или missing CMDBuild role останавливают весь run до write-операций.
- Bootstrap tool не изменяет существующие AD groups и не добавляет members.
- Dry-run не пишет CMDBuild, AD и state.
