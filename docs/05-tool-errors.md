# FS-05: Ошибки, понятные клиенту

Приоритет: P1. Источники: Program.HandleToolsCallAsync, ToolRegistry, McpProtocol.

Статус: выполнено.

Версия реализации: **1.6.0**. Принято независимым ревью: в первом раунде подняты замечания (таблица `directory_not_found`, sensitivity-проверка, genuinely unexpected correlation path, `resource_limit` для regex deadline, фиксированный текст `-32602`, формулировки границ), они исправлены и подтверждены в раундах 2–3.

## Дефект

Любое исключение в tools/call превращается в -32603 Internal error. Недоступный файл, conflict, неверные arguments и unknown tool клиент различить не может.

## Контракт

- Expected operational failures → result.isError=true и text JSON object {code,message,details?}. success → isError=false.
- Codes: file_not_found, directory_not_found, file_locked, access_denied, path_outside_workspace, symlink_not_allowed, hash_conflict, target_not_found, file_exists, unsupported_encoding, binary_file, resource_limit, cancelled.
- details ограничены по размеру и не содержат file contents, stack trace, внешние абсолютные пути или секреты. Можно сообщить безопасный requested relative path и retryable flag.
- Unknown tool → JSON-RPC -32602; неверная форма/тип/обязательные поля tool arguments → -32602. Invalid request/parse/method — FS-13.
- Непредвиденный дефект → -32603 с нейтральным сообщением и correlation id; полный exception идёт в best-effort log FS-06.
- Hash conflict сообщение явно рекомендует повторный read; locked отличает временный отказ от access denied. Не выдавать blind retry advice для неоднозначной записи.
- Внутренние typed exceptions/result codes предпочтительны вместо классификации по строке Message. Все entry points получают единое отображение.

## Приёмка

ToolErrorsTests через живой stdio: missing file и stale hash возвращают operational codes/isError=true; unknown tool и неверные аргументы — -32602. Проверки анализируют parsed JSON, не текст англоязычного exception.

Дополнить таблицу для всех codes, включая filesystem-specific HResult, отсутствие sensitive content, source-generated serialization/AOT, correlation id unexpected failure, logger unavailable, no duplicate responses. Stable machine code обязателен; human message не использовать как API.

Зависимости: FS-06 для безопасной диагностики, FS-13 для transport validation. Ссылки: MCP Tools https://modelcontextprotocol.io/specification/2025-06-18/server/tools#error-handling.

## Таблица кодов

Единый источник — `ToolErrorCodes` / `ToolErrorMessages` / `ToolErrorMapper` (`ToolErrorMapping.cs`); `retryable` — часть машинного контракта, а не текст.

| code | Триггер | `retryable` |
|---|---|---|
| `file_not_found` | Файл (или последний компонент пути) не существует: `FileNotFoundException`, `ERROR_FILE_NOT_FOUND` (2), `ENOENT`, `MutationException` из atomic write | false |
| `directory_not_found` | Отсутствует каталог в пути: `DirectoryNotFoundException` (обе платформы) либо `ERROR_PATH_NOT_FOUND` (0x80070003) | false |
| `file_locked` | Sharing/lock violation чужого handle: `ERROR_SHARING_VIOLATION` (32), `ERROR_LOCK_VIOLATION` (33); на POSIX не выводится | true |
| `access_denied` | Отказ доступа: `UnauthorizedAccessException`, `ERROR_ACCESS_DENIED` (5), `EACCES` (13) / `EPERM` (1), read-only/immutable файл | false |
| `path_outside_workspace` | Логический или физический выход за workspace (`PathPolicyException`) | false |
| `symlink_not_allowed` | Ссылка при `--allowSymLinks=false` либо ссылка на корне пути | false |
| `hash_conflict` | Наблюдаемое состояние изменилось между read и commit (FS-02), в том числе stale `original_hash` | false |
| `target_not_found` | `target_snippet` не найден в файле | false |
| `file_exists` | `create_file` по существующему пути либо конкурентное появление файла | false |
| `unsupported_encoding` | Строгий decoder: невалидный UTF-8/16/32 или неподдерживаемая кодировка | false |
| `binary_file` | NUL в UTF-8 без BOM (FS-03); бинарный файл не обрабатывается как текст | false |
| `resource_limit` | Guard-ы строк: полное чтение > 1000 строк без `allow_large_read`, диапазон больше effective `max_lines`, > 50000 строк без явного `max_lines`; исчерпание 1-секундного match deadline регулярного выражения в `search`; **FS-07** — сработавший `operationTimeoutMs` (кроме операций с частичным результатом), превышение `maxFileBytes` (чтение, а также результат и входной текст записи), превышение `maxLineChars`, превышение `maxResponseChars` при чтении `read_file` (полное чтение, range, prefix `allow_large_read`) | false |
| `cancelled` | `OperationCanceledException` / `TaskCanceledException` от **peer-причины по адресу**: `notifications/cancelled`, `$/cancelRequest` — peer жив и ждёт ответ. **FS-07 сужает строку:** сработавший `operationTimeoutMs` — это `resource_limit` (или частичный результат у `search`), а **EOF и shutdown ответа не порождают вовсе**, потому что адресат ответа исчез; это отличие от peer cancel, а не вторая peer-причина | false |

