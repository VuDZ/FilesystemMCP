# FS-11: Пустая замена и whitespace snippets

Приоритет: P2. Источник: ReplaceInFileTool.GetRequiredString.

## Дефект

Schema разрешает replacement_snippet="", сервис умеет deletion, но tool отвергает пустой/whitespace text через IsNullOrWhiteSpace. Невозможно удалить фрагмент или заменить на перенос/пробелы.

## Контракт

- replacement_snippet обязательно присутствует и имеет JSON string type; разрешены "", " ", "\n", "\t" и любые combinations.
- target_snippet обязателен, string type и непустой по Length; whitespace-only допустим и сопоставляется точно.
- original_hash обязателен, непустой; ведущие/конечные whitespace не trim автоматически. path сохраняет общий contract.
- Missing/null/wrong type → -32602 через FS-05; не подставлять empty вместо отсутствующего поля.
- Delete может удалить всё содержимое, оставив корректный пустой файл в исходном encoding/BOM contract FS-03.
- Нормализация EOL для matching остаётся; поиск первого точного совпадения сохраняется. Не добавлять change-all/ambiguity rejection в этом пункте.
- Новый hash и snippet корректны для empty/whitespace итогового текста. BuildSnippet использует фактическую позицию правки, а не поиск пустого search token.

## Приёмка

EmptyReplacementTests: delete middle, delete all, replacement whitespace, whitespace-only target и SHA256 итогового normalized текста. Invalid empty target и отсутствующий replacement — контрольные tests.

Добавить: wrong types/null, newline replacement в CRLF/UTF-16, удаление terminal delimiter, длинный файл и snippet около фактической правки, несколько одинаковых targets (сохранить first-match). Expected match count/позиционный anchor — backlog.

Зависимости: FS-02/03/05/12. Schema и runtime validator обновляются вместе.
