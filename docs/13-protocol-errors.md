# FS-13: JSON-RPC validation и negotiation

Приоритет: P2. Источники: Program.Main/ProcessRequestAsync/HandleInitialize, McpProtocol.

Статус: выполнено.

Версия реализации: 1.14.0.

## Дефект

Parse error с null id отбрасывается общей проверкой отсутствующего id. initialize отражает произвольный protocolVersion, включая 2099-01-01. Ошибки формы params ошибочно становятся parse errors.

## Контракт

- Malformed JSON → один -32700 response с явно сериализованным id:null. Valid JSON wrong request shape/jsonrpc/method → -32600; unknown method → -32601; invalid params/arguments → -32602.
- Отсутствующий id у валидного notification → никакого response, даже для unknown notification. Это отдельная ветка, не условие «все response без id отбросить».
- Requests принимают string или integer id в рамках поддерживаемой MCP version; object/array/bool/null id отвергать как invalid request. Notification отличается отсутствием id. Ошибка с неопределимым id использует null.
- Поддерживаемый набор на первом этапе: 2024-11-05 (существующая базовая версия). Requested supported version отражать; unsupported version возвращает 2024-11-05. Новые версии добавлять только с тестами реализации, не копировать client value.
- initialize проверяет required protocolVersion/capabilities/clientInfo и их типы. После ответа клиент шлёт notifications/initialized. До initialized разрешить initialize/ping; premature tools request → -32600 с понятной причиной. Повторный initialize → invalid request.
- params JSON должен быть object для текущих методов; type/required violations не путать с синтаксическим parse error.
- Каждый request получает ровно один response с тем же id; stdout newline-delimited UTF-8 JSON, никакой диагностики. EOF завершает процесс; cancellation FS-07 не производит response на notification.
- Batch для целевой MCP version не поддерживать и отклонять явно -32600, без частичного исполнения.

## Приёмка

ProtocolTests через настоящий process: malformed→id:null/-32700, unsupported version fallback, wrong params→-32602, invalid id→-32600. Baseline handshake/tools-list/ping и notification без ответа (проверка следующим ping).

Дополнить table-driven invalid shapes, null JSON, batch, numeric/string id, initialize missing fields, duplicate init, premature calls, invalid UTF-8 frame, EOF/shutdown, cancellation и bounded framing FS-07. Null id в error нельзя терять из-за DefaultIgnoreCondition.WhenWritingNull.

Ссылки: https://www.jsonrpc.org/specification и https://modelcontextprotocol.io/specification/2025-06-18/basic/lifecycle. Изменения handshake/custom API документируются вместе с FS-12. Ссылка на lifecycle 2025-06-18 — источник формы handshake, не заявление о поддержке этой версии.

## Реализация (1.14.0)

Реализована одна MCP-версия: `2024-11-05`. Запрошенная поддерживаемая версия отражается. `2099-01-01`, `2025-06-18` и любая другая строка отвечают `2024-11-05`; текст клиента не копируется. Другие версии не заявляются.

