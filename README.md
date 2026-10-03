# FilesystemMCP

**Version:** 1.9.0

[🇺🇸 English](#english-version) | [🇷🇺 Русский](#русская-версия)

---

## English Version

A lightweight, zero-dependency Model Context Protocol (MCP) server for local file operations, built with C# .NET 10 Native AOT.

Designed for autonomous LLM agent interactions via JSON-RPC 2.0 over `stdio`. It keeps incoming paths within the logical workspace, supports links to internal/external files by default, and uses optimistic locking for replacements. An explicit `--allowSymLinks=false` enables a physical workspace jail without links.

Compatible with Cursor, OpenCode, RooCode, and Claude Desktop. Implements the MCP handshake (`initialize`, `tools/list`, `prompts/list`, `resources/list`) and writes best-effort diagnostic logs to `mcplog-<date>-pid<pid>.log` in a per-user directory (not next to the executable); `--logDirectory=<path>` overrides it.

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

### Workspace links and result contracts

`FilesystemMCP.exe <workspace> [--allowSymLinks=true|false]` configures immutable startup options. Links are enabled by default. Unknown options, duplicate options, a bare flag, and values other than lowercase `true` or `false` fail startup with a nonzero exit and stderr diagnostics; stdout remains reserved for protocol frames.

With `false`, direct access through any file/directory symlink or Windows junction is rejected with `symlink_not_allowed`, including parents of a new file and a linked workspace root; ordinary paths stay in the physical jail. System ancestors above the root are physically canonicalized. With `true` (including omitted option), internal and external link targets are allowed when reached through an incoming path inside the logical workspace. Direct absolute outside paths and `../` escapes are rejected with `path_outside_workspace` in both modes, including temporary logical escapes followed by reentry. A linked startup root establishes the physical base of the logical workspace. Link chains and physical `..` are resolved component by component without Unicode normalization; incoming logical path depth is checked separately.

Windows drive/root-changing components such as `dummy/C:outside` are rejected in both modes; resolved paths are fully qualified. Colons remain valid filename characters on Unix. Windows directory link targets/root spellings are canonicalized using their opened handle's final path. Search tracks directory volume/file identity (dev/inode on Unix), so 8.3 aliases cannot cause repeated traversal; logical result aliases remain usable.

`list_directory` returns a JSON object `{"entries":[{"name":"example","type":"file"}]}`. Types are `file`, `directory`, and `link`; listing a link name does not open its target.

`search` returns a JSON object such as `{"matches":[{"path":"file.txt","line":1}],"truncated":false,"incomplete":true,"skipped_count":1,"skipped":[{"path":"link","reason":"symlink_not_allowed","code":"symlink_not_allowed"}]}` (shown for explicit `false`). `reason` is the canonical FS-04 field; `code` carries the same value for backward compatibility with the FS-01 baseline tests. `matches` holds at most 50 entries, and `truncated` reflects only that result/budget cap — it says nothing about completeness. `incomplete=true` means at least one item was skipped because of a lock, denied access, disappearance, or a link; `skipped_count` is the full number of skipped items even when the bounded `skipped` detail list is capped at 50 entries. An empty `matches` array must therefore not be read as "no matches" while `incomplete=true`. Search keeps going past these expected per-entry errors, retains the logical workspace alias in `path`, skips rejected/dangling/cyclic links and already-visited physical directories (`already_visited`), and shares one open read handle between the binary probe and the match scan, so a concurrent rename/delete cannot abort the whole search. In default `true` mode it searches external linked directories as well.

Path errors in `tools/call` return `result.isError=true` and JSON text with `code` and `message`. Codes added for paths are `symlink_not_allowed`, `path_outside_workspace`, `symlink_dangling`, `symlink_cycle`, `unsupported_reparse_point`, `path_changed`, `unsupported_safe_write`, and `path_is_directory` (root/existing directory used as a file). Legacy direct path methods use JSON-RPC error `-32001` with the same code in `message` until FS-12 removes them. The complete operational error mapping (all 13 codes, bounded `details`, `retryable`, unknown-tool/argument validation, correlation ids for unexpected defects) is implemented by FS-05 in version 1.6.0; see [FS-05 contract and code table](docs/05-tool-errors.md).

`read_file` and `search` open files with `FileShare.ReadWrite | FileShare.Delete` where the platform supports it; write sharing remains the FS-02 strategy and is deliberately not copied from reads. A direct read retries a transient sharing violation at most three times with 50/100 ms backoff and an overall deadline (honoring cancellation); `search` skips instead of retrying for responsiveness. Sharing/lock violations, denied access and disappearance are classified by HRESULT (`ERROR_SHARING_VIOLATION`/`ERROR_LOCK_VIOLATION` → `file_locked`, `ERROR_ACCESS_DENIED` → `access_denied`, file/path not found → `file_not_found`) through one shared classifier used by `read_file`, `list_directory`, `replace_in_file` and search, not by exception text. Access-denied, missing-file and `hash_conflict` outcomes are never retried, and a mutation is never retried after an uncertain commit. POSIX sharing is advisory, so the Windows lock behavior is not claimed as equivalent there.

Writes recheck policy before committing and use directory/leaf handles: Windows verifies the physical anchor handle, pins directories against delete sharing, and opens children relative to those handles with reparse following disabled. Atomic publication uses a handle-relative rename with POSIX replacement semantics; pinned handles are rechecked for reparse changes. Allowed external targets use their existing physical parent as the anchor. 64-bit Linux/macOS use `openat` with `O_NOFOLLOW` and compare pinned/live parent dev/inode identities before opening the leaf and before atomic publication. Unsupported platforms/scenarios fail closed. Root/existing-directory file writes are rejected before mutation. These guards prevent replacement links and detected parent identity swaps from redirecting a write. FS-02 atomic writes are accepted in version 1.3.0: all writers prepare a unique same-directory temp, flush to disk, recheck state, and publish atomically. Atomic replacement changes the selected name to a new inode while other hard links retain old bytes; Windows case-sensitive directory semantics remain a backlog limitation. A complete OS sandbox guarantee against arbitrary concurrent renames, including renames after commit, is not claimed.

Writes are serialized across cooperating local server processes by canonical physical path and file identity. Before commit, changes to bytes, identity or observed metadata return `hash_conflict` without mutation. This fresh check is not an OS compare-and-swap: a noncooperating external writer can still race the check and rename. Replacement preserves encoding/BOM, untouched line endings, ACL/access metadata and attributes; read-only files are rejected. Create retains supplied line endings and returns `status`, `md5`, and `sha256` for the complete normalized stored document (BOM excluded). After commit, late cancellation, logger or cleanup failures keep the successful result; mutations are never automatically retried. Temp data receives `Flush(true)` before publication, but directory/power-loss durability is not guaranteed. See [FS-02 guarantees and limitations](docs/02-atomic-writes.md).

Mutation tool failures use `result.isError=true` with JSON `code`/`message`: `hash_conflict`, `file_exists`, `access_denied`, `unsupported_encoding`, `binary_file`, `target_not_found`, and `file_not_found`. `hash_conflict` requires a fresh read before another patch. These were the FS-02 prerequisites; the complete FS-05 error table (including `directory_not_found`, `file_locked`, `path_outside_workspace`, `symlink_not_allowed`, `resource_limit`, `cancelled` and the `details`/`retryable` object) is implemented in version 1.6.0, and FS-06 logging configuration, redaction, queue and rotation are implemented in version 1.7.0.

### Diagnostics and logging

`FilesystemMCP.exe <workspace> [--allowSymLinks=true|false] [--logDirectory=<path>]`.

Logging is best effort by contract (FS-06, version 1.7.0): no stage — path selection, directory creation, formatting, queueing, file write or stderr fallback — can throw into a tool or the transport, and a logging failure after commit never changes a successful mutation into a failure. stdout carries protocol frames only.

- Default directory is per-user, not the binary directory: `%LOCALAPPDATA%\FilesystemMCP\logs` on Windows, `$XDG_STATE_HOME/FilesystemMCP/logs` (or `~/.local/state/FilesystemMCP/logs`) on Unix, with a temp-directory fallback.
- `--logDirectory=<path>` overrides it. A missing value or a duplicate option fails startup in stderr with a nonzero exit code; an *unavailable* directory (name occupied by a file, denied ACL, I/O error) does not: the file sink is disabled, one safe message goes to stderr, and the server keeps serving requests.
- Rotation: 10 MiB per file, at most five files per session, named `mcplog-<yyyyMMdd>-pid<pid>.log` plus `.1`…`.4`. Startup and cleanup touch only this session's names and never delete foreign logs.
- File contents are never logged, not even partially: `content`, `text`, `target_snippet`, `replacement_snippet`, `snippet`, `body`, `data`, `message`, `old_string`, `new_string`, `old_text`, `new_text`, `pattern` and `regex` become `<redacted N chars>`. Diagnostics carry the operation, the safe relative path, lengths, hash/code, a UTC timestamp (same format for INFO and ERROR) and the FS-05 request correlation id. Exception entries carry the type, HResult and stack trace, never the platform message.
- A single bounded queue with one background writer keeps logging off the request path: overflow drops diagnostic records (with a single aggregate counter notice) instead of blocking the transport, and process exit flushes the queue under a bounded deadline.

### Agent & OpenCode Templates

- `AGENTS.md.sample` - starter prompt/rules for autonomous agents working through this MCP server.
- `opencode.json.sample` - sample OpenCode MCP config for running `FilesystemMCP` as a local server.

When using templates:
- Copy `AGENTS.md.sample` to `AGENTS.md` and adapt rules to your workflow.
- Copy `opencode.json.sample` to your OpenCode config and set:
  - `command[0]` -> path to built `FilesystemMCP.exe`
  - `command[1]` -> target workspace root path

### Installer

`install2opencode.ps1` does the two steps above and merges into an existing configuration instead of replacing it:

```powershell
# from the publish directory, installing into another project
.\install2opencode.ps1 -WorkspacePath D:\MyProject
# from the target project, using the binary next to the script
C:\Tools\FilesystemMCP\install2opencode.ps1
# strict jail for this workspace
.\install2opencode.ps1 -WorkspacePath D:\MyProject -AllowSymLinks false
# show what would change, write nothing
.\install2opencode.ps1 -WorkspacePath D:\MyProject -WhatIf
```

- Existing `model`, other `mcp` servers, unknown top-level settings and the extra fields of an existing `filesystem-mcp` entry are preserved. Only `type` and the command prefix (binary, workspace) are rewritten; custom `--option` arguments survive, positional leftovers do not. `$schema` is added only when absent.
- Strict JSON only. Comments, trailing commas, single quotes, trailing content, a non-object root or a non-object `mcp` are refused with a nonzero exit and without touching the file. An explicit `"mcp": null` counts as "not configured". Unknown settings survive at any realistic nesting; a configuration nested deeper than the serializer can reproduce is refused rather than written with settings dropped.
- The first mutating run writes a byte-exact backup next to the config (`opencode.json.filesystemmcp-backup-*.bak`) and prints its path. A run whose config already matches writes nothing and creates no backup.
- Both files are prepared first, then written to a temp file and renamed into place. If the second commit fails, the config is restored from the backup and the recovery outcome is reported.
- `-AllowSymLinks` (default `true`) only shapes the command; direct relative/absolute escapes from the workspace stay rejected in both modes. `-WhatIf` reports without writing, `-AsJson` prints one JSON summary line. The script needs `AGENTS.md.sample` next to it.

### MCP Tools

All path-based tools accept the file location as **`path`**, **`filePath`**, or **`file_path`** (first non-empty wins; priority: `path` > `filePath` > `file_path`).

#### `list_directory`
Lists files and directories in the specified folder (non-recursive). Agents MUST use this to explore the project structure before assuming file paths.

<details>
<summary>Parameters</summary>

- `path` (string, required) - Relative path inside the `WorkspaceRoot`. Use `.` for the root directory.

</details>

#### `read_file`
Reads a text file and returns the content along with MD5/SHA256 hashes. Supports UTF-8 with or without BOM, and UTF-16/UTF-32 LE/BE when a BOM is present. Invalid bytes are rejected and left unchanged. Windows-1251 is not detected. Binary files are rejected.

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

**Версия:** 1.9.0

Легковесный MCP-сервер для локальных файловых операций через JSON-RPC 2.0 по `stdio`, написанный на C# .NET 10 Native AOT.

Разработан для автономной работы LLM-агентов. Входные пути ограничены логическим workspace; ссылки на внутренние и внешние файлы включены по умолчанию. `--allowSymLinks=false` включает физический jail без ссылок. Замены используют оптимистичную блокировку. [Контракт ссылок, результатов и ошибок](#workspace-links-and-result-contracts).

Совместим с Cursor, OpenCode, RooCode и Claude Desktop. Реализует MCP-handshake (`initialize`, `tools/list`, `prompts/list`, `resources/list`). Диагностические логи best-effort пишутся в `mcplog-<date>-pid<pid>.log` в пользовательском каталоге (не рядом с исполняемым файлом); `--logDirectory=<path>` переопределяет путь.

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

### Диагностика и логирование

`FilesystemMCP.exe <workspace> [--allowSymLinks=true|false] [--logDirectory=<path>]`.

Логирование по контракту best effort (FS-06, версия 1.7.0): ни одна стадия — выбор пути, создание каталога, форматирование, постановка в очередь, запись в файл или fallback в stderr — не может выбросить исключение в tool или transport, а сбой логирования после commit не превращает успешную mutation в отказ. stdout содержит только protocol frames.

- Каталог по умолчанию — пользовательский, а не каталог бинарника: `%LOCALAPPDATA%\FilesystemMCP\logs` на Windows, `$XDG_STATE_HOME/FilesystemMCP/logs` (или `~/.local/state/FilesystemMCP/logs`) на Unix, с fallback в каталог временных файлов.
- `--logDirectory=<path>` переопределяет путь. Отсутствующее значение или дубликат опции — отказ запуска в stderr с ненулевым exit code; **недоступный** каталог (имя занято файлом, denied ACL, ошибка ввода-вывода) — нет: file sink отключается, в stderr уходит одно безопасное сообщение, сервер продолжает обслуживать запросы.
- Ротация: 10 MiB на файл, не более пяти файлов на session, имена `mcplog-<yyyyMMdd>-pid<pid>.log` и `.1`…`.4`. Startup и cleanup работают только со своими именами и никогда не удаляют чужие logs.
- Содержимое файлов не логируется даже частично: `content`, `text`, `target_snippet`, `replacement_snippet`, `snippet`, `body`, `data`, `message`, `old_string`, `new_string`, `old_text`, `new_text`, `pattern` и `regex` заменяются на `<redacted N chars>`. В диагностике остаются операция, безопасный relative path, длины, hash/code, UTC-метка (одинаковый формат для INFO и ERROR) и request correlation id из FS-05. Запись об исключении содержит тип, HResult и стек-трейс, но не платформенный текст сообщения.
- Единая bounded queue и один фоновый writer держат логирование вне пути запроса: переполнение отбрасывает диагностические записи (с одним агрегированным уведомлением-счётчиком), а не блокирует transport; exit flush ограничен по времени.

### Шаблоны для агента и OpenCode

- `AGENTS.md.sample` - стартовый шаблон системных правил для автономного агента, работающего через этот MCP.
- `opencode.json.sample` - пример конфигурации OpenCode для запуска `FilesystemMCP` как локального MCP-сервера.

Как использовать:
- Скопируй `AGENTS.md.sample` в `AGENTS.md` и адаптируй правила под проект.
- Скопируй `opencode.json.sample` в конфиг OpenCode и укажи:
  - `command[0]` -> путь к собранному `FilesystemMCP.exe`
  - `command[1]` -> путь к целевой workspace-директории

### Установщик

`install2opencode.ps1` делает оба шага сам и **дополняет** существующую конфигурацию, а не заменяет её:

```powershell
# из publish-каталога, установка в другой проект
.\install2opencode.ps1 -WorkspacePath D:\MyProject
# из целевого проекта, binary рядом со скриптом
C:\Tools\FilesystemMCP\install2opencode.ps1
# строгий jail для этого workspace
.\install2opencode.ps1 -WorkspacePath D:\MyProject -AllowSymLinks false
# показать, что изменится, и ничего не писать
.\install2opencode.ps1 -WorkspacePath D:\MyProject -WhatIf
```

- Существующие `model`, другие `mcp` servers, неизвестные top-level настройки и дополнительные поля уже имеющегося `filesystem-mcp` сохраняются. Переписываются только `type` и префикс command (binary, workspace); пользовательские `--option` аргументы остаются, позиционные остатки — нет. `$schema` добавляется только при отсутствии.
- Только строгий JSON. Комментарии, trailing comma, одинарные кавычки, trailing content, необъектный корень или необъектный `mcp` — отказ с ненулевым кодом и без изменения файла. Явный `"mcp": null` считается «не настроено». Неизвестные настройки переживают установку на любой разумной вложенности; конфигурация, которую сериализатор не может воспроизвести без потери настроек, отвергается, а не записывается урезанной.
- Первый изменяющий запуск кладёт рядом с config побайтовый backup (`opencode.json.filesystemmcp-backup-*.bak`) и печатает его путь. Запуск, чей config уже совпадает, не пишет ничего и backup не создаёт.
- Оба файла сначала готовятся, затем пишутся во временный файл и переименовываются на место. Если второй commit сорвался, config восстанавливается из backup, а результат восстановления сообщается.
- `-AllowSymLinks` (по умолчанию `true`) влияет только на command: прямые absolute/`../` выходы из workspace запрещены в обоих режимах. `-WhatIf` показывает отчёт без записи, `-AsJson` печатает одну JSON-строку. Скрипту нужен `AGENTS.md.sample` рядом с собой.

### MCP Инструменты

Все tools с путём к файлу/директории принимают **`path`**, **`filePath`** или **`file_path`** (используется первое непустое; приоритет: `path` > `filePath` > `file_path`).

#### `list_directory`
Показывает файлы и директории в указанной папке (без рекурсии). Агенты обязаны использовать этот инструмент для изучения структуры проекта, прежде чем предполагать пути к файлам.

<details>
<summary>Параметры</summary>

- `path` (string, required) - относительный путь внутри `WorkspaceRoot`; используй `.` для корня.

</details>

#### `read_file`
Читает текстовый файл и возвращает содержимое с MD5/SHA256 хешами. Поддерживается UTF-8 без BOM и с BOM, а также UTF-16/UTF-32 LE/BE при наличии BOM. Невалидные байты отклоняются и не изменяются. Windows-1251 не определяется. Бинарные файлы отклоняются.

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

### План доработок и тесты / Development

- [Спецификации исправлений FS-01…FS-13](docs/README.md)
- [Backlog остальных улучшений](docs/backlog.md)
- [Отдельный regression test project и команды запуска](FilesystemMcp.Tests/README.md)

FS-01 реализован и принят независимым ревью в третьем раунде; версия 1.2.0 включает это самостоятельное исправление. FS-02 принят независимым ревью в первом раунде; версия 1.3.0 включает атомарную запись. FS-03 принят независимым ревью в первом раунде; версия 1.4.0 включает строгое декодирование и сохранение encoding, BOM и переводов строк. FS-04 принят независимым ревью в первом раунде; версия 1.5.0 включает корректный read sharing, единый поток probe+decode+search и неполный поиск с classification file_locked/access_denied/file_not_found. FS-05 реализован в версии 1.6.0: единый `ToolErrorMapper`, полная таблица кодов, безопасные `details`/`retryable`, `-32602` для unknown tool и формы аргументов, correlation id для непредвиденных дефектов. Принят независимым ревью: в первом раунде подняты замечания, они исправлены и подтверждены в раундах 2–3. FS-06 реализован в версии 1.7.0 и принят независимым ревью (в первом раунде подняты замечания, они исправлены и подтверждены во втором раунде): отказоустойчивый logger (best-effort все стадии, пользовательский каталог и `--logDirectory`, bounded queue с одним writer-ом, ротация 10 MiB × 5, запрет content в логах, UTC + correlation, bounded exit flush); полный текст — в [06-fail-safe-logging.md](docs/06-fail-safe-logging.md). FS-07 реализован в версии 1.8.0: бюджеты запуска на кадр, файл, строку, ответ, обход, дедлайн и параллельные чтения, отмена по `requestId` не блокирует ping, отказ чтения не материализует файл целиком; полный текст — в [07-resource-budgets.md](docs/07-resource-budgets.md). FS-08 реализован в версии 1.9.0: установщик разбирает строгий JSON, сохраняет неизвестные настройки, чужие servers и дополнительные поля `filesystem-mcp`, пишет config и AGENTS.md через temp + atomic rename, делает побайтовый backup только на изменяющем запуске, восстанавливает config из backup при отказе второго commit и получил `-AllowSymLinks`/`-WhatIf`/`-AsJson`; полный текст — в [08-installer-merge.md](docs/08-installer-merge.md). Остальные спецификации описывают запланированные изменения. KnownDefect проверяют ожидаемое исправленное поведение; Baseline содержит исправленные и контрольные сценарии.