`retryable=true` только для `file_locked`: это единственный транзиентный отказ. Для `hash_conflict` blind retry запрещён контрактом (нужен повторный read), поэтому `retryable=false` при явной рекомендации повторного чтения в `message`.

`ERROR_DIRECTORY` (267) намеренно **не** распознаётся и не отображается в `directory_not_found`: код означает «путь указывает на каталог, а ожидался файл», а не «каталог не найден». `list_directory` по пути к файлу — предвиденная ошибка клиента, но FS-04 уже приняла её как внутренний отказ (`FileLocksTests.ListDirectoryOnFileIsNotMislabeledFileNotFound` фиксирует `-32603`), и FS-05 это поведение не меняет. Такой отказ попадает в correlation path вместе с действительно неожиданными дефектами.

Коды FS-01 вне таблицы FS-05 (`symlink_dangling`, `symlink_cycle`, `unsupported_reparse_point`, `path_is_directory`, `path_changed`, `unsupported_safe_write`) остаются операционными: они получают свой код и фиксированное сообщение `PathPolicy.Error`, но в таблицу FS-05 не входят.

## Реализованный контракт и границы

`ToolErrorMapper.TryMap` — единственное место классификации. Он разбирает **типы** (`OperationCanceledException`, `PathPolicyException`, `MutationException`, `OperationalException`), а для сырых I/O-ошибок вызывает `FileErrorClassifier.TryGetCode`; классификации по `Message` нет. `tools/call` (`Program.HandleToolsCallAsync`) вызывает этот mapper. FS-12 (1.13.0) удалил прямые RPC-методы: запрос с method `read_file` / `create_file` / `replace_in_file` / `list_directory` / `search` / `append_to_file` — JSON-RPC `-32601` и не доходит до mapper. До удаления оболочка legacy была `-32001` с `message` = code и без `details`, а код был общим с `tools/call` (`RemovedDirectRpcIsMethodNotFound` фиксирует новый отказ, машинный код отсутствующего файла по-прежнему проверяется через `tools/call`). `tools/call` отдаёт `result.isError=true` с `content[0].text` = JSON `{code,message,details?}` и только он несёт `details`/`retryable`. Историческая граница до FS-12: менять контракт legacy означало ломать принятые проверки (`docs/04-file-locks.md`, `docs/02-atomic-writes.md`). Ручная сборка JSON конкатенацией строк удалена: и результат, и ошибка сериализуются через `McpJsonContext`.

Классификация файловых систем: `FileErrorClassifier` дополнен `directory_not_found`. Типы `FileNotFoundException`/`DirectoryNotFoundException` проверяются **до** таблиц `HResult`/errno, потому что ENOENT (и `ERROR_PATH_NOT_FOUND` в младших 16 битах) не различает отсутствующий файл и отсутствующий каталог; тип — единственный сигнал, который это различает на обеих платформах. `ERROR_FILE_NOT_FOUND` (2) → `file_not_found`, `ERROR_PATH_NOT_FOUND` (3) → `directory_not_found`, sharing/lock (32/33) → `file_locked`, access denied (5) → `access_denied`. Win32-таблица по-прежнему применяется только на Windows; Unix использует отдельный `ClassifyUnixErrno` (EACCES/EPERM → `access_denied`, ENOENT → `file_not_found`, остальное → `null`), поэтому errno 32 (EPIPE) не читается как sharing violation.

