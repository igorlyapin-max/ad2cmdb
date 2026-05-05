# Карта доступов

## Внешние подключения

| ID | Откуда | Куда | Protocol | Port | Назначение |
| --- | --- | --- | --- | --- | --- |
| ACC-001 | `adgroups2cmdbuild` | MS AD DC | LDAP | `389` | Чтение AD groups/users, если `UseSsl=false` |
| ACC-002 | `adgroups2cmdbuild` | MS AD DC | LDAPS | `636` | Чтение AD groups/users, если `UseSsl=true` |
| ACC-003 | `adgroups2cmdbuild` | CMDBuild | HTTP/HTTPS REST v3 | deployment-specific | Чтение roles/users, create/update/disable users |
| ACC-004 | `adgroups2cmdbuild` | Indeed PAM/AAPM | HTTPS | `443` | Получение секретов по `secret://...` |
| ACC-005 | `adgroups2cmdbuild` | ELK / Elasticsearch | HTTPS | deployment-specific | Optional structured logs |
| ACC-006 | `bootstrap-ad-groups` | CMDBuild | HTTP/HTTPS REST v3 | deployment-specific | Чтение CMDBuild roles |
| ACC-007 | `bootstrap-ad-groups` | MS AD DC | LDAP/LDAPS | `389`/`636` | Поиск и создание AD groups |

## Минимальные Права

MS AD bind account для сервиса:
- read groups в `ActiveDirectory:GroupSearchBaseDn`;
- read group `member`;
- read user attributes: login, displayName, mail, userAccountControl, objectClass.

MS AD bind account для bootstrap tool:
- read groups в `ActiveDirectory:GroupSearchBaseDn`;
- create group objects в `BootstrapAdGroups:TargetOuDn`;
- write attributes `cn`, `sAMAccountName`, `groupType`, optional `description`.

CMDBuild account для сервиса:
- read roles;
- read users;
- create users;
- update users, включая `active`, `userGroups`, `defaultUserGroup`, ФИО и email fields.

CMDBuild account для bootstrap tool:
- read roles.

PAM/AAPM bootstrap credentials:
- право читать только secret IDs, нужные этому deployment.

ELK credentials:
- право писать documents в configured index.
