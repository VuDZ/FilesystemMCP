# FilesystemMCP

Легковесный MCP-сервер для локальных файловых операций через JSON-RPC 2.0 по `stdio`.

## MCP Utilities

### `list_directory`
Возвращает список элементов в указанной директории workspace.

<details>
<summary>Параметры</summary>

- `path` (string) - относительный путь внутри `WorkspaceRoot`

</details>

### `read_file`
Читает содержимое файла (или диапазон строк) и возвращает хеши контента.

<details>
<summary>Параметры</summary>

- `path` (string) - относительный путь внутри `WorkspaceRoot`
- `start_line` (number, optional) - начальная строка
- `end_line` (number, optional) - конечная строка

</details>

### `search`
Ищет совпадения по регулярному выражению в файлах по маске.

<details>
<summary>Параметры</summary>

- `regex` (string) - регулярное выражение
- `file_mask` (string) - маска файлов (только безопасная относительная)

</details>

### `create_file`
Создает новый файл с переданным содержимым.

<details>
<summary>Параметры</summary>

- `path` (string) - относительный путь внутри `WorkspaceRoot`
- `content` (string) - содержимое файла

</details>

### `replace_in_file`
Заменяет фрагмент текста в файле с optimistic locking по исходному хешу.

<details>
<summary>Параметры</summary>

- `path` (string) - относительный путь внутри `WorkspaceRoot`
- `target_snippet` (string) - заменяемый фрагмент
- `replacement_snippet` (string) - новый фрагмент
- `original_hash` (string) - ожидаемый хеш исходного состояния

</details>

### `append_to_file`
Добавляет текст в конец существующего файла.

<details>
<summary>Параметры</summary>

- `path` (string) - относительный путь внутри `WorkspaceRoot`
- `content` (string) - добавляемое содержимое

</details>