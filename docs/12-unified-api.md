# FS-12: Один сервис операций и честный MCP API

Приоритет: P2. Источники: Program, MutationService, FileService, CreateFileTool, DTO, AGENTS.md.sample.

Статус: выполнено.

Версия реализации: 1.13.0. Независимое ревью этой реализации ещё не проводилось.

## Дефект

Стандартные tools и custom RPC methods имеют разные write/create semantics. list/search/append custom methods — заглушки с успешным result. Sample рекомендует несуществующий append tool. Direct create содержит File.Exists→Create race.

## Состояние после FS-07 (1.8.0), закрыто в 1.13.0

До этой эпохи пункт удаления прямых RPC-методов не был выполнен. Расхождение реализаций FS-07 уже убрал: `read_file`, `create_file`, `replace_in_file`, `list_directory` и `search` вызывали тот же registered tool, что и `tools/call`, поэтому `UnifiedApiTests.CustomRpcMethodsAreRejectedInsteadOfExecutingDivergentOrStubOperations` падал на всех шести `InlineData`. FS-12 удаляет сами методы: такой `method` получает JSON-RPC `-32601` и не выполняется. Оболочки `-32001` больше нет.

## Решение и контракт

- Сохранить стандартный MCP tools/call и пять tools: list_directory, search, create_file, read_file, replace_in_file.
- Прямые read_file/create_file/replace_in_file/list_directory/search/append_to_file RPC methods убрать; запрос с таким method получает -32601. Не оставлять success/stub или пустые псевдорезультаты.
- append_to_file не добавлять в этой итерации. Удалить рекомендацию из AGENTS.md.sample/README, future append вынести в backlog.
- Единый FileOperationsService использует PathPolicy, TextDocument decoder, AtomicFileWriter и error mapper. MutationService удаляется/сливается после перевода tool implementations; duplicate implementations не сохранять.
- Create использует atomic create-no-overwrite; text supplied line endings сохраняются по FS-03. Существующий файл → file_exists, concurrent create не заменяет bytes.
- Path aliases path/filePath/file_path сохранены. Schema требует хотя бы один из них через anyOf. Более одного непустого alias разрешать только при одинаковом значении; разные → -32602. Не выбирать путь молча.
- Validate additionalProperties и типы на сервере; schema сама по себе не заменяет runtime checks.
- Read/create/replace results стабильно содержат path и SHA256, MD5 сохраняется для совместимости. Replace сохраняет status/new_hash/snippet; обязательные общие поля согласовать без скрытых расхождений. List/search переходят на objects с metadata FS-04/10.
- Publish version увеличить при реализации API change; docs и client examples должны описывать migration с custom RPC на tools/call.

## Handshake (уточнено FS-13, версия 1.14.0)

Прямые method `read_file`, `create_file`, `replace_in_file`, `list_directory`, `search` и `append_to_file` по-прежнему JSON-RPC `-32601` и не выполняются. Handshake, который FS-12 не расписывал отдельно:

- Реализована одна MCP-версия: `2024-11-05`. Запрошенная поддерживаемая версия отражается. Любая другая строка, включая `2099-01-01` и `2025-06-18`, отвечает `2024-11-05`; значение клиента не копируется. Другие версии не заявляются.
- `initialize` требует `protocolVersion` (непустая строка), `capabilities` (object) и `clientInfo` (object со строковыми `name` и `version`). Нарушение типа или обязательности — `-32602`. Повторный успешный `initialize` — `-32600`.
- После ответа клиент шлёт `notifications/initialized`. До этого разрешены только `initialize` и `ping`; запрос tools — `-32600` с причиной про `notifications/initialized`. Исторический alias `initialized` без префикса тоже открывает сессию.
- JSON-RPC batch для этой версии не поддерживается: один `-32600` с `id:null`, элементы не выполняются.

## Приёмка

UnifiedApiTests: все шесть custom methods → -32601; sample не рекомендует append; conflicting path aliases отвергаются. Baseline: standard create не overwrite, tools/list объявляет пять tools.

Добавить schema/runtime parity для всех aliases и unknown properties, одинаковые defaults/services, hash after create/replace/read, concurrent create FS-02, source-generation/AOT DTO tests. Не добавлять тест, который просто сверяет строки исходного кода; sample contract test допустим как проверка опубликованной инструкции.

Зависимости: FS-01…05/09…11/13. SDK migration, structured output и append — backlog.

## Реализация (1.13.0)

