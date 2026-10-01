# FS-08: Установщик сохраняет конфигурацию пользователя

Приоритет: P1. Источник: install2opencode.ps1.

## Дефект

Write-OpenCodeConfig генерирует новый config и стирает existing model/mcp servers. Нет backup/merge/atomic commit. Отказ последующего AGENTS update оставляет partial install.

## Контракт

- При существующем opencode.json разобрать поддерживаемый строгий JSON, сохранить все unknown settings/servers, изменить только mcp.filesystem-mcp и добавить schema только если она отсутствует.
- Existing filesystem-mcp: обновить type/command, сохранить прочие параметры этого server. При JSONC/invalid JSON/необъектном mcp отказаться без изменения файла; поддержку JSONC вынести в backlog.
- Первый изменяющий запуск создаёт уникальный backup точных исходных bytes. Backup path сообщается пользователю. Повторный идентичный запуск не переписывает config и не создаёт лишний backup.
- Prepare/validate config и AGENTS до публикации. Temp write + atomic rename; если второй commit сорвался, восстановить первый из backup и сообщить recovery outcome. Не обещать crash-atomic transaction двух файлов: на restart должен быть понятный recovery path.
- BinaryPath обязан быть существующим файлом, WorkspacePath — директорией; LiteralPath для путей с Unicode/скобками. Значения сериализовать JSON API, не вставлять raw strings.
- Согласовать будущую настройку -AllowSymLinks с пользовательским override FS-01: boolean default true; явное false добавляет --allowSymLinks=false в command. На reinstall корректно убрать старый флаг при default true. Прямые outside absolute/../ пути остаются запрещены в обоих режимах. Эта настройка installer ещё не реализована.
- AGENTS существующие правила сохраняет, повторное добавление не дублирует раздел. Несовместимые типы узлов — явный отказ вместо overwrite.

## Приёмка

InstallerTests изолированно запускает PowerShell: model, другие servers и unknown options сохраняются; invalid JSON не изменяется. Fixtures содержат spaces/кириллицу. Binary/Workspace тестовые, не изменять настоящий пользовательский config.

Добавить: fresh install, idempotent bytes, exact backup, existing server extra fields, invalid mcp types, binary directory/workspace file, failure второго commit/recovery, denied permissions, -AllowSymLinks/default transition и AGENTS idempotency. Проверять PowerShell 5.1 и 7 там, где они доступны.

Зависимости: FS-01 parser, FS-12 актуальный sample; publish packaging вынесен отдельно в backlog.
