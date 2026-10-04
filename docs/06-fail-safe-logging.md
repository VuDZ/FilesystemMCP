# FS-06: Логирование не влияет на файловые операции

Приоритет: P1. Источники: McpLogger, LogSanitizer, ToolRegistry.

Статус: выполнено.

Версия реализации: **1.7.0**. Принято независимым ревью: в первом раунде подняты замечания (deny-ACL только на каталоге, сбой внутри `LogFileSink.Write` не выводил sink из эксплуатации, after-commit hook не входил в `LoggingTests`, тест stderr не доходил до ветки fallback, bounded exit не отличал kill watchdog от штатного выхода, `LoggerUnavailableStillProducesTheResponse` не отключал logger), они исправлены и подтверждены во втором раунде.

## Дефект

Создание каталога logs происходит вне try/catch; занятое имя/ACL throws наружу. Ошибка логирования после commit может превратить успешную mutation в отказ. Повторное логирование exception усугубляет сбой.

## Контракт

- Все стадии logger (выбор пути, mkdir, format, serialize, open/write/flush) best effort: не выбрасывают исключений в tool/transport.
- По умолчанию доступный пользовательский каталог, а не каталог бинарника; отдельная настройка --logDirectory=<path> задаёт путь. Недоступный каталог отключает file sink с единичным безопасным сообщением stderr; process и tools продолжают работу.
- stdout зарезервирован за JSON-RPC. File log/error fallback никогда не пишет туда. Ошибка после commit не меняет результат success.
- Content/text/snippets не выводятся даже частично. По умолчанию логировать operation, безопасный relative path, lengths, hash/code; не считать усечение редактированием секретов.
- Единая bounded queue/writer для будущего параллелизма. Переполнение очереди допускает drop диагностических записей со счётчиком, но не блокирует transport.
- Ротация: 10 MiB на файл, до пяти файлов на process/session. Startup/cleanup не удаляют чужие logs. UTC timestamp и request correlation одинаковы для Info/Error.
- Exit flush ограничен по времени; logger failure не замещает исходный exception.

## Приёмка

LoggingTests запускает изолированную копию сервера с именем logs, занятым файлом, и явно заданным --logDirectory. Create всё равно успешен, bytes существуют, следующий ping жив. Отдельный тест проверяет отсутствие secret marker из content в stderr/file logs.

Добавить: denied ACL directory/file, full disk sink, formatting failure, queue pressure, rotation bounds, after-commit failure hook, stderr unavailable, simultaneous requests и bounded exit. Тесты используют отдельный process; не меняют global logger settings других тестов.

Зависимости: FS-05 для code/correlation, FS-07 для parallel dispatcher.

## Реализованный контракт и границы

Разделение ответственности: `McpLogger` (фасад вызовов из tool/transport), `Logging/BoundedLogQueue.cs` (`BoundedLogQueue`), `Logging/LogFileSink.cs` (`LogFileSink`), `Logging/Logging.cs` (`ILogSink`, `LogEntry`, общий формат строки).

`ILogSink` — единственная точка фактической записи: `Write(string line)`, `Flush()` и `IsFailed` (признак того, что sink больше не принимает записи). Формат строки одинаков для INFO и ERROR и задаётся writer-ом, а не вызывающей стороной: `[yyyy-MM-ddTHH:mm:ss.fffZ] [LEVEL] [req=<id>] <message>`, где `Z` и UTC-время обязательны, а часть `[req=…]` присутствует только при наличии correlation id. Поэтому «UTC timestamp и request correlation одинаковы для Info/Error» — свойство одного форматтера, а не соглашение двух вызывающих путей.

Все стадии pipeline обёрнуты в try/catch по отдельности: format → enqueue → dequeue (writer) → `ILogSink.Write` → flush, а вывод в stderr — отдельная best-effort стадия. Ни одна из них не пробрасывает исключение наружу, поэтому сбой логирования не может ни превратить успешную mutation в отказ, ни заменить исходное исключение: ERROR-запись вызывается после формирования ответа и изолирована от него.

**Путь запроса не выполняет ни одного ввода-вывода, включая stderr.** `Submit` только форматирует запись (под try/catch) и кладёт её в очередь под коротким lock; саму запись делает writer-поток, который сам выбирает назначение — файловый sink, пока тот здоров, иначе stderr. Поэтому «залипший» stderr (переполненный pipe у хост-процесса) не может задержать ответ, а `Shutdown` не ждёт освобождения state-lock: флаг ставится под lock, а `Join` с бюджетом выполняется уже без него. Ожидание ограничено бюджетом целиком, а не только сливом.

