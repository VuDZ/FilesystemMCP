# FS-03: Строгое декодирование и сохранение формата

Приоритет: P1. Источники: FileTextHelper, FileService, MutationService.

Статус: выполнено.

Версия реализации: **1.4.0**. Принято независимым ревью в первом раунде.

## Дефект

Не-UTF-8 без BOM читается permissive UTF-8 с replacement fallback. Windows-1251 кириллица превращается в U+FFFD и закрепляется записью. Patch UTF-16 BOM + CRLF преобразует весь файл в UTF-8 no BOM + LF.

## Контракт

- Поддержать UTF-8 без/с BOM, UTF-16 LE/BE BOM, UTF-32 LE/BE BOM. Использовать строгие decoders с exception fallback для всех кодировок, включая неполные последние sequences.
- Без BOM принимать только валидный UTF-8. Невалидный поток — unsupported_encoding и bytes остаются прежними. В этом цикле CP1251 auto-detect и explicit legacy encoding не добавлять; вынести в backlog.
- TextDocument хранит decoded text, encoding, BOM и исходные переводы строк. Для поиска/locking hash — normalized LF text; для хранения — исходный формат.
- В нетронутых участках сохранить bytes. Вставленный normalized snippet использует стиль заменяемого участка, при отсутствии переносов в нём — преобладающий стиль файла, при равенстве — первый встретившийся; для файла без переносов — LF. Смешанные окончания вне изменяемого span сохранить.
- BOM не входит в decoded text/hash. Окончание файла сохранять, если patch не изменяет его явно. Create без отдельной настройки пишет UTF-8 no BOM и не меняет supplied line endings.
- Успешное чтение не должно «исправлять» неверные bytes незаметно. Binary detection отделить от decode, общий механизм использовать в FS-09.

## Реализация

Не пытаться восстановить формат после полного NormalizeLineEndings/WriteUtf8. Нужна карта normalized offsets → исходные spans либо эквивалентное представление. Hash legacy semantics сохранить для поддерживаемого текста; client hash всегда описывает целый normalized файл, в том числе при range.

## Приёмка

TextEncodingTests: CP1251 rejected; UTF-8 BOM, UTF-16/UTF-32 LE/BE BOM и CRLF сохраняются после ASCII replacement. Baseline UnicodePathTests проверяет кириллицу/中文/emoji в путях и stdio.

Дополнить: invalid UTF-8/UTF-16/UTF-32 в начале и конце, BOM split на границе buffer, mixed EOL, CR-only, empty/BOM-only файл, trailing newline, replacement с многострочным текстом, hash parity между read/search parser. Ошибка encoder до commit сохраняет оригинал FS-02.

Unicode normalization имён не входит в исправление; InvariantGlobalization не менять без отдельного failing repro. Зависимости: FS-02/05.

## Реализованный контракт

`TextDocument` — общий strict parser чтения и будущих вызовов FS-09. `Decode(byte[])` разбирает уже собранный буфер. `ParseAsync(Stream)` сначала склеивает короткие `Read`, и только потом выбирает подпись: BOM, разрезанный между чтениями (в том числе по 1–3 байта), не теряется. Неполная хвостовая sequence отклоняется строгим decoder, без подстановки U+FFFD. `Classify` / `ClassifyAsync` отдельно сообщают `Text`, `Binary` или `UnsupportedEncoding`.

Поддержаны UTF-8 без BOM и с BOM, UTF-16 LE/BE BOM, UTF-32 LE/BE BOM. Экземпляры кодировок создаются с `throwOnInvalidBytes: true`. `DecoderFallbackException` становится `unsupported_encoding`. `hash_conflict`, `target_not_found`, `binary_file` и отмена не перехватываются. Без BOM принимается только валидный UTF-8. CP1251 и UTF-16 без BOM не угадываются; это B-09.

Сырой NUL не заменяет декодирование BOM-кодировок: в валидном UTF-16/UTF-32 такие байты обычны, и при корректной подписи файл остаётся текстом. Декодированный U+0000 в UTF-8 без BOM — `binary_file`.

Документ хранит исходный текст, encoding, байты BOM и карту normalized offset → исходный span. Поиск и locking hash считают `FileTextHelper.ComputeContentHashes` от всего LF-нормализованного текста без BOM; range read хеширует тот же целый текст. Незатронутые spans, их переводы строк и BOM остаются теми же байтами. Вставленный normalized snippet берёт стиль заменяемого span; если в span нет переносов — преобладающий стиль файла; при равенстве счётчиков — первый встретившийся стиль, а не порядок словаря; файл без переносов — LF.

Create без отдельной настройки пишет UTF-8 без BOM и не переписывает переданные переводы строк. Если supplied text начинается с U+FEFF, эти байты становятся физической UTF-8 BOM, а success hash исключает её так же, как последующее чтение. Encoder завершается в `Replace` до создания temp; сбой encoder/decoder до commit не меняет исходные bytes.

FS-09 реализован в версии 1.10.0: `SearchTool` использует общий scanner `FileContentReader.SearchLinesAsync` (тот же `ScanOnceAsync`, что и read_file), а не `TextDocument.ParseAsync`; NUL-probe до BOM-детекции и permissive UTF-8 reader поиска удалены. Паритет read/search подтверждён тестом `ReadAndSearchDecodersAgreeOnEveryClassification`; `SearchEncodingTests` для UTF-16/UTF-32 переведены в Baseline. Нормализация имён NFC/NFD и `InvariantGlobalization` не менялись.
