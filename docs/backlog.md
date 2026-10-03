# Backlog FilesystemMcp

Только улучшения сверх исправлений FS-01…FS-13. Обязательные сценарии приёмки этих исправлений остаются в соответствующих спеках. Статус всех задач: planned.

| ID | Приоритет | Задача | Результат / критерий готовности |
|---|---|---|---|
| B-01 | P2 | CI managed + Native AOT, Windows/Linux/macOS | Полный regression gate на managed; опубликованный бинарник проходит stdio contract suite. Publish/trim warnings разбираются, unsupported OS capabilities явно отмечены. |
| B-02 | P2 | Packaging publish | install2opencode.ps1, AGENTS.md.sample, opencode.json.sample и docs реально входят в publish output; smoke installation из чистого publish каталога. Установщик 1.9.0 (FS-08) требует рядом с собой `AGENTS.md.sample` — publish layout обязан его содержать. |
| B-03 | P2 | Pagination list/search | Cursor, детерминированный порядок, stable metadata и bounded ответ без пропусков/дубликатов при неизменной FS. Не смешивать с обязательным truncation FS-04/10. |
| B-04 | P2 | Search scopes/excludes/gitignore | Настраиваемый subtree, include/exclude, gitignore policy; объяснимые defaults для node_modules/generated/vendor. Не исключать пользовательский код молча. |
| B-05 | P2 | Deduplicate search matches | Режим unique path/line и полезный bounded excerpt; частый regex не расходует 50 результатов на одну строку. Migration текущей семантики документирован. |
| B-06 | P2 | Startup workspace diagnostics | Наличие, directory type, read/write readiness и понятная ошибка без первого tool; read-only workspace допустим в явном режиме. |
| B-07 | P2 | Матрица путей | Определить absolute/file URI/long paths/UNC/SMB/ADS/device names и Windows case-sensitive directories. Проверить Unicode в клиентах коллег, не нормализовать NFC/NFD автоматически. |
| B-08 | P2 | Hard-link policy / усиление path identity | Проверить inode/file identity, hard links наружу, rename roots и platform handle-based jail. Не заявлять полную sandbox гарантию до определённого threat model. |
| B-09 | P2 | Явные legacy encodings | Opt-in encoding=windows-1251 и UTF-16 no BOM с сохранением bytes; strict validation, zero silent auto-detect, AOT support/codepages packaging. |
| B-10 | P2 | Точность выбора patch | expected_match_count или position/anchor; исключить случайную правку первого совпадения при неоднозначном snippet. |
| B-11 | P3 | Append tool | Решить необходимость; если нужен — original_hash, atomic mutation, shared budgets/error model, без legacy stub. |
| B-12 | P3 | Официальный MCP SDK | Сравнить protocol maintenance с dependency/AOT cost; только после prototype trim/publish и parity suite. Не считать migration обязательным исправлением. |
| B-13 | P3 | Structured output / tool annotations | outputSchema/structuredContent, readOnly/destructive hints, capabilities только для реально поддержанных версий; сохранить text JSON fallback. |
| B-14 | P2 | Наблюдаемость и support bundle | Request/session ids, relative path, codes, sizes, skipped counts, metrics; opt-in sanitized bundle для реального Unicode/lock инцидента. Секреты не логируются. |
| B-15 | P2 | Extended fault/performance lab | SMB interruptions, antivirus/indexer locks, process crash между prepare/commit, memory/time profiles и долгие traversal. Не заменяет детерминированные tests FS-02/07. |
| B-16 | P3 | JSONC installer support | Parse/edit preserving comments и settings либо поддерживаемая миграция. С FS-08 (1.9.0) установщик уже отказывает без перезаписи: комментарии, trailing comma, одинарные кавычки и trailing content отвергаются строгим валидатором до любой записи, backup и temp не создаются. Остаётся именно поддержка JSONC, а не безопасный отказ. |
| B-17 | P3 | Административные ограничения внешних целей | Будущая optional allowedRoots policy может ограничивать разрешённые внешние targets при allowSymLinks=true. Сейчас true по умолчанию разрешает external links через logical workspace route; прямой outside absolute/../ остаётся запрещён. |

## Как вести backlog

Перед реализацией назначить владельца и выписать acceptance tests; технический риск не превращать в незаметную смену API. При новой реализации обновлять версию, README/samples и test matrix. Исправление конкретного дефекта закрывать по всей приёмке его FS-спеки, не только по первому passing test.
