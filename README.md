# FilesystemMCP

[🇺🇸 English](#english-version) | [🇷🇺 Русский](#русская-версия)

---

## English Version

A lightweight, zero-dependency Model Context Protocol (MCP) server for local file operations, built with C# .NET 10 Native AOT.

Designed for safe, autonomous LLM agent interactions via JSON-RPC 2.0 over `stdio`. It features a strict "Workspace Jail" to prevent path traversal and optimistic locking to prevent code corruption during LLM hallucinations.

### Build & Installation

#### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/)

#### Building from Source
Since this is a Native AOT project, you can compile it into a single, standalone executable (no .NET runtime required on the target machine):

```bash
dotnet publish -c Release
```

The compiled binary will be located in your publish directory (e.g., `bin/Release/net10.0/win-x64/publish/`). The publish folder also includes `install2opencode.ps1` and `AGENTS.md.sample` for OpenCode setup.

### Client Configuration (Cursor / RooCode / Claude Desktop)
Add the compiled executable to your MCP client settings. 
**CRITICAL:** You MUST pass the target workspace directory as the first CLI argument. Without it, the server will block all file operations.

**Example for Cursor (Settings -> MCP):**
- **Name:** `FilesystemMCP`
- **Type:** `command`
- **Command:** `C:\path\to\FilesystemMcp.exe`
- **Args:** `C:\path\to\your\target\repository`

### OpenCode Installation (Recommended)

After `dotnet publish`, use `install2opencode.ps1` from the publish directory. It ships next to `FilesystemMCP.exe` and reads `AGENTS.md.sample` from the same folder.

**From the target project** (current directory becomes workspace):

```powershell
cd C:\path\to\your\target\repository
C:\path\to\publish\install2opencode.ps1
```

**From the publish directory** (pass workspace explicitly):

```powershell
cd C:\path\to\publish
.\install2opencode.ps1 -WorkspacePath C:\path\to\your\target\repository
```

The script:
- writes `opencode.json` with `mcp.filesystem-mcp` (`command[0]` = binary, `command[1]` = workspace root)
- creates or updates `AGENTS.md` from `AGENTS.md.sample` if the sample rules are not already present

### Agent & OpenCode Templates

- `AGENTS.md.sample` - starter prompt/rules for autonomous agents (copied to publish output).
- `opencode.json.sample` - reference OpenCode MCP config (manual setup alternative).
- `install2opencode.ps1` - automated installer (copied to publish output).

Manual setup (alternative):
- Copy `AGENTS.md.sample` to `AGENTS.md` and adapt rules to your workflow.
- Copy `opencode.json.sample` to `opencode.json` and set:
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
Reads a file's content and returns the text along with its MD5/SHA hash. Always use this tool before `replace_in_file` to get the current state and the required `hash`.

<details>
<summary>Parameters</summary>

- `path` (string, required) - Relative path inside the `WorkspaceRoot`.
- `start_line` (number, optional) - Starting line number.
- `end_line` (number, optional) - Ending line number.

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

Легковесный MCP-сервер для локальных файловых операций через JSON-RPC 2.0 по `stdio`, написанный на C# .NET 10 Native AOT.

Разработан для безопасной, автономной работы LLM-агентов. Включает строгую «Песочницу» (Workspace Jail) для защиты от выхода за пределы директории и механизм оптимистичной блокировки (optimistic locking) для предотвращения порчи кода при галлюцинациях нейросетей.

### Сборка и установка

#### Требования
- [.NET 10 SDK](https://dotnet.microsoft.com/)

#### Сборка из исходников
Так как это Native AOT проект, он компилируется в один независимый исполняемый файл (не требует установки рантайма .NET на целевой машине):

```bash
dotnet publish -c Release
```

Скомпилированный бинарник будет лежать в директории publish (например, `bin/Release/net10.0/win-x64/publish/`). В publish также попадают `install2opencode.ps1` и `AGENTS.md.sample` для установки в OpenCode.

### Настройка клиента (Cursor / RooCode / Claude Desktop)
Добавь скомпилированный файл в настройки MCP твоего клиента. 
**КРИТИЧНО:** Ты ОБЯЗАН передать целевую рабочую директорию (workspace) первым аргументом командной строки. Без неё сервер заблокирует любые операции с файлами.

**Пример для Cursor (Settings -> MCP):**
- **Name:** `FilesystemMCP`
- **Type:** `command`
- **Command:** `C:\path\to\FilesystemMcp.exe`
- **Args:** `C:\path\to\your\target\repository`

### Установка в OpenCode (рекомендуется)

После `dotnet publish` используй `install2opencode.ps1` из каталога publish. Скрипт лежит рядом с `FilesystemMCP.exe` и читает `AGENTS.md.sample` из той же папки.

**Из целевого проекта** (текущая директория = workspace):

```powershell
cd C:\path\to\your\target\repository
C:\path\to\publish\install2opencode.ps1
```

**Из каталога publish** (workspace передаётся явно):

```powershell
cd C:\path\to\publish
.\install2opencode.ps1 -WorkspacePath C:\path\to\your\target\repository
```

Скрипт:
- создаёт `opencode.json` с `mcp.filesystem-mcp` (`command[0]` = бинарник, `command[1]` = корень workspace)
- создаёт или дополняет `AGENTS.md` из `AGENTS.md.sample`, если правил ещё нет

### Шаблоны для агента и OpenCode

- `AGENTS.md.sample` - стартовый шаблон системных правил (копируется в publish).
- `opencode.json.sample` - пример конфигурации OpenCode (ручная настройка).
- `install2opencode.ps1` - автоматическая установка (копируется в publish).

Ручная настройка (альтернатива):
- Скопируй `AGENTS.md.sample` в `AGENTS.md` и адаптируй правила под проект.
- Скопируй `opencode.json.sample` в `opencode.json` и укажи:
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
Читает содержимое файла и возвращает текст и его MD5/SHA хеш. Этот инструмент всегда нужно вызывать перед `replace_in_file`, чтобы получить актуальный `hash`.

<details>
<summary>Параметры</summary>

- `path` (string, required) - относительный путь внутри `WorkspaceRoot`
- `start_line` (number, optional) - начальная строка
- `end_line` (number, optional) - конечная строка

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