`details` содержит `requested` relative path (безопасный), `retryable` и — с FS-07 — опциональный `truncation_reason` из закрытого перечисления FS-07 R7. Первые два поля остаются ровно теми же; третье появляется только тогда, когда транспорт заменил уже усечённый результат по бюджету ответа, и его значение всегда машинный токен из таблицы FS-07, а не произвольный текст tool. Абсолютный путь (в том числе внутри workspace) не возвращается никогда, длина ограничена `ToolErrorMapper.MaxRequestedPathLength = 200` с явным маркером усечения, управляющие символы заменяются; ограничение и очистка выполняются общим `LogSanitizer.SanitizeText`. Сообщения кодов — фиксированный текст продукта (`ToolErrorMessages`), поэтому платформенный текст исключения (в котором .NET помещает абсолютный путь) не попадает ни в `message`, ни в `details` операционного ответа.

Граница по `-32602`: клиент получает фиксированное `"Invalid arguments."`, а не текст .NET-исключения. Причина — этот текст не принадлежит продукту: `ArgumentException`-сообщения формирует фреймворк, и будущая версия может встроить в них путь. Полное (очищенное и ограниченное 200 символами плюс маркер усечения) сообщение уходит только в best-effort лог (`Program.CreateInvalidParamsResponse`). Исключение — `UnknownToolException`: `"Unknown tool: '<name>'"` это собственный текст продукта, имя клиента очищено и ограничено. Инвариант `ToolErrorMessages.ForCode(code, specific)`: функция не ограничивает `specific` и безопасна только потому, что каждый производитель typed-ошибки передаёт фиксированный литерал — единственная фабрика `PathPolicyException` это `PathPolicy.Error`, а `MutationException`/`OperationalException` строятся из литералов или из этой же таблицы. Новый производитель, передающий произвольный текст (например, платформенное сообщение), обязан сначала применить `LogSanitizer.SanitizeText`; иначе инвариант нарушится.

Protocol errors: неизвестный tool (`UnknownToolException` из `ToolRegistry`) и неверная форма/тип/обязательные поля аргументов → JSON-RPC `-32602` без `result`. Отсутствующие аргументы и неверные типы распознаются по типизированному семейству `ArgumentException` (исключая `ArgumentNullException` — это проверка контракта сервисов, то есть дефект), `params` не-объект для `tools/call` — это `-32602` до разбора аргументов, а неверный тип поля внутри объекта — `JsonException` вокруг `DeserializeParams`, тоже `-32602`. FS-13 (1.14.0) закрыл прежние дефекты протокола: синтаксически сломанный кадр — `-32700` с явно сериализованным `id:null` (он больше не отбрасывается); неверная форма request/`jsonrpc`/method и id не string/integer — `-32600`; неизвестный method — `-32601`; ошибка формы `params` — `-32602`, а не parse error. `initialize` отражает только реализованную версию `2024-11-05` и не копирует произвольный `protocolVersion`.

Непредвиденный дефект: `Program.Main` формирует neutral message (`Internal error`) и correlation id в `error.data.correlationId`, а полный exception уходит в best-effort `McpLogger.LogError`; вызов logger дополнительно обёрнут, поэтому недоступный logger не мешает ответу. Один request даёт ровно один response: ответ пишется один раз в конце обработки, отдельной ветки повторной записи нет. Никакой внутренний текст исключения в ответ не попадает.

**Один код `-32603`, два разных случая (FS-07).** Кроме непредвиденного дефекта выше, `-32603` несёт и предсказуемый отказ по бюджету ответа: `message` = `Response exceeds maxResponseChars`, без `correlationId`, в `error.data.truncation_reason` — причина усечения из таблицы FS-07 R7. Это не дефект: отказ ожидаем, ограничен, не логируется как сбой и не идёт в correlation path. Клиент различает случаи по `message` (`Response exceeds maxResponseChars` против `Internal error`), а не по коду.

`resource_limit` отображает guard-ы чтения `FileService` (1000/50000 строк, диапазон больше effective `max_lines`) и 1-секундный match deadline `search` (`RegexMatchTimeoutException`; патологический клиентский regex — это отказ по ресурсу, который клиент может исправить, а не дефект сервера), а с FS-07 — ещё четыре триггера: превышение `maxFileBytes` (при чтении, а также при записи — на результате и входном тексте, до commit), превышение `maxLineChars`, превышение `maxResponseChars` при чтении (полное чтение, range и prefix `allow_large_read` — для `read_file` это отказ, а не усечение) и сработавший `operationTimeoutMs` для операций без частичного результата. Ранее здесь было написано, что настраиваемые `maxFileBytes`/`maxRequestBytes`/`operationTimeoutMs` «остаются FS-07» в смысле отсутствия: теперь они в силе, и таблица кодов выше это фиксирует.

