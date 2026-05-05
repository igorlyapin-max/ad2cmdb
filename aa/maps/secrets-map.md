# Карта секретов

## Секретные Поля

| ID | Config path | Назначение | Рекомендуемый источник |
| --- | --- | --- | --- |
| SEC-001 | `ActiveDirectory:BindPassword` | Пароль AD bind account | PAM/AAPM `secret://...` |
| SEC-002 | `Cmdbuild:Password` | Пароль CMDBuild service account | PAM/AAPM `secret://...` |
| SEC-003 | `Cmdbuild:NewUserPassword` | Initial password для создаваемых CMDBuild users, если нужен | PAM/AAPM или empty |
| SEC-004 | `ElkLogging:ApiKey` | API key для ELK/Elasticsearch | PAM/AAPM `secret://...` |
| SEC-005 | `Secrets:IndeedPamAapm:ApplicationToken` | Bootstrap token приложения для PAM/AAPM | Docker/Kubernetes secret или env |
| SEC-006 | `Secrets:IndeedPamAapm:ApplicationUsername` | Bootstrap username для PAM/AAPM | Docker/Kubernetes secret или env |
| SEC-007 | `Secrets:IndeedPamAapm:ApplicationPassword` | Bootstrap password для PAM/AAPM | Docker/Kubernetes secret или env |

## Compatibility Env

| Env | Target config |
| --- | --- |
| `PAMURL` | `Secrets:IndeedPamAapm:BaseUrl` |
| `PAMTOKEN` | `Secrets:IndeedPamAapm:ApplicationToken` |
| `PAMUSERNAME` | `Secrets:IndeedPamAapm:ApplicationUsername` |
| `PAMPASSWORD` | `Secrets:IndeedPamAapm:ApplicationPassword` |
| `PAMDEFAULTACCOUNTPATH` | `Secrets:IndeedPamAapm:DefaultAccountPath` |

Если `PAMURL` плюс `PAMTOKEN` или `PAMUSERNAME`/`PAMPASSWORD` заданы, provider автоматически считается `IndeedPamAapm`.

## Companion Fields

Для любого существующего чувствительного поля можно использовать companion `<FieldName>Secret`, если целевое поле пустое.

Примеры:

```bash
ActiveDirectory__BindPassword=
ActiveDirectory__BindPasswordSecret=AAA.LOCAL/PROD/ad-bind

Cmdbuild__Password=
Cmdbuild__PasswordSecret=AAA.LOCAL/PROD/cmdbuild-admin

ElkLogging__ApiKey=
ElkLogging__ApiKeySecret=AAA.LOCAL/PROD/elk-api-key
```

Resolver преобразует companion value в `secret://...` и получает фактический секрет из PAM/AAPM.
Значение секрета добавляется только в memory configuration и не записывается в файлы.

## Правила

- Production secrets не коммитить.
- `appsettings.json` хранит только пустые значения или `secret://id`.
- Если в config есть `secret://...`, но `Secrets:Provider=None`, сервис падает на старте.
- Bootstrap tool использует тот же resolver и те же secret rules, что и сервис.