Две стадии различаются по последствиям. Сбой **format** локален для записи: наружу ничего не выходит, а запись уходит дальше с описанием самого сбоя, sink остаётся рабочим. Сбой **write** — это и исключение из sink, и отказ, который sink проглотил и отметил через `IsFailed` (запись, flush или ротация): запись считается потерянной, sink выводится из эксплуатации ровно один раз, и уведомление о fallback ставится в ту же очередь (значит, и отчёт о сбое не блокирует вызывающего), а последующие записи уходят в stderr. Признак выведенного из эксплуатации sink проверяется writer-ом для каждой записи, поэтому после первого сбоя ни одна запись не попадает в мёртвый sink — и уведомление остаётся единичным.

Тело цикла writer-а обёрнуто целиком: исключение не может убить поток (мёртвый writer означает молчаливую потерю всей диагностики), а короткая пауза в обработчике не даёт крутиться в 100 % CPU при постоянно падающей очереди.

### Очередь и writer

`BoundedLogQueue` — единственный буфер; `TryEnqueue` не блокирует transport (при переполнении запись отбрасывается, счётчик `DroppedCount` растёт, вызов возвращается сразу). Единственный фоновый поток writer (`IsBackground = true`) снимает очередь и пишет в назначение, поэтому порядок записей сохраняется, а стоимость ввода-вывода не попадает в обработку запроса. `Shutdown(TimeSpan)` ограничивает exit flush: writer получает deadline, дописывает то, что успел, закрывает sink и не ждёт дольше указанного времени. Сводка о потерянных записях отправляется один раз и только тогда, когда очередь уже пуста, чтобы агрегат не вытеснял живую диагностику; сбои записи считаются и в основном цикле, и на финальном сливе.

### Каталог и файл

`--logDirectory=<path>` (immutable `ServerOptions`, форма `--name=value`) задаёт каталог; неизвестное имя, пустое значение или дубликат — отказ запуска в stderr и ненулевой exit code. Без опции используется пользовательский каталог: `%LOCALAPPDATA%\FilesystemMCP\logs` на Windows, `$XDG_STATE_HOME/FilesystemMCP/logs` или `~/.local/state/FilesystemMCP/logs` на Unix, с fallback в `Path.GetTempPath()`; каталог бинарника не используется ни как основной, ни как fallback.

Недоступность каталога (занятое файлом имя, ACL, ошибка ввода-вывода) — не отказ запуска: sink отключается, в stderr уходит ровно одно безопасное сообщение, процесс продолжает обслуживать запросы. Ошибка открытия/записи/ротации/закрытия после запуска также переводит sink в отключённое состояние и не повторяет сообщение на каждую запись.

Файл сессии: `mcplog-<yyyyMMdd>-pid<pid>.log` (UTC-дата). Ротация при превышении 10 MiB: текущий файл получает следующий индекс, самые старые собственные индексные файлы удаляются, всего не более пяти файлов на session. Предел размера соблюдается и для отдельной записи: она усекается по границе UTF-8, поэтому один вызов не может перерасти лимит файла. Startup и cleanup работают только со своими именами: чужие logs (другой процесс/session, произвольные имена) не читаются, не переименовываются и не удаляются. Файл открывается на append с `FileShare.ReadWrite | FileShare.Delete`, поэтому внешний читатель (и тест) может прочитать лог во время работы процесса; `Close()` помечает sink закрытым, чтобы запись после закрытия не «оживила» файл, а неудачное открытие не оставляет открытый дескриптор.

### Что попадает в лог

`LogSanitizer` редактирует content-значения целиком: `content`, `text`, `target_snippet`, `replacement_snippet`, `snippet`, `body`, `data`, `message`, `old_string`, `new_string`, `old_text`, `new_text`, `pattern`, `regex` заменяются на `<redacted N chars>` (N — длина строки либо число элементов). Частичный вывод запрещён: усечение не считается редактированием секретов, поэтому content не логируется ни в каком объёме. Имя проверяется до рекурсии, поэтому глубина вложенности не обходит редактирование, а превышение `maxDepth` даёт fail-closed `<max depth>`. Не-content строки сохраняют прежнее поведение (escape управляющих символов, ограничение длины с явным маркером усечения); безопасный relative path остаётся видимым.

Платформенный `Message` исключения не логируется нигде, включая ветку `-32602`: `ToolErrorMapper.ArgumentFailureDetail` (текст идёт только в лог) отдаёт тип/HResult/стек, потому что `.NET`-сообщение аргументного исключения не принадлежит продукту и может содержать клиентский контент — например, сам шаблон регулярного выражения из `search`. `LogSanitizer` при этом отвечает только за *аргументы* вызова; сообщение исключения редактированию не подлежит, поэтому оно не попадает в лог вообще. `DefaultMaxLength` (200) и поведение `SanitizeText` не изменены — их использует контракт FS-05.

