# FS-10: Точное чтение строк и явное truncation

Приоритет: P2. Источники: FileTextHelper.ExtractRequestedContent, FileService.ReadFileAsync, ReadFileResult.

## Дефект

selected.Length теряет начальные пустые строки. Text теряет trailing newline. Custom max_lines возвращает prefix без признака truncation, хотя hash описывает весь файл.

## Контракт

- Полный read возвращает полный normalized LF text, включая leading blank lines и terminal LF. Empty file → text="", total_lines=0. Terminal LF не добавляет фиктивную новую строку: "\n" содержит одну пустую строку, "a\n" — одну строку.
- Range 1-based включителен. Между выбранными строками LF, а LF после последней выбранной строки возвращается, если этот delimiter присутствовал в исходном normalized тексте. Начальные пустые строки диапазона сохраняются.
- start_line за EOF → пустой диапазон и metadata; end_line за EOF clamp к последней доступной строке. Invalid отрицательный/обратный range — ошибка аргументов.
- md5/sha256 всегда описывают полный normalized decoded файл без BOM; никогда не вычислять hash лишь по selection.
- Read result дополняется total_lines, start_line, end_line (null для empty selection), truncated, has_more. start/end описывают фактически возвращённый диапазон. has_more означает наличие строк после возвращённой end_line; truncated означает срабатывание cap, а не сам запрос range.
- Сохранить default full-read rejection >1000 и explicit range limit; allow_large_read+max_lines — допускаемый prefix cap с truncated=true при превышении. max_lines при allow_large_read=false — invalid arguments вместо молчаливого игнорирования.
- Когда ответ превысил chars budget, возвращать валидный bounded result с metadata или resource_limit, не скрыто обрезанный JSON.

## Приёмка

ReadFidelityTests: leading blank lines, trailing newline, empty-only content, range starting at empty line; max_lines prefix имеет total_lines/truncated/has_more и полный hash. Unicode и hash-control остаются baseline.

Добавить: empty file, no terminal LF, exact cap/cap+1, CRLF normalization, out-of-range, huge single line FS-07, max_lines без opt-in, invalid ranges и consistent DTO source generation/AOT.

Зависимости: FS-03/07/12. Изменение trailing delimiter в text — документируемая коррекция; agents должны брать exact snippet из исправленного ответа.