- Кадр классифицирует `JsonRpcFrameDecoder`, а не общая проверка «нет id — молчать». Синтаксически сломанный JSON, кадр из одних пробелов, пустой кадр и невалидный UTF-8 — один `-32700` с `id`, который на проводе есть и равен `null`. Для этого id — `JsonElement` вида Null: `DefaultIgnoreCondition.WhenWritingNull` его не выкидывает. Valid JSON неверной формы, неверный `jsonrpc`/`method`, id не string и не integer, повторный `initialize` и batch — `-32600`. Неизвестный method — `-32601`. Не-object `params` и нарушение типа/обязательности — `-32602`, не parse error.
- Notification — это отсутствие члена `id`, отдельная ветка. Неизвестный notification, `ping` без id и `notifications/cancelled` ответа не дают. Ошибка, у которой id нельзя определить, сериализует `id:null`.
- `initialize` проверяет `protocolVersion` (непустая строка), `capabilities` (object), `clientInfo` (object) и строковые `clientInfo.name` / `clientInfo.version`. До `notifications/initialized` разрешены только `initialize` и `ping`; tools и прочие запросы — `-32600` с текстом про `notifications/initialized`. Эта проверка для `tools/call` и прочих методов вне reader-списка выполняется на reader до следующего кадра: более поздний `notifications/initialized` в том же буфере не открывает уже прочитанный запрос. Исторический alias `initialized` тоже открывает сессию. Прямые RPC FS-12 после handshake по-прежнему `-32601`; до `notifications/initialized` тот же method — `-32600`.
- Batch (JSON-массив) — один `-32600`, элементы не исполняются. EOF и EOF посреди кадра завершают процесс с кодом 0 и без ответа: `BoundedFrameReader` больше не отдаёт незакрытый LF кадр в разбор (норма FS-07 R10). Незавершённый `tools/call` при EOF тоже не получает кадр: отмена сессии не пишется клиенту. Peer `notifications/cancelled` своего кадра не пишет; отменённый request при живом peer получает один ответ `cancelled`.
- `TransportBudgetTests.EveryDeliveredFrameIsWholeJsonCarryingTheRequestIdAtAnyResponseBudget` сначала проходит handshake: при закрытых до initialized tools проверка бюджета ответа иначе читала бы `-32600`. Ассерты бюджета кадра не менялись.

Приёмка на Windows, Debug, сборка `dotnet build FilesystemMCP.sln -m:1 -p:UseSharedCompilation=false -p:PublishAot=false -p:PublishTrimmed=false --no-incremental` — 0 warnings, 0 errors. Затем `--no-build -p:PublishAot=false -p:PublishTrimmed=false`: первая приёмка `Spec=FS-13` — 85 cases, 85 passed, 0 failed, 0 skipped; `Status=Baseline` — 581 cases, 573 passed, 0 failed, 8 skipped; `Status=KnownDefect` — фильтр не нашёл ни одного теста (exit 0). Полный прогон — 581 cases, 572 passed, 1 failed, 8 skipped. Единственное падение — внешний тайминговый дефект FS-07 `SearchBudgetTests.SearchThatGenuinelyExceedsTheOperationTimeoutReturnsAPartialResult` (`KeyNotFoundException` на `truncation_reason`, обход уложился в `--operationTimeoutMs=2000`). В Baseline этого же билда он прошёл, в полном прогоне и в изолированном повторе — упал. Он не из FS-13 и здесь не исправлялся, поэтому падающий набор полного прогона не совпадает с пустым KnownDefect. До правки: Baseline 500 (492 passed, 8 skipped), KnownDefect 2 (оба FS-13), весь проект 502. Изменения матрицы: `BooleanIdIsInvalidRequest` и `UnsupportedVersionFallsBackToImplementedVersion` переведены в Baseline, добавлено 79 cases. 85 = 4 прежних Baseline + 2 переведённых + 79 новых (32 формы кадра, 20 полей `initialize`, 7 id, 6 notifications, 4 строки версии, 10 сценариев: один response, initialize без id, lifecycle, alias `initialized`, batch, UTF-8, два EOF, cancel, oversized frame). KnownDefect 2 → 0, Baseline 500 → 581, проект 502 → 581.

Раунд ревью (версия та же, 1.14.0; Baseline и полный прогон не перезапускались). Та же сборка, 0 warnings, 0 errors; `Spec=FS-13` — 88 cases, 88 passed, 0 failed, 0 skipped. Добавлены 3 cases: `PipelinedInitializedDoesNotOpenAPrematureRequest` (tools/call и прямой `read_file` в одном буфере с более поздним `notifications/initialized` — `-32600`, файл не создаётся), `BlankAndWhitespaceFramesAreParseErrorsWithExplicitNullId` (кадр из пробела и пустой кадр — `-32700` с `id:null`, следующий ping сохраняет id), `EofWhileAToolIsParkedWritesNoFrameAndExitsZero` (EOF при parked `read_file` — нет кадра, exit 0; peer cancel по-прежнему даёт `cancelled`). 88 = 85 + эти 3.
