# Интеграция с CMDBuild REST API

Документ фиксирует текущий контракт `cmdb2monitoring` с CMDBuild. Это не полный справочник CMDBuild REST API, а практическое описание того, какие endpoints использует наше ПО и какие нюансы важны при эксплуатации.

Проверенная версия: CMDBuild `4.1.0`, REST API v3, base URL вида:

```text
http://<host>:<port>/cmdbuild/services/rest/v3
```

## Где используется CMDBuild REST API

| Компонент | Зачем ходит в CMDBuild |
| --- | --- |
| `monitoring-ui-api` | Sync CMDBuild catalog, чтение/изменение webhooks, подготовка audit model, quick audit |
| `cmdbkafka2zabbix` | Подъем lookup/reference/domain leaf values по `source.fields[].cmdbPath`, чтение binding-ов для точного update/delete |
| `zabbixbindings2cmdbuild` | Обратная запись `zabbix_main_hostid` и карточек `ZabbixHostBinding` |
| scripts `scripts/cmdbuild-*.mjs` | Создание тестовой модели, тестовых карточек и relations |

Браузер напрямую в CMDBuild не ходит. Все REST-вызовы делает BFF/microservice.

## Авторизация

Текущая рабочая схема:
- `monitoring-ui-api` запрашивает CMDBuild login/password у пользователя при первой операции, где нужен CMDBuild API, и хранит их только в server-side session.
- `cmdbkafka2zabbix` и `zabbixbindings2cmdbuild` используют service account из своих `appsettings*.json` / env / PAM.
- Внешняя авторизация UI через MS AD/IdP не используется как credential для CMDBuild API.
- Production passwords не хранятся в git. Использовать env/secret storage или PAM/AAPM `secret://id`.

HTTP headers:

```text
Accept: application/json
Authorization: Basic <base64(username:password)>
Content-Type: application/json   # только когда есть body
CMDBuild-View: admin             # для ETL/webhook и части admin/write операций
```

`monitoring-ui-api` технически умеет отправлять `Authorization: Bearer <accessToken>`, если credential object уже содержит `accessToken`, но штатный пользовательский сценарий сейчас основан на Basic credentials.

## Endpoints

### Catalog Sync

Используется UI для построения дерева классов/атрибутов и проверки правил.

| Method | Path | Назначение |
| --- | --- | --- |
| `GET` | `/classes` | Список классов |
| `GET` | `/classes/{class}/attributes` | Атрибуты класса |
| `GET` | `/lookup_types` | Типы lookup |
| `GET` | `/lookup_types/{lookupType}/values` | Значения lookup |
| `GET` | `/domains` | Список domains |
| `GET` | `/domains/{domain}` | Детали domain |

Нюансы:
- class/domain/lookup names обязательно URL-encode.
- UI фильтрует inactive classes, если не включен `IncludeInactiveClasses`.
- UI читает атрибуты только для первых 250 выбранных классов, lookup types и domains ограничены 500 элементами. Для очень крупных моделей лимиты надо пересматривать отдельно.
- Ответ CMDBuild нормализуется из `data[]`, `items[]` или одиночного `data`.

### Webhook Setup

Используется меню `Настройка webhooks`.

| Method | Path | Назначение |
| --- | --- | --- |
| `GET` | `/etl/webhook/?detailed=true` | Загрузить текущие CMDBuild webhooks |
| `POST` | `/etl/webhook/` | Создать managed webhook |
| `PUT` | `/etl/webhook/{id}/` | Изменить managed webhook |
| `DELETE` | `/etl/webhook/{id}/` | Удалить managed webhook |

Нюансы:
- Для этих вызовов используется `CMDBuild-View: admin`.
- UI применяет изменения только к webhooks с prefix `cmdbwebhooks2kafka-`.
- Undo/redo в UI не откатывает уже выполненные изменения в CMDBuild.
- `Save file as` может сохранить webhook artifact рядом с rules, но token/password/secret/API key/Authorization значения должны быть замаскированы как `XXXXX`.
- Webhook payload ожидается плоским. Reference, lookup и domain значения обычно приходят как id, а путь до leaf хранится в rules.

### Card Read / Quick Audit

