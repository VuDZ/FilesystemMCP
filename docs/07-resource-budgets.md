# FS-07: Ограничение ресурсов, cancellation и отзывчивый transport

Приоритет: P1. Источники: Program.Main, FileTextHelper, FileService, SearchTool, ListDirectoryTool.

## Дефект

Лимит строк применяется после полного ReadToEnd и hash byte[]; огромная строка не ограничена. Main ждёт tool до чтения нового frame, поэтому ping/cancel задерживаются. Regex timeout не ограничивает traversal.

## Настройки и контракт

Все лимиты положительны, проверяются на startup; CLI --name=value:

| Настройка | Default | Назначение |
|---|---:|---|
| maxRequestBytes | 1048576 | UTF-8 bytes одного frame |
| maxFileBytes | 67108864 | bytes одного текстового файла |
| maxLineChars | 262144 | UTF-16 code units одной строки |
| maxResponseChars | 1048576 | characters payload before framing |
| searchMaxFiles | 100000 | посещённые files |
| searchMaxDirectories | 10000 | посещённые directories |
| operationTimeoutMs | 30000 | deadline одного tool |
| maxConcurrentReads | 4 | ограниченный read/search/list parallelism |

- Bounded frame reader при превышении maxRequestBytes дочитывает/отбрасывает frame до LF, возвращает -32600 с id=null, затем принимает следующий frame. Из слишком большого frame не выполнять tools даже частично.
- file/line cap → resource_limit до materialization огромного string/byte[]. Range read не отменяет maxFileBytes, поскольку hash нужен для всего файла.
- Compute hash потоково; memory зависит от chunk, selected range и ограниченного output, не от полного файла. Не строить full-file UTF-8 copy.
- Search budget/deadline → partial result с truncated/incomplete и reason. Превышение output не создаёт invalid JSON/обрезанный text content.
- Отдельный reader принимает ping и notifications/cancelled во время tool. Ping отвечает независимо от очереди tool; cancellation привязан к requestId. Один output writer сохраняет целостность frames.
- Reads могут выполняться одновременно до limit; mutations координируются по файлу FS-02. EOF/shutdown отменяет незавершённые операции и не повреждает originals.
- Cancellation до commit → cancelled; после commit → success FS-02. Не поддерживаемые peer notifications не получают response.

## Приёмка

ResourceBudgetsTests: CLI maxFileBytes с маленьким порогом отклоняет файл; превышенный input frame не создаёт файл и следующий ping обрабатывается. Fixtures малого размера, без реального OOM.

Добавить при реализации fake stream/hook tests для chunk allocation, oversized line, output budget, traversal caps, timeout, cancellation и concurrent-read limit. Stdio test с barrier-held tool: ping успевает до release, cancel останавливает tool. Таймер лишь watchdog, не средство синхронизации. Не закрывать спеку без этих проверок.

Зависимости: FS-01/02/04/05/06. Integration load/AOT/Linux CI — backlog.