## Матрица проверки

`LoggingTests` (`Spec=FS-06`, 20 cases, все в Baseline) — живой stdio через `FilesystemMcp.TestHost` плюс unit-уровень для швов, которые нельзя наблюдать из другого процесса.

| Проверка | Что фиксирует |
|---|---|
| `UnavailableLogDirectoryDoesNotFailCreateOrTerminateServer` | Имя `logs` занято файлом и задано `--logDirectory`: create успешен, bytes на диске, следующий ping жив, чужой файл не затёрт, fallback сообщён ровно один раз |
| `ContentSecretDoesNotAppearInDiagnostics` | Маркер из `content` отсутствует в файле лога и stderr; операция, безопасный relative path и `<redacted …>` присутствуют |
| `DeniedLogDirectoryKeepsServerServingRequests` | Deny `WriteData/AppendData/CreateFiles` на каталоге логов: create и ping работают, отчёт о fallback единичный (повторные запросы не добавляют сообщений) |
| `DeniedLogFileKeepsPriorBytesAndReportsOnce` | Deny `WriteData/AppendData` на конкретном файле сессии: `Start` не бросает, sink выключен, прежние байты файла не меняются, fallback один, create успешен, следующий Info идёт в stderr |
| `SimultaneousRequestsProduceOneValidFrameEachAndNoContentInLogs` | 20 последовательных `tools/call`: по одному валидному фрейму на запрос, `isError=false`, 20 файлов, ни одного секрета в логах |
| `BoundedExitFlushesQueuedRecords` | EOF → процесс сам выходит в бюджете 2 с (exit code 0, watchdog не убивал); последний tool-record в файле, метка UTC на месте |
| `StdoutStaysReservedForProtocolFrames` | Отдельный процесс с `--logDirectory` и закрытым stdin: exit 0, stdout пуст |
| `UnknownOrDuplicateLogDirectoryOptionFailsStartupWithoutStdout` | `--logDirectory=` (пусто/пробелы), дубликат и неизвестная опция → exit 1, `Startup failed:` в stderr, stdout пуст |
| `UnavailableDirectoryReportsOnceAndKeepsDiagnosticsOnStderr` | Недоступное имя каталога: sink выключен, диагностика продолжается в stderr, отчёт один |
| `WriteFailureIsReportedOnceAndNeverPropagates` | Сбой стадии write до вызова sink: запись теряется и считается (`DroppedCount`), sink выводится из эксплуатации, fallback сообщается один раз, последующие записи идут в stderr, исключение не пробрасывается |
| `SinkInternalWriteFailureIsReportedOnceAndNeverPropagates` | Сбой внутри `LogFileSink.Write` (catch самого sink): та же потеря и счётчик, sink выведен из эксплуатации, fallback один, следующие записи в stderr, вызов не бросает |
| `QueueOverflowDropsRecordsWithoutBlockingTheCaller` | 5000 записей при заполненной очереди: вызовы не блокируются, `DroppedCount` растёт, уже принятые записи не вытесняются |
| `FrameBoundQueueKeepsOneRecordPerLineAndBoundsEntryLength` | Одна запись — одна строка, длина записи ограничена с явным маркером усечения |
| `RotationIsBoundedAndKeepsForeignLogs` | Маленькие bound-ы: ровно 5 своих файлов, шестого нет, новейшее содержимое сохранено, чужие `mcplog-*`/`other.log` не изменены |
| `FormattingFailureDoesNotEscapeAndLaterRecordsStillFlow` | Сбой стадии format: запись уходит в файл с описанием сбоя, следующая запись обрабатывается нормально |
| `StderrUnavailableKeepsDiagnosticsSilentAndProcessAlive` | Здоровый файловый sink при бросающем `ErrorWriter`: записи попадают в файл, stderr не вызывается (`FailureStderr` остаётся несъеденной), исключение не выходит наружу |
| `StderrBranchCountsTheLossAndKeepsTheWriterAlive` | File sink отсутствует, `ErrorWriter` бросает: `FailureStderr` съедается, потеря считается, исключение не выходит, writer жив и принимает следующую запись |
| `CompletionLogFailureAfterCommitDoesNotChangeCreateSuccess` | После commit бросающий completion-log не меняет success Create и байты на диске |
| `CompletionLogFailureDoesNotReplaceTheToolException` | Если tool и completion-log оба бросают, наружу выходит исходное исключение tool |
| `UtcTimestampAndCorrelationUseOneFormatForInfoAndError` | INFO и ERROR совпадают по формату `[UTC Z] [LEVEL] [req=…]`; `Message` исключения не логируется, тип и стек-трейс — логируются |

