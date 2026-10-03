# FS-09: Поиск использует общий text decoder

Приоритет: P2. Источники: SearchTool.IsTextFileAsync/CollectMatchesAsync, FileTextHelper.

Статус: выполнено.

Версия реализации: **1.10.0**. Принято независимым ревью в трёх раундах: в первом подняты два замечания (устаревший комментарий `TextDocument` после переключения поиска на общий decoder; безусловная аллокация `Encoder` вне digest-пути вопреки комментарию), во втором — два документационных (устаревшая фраза в [04-file-locks.md](04-file-locks.md); противоречивый раздел приёмки этой спеки без измеренного снимка), все четыре закрыты и подтверждены прогонами; третий раунд — PASS без замечаний, ворота перепроверены. Попутно верифицирован Native AOT (publish exit 0 / 0 warnings; смоук initialize 1.10.0.0 / ping / search UTF-16 line=2; Baseline против AOT-exe 404/0/8 из 412 через `FILESYSTEM_MCP_TEST_SERVER`) и проведено кросс-спек ревью оркестратора (FS-02/03/04/05/07/10/12) — противоречий нет, лишь EOF-формулировка в test README приведена к норме R3/R10.

## Дефект

IsTextFileAsync запрещает любой NUL byte до BOM detection. UTF-16/UTF-32 с BOM содержит NUL в нормальном тексте и пропускается, хотя read_file умеет его читать.

## Контракт

- Один общий decoder/classifier FS-03 для read/search. Сначала BOM и encoding, затем проверка decoded content и binary policy.
- Поддержать UTF-8 no BOM/BOM, UTF-16 LE/BE BOM, UTF-32 LE/BE BOM. UTF-16 без BOM не угадывать; invalid input не декодировать с replacement.
- Regex работает над decoded строкой; BOM не считается символом первой строки. Line numbers остаются 1-based после CRLF/LF/CR parsing.
- Binary file пропускается с агрегированным счётчиком; unsupported encoding сообщает skipped reason и incomplete согласно FS-04. Не выдавать «нет совпадений» без metadata при skip из-за ошибки.
- Probe/decode используют один stream, сохраняя bytes за границей probe. Нельзя считать BOM-файл всегда безопасным: invalid sequences должны быть замечены и при поздней ошибке.
- Unicode path возвращается как логическое имя внутри workspace без ANSI conversion. Общий budget FS-07 применяется до загрузки huge line.

## Приёмка

SearchEncodingTests: одинаковый needle находится в каждой поддерживаемой encoding; текст и line=2 совпадают. Все шесть вариантов (UTF-8 no BOM/BOM, UTF-16/UTF-32 LE/BE) находятся в Baseline; четыре прежних KnownDefect-случая UTF-16/32 переведены в Baseline реализацией 1.10.0.

Матрица расширена по контракту: CR-only/mixed EOL, BOM-only, binary early/late NUL, invalid sequence after 512-byte probe, BOM split across chunk, file disappearance и large line; результаты read/search decoder согласуются (`ReadAndSearchDecodersAgreeOnEveryClassification`, включая UTF-16 без BOM → `binary_file`). Signature probe собирается по коротким чтениям и не ожидает заполненных 512 bytes (доказано швом `SearchTool.OpenReadStreamForTests` чтениями по 1 байту).

Приёмка (версия 1.10.0, Windows, SDK 10.0.401): managed build `-m:1 -p:UseSharedCompilation=false -p:PublishAot=false -p:PublishTrimmed=false --no-incremental` — 0 warnings/errors; `Spec=FS-09` — 23 passed, 0 failed, 0 skipped (23 cases); `Status=Baseline` — 404 passed, 8 skipped, 0 failed (412 cases); полный прогон — 404 passed, 19 failed, 8 skipped (431 cases), где все 19 падений — прежние KnownDefect FS-10(5)+FS-11(4)+FS-12(8)+FS-13(2), ни одного падения в FS-09 или Baseline. Подробности и матрица — в [test README](../FilesystemMcp.Tests/README.md). Windows-only sharing/ACL-сценарии проверены на текущем Windows host; Unix lanes не исполнялись. Native AOT (в среде появился C++ toolchain, блокировавший публикацию в эпоху 1.2.0): `dotnet publish FilesystemMCP.csproj -c Release -r win-x64` — exit 0, 0 warnings/errors; бинарник `bin/Release/net10.0/win-x64/publish/FilesystemMCP.exe` (FileVersion 1.10.0.0, 4 691 968 байт). Смоук по stdio (newline-delimited JSON, `--logDirectory` внутри temp-sandbox, транскрипт в снимке test README): initialize → `serverInfo {name:"FilesystemMCP", version:"1.10.0.0"}`; ping → `result:{}`; `tools/call search` по UTF-16 LE BOM-файлу (`first\r\nneedle Привет\r\n`) → `matches:[{path:"файл.txt",line:2}]`, `skipped_count:0`, `incomplete:false`, exit 0, stderr пуст.

Зависимости: FS-03, FS-04, FS-07; pagination/deduplicate matches — backlog.
