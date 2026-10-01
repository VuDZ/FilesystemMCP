# FS-13: JSON-RPC validation и negotiation

Приоритет: P2. Источники: Program.Main/ProcessRequestAsync/HandleInitialize, McpProtocol.

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

Ссылки: https://www.jsonrpc.org/specification и https://modelcontextprotocol.io/specification/2025-06-18/basic/lifecycle. Изменения handshake/custom API документируются вместе с FS-12.