Приёмка (версия 1.7.0, Windows 10.0.26200, SDK 10.0.401): managed build — 0 warnings/errors; `Spec=FS-06` — 20 passed, 0 skipped, 0 failed; `Status=Baseline` — 220 passed, 8 skipped, 0 failed (228 cases); контрольный прогон `Spec=FS-05` — 28 passed. `Status=KnownDefect` и полный прогон в этом проходе не перезапускались (предыдущий снимок: KnownDefect — 0 passed, 28 failed, все — прежние FS-08…FS-13; полный прогон — 215 passed, 28 failed, 8 skipped, 251 cases).

## Независимое ревью и внесённые по нему правки

Ревью шло тремя ограниченными срезами (очередь/writer/shutdown; файловый sink и ротация; санитайзер, CLI-опции и stdout), каждый — чтение кода против контракта, без правок автора. Найденные дефекты исправлены до финального прогона:

- writer-поток мог умереть от исключения внутри обработчика сбоя sink (`AbandonSink` вызывался из `catch` без защиты) — тело итерации writer-а обёрнуто целиком, уведомление о fallback ставится в очередь вместо прямой записи в stderr;
- `Shutdown` мог ждать дольше бюджета: state-lock брался до `Join`, а `Submit` писал в stderr, держа этот lock, — путь запроса больше не делает ввода-вывода, а `Join` выполняется без lock;
- финальный слив не считал потерянные записи и мог не закрыть handle — счётчик и закрытие разведены по отдельным защищённым блокам;
- `LogSanitizer`-путь `-32602` мог попасть в лог с платформенным `Message` (включая клиентский regex) — `ArgumentFailureDetail` теперь отдаёт только тип/HResult/стек; вызов логгера на пути ответа дополнительно обёрнут;
- `LogFileSink`: один вызов мог превысить лимит файла (теперь усечение по границе UTF-8), `Open()` мог утечь дескриптор при сбое конструктора `FileStream`, `Close()` не помечал sink закрытым.

Срезы и их вердикты сохранены в истории сессии; повторные прогоны после правок: `Spec=FS-06` — 15 passed, `Spec=FS-05` — 28 passed, `Status=Baseline` — 215 passed / 0 failed, полный прогон — 215 passed, 28 failed (все KnownDefect FS-08…FS-13), 8 skipped.

Границы и известные ограничения этой эпохи:

- **stderr-ветка проверяется инъекцией, а не закрытым пайпом.** Windows-рантайм считает `ERROR_NO_DATA`/`ERROR_BROKEN_PIPE`/`ERROR_PIPE_NOT_CONNECTED` успехом записи, а невалидный с самого начала handle даёт `TextWriter.Null`; изнутри процесса «недоступный stderr» неотличим от «вывод потерян». Поэтому закрытый stderr не может быть триггером теста — используется шов `ErrorWriter`/`FailNextWrite` (стадии 0/1/2). Дополнительно: `ServerProcess.DrainStderrAsync` при закрытии своего конца пайпа не завершается, поэтому такой сценарий в harness не добавлен.
- **Читатель живого лога обязан разделять доступ.** `File.ReadAllText` открывает файл с `FileShare.Read` и падает с `IOException 0x80070020`, пока writer держит файл; sink отдаёт `FileShare.ReadWrite | FileShare.Delete`, и тесты/оператор читают лог с тем же режимом. Это не дефект продукта, но обязательное условие проверки.
- **Handles вместо `FileMode.Append`.** `LogFileSink` открывает файл через `File.OpenHandle(..., FileShare.ReadWrite | FileShare.Delete)` и `new FileStream(handle, FileAccess.Write)`: высокоуровневый append-конструктор с таким же `FileShare` на практике делает живой лог недоступным для чтения, а явный handle снимает вопрос и сохраняет append-семантику.
- **Отдельного управления уровнем/фильтром нет.** Все записи INFO/ERROR пишутся; объём ограничен очередью, длиной записи и ротацией, а не уровнем. Это соответствует контракту и остаётся границей эпохи.
- **Параллельный dispatcher — FS-07.** Очередь и единственный writer потокобезопасны, но одновременная обработка запросов появится вместе с FS-07; сейчас «simultaneous requests» означает последовательные запросы одного stdio-сеанса.
- **`Flush` на каждую запись.** Диагностический объём мал, поэтому sink сбрасывает буфер после каждой строки: это делает лог читаемым во время работы и не влияет на путь запроса (flush происходит на writer-потоке). При росте объёма это первое место для батчинга.
- **AOT publish не выполнялся**: на этом хосте отсутствует MSVC/Windows SDK (`Platform linker not found`), сборка и тесты — managed. Это существующее ограничение окружения, а не FS-06.

Зависимости: FS-05 для code/correlation, FS-07 для parallel dispatcher.
