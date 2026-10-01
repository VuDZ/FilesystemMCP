# FS-05: Ошибки, понятные клиенту

Приоритет: P1. Источники: Program.HandleToolsCallAsync, ToolRegistry, McpProtocol.

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