| Method | Path | Назначение |
| --- | --- | --- |
| `GET` | `/classes/{class}/cards?limit={n}&offset={m}` | Пакетное чтение карточек выбранного класса |
| `GET` | `/classes/ZabbixHostBinding/cards?limit=5000` | Чтение binding-ов дополнительных profiles |

Нюансы:
- Quick audit читает карточки пакетами через `limit/offset`.
- Для каждого выбранного класса выполняется отдельный запрос.
- Значения карточки нормализуются: lookup/reference object может быть свернут в `code`, `_description_translation`, `description`, `_id` или `id`.

### Resolver для lookup/reference/domain leaf

Используется `cmdbkafka2zabbix` при обработке Kafka event.

| Method | Path | Назначение |
| --- | --- | --- |
| `GET` | `/classes/{class}/attributes` | Узнать тип атрибута, target class и lookup type |
| `GET` | `/classes/{class}/cards/{cardId}` | Прочитать карточку reference leaf |
| `GET` | `/lookup_types/{lookupType}/values` | Преобразовать lookup id в code/description/translation |
| `GET` | `/classes/{sourceClass}/cards/{sourceCardId}/relations` | Найти связанные карточки для domain path |

Нюансы:
- Если resolver включен в rules, но `Cmdbuild:BaseUrl/Username/Password` не заполнены, lookup/reference/domain значения не будут подняты.
- Для обычного reference/lookup при ошибке resolver оставляет исходное значение, чтобы не потерять event.
- Для domain path при ошибке unresolved relation id удаляется из source fields, чтобы не отправить в Zabbix числовой id вместо leaf value.
- Lookup default `valueMode` - `code`. Возможные режимы: `code`, `description`, `translation`, `id`.
- Если карточка CMDBuild уже содержит companion-поля вида `_{Attribute}_code`, `_{Attribute}_description`, `_{Attribute}_description_translation`, resolver использует их до отдельного lookup lookup_types call.
- Reference path защищен `MaxPathDepth` и проверкой циклов.
- Runtime cache живет только на один resolver event: update события заново читают lookup/reference/domain leaf values.

Примеры `cmdbPath`:

```text
Класс.ReferenceAttribute.LeafAttribute
Класс.Reference1.Reference2.LookupAttribute
Класс.{domain:СвязанныйКласс}.LeafAttribute
Класс.{domain:СвязанныйКласс}.ReferenceAttribute.LookupAttribute
```

Для `{domain:СвязанныйКласс}` указывается класс второго конца связи, а не имя domain. Resolver читает relations исходной карточки и выбирает endpoint, class которого совпадает с указанным target class.

### Reverse Binding

Используется `zabbixbindings2cmdbuild` после успешной записи в Zabbix.

| Method | Path | Назначение |
| --- | --- | --- |
| `PUT` | `/classes/{sourceClass}/cards/{sourceCardId}` | Записать или очистить `zabbix_main_hostid` для main profile |
| `GET` | `/classes/ZabbixHostBinding/cards?limit={n}` | Найти существующую binding-карточку |
| `POST` | `/classes/ZabbixHostBinding/cards` | Создать binding для дополнительного profile |
| `PUT` | `/classes/ZabbixHostBinding/cards/{bindingCardId}` | Обновить binding для дополнительного profile |

Минимальная модель:
- атрибут `zabbix_main_hostid` на каждом конкретном CMDBuild class, участвующем в rules;
- service class `ZabbixHostBinding` для дополнительных profiles.

Нюансы:
- Main profile хранится прямо в исходной карточке.
- Additional profiles хранятся отдельными карточками `ZabbixHostBinding`.
- Поиск binding сейчас читает до `BindingLookupLimit` карточек и фильтрует локально по `OwnerClass + OwnerCardId + HostProfile`. Для крупных инсталляций нужен контроль лимита и желательно индексы/ограничения на эти поля со стороны CMDBuild.
- При `host.delete` `zabbix_main_hostid` очищается, а binding status для дополнительных profiles переводится в `deleted`.

### Подготовка Audit Model

Используется UI меню `Аудит`.

| Method | Path | Назначение |
| --- | --- | --- |
| `POST` | `/classes` | Создать service class `ZabbixHostBinding` |
| `POST` | `/classes/{class}/attributes` | Создать `zabbix_main_hostid` или атрибуты binding class |

Нужны права администратора модели CMDBuild. Для простого анализа плана эти права не нужны.

### Demo/Test Scripts

Тестовые scripts используют дополнительные write endpoints:

| Method | Path | Назначение |
| --- | --- | --- |
| `POST` | `/classes` | Создание тестовых классов |
| `POST` | `/classes/{class}/attributes` | Создание атрибутов |
| `POST` | `/lookup_types` | Создание lookup type |
| `POST` | `/lookup_types/{lookupType}/values` | Создание lookup value |
| `POST` | `/domains` | Создание domain |
| `POST` | `/classes/{class}/cards` | Создание карточки |
| `PUT` | `/classes/{class}/cards/{cardId}` | Обновление карточки |
| `POST` | `/domains/{domain}/relations` | Создание relation, основной вариант |
| `POST` | `/classes/{sourceClass}/cards/{sourceId}/relations` | Создание relation, fallback-варианты |

Эти scripts предназначены для dev/test CMDBuild. В production их не запускать без отдельного плана.

## Права CMDBuild

Минимальные права зависят от сценария:

| Сценарий | Права |
| --- | --- |
| Catalog sync | read metadata classes/attributes/domains, lookup types/values |
| Rule editor с catalog validation | read catalog cache; для обновления cache нужны права catalog sync |
| Webhook load | read ETL/webhook records |
| Webhook apply | create/update/delete ETL/webhook records |
| Converter resolver | read attributes, cards, relations, lookup values по участвующим классам |
| Quick audit | read classes/cards участвующих классов и `ZabbixHostBinding` |
| Audit model apply | model admin: create class/attributes |
| Reverse binding writer | read/update участвующих карточек, read/create/update `ZabbixHostBinding` |

## Основные нюансы и риски

1. **Webhook не является источником полного объекта.** Он передает плоский payload. Если нужен leaf через reference/domain/lookup, metadata должна быть в rules, а фактический leaf дочитывается через REST.

2. **Изменение связанной карточки само по себе не обновит мониторинг исходной карточки.** Если поменялся reference/domain leaf, но исходная карточка не модифицировалась и webhook по исходной карточке не пришел, converter не получит событие.

3. **Lookup id нельзя напрямую отправлять в Zabbix как бизнес-значение.** Нужно преобразовать id в `code`/`description`/`translation` через lookup values или companion-поля карточки.

4. **N:N domain не представлен как attribute карточки.** Для него нужен `/classes/{class}/cards/{id}/relations` и `cmdbPath` с `{domain:СвязанныйКласс}`.

5. **Reference path ограничен глубиной.** По умолчанию используется глубина 2, допустимый runtime диапазон 2-5. Это защита от циклов и тяжелых цепочек.

6. **Class names и attribute names не встроены в продукт.** Конкретная модель CMDBuild определяется catalog + webhook + rules. В документации использовать абстрактные имена, если речь не о тестовом объекте.

7. **Superclasses/prototypes не должны быть leaf target для правил.** В UI нужно выбирать конкретные классы, а не абстрактные superclasses.

8. **Большие модели требуют контроля лимитов.** UI catalog sync и audit имеют защитные лимиты. Для крупных CMDBuild инсталляций нужно отдельно оценить объем classes/cards/lookups/domains и производительность.

9. **ETL/webhook операции необратимы через UI undo.** Undo/redo меняет только локальный UI draft, но не откатывает уже примененные REST changes в CMDBuild.

10. **Не ходить напрямую в БД CMDBuild.** Наш продукт работает через REST API. Прямая работа с БД CMDBuild не является частью контракта совместимости.

## Проверка после изменений CMDBuild модели

1. Выполнить sync CMDBuild catalog в UI.
2. Проверить, что новые классы/атрибуты/lookup/domain видны в catalog cache.
3. В rule editor создать или обновить rules.
4. В Webhook Setup нажать `Проанализировать rules`.
5. Применить недостающие webhook payload fields.
6. Сохранить rules, опубликовать файл во внешний git/рабочую директорию.
7. Нажать `Перечитать правила конвертации` для converter-а.
8. Создать или обновить тестовую карточку CMDBuild.
9. Проверить цепочку Events: CMDBuild event -> Zabbix request -> Zabbix response -> binding event.
10. Запустить Quick audit по выбранному классу.
