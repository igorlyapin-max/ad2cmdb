# Бизнес-процессы

## BP-001. Периодическая синхронизация AD -> CMDBuild

Цель: поддерживать учетные записи и группы CMDBuild в состоянии, соответствующем выбранным группам MS AD.

Периодичность: каждые `Sync:IntervalSeconds`, по умолчанию 300 секунд.

Участники:
- `adgroups2cmdbuild`;
- MS AD LDAP/LDAPS;
- CMDBuild REST API v3;
- optional PAM/AAPM для секретов;
- optional ELK или platform logging для логов.

Основной поток:

1. Сервис загружает конфигурацию из `appsettings*.json`, runtime env и PAM/AAPM ссылок.
2. Сервис читает все группы из `ActiveDirectory:GroupNames`.
3. Если хотя бы одна настроенная AD group не найдена, sync-run завершается ошибкой без изменений.
4. Сервис читает CMDBuild roles и users.
5. Если для любой настроенной AD group нет CMDBuild role с тем же именем, sync-run завершается ошибкой без изменений.
6. Для каждого AD user из `ProvisioningGroupName` сервис создает или обновляет CMDBuild user.
7. CMDBuild `username` равен AD login из `ActiveDirectory:UserLoginAttribute`.
8. CMDBuild `description` или другой `Cmdbuild:UserDisplayNameField` получает ФИО из AD.
9. CMDBuild `email` или другой `Cmdbuild:UserEmailField` получает email из AD.
10. CMDBuild `userGroups` для управляемых groups приводятся к membership в AD.
11. Неуправляемые CMDBuild groups сохраняются, если `Cmdbuild:PreserveUnmanagedGroups=true`.
12. State-файл обновляется только если `Sync:DryRun=false`; при partial failure в state попадают только успешно примененные операции.

Защиты:
- `Sync:DryRun=true` по умолчанию.
- Missing AD group и missing CMDBuild role считаются hard error.
- Ошибка CMDBuild по одному пользователю логируется, увеличивает `failedUsers` и не останавливает batch.
- Transient LDAP/LDAPS и CMDBuild REST ошибки повторяются с exponential backoff и jitter.
- Локальный lock-файл `Sync:InstanceLockPath` защищает от двух sync-run на одном хосте.
- Ошибки отправки ELK logs не ломают sync.
- PAM/AAPM `secret://...` без активного provider считается конфигурационной ошибкой.

Остановка:

1. При SIGTERM/SIGINT worker прекращает запуск новых sync-run.
2. Если sync-run уже выполняется, он продолжает работу до `Sync:ShutdownGracePeriodSeconds`.
3. Если run успел завершиться, статус фиксируется как обычный completed/partial failure.
4. Если timeout истек, run отменяется, `/sync/status` получает `lastSucceeded=false`, lock освобождается.

## BP-002. Блокировка пользователя

Триггер: пользователь отсутствует в `ActiveDirectory:ProvisioningGroupName`.

Кандидатами на блокировку считаются:
- пользователи из локального state-файла `ManagedLogins`;
- пользователи, найденные в любой управляемой AD group, но не в provisioning group;
- CMDBuild users, которые состоят в управляемых CMDBuild roles.

Действие:
- `active=false`;
- `userGroups=[]`;
- `defaultUserGroup=null`.

Особенность: при блокировке отзываются все CMDBuild groups, включая неуправляемые. Это соответствует требованию "при пропадании из provisioning group сотрудник блокируется и все группы отзываются".

## BP-003. Bootstrap AD groups

Цель: на этапе развертывания создать в AD группы, соответствующие существующим CMDBuild roles.

Компонент: `tools/bootstrap-ad-groups`.

Основной поток:

1. Оператор запускает `scripts/bootstrap-ad-groups.sh`.
2. Tool читает CMDBuild roles через REST v3.
3. Tool выбирает roles по `--prefix`, `--include`, `--all` или fallback `ActiveDirectory:GroupNames`.
4. Tool ищет группы в AD по `ActiveDirectory:GroupSearchBaseDn` и `GroupNameAttribute`.
5. Tool печатает план.
6. Если указан `--apply`, tool создает отсутствующие security groups в `--target-ou`.

Защиты:
- без `--apply` выполняется только dry-run;
- `--apply` без явного selection запрещен, кроме случая `BootstrapAdGroups:RequireExplicitSelectionForApply=false`;
- существующие AD groups не пересоздаются и не изменяются;
- членство пользователей не переносится, создаются только group objects.

## BP-004. Диагностика и Логирование

Цель: дать оператору достаточно данных для разбора sync-run без постоянного включения чрезмерной детализации.

Режимы:

- `Debug:Enabled=false`: только обычные информационные, warning и error logs.
- `Debug:Enabled=true`, `Debug:Level=Basic`: счетчики и ключевые этапы sync-run.
- `Debug:Enabled=true`, `Debug:Level=Verbose`: Basic плюс per-user действия и resolved login lists; sensitive values редактируются, если `Debug:LogSensitiveValues=false`.

Каналы:

1. Console stdout/stderr всегда доступен.
2. ELK используется только при заполненном `ElkLogging:Endpoint` и `ElkLogging:Enabled=true`.
3. Syslog не встроен в код: Docker пересылает stdout/stderr через `--log-driver=syslog`.

Ограничение: `Verbose` с `Debug:LogSensitiveValues=true` может раскрывать логины и состав групп, поэтому включается только на диагностическое окно.
