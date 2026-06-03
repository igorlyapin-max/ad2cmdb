# Интеграция с CMDBuild REST API

Документ фиксирует контракт `adgroups2cmdbuild` с CMDBuild REST API v3.
Это не полный справочник CMDBuild, а перечень endpoints, прав и особенностей, которые использует этот репозиторий.

Base URL задается через `Cmdbuild:BaseUrl`, например:

```text
https://cmdbuild.example/cmdbuild/services/rest/v3
```

В `Production` URL должен быть HTTPS.

## Где используется CMDBuild REST API

| Компонент | Зачем ходит в CMDBuild |
| --- | --- |
| `adgroups2cmdbuild` | Читает roles/users, создает пользователей, обновляет активность и группы |
| `bootstrap-ad-groups` | Читает roles, чтобы построить план создания одноименных AD groups |

## Авторизация

Оба компонента используют CMDBuild service account из config/env/PAM.
Credentials не хранятся в git.

HTTP headers:

```text
Accept: application/json
Authorization: Basic <base64(username:password)>
Content-Type: application/json
CMDBuild-View: admin
```

`Content-Type` отправляется только для запросов с body.

## Endpoints

### Role Read

Используется сервисом и bootstrap tool.

| Method | Path | Назначение |
| --- | --- | --- |
| `GET` | `/roles?limit={n}&offset={m}&detailed=true` | Пакетное чтение roles |
| `GET` | `/roles?limit=1&offset=0&detailed=false` | Lightweight readiness check |

Role name берется из первого непустого поля, заданного в `Cmdbuild:RoleNameFields`.
По умолчанию используются `name`, `code`, `description`.

### User Read

Используется сервисом перед каждым sync-run.

| Method | Path | Назначение |
| --- | --- | --- |
| `GET` | `/users?limit={n}&start={m}&detailed=true` | Пакетное чтение users и групп |

Сервис читает:

- `_id` или `id`;
- `username`;
- `active`;
- `userGroups[]`;
- поле ФИО из `Cmdbuild:UserDisplayNameField`, default `description`;
- поле email из `Cmdbuild:UserEmailField`, default `email`.

### User Write

Используется только основным сервисом.

| Method | Path | Назначение |
| --- | --- | --- |
| `POST` | `/users` | Создать отсутствующего CMDBuild user |
| `PUT` | `/users/{userId}` | Обновить user fields, active state и `userGroups` |

При создании пользователя сервис отправляет generated password, если `Cmdbuild:NewUserPassword` пустой.
Это рассчитано на установки, где фактический вход выполняется через внешнюю аутентификацию.

При блокировке пользователя сервис отправляет:

```json
{
  "active": false,
  "multiGroup": true,
  "userGroups": [],
  "defaultUserGroup": null
}
```

## Mapping

| Source | Target | Rule |
| --- | --- | --- |
| AD login | CMDBuild `username` | `ActiveDirectory:UserLoginAttribute`, default `sAMAccountName` |
| AD display name | CMDBuild display field | `Cmdbuild:UserDisplayNameField`, default `description` |
| AD mail | CMDBuild email field | `Cmdbuild:UserEmailField`, default `email` |
| AD group name | CMDBuild role name | Case-insensitive string match |
| Provisioning group membership | CMDBuild `active=true` | Only users in `ActiveDirectory:ProvisioningGroupName` stay active |

Every configured AD group must have a CMDBuild role with the same name.
If any configured role is missing, the sync-run fails before write operations.

## Error Handling

- CMDBuild HTTP `408`, `429`, `5xx`, timeout and network failures are retried with bounded exponential backoff and jitter.
- `401`, `403` and other permanent failures are not retried.
- A failed operation for one user does not stop the whole batch; `/sync/status` reports `lastSummary.failedUsers`.
- Dry-run mode does not call CMDBuild write endpoints.

## Required Rights

CMDBuild service account for `adgroups2cmdbuild`:

- read roles;
- read users;
- create users;
- update users, including `active`, `userGroups`, `defaultUserGroup`, display-name field and email field.

CMDBuild service account for `bootstrap-ad-groups`:

- read roles.

## Operational Notes

- Do not write directly to the CMDBuild database; this integration uses REST API only.
- Keep `Cmdbuild:UsersPageSize` and `Cmdbuild:RolesPageSize` positive and adjust them if the CMDBuild installation is very large.
- Use `Readiness:CheckDependencies=true` in production so `/ready` performs a lightweight CMDBuild call.
- Store `Cmdbuild:Password` through env, mounted secret, or PAM/AAPM `secret://...`; never commit real credentials.
