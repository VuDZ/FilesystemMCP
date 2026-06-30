# FilesystemMCP

**Version:** 1.1.1

[🇺🇸 English](#english-version) | [🇷🇺 Русский](#русская-версия)

---

## English Version

A lightweight, zero-dependency Model Context Protocol (MCP) server for local file operations, built with C# .NET 10 Native AOT.

Designed for safe, autonomous LLM agent interactions via JSON-RPC 2.0 over `stdio`. It features a strict "Workspace Jail" to prevent path traversal and optimistic locking to prevent code corruption during LLM hallucinations.

Compatible with Cursor, OpenCode, RooCode, and Claude Desktop. Implements the MCP handshake (`initialize`, `tools/list`, `prompts/list`, `resources/list`) and writes diagnostic logs to `logs/mcplog-*.log` next to the executable.

### Build & Installation

#### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/)

#### Building from Source
Since this is a Native AOT project, you can compile it into a single, standalone executable (no .NET runtime required on the target machine):

```bash
dotnet publish -c Release
```

The compiled binary will be located in your publish directory (e.g., `bin/Release/net10.0/win-x64/publish/`).

**Versioning:** the release version is defined in `FilesystemMCP.csproj` (`<Version>`). Increment it on every functional update before publishing a new binary.

### Client Configuration (Cursor / OpenCode / RooCode / Claude Desktop)
Add the compiled executable to your MCP client settings. 
**CRITICAL:** You MUST pass the target workspace directory as the first CLI argument. Without it, the server will block all file operations.

**Example for Cursor (Settings -> MCP):**
- **Name:** `FilesystemMCP`
- **Type:** `command`
- **Command:** `C:\path\to\FilesystemMcp.exe`
- **Args:** `C:\path\to\your\target\repository`

### Agent & OpenCode Templates

- `AGENTS.md.sample` - starter prompt/rules for autonomous agents working through this MCP server.
- `opencode.json.sample` - sample OpenCode MCP config for running `FilesystemMCP` as a local server.

When using templates:
- Copy `AGENTS.md.sample` to `AGENTS.md` and adapt rules to your workflow.
- Copy `opencode.json.sample` to your OpenCode config and set:
  - `command[0]` -> path to built `FilesystemMCP.exe`
  - `command[1]` -> target workspace root path

### MCP Tools

#### `list_directory`
Lists files and directories in the specified folder (non-recursive). Agents MUST use this to explore the project structure before assuming file paths.

<details>
<summary>Parameters</summary>

- `path` (string, required) - Relative path inside the `WorkspaceRoot`. Use `.` for the root directory.

</details>

#### `read_file`
Reads a text file and returns the content along with MD5/SHA256 hashes. Supports UTF-8 and UTF-16 (with BOM). Binary files are rejected.

**Hash semantics:** `md5` and `sha256` always describe the **full file** after normalizing line endings (`\r\n` → `\n`). This is the locking hash for `replace_in_file`, even when `text` contains only a line range.

**Line limits (context protection):**
- Default: up to **1000 lines** per request (full file or explicit range).
- For larger reads, the agent must opt in explicitly:
  - `allow_large_read: true` — raises the limit (up to 50,000 lines by default).
  - `max_lines: N` — custom cap when used together with `allow_large_read` (1–50,000).

Prefer `start_line` / `end_line` for partial reads before requesting a full large file.

Always call this tool before `replace_in_file` to obtain the current `hash`.

<details>
<summary>Parameters</summary>

- `path` (string, required) - Relative path inside the `WorkspaceRoot`.
- `start_line` (number, optional) - Starting line number (1-based). Must be used together with `end_line`.
- `end_line` (number, optional) - Ending line number (1-based). Must be used together with `start_line`.
- `allow_large_read` (boolean, optional, default `false`) - Explicit opt-in to read more than 1000 lines.
- `max_lines` (number, optional) - Custom line limit when `allow_large_read` is `true` (max 50,000).

</details>

<details>
<summary>Examples</summary>

Read the first 200 lines:
```json
{ "path": "src/Program.cs", "start_line": 1, "end_line": 200 }
```

Read a large log file (first 5000 lines):
```json
{ "path": "app.log", "allow_large_read": true, "max_lines": 5000 }
```

</details>

#### `search`
Searches for regex matches across files and returns file paths with line numbers. Features a hard limit of 50 total matches to prevent context blowout.

<details>
<summary>Parameters</summary>

- `regex` (string, required) - The regular expression to search for.
- `file_mask` (string, optional) - File extension filter (e.g., `*.cpp` or `*.cs`).

</details>

#### `create_file`
Creates a strictly NEW file. Do not use this to edit existing files (use `replace_in_file` instead).

<details>
<summary>Parameters</summary>

- `path` (string, required) - Relative path inside the `WorkspaceRoot`.
- `content` (string, required) - Content of the new file.

</details>

#### `replace_in_file`
Replaces the first exact match of a text snippet in a file using optimistic locking. The `original_hash` is mandatory and must be obtained from the latest `read_file` call.

<details>
<summary>Parameters</summary>

- `path` (string, required) - Relative path inside the `WorkspaceRoot`.
- `target_snippet` (string, required) - The exact code block to be replaced.
- `replacement_snippet` (string, required) - The new code block.
- `original_hash` (string, required) - The hash of the file's current state.

</details>

---

## Русская версия

**Версия:** 1.1.1

Легковесный MCP-сервер для локальных файловых операций через JSON-RPC 2.0 по `stdio`, написанный на C# .NET 10 Native AOT.

Разработан для безопасной, автономной работы LLM-агентов. Включает строгую «Песочницу» (Workspace Jail) для защиты от выхода за пределы директории и механизм оптимистичной блокировки (optimistic locking) для предотвращения порчи кода при галлюцинациях нейросетей.

Совместим с Cursor, OpenCode, RooCode и Claude Desktop. Реализует MCP-handshake (`initialize`, `tools/list`, `prompts/list`, `resources/list`). Диагностические логи пишутся в `logs/mcplog-*.log` рядом с исполняемым файлом.

### Сборка и установка

#### Требования
- [.NET 10 SDK](https://dotnet.microsoft.com/)

#### Сборка из исходников
Так как это Native AOT проект, он компилируется в один независимый исполняемый файл (не требует установки рантайма .NET на целевой машине):

```bash
dotnet publish -c Release
```

Скомпилированный бинарник будет лежать в директории publish (например, `bin/Release/net10.0/win-x64/publish/`).

**Версионирование:** номер версии задаётся в `FilesystemMCP.csproj` (`<Version>`). Увеличивай его при каждом функциональном обновлении перед публикацией нового бинарника.

### Настройка клиента (Cursor / OpenCode / RooCode / Claude Desktop)
Добавь скомпилированный файл в настройки MCP твоего клиента. 
**КРИТИЧНО:** Ты ОБЯЗАН передать целевую рабочую директорию (workspace) первым аргументом командной строки. Без неё сервер заблокирует любые операции с файлами.

**Пример для Cursor (Settings -> MCP):**
- **Name:** `FilesystemMCP`
- **Type:** `command`
- **Command:** `C:\path\to\FilesystemMcp.exe`
- **Args:** `C:\path\to\your\target\repository`

### Шаблоны для агента и OpenCode

- `AGENTS.md.sample` - стартовый шаблон системных правил для автономного агента, работающего через этот MCP.
- `opencode.json.sample` - пример конфигурации OpenCode для запуска `FilesystemMCP` как локального MCP-сервера.

Как использовать:
- Скопируй `AGENTS.md.sample` в `AGENTS.md` и адаптируй правила под проект.
- Скопируй `opencode.json.sample` в конфиг OpenCode и укажи:
  - `command[0]` -> путь к собранному `FilesystemMCP.exe`
  - `command[1]` -> путь к целевой workspace-директории

### MCP Инструменты

#### `list_directory`
Показывает файлы и директории в указанной папке (без рекурсии). Агенты обязаны использовать этот инструмент для изучения структуры проекта, прежде чем предполагать пути к файлам.

<details>
<summary>Параметры</summary>

- `path` (string, required) - относительный путь внутри `WorkspaceRoot`; используй `.` для корня.

</details>

#### `read_file`
Читает текстовый файл и возвращает содержимое с MD5/SHA256 хешами. Поддерживает UTF-8 и UTF-16 (с BOM). Бинарные файлы отклоняются.

**Семантика хеша:** `md5` и `sha256` всегда описывают **весь файл** после нормализации переводов строк (`\r\n` → `\n`). Это locking-хеш для `replace_in_file`, даже если в `text` возвращён только диапазон строк.

**Лимиты строк (защита контекста):**
- По умолчанию: не более **1000 строк** за запрос (целиком или диапазон).
- Для больших файлов агент должен явно указать:
  - `allow_large_read: true` — поднимает лимит (по умолчанию до 50 000 строк).
  - `max_lines: N` — свой лимит вместе с `allow_large_read` (1–50 000).

Сначала используй `start_line` / `end_line` для частичного чтения, прежде чем запрашивать весь большой файл.

Перед `replace_in_file` всегда вызывай этот инструмент, чтобы получить актуальный `hash`.

<details>
<summary>Параметры</summary>

- `path` (string, required) - относительный путь внутри `WorkspaceRoot`
- `start_line` (number, optional) - начальная строка (с 1). Только вместе с `end_line`.
- `end_line` (number, optional) - конечная строка (с 1). Только вместе с `start_line`.
- `allow_large_read` (boolean, optional, default `false`) - явное разрешение читать больше 1000 строк.
- `max_lines` (number, optional) - свой лимит при `allow_large_read: true` (максимум 50 000).

</details>

<details>
<summary>Примеры</summary>

Первые 200 строк:
```json
{ "path": "src/Program.cs", "start_line": 1, "end_line": 200 }
```

Большой лог (первые 5000 строк):
```json
{ "path": "app.log", "allow_large_read": true, "max_lines": 5000 }
```

</details>

#### `search`
Ищет совпадения по регулярному выражению (regex) в файлах и возвращает пути + номера строк. Встроен жесткий лимит: максимум 50 совпадений суммарно для защиты контекстного окна.

<details>
<summary>Параметры</summary>

- `regex` (string, required) - регулярное выражение для поиска
- `file_mask` (string, optional) - маска файлов, например `*.cpp` или `*.cs`

</details>

#### `create_file`
Создает строго новый файл. Не используй для изменения существующих файлов (для этого есть `replace_in_file`).

<details>
<summary>Параметры</summary>

- `path` (string, required) - относительный путь внутри `WorkspaceRoot`
- `content` (string, required) - содержимое нового файла

</details>

#### `replace_in_file`
Заменяет первое точное вхождение фрагмента текста в файле с использованием оптимистичной блокировки. Параметр `original_hash` обязателен и должен быть получен из последнего вызова `read_file`.

<details>
<summary>Параметры</summary>

- `path` (string, required) - относительный путь внутри `WorkspaceRoot`
- `target_snippet` (string, required) - заменяемый фрагмент кода
- `replacement_snippet` (string, required) - новый фрагмент кода
- `original_hash` (string, required) - хеш актуального состояния файла

</details>