- Прямые method `read_file`, `create_file`, `replace_in_file`, `list_directory`, `search`, `append_to_file` сняты с диспетчера `Program`. Неизвестный method, включая эти шесть, — `-32601` `Method not found`, без `result` и без записи. `append_to_file` не добавлен; рекомендация удалена из `AGENTS.md.sample` и README; будущий append остаётся B-11 в [backlog.md](backlog.md).
- `FileOperationsService` — единственный сервис чтения, создания и замены. Он использует `PathPolicy`, decoder `TextDocument` (через `AtomicFileWriter` / `FileContentReader`), `AtomicFileWriter` и перевод ошибок `FileErrorClassifier`. `MutationService` удалён. `create_file`, `read_file` и `replace_in_file` вызывают один экземпляр сервиса из composition root; `CreateFileTool` больше не пишет через собственный вызов `AtomicFileWriter`.
- Create остаётся atomic create-no-overwrite (`AtomicFileWriter`, `create: true`). Существующий файл — `file_exists`. Конкурирующий create не заменяет байты победителя. Переводы строк создаваемого текста сохраняются по FS-03.
- Алиасы `path` / `filePath` / `file_path` сохранены. Schema четырёх path-tools требует хотя бы один через `anyOf`. Строка из одних пробелов не является заданным путём — то же множество, что `PathPolicy.Resolve` (`string.IsNullOrWhiteSpace`): schema каждого алиаса — `minLength: 1` и `pattern: "\\S"`, а `ToolArguments.GetRequiredPath` отвечает `ArgumentException` → `-32602` до записи. Runtime также отвергает не-string и неизвестное свойство; два и более заданных алиаса допустимы только при одинаковом значении, разные — `-32602`. Путь из конфликтующих алиасов не выбирается.
- `additionalProperties: false` и типы проверяются на сервере, а не только схемой.
- Результаты create/read/replace стабильно содержат `path`, `md5`, `sha256`. Create сохраняет `status`. Replace сохраняет `status`, `new_hash`, `snippet`; `sha256` и `new_hash` — один дайджест. List и search остаются объектами: list — `entries` из `{name,type}` и метаданные усечения FS-07; search — `matches`/`truncated`/`incomplete`/`skipped_count`/`skipped` FS-04 и `truncation_reason` FS-07. Эти DTO сериализуются source-generated `McpJsonContext`.
- Версия публикации: `1.13.0` (`Version`, `AssemblyVersion` `1.13.0.0`, `FileVersion` `1.13.0.0`). README и `AGENTS.md.sample` описывают переход с custom RPC на `tools/call`.

Приёмка на Windows, Debug, сборка `dotnet build FilesystemMCP.sln -m:1 -p:UseSharedCompilation=false -p:PublishAot=false -p:PublishTrimmed=false --no-incremental` — 0 warnings, 0 errors. Затем `--no-build -p:PublishAot=false -p:PublishTrimmed=false`: `Spec=FS-12` — 14 cases, 14 passed, 0 failed, 0 skipped; `Status=Baseline` — 500 cases, 492 passed, 0 failed, 8 skipped; `Status=KnownDefect` — 2 cases, 0 passed, 2 failed (только прежние FS-13 `BooleanIdIsInvalidRequest` и `UnsupportedVersionFallsBackToImplementedVersion`); полный прогон — 502 cases, 492 passed, 2 failed, 8 skipped. Падающий набор полного прогона совпадает с KnownDefect. Внешний тайминговый дефект FS-07 `SearchBudgetTests.SearchThatGenuinelyExceedsTheOperationTimeoutReturnsAPartialResult` в этих прогонах не упал и здесь не исправлялся. До правки: Baseline 487 (479 passed, 8 skipped), KnownDefect 10 (FS-12 ×8 + FS-13 ×2), весь проект 497. Изменения матрицы: восемь прежних KnownDefect-случаев `UnifiedApiTests` переведены в Baseline, добавлено 5 cases (`SchemaAndRuntimeAgreeOnAliasesTypesAndUnknownProperties`, `CreateReplaceAndReadSharePathAndHashes`, `CreateReadAndReplaceShareOneServiceAndTheDefaultBudget`, `ConcurrentCreateDoesNotReplaceTheWinnerBytes`, `ResultsSerializeThroughTheSourceGeneratedContext`). 14 = 8 переведённых + 1 прежний Baseline `StandardCreateDoesNotOverwriteExistingFile` + 5 новых. KnownDefect 10 → 2, Baseline 487 → 500, проект 497 → 502.
