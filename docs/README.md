# План исправлений FilesystemMcp

Статус: FS-01 реализован и принят независимым ревью в третьем раунде (версия 1.2.0); FS-02 принят независимым ревью в первом раунде (версия 1.3.0); FS-03 принят независимым ревью в первом раунде (версия 1.4.0); FS-04 принят независимым ревью в первом раунде (версия 1.5.0); FS-05 реализован и принят независимым ревью: в первом раунде подняты замечания, они исправлены и подтверждены в раундах 2–3 (версия 1.6.0); FS-06 реализован и принят независимым ревью: в первом раунде подняты замечания, они исправлены и подтверждены во втором раунде (версия 1.7.0); остальные исправления ещё не приняты. Основание — аудит версии 1.1.2 от 2026-10-01. Номера FS-01…FS-13 соответствуют пунктам 1…13 аудита.

## Спецификации

| ID | Приоритет | Исправление | Тестовый класс |
|---|---|---|---|
| [FS-01](01-workspace-links.md) | P1 | Логическая граница workspace, allowSymLinks=true по умолчанию и strict false | WorkspaceLinksTests / WorkspaceLinksAcceptanceTests |
| [FS-02](02-atomic-writes.md) | P1 | Атомарная запись и конфликты | AtomicWritesTests / AtomicWriteAcceptanceTests |
| [FS-03](03-text-encoding.md) | P1 | Строгий decoder и сохранение формата | TextEncodingTests |
| [FS-04](04-file-locks.md) | P1 | Занятые файлы и неполный поиск | FileLocksTests |
| [FS-05](05-tool-errors.md) | P1 | Диагностика ошибок tools | ToolErrorsTests |
| [FS-06](06-fail-safe-logging.md) | P1 | Отказоустойчивое логирование | LoggingTests |
| [FS-07](07-resource-budgets.md) | P1 | Лимиты, cancellation, отзывчивость | ResourceBudgetsTests |
| [FS-08](08-installer-merge.md) | P1 | Безопасная установка | InstallerTests |
| [FS-09](09-search-encoding.md) | P2 | Поиск по UTF-16/UTF-32 | SearchEncodingTests |
| [FS-10](10-read-fidelity.md) | P2 | Строки и явная неполнота ответа | ReadFidelityTests |
| [FS-11](11-empty-replacement.md) | P2 | Пустые/whitespace snippets | EmptyReplacementTests |
| [FS-12](12-unified-api.md) | P2 | Один API и общая реализация | UnifiedApiTests |
| [FS-13](13-protocol-errors.md) | P2 | JSON-RPC и negotiation | ProtocolTests |

## Общие договорённости

- После завершения и приёмки каждой спецификации в ней явно указывать `Статус: выполнено.` и `Версия реализации: x.y.z.`.

- Настройки — immutable ServerOptions, задаваемые при запуске. Первый аргумент остаётся workspace; новые аргументы имеют форму --name=value. Неизвестные имена, неверные boolean/numeric значения и дубликаты — ошибка запуска в stderr и ненулевой exit code. stdout содержит только protocol frames. Конфигурация не меняется через tools/call.
- allowSymLinks — точное имя boolean-настройки, default true по override пользователя; --allowSymLinks=false включает strict jail без ссылок. True разрешает внешние цели ссылок внутри workspace; прямой outside absolute/../ входной маршрут запрещён в обоих режимах.
- logDirectory — точное имя настройки каталога диагностики: --logDirectory=<path>. Без неё используется пользовательский каталог (не каталог бинарника). Пустое значение и дубликат — ошибка запуска; недоступный каталог (занятое имя, ACL, I/O) — не ошибка: file sink отключается, одно безопасное сообщение в stderr, сервер продолжает работу (FS-06).
- Ошибки выполнения tool: result.isError=true, content[0].text — JSON с code, message и при необходимости безопасными деталями. Ошибки формы протокола/аргументов: JSON-RPC error. Клиент не должен разбирать текст exception.
- Коды FS-05 (`file_not_found`, `directory_not_found`, `file_locked`, `access_denied`, `path_outside_workspace`, `symlink_not_allowed`, `hash_conflict`, `target_not_found`, `file_exists`, `unsupported_encoding`, `binary_file`, `resource_limit`, `cancelled`), их триггеры и семантика `retryable` — в [05-tool-errors.md](05-tool-errors.md); `retryable=true` только для транзиентного `file_locked`.
- Для read/list/search результаты — JSON object в text content; при поддержке structuredContent тот же object. Изменение search/list с array на object — изменение контракта, требующее версии и обновления README/client samples.
- FS-12 сохраняет пять текущих стандартных tools и удаляет прямые custom RPC методы. append не реализуется в этом цикле; его рекомендации удаляются.
- Новые публичные error codes, metadata и CLI options документируются вместе с реализацией. Не заявлять поддержку MCP-версий без protocol tests.
- Unicode-пути не перекодируются в ANSI и не нормализуются NFC/NFD автоматически.

## Тесты и порядок реализации

Отдельный проект: ../FilesystemMcp.Tests. [Инструкции запуска и статус покрытия](../FilesystemMcp.Tests/README.md).

Тесты с Status=KnownDefect проверяют желаемое поведение и сейчас могут падать. Они не пропускаются и не ожидают ошибочного поведения. Фильтр Baseline отделяет контрольные проверки. После реализации соответствующей спеки снять KnownDefect у исправленных тестов и расширить покрытие её матрицы; целевой gate — весь проект без фильтра.

Порядок: FS-05/06 → FS-01 → FS-02/03 → FS-04/09 → FS-07 → FS-10/11/12/13 и FS-08 независимо. Параллелизм FS-07 включать после защиты записи FS-02.

Наличие текущих тестов не означает полного покрытия: fault injection, ACL, гонки, cancellation и AOT перечислены в спеках как обязательные дополнительные проверки перед закрытием соответствующего пункта.

Вне этого цикла: [backlog.md](backlog.md).