`cancelled` сужен относительно первоначальной формулировки: к нему приводит `OperationCanceledException` **от peer-причины** — `notifications/cancelled`, `$/cancelRequest`. Отмена по адресу доступна в dispatcher (FS-07), поэтому код проверяется живым stdio, а не только на уровне mapper. Сработавший `operationTimeoutMs` даёт `resource_limit` (или частичный результат у `search`), EOF и shutdown ответа не порождают вовсе. Прежнее утверждение «любой `OperationCanceledException` — это `cancelled`» и «источника отмены в dispatcher пока нет» относилось к состоянию до FS-07 и больше не действует.

Correlation id: непредвиденный дефект проверяется двумя живыми маршрутами. Во-первых, инъекция через штатный FS-02 barrier protected host: релиз барьера командой, которую host отвергает, заставляет его бросить `IOException` из тестовой обвязки — экологический дефект, недостижимый действиями клиента (`InjectedFaultIsCorrelatedAndLeavesNoPartialWrite`). Во-вторых, `ERROR_DIRECTORY` (предвиденная ошибка клиента, намеренно нераспознанная) как второстепенный случай. Гарантия correlation id, нейтрального сообщения и «полный exception только в логе» не привязана к одному спорному триггеру.

Serialization/AOT: `ToolOperationError`, `ToolErrorDetails` и `ErrorCorrelationData` объявлены в `McpProtocol.cs` и зарегистрированы в source-generated `McpJsonContext`; рефлексия не используется (`JsonSerializerIsReflectionEnabledByDefault=false`, `PublishAot=true`), сборка остаётся 0 warnings.

## Матрица проверки

`ToolErrorsTests` (живой stdio через `FilesystemMcp.TestHost`, разбор JSON, без проверок англоязычного текста исключения): missing file, missing containing directory (`file_not_found` vs `directory_not_found`), stale hash без записи, target not found, existing file, unsupported encoding, binary file, `resource_limit` по read guard (`OversizedReadIsResourceLimitAndNotRetryable`) и по regex deadline (`PathologicalRegexIsResourceLimitAndNotADefect`), path_outside_workspace с сокрытием абсолютного пути, symlink_not_allowed, file_locked vs access_denied (`retryable`, различимость формулировок), unknown tool, отсутствующий аргумент, неверный тип аргумента, отсутствие sensitive content, bounded/sanitized `requested`, source-generated сериализация объекта ошибки, correlation id + полный exception только в логе для экологического дефекта (`InjectedFaultIsCorrelatedAndLeavesNoPartialWrite`) и для `ERROR_DIRECTORY` (`UnexpectedDefectIsCorrelatedAndOnlyLoggedInFull`), недоступный logger, отсутствие дублирующихся response, стабильность формы success/failure, legacy entry point с тем же кодом, а также self-check самой проверки на sensitive content (`SensitiveContentCheckRejectsEscapedAbsolutePaths`: экранированный абсолютный путь внутри JSON-текста payload обязан её провалить).

Матрица всех 13 кодов и Windows HResult-строк (`ERROR_FILE_NOT_FOUND`/`ERROR_PATH_NOT_FOUND`/sharing/access denied, а также неприменимость Win32-таблицы к Unix) проверяется отдельными случаями. **12 из 13 кодов подтверждены живым stdio**: `file_not_found`, `directory_not_found`, `file_locked`, `access_denied`, `path_outside_workspace`, `symlink_not_allowed`, `hash_conflict`, `target_not_found`, `file_exists`, `unsupported_encoding`, `binary_file`, `resource_limit`. `cancelled` подтверждён только на уровне mapper (`CancellationIsOperationalCode`): живого триггера в dispatcher нет, отмена целой операции принадлежит FS-07; триггер не имитируется.

Приёмка (версия 1.6.0, Windows, SDK 10.0.401): managed build — 0 warnings/errors; `Spec=FS-05` — 28 passed, 0 skipped, 0 failed (28 cases); `Status=Baseline` — 200 passed, 8 skipped, 0 failed (208 cases); `Status=KnownDefect` — 0 passed, 30 failed (30 cases); полный прогон — 200 passed, 30 failed, 8 skipped (238 cases), все 30 падений — прежние KnownDefect FS-06…FS-13. Контрольные прогоны FS-01/02/03/04 — 32+43+60+25 passed, 0 failed. Подробности и artifacts — в [test README](../FilesystemMcp.Tests/README.md). Одиночные падения межпроцессных гонок FS-02 на 5-секундном watchdog были дедлайном ответа, в который входило ожидание на барьере; чтение ответа начинается после последнего Release.
