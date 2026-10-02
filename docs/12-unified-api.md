# FS-12: Один сервис операций и честный MCP API

Приоритет: P2. Источники: Program, MutationService, FileService, CreateFileTool, DTO, AGENTS.md.sample.

## Дефект

Стандартные tools и custom RPC methods имеют разные write/create semantics. list/search/append custom methods — заглушки с успешным result. Sample рекомендует несуществующий append tool. Direct create содержит File.Exists→Create race.

## Состояние после FS-07 (1.8.0)

Пункт 12 ниже (удаление прямых RPC-методов) **не выполнен** — это работа FS-12. Но расхождение реализаций, из-за которого пункт 12 существует, уже устранено: `read_file`, `create_file`, `replace_in_file`, `list_directory` и `search` больше не имеют собственных stub/divergent путей, они вызывают тот же registered tool, что и `tools/call`. Практическое следствие для приёмки: `UnifiedApiTests.CustomRpcMethodsAreRejectedInsteadOfExecutingDivergentOrStubOperations` падает на всех шести `InlineData`, потому что методы теперь **выполняют** операцию, а не возвращают заглушку или успешный псевдорезультат. Это ожидаемое промежуточное состояние: FS-07 закрыл именно расхождение semantics, а FS-12 удаляет сами методы и переводит их KnownDefect-случаи в `-32601`. До тех пор оболочка ответа legacy-методов остаётся принятой FS-01/FS-04 (`-32001`, без `details`), а лимиты, коды и cancellation у них общие с tool (FS-07 R16).

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

## Приёмка

UnifiedApiTests: все шесть custom methods → -32601; sample не рекомендует append; conflicting path aliases отвергаются. Baseline: standard create не overwrite, tools/list объявляет пять tools.

Добавить schema/runtime parity для всех aliases и unknown properties, одинаковые defaults/services, hash after create/replace/read, concurrent create FS-02, source-generation/AOT DTO tests. Не добавлять тест, который просто сверяет строки исходного кода; sample contract test допустим как проверка опубликованной инструкции.

Зависимости: FS-01…05/09…11/13. SDK migration, structured output и append — backlog.
