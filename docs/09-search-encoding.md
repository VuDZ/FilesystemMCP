# FS-09: Поиск использует общий text decoder

Приоритет: P2. Источники: SearchTool.IsTextFileAsync/CollectMatchesAsync, FileTextHelper.

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

SearchEncodingTests: одинаковый needle находится в каждой поддерживаемой encoding; текст и line=2 совпадают. UTF-8 варианты — baseline, UTF-16/32 — KnownDefect.

Добавить: CR-only/mixed EOL, BOM-only, binary early/late NUL, invalid sequence after 512-byte probe, BOM split across chunk, file disappearance и large line; результаты read/search decoder должны согласоваться. Byte probe должен работать при short read, а не ожидать заполненные 512 bytes.

Зависимости: FS-03, FS-04, FS-07; pagination/deduplicate matches — backlog.
