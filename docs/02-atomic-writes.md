# FS-02: Атомарные изменения файла и конфликтующие писатели

Приоритет: P1. Источники: FileTextHelper.WriteUtf8WithoutBomAsync, FileService.ReplaceInFileAsync, MutationService.

Статус: выполнено.

Версия реализации: **1.3.0**. Принято независимым ревью в первом раунде.

## Дефект

FileMode.Create обрезает оригинал до завершения записи. Уже отменённый token оставляет файл длиной 0. Проверка hash и запись разнесены; редактор/другой сервер может изменить файл между ними.

## Требуемое поведение

- До commit оригинал остаётся byte-for-byte прежним. Cancellation проверять до любых побочных эффектов; отмена и ошибка подготовки/flush не меняют оригинал.
- Подготовить уникальный temp в том же каталоге, записать все bytes, завершить encoder и flush; выполнить поддерживаемую OS атомарную замену. Create публикует только новый файл без overwrite при конкурентном создании.
- После commit возвращать success независимо от ошибки logger/cleanup. Нельзя обещать, что cancellation после commit откатит запись: вернуть committed result, не повторять операцию.
- Координировать read-check-write для серверных писателей по physical file identity/каноническому пути, включая несколько процессов на одной машине. Перепроверять актуальное состояние непосредственно перед commit.
- Если наблюдаемое состояние изменилось, вернуть hash_conflict без записи. Никогда не повторять mutation автоматически после неопределённого commit.
- Сохранить ACL/attributes и формат FS-03. Read-only файл — ошибка без изменения attributes. Не разрушать внешний hard-linked inode неявно; ограничения стратегии описать.

## Реализация и гарантии

Выделить AtomicFileWriter и управляемые точки fault injection после temp-write, перед commit и после commit. Только production-realistic hooks через internal dependency, не задержки Thread.Sleep.

Атомарность видимого содержимого и durability после отключения питания — разные гарантии. Flush-to-disk определять явно. Повторный hash-check не является OS compare-and-swap: для некооперативных внешних писателей остаётся окно, если платформа не даёт более сильной защиты. Документировать это ограничение; не писать «полная защита от любых внешних гонок».

Cleanup удаляет только собственные temp files; recovery не удаляет неизвестные файлы. Новый write-сервис используется всеми entry points.

## Приёмка

AtomicWritesTests уже фиксирует сохранность оригинала при pre-cancelled token и корректное отклонение устаревшего hash. Добавить: failure на каждом hook, cancelled mid-write, after-commit cancellation, disk-full, два server processes с одним original_hash (один success, один conflict), конкурентный create, observer видит только старый/новый текст, ACL/read-only и temp cleanup.

Не моделировать concurrency случайными sleeps; использовать barriers/hooks и subprocess deadlines. Успешная обычная запись возвращает hash полного итогового normalized текста. Зависимости: FS-01, FS-03, FS-05, FS-06.

## Реализованный контракт и границы

Все действующие write entry points (tools, legacy `MutationService`, `FileService`, `NativePath.WriteAsync`, `FileTextHelper.WriteUtf8WithoutBomAsync`) используют `AtomicFileWriter`. Уже отменённый token проверяется до создания mutex, parent directories и temp. Строгий encoder завершается в memory до temp creation. Temp имеет уникальное `.filesystemmcp-<guid>.tmp` имя в закреплённом физическом parent; chunks записываются только в него, затем выполняется `Flush(true)` до commit. Ошибка encoder/write/flush/hooks или отмена до публикации сохраняет оригинальные bytes.

Windows 64-bit публикует открытый temp handle через `NtSetInformationFile(FileRenameInformationEx)` относительно pinned parent, с `REPLACE_IF_EXISTS | POSIX_SEMANTICS` для replace и без replace для create. Старые reader handles продолжают читать старый inode; новые opens видят весь новый файл. OS/filesystem без этой операции возвращает `unsupported_safe_write`. Родители запрещают delete sharing (rename закреплённого parent отклоняется); write sharing необходимо destination-directory lookup операции rename. Child opens остаются `NtCreateFile` относительно handles с reparse following disabled. Все pinned handles повторно проверяются на reparse перед commit; policy/hooks FS-01 сохранены.

64-bit Linux/macOS закрепляют родителей через `openat/O_NOFOLLOW`, сверяют pinned/live dev/inode и публикуют `renameat` для replace либо `linkat` для exclusive create. После успешного Unix create удаляется только собственное temp имя. Linux metadata ABI явно различает x64/arm64; прочие Linux architectures не поддерживают metadata commit. Windows 32-bit и прочие OS отклоняются безопасно.

Кооперативные серверы на одной машине используют named OS mutex в порядке: стабильный canonical physical path (ключ не меняется при создании missing parents), parent dev/inode или volume/file-id + leaf, existing physical file identity. Windows имена mutex находятся в Global namespace. Read/check/prepare/fresh-check/commit выполняются внутри координации на одном worker thread: thread-affine mutex не переносится через await. Aliases/junctions/8.3 routes сходятся по canonical path/physical identity. Разные hard-link имена тоже сериализуются по исходному inode, но publication меняет только выбранное имя: остальные ссылки продолжают содержать прежние bytes. Поэтому последовательные patches разных hard-link имён могут оба успешно использовать старый hash: другой inode по второму имени ещё не изменён.

Непосредственно перед commit сравниваются physical identity, все исходные bytes, length/mtime/attributes и security/permission metadata. Наблюдаемое изменение возвращает `hash_conflict` с рекомендацией повторного read, без mutation. Это **не OS compare-and-swap**: некооперативный writer может изменить содержимое/metadata или имена после проверки и до rename. Unix дополнительно сохраняет промежуток между live-parent/owned-temp identity check и name operation; произвольные внешние concurrent renames не составляют полную sandbox/CAS гарантию. Cancellation после commit не откатывает запись. После подтверждённой публикации возвращается заранее подготовленный committed result, независимо от late cancellation, диагностических callbacks, logger/stream disposal/cleanup ошибок. Автоматических mutation retries, в том числе при неопределённом результате native commit, нет.

Windows сохраняет owner/group/DACL, inheritance/protection control и обычные attributes; protected ACL с отличным от parent ACL проверяется SDDL comparison. Unix сохраняет owner/group/mode, access ACL/extended attributes и inode flags, либо безопасно отклоняет операцию, если перенос metadata недоступен. Read-only/immutable/append-only не обходятся заменой через writable parent. Замена выделяет новый inode; внешние hard links сохраняют старый inode/bytes, с исходным доступом. Особенности hard links вне выбранного имени не имитируют in-place mutation.

Минимальный prerequisite FS-03 — `TextDocument`: strict UTF-8/UTF-16/UTF-32 BOM decoding, BOM вне text/hash, карта normalized offsets, сохранение encoding/BOM и untouched mixed EOL spans. Вставленные LF используют стиль заменяемого span либо преобладающий стиль файла, ties — первый. Create сохраняет supplied line endings, пишет UTF-8 без дополнительного BOM. Если supplied text начинается U+FEFF, его bytes составляют физическую BOM signature; success hash исключает её так же, как subsequent read. Минимальный prerequisite FS-05 — typed mutation codes `hash_conflict`, `file_exists`, `access_denied`, `unsupported_encoding`, `binary_file`, `target_not_found`, `file_not_found` в tools; legacy mutation failures используют `-32001` с code. Минимальный prerequisite FS-06 — best-effort logger boundaries и completion logging. Общие CLI logging options, queue/rotation, полная error table, budgets и полная приёмка FS-03/05/06 остаются их отдельными эпохами.

Cleanup работает с temp handle Windows или проверенным собственным temp name+identity Unix и не сканирует/recover неизвестные файлы. Hook errors не мешают попытке собственного cleanup. Если cleanup невозможно или процесс завершён до cleanup, собственный temp может остаться; чужие/старые неизвестные `.tmp` файлы автоматически не удаляются.

`Flush(true)` означает запрос OS на flush данных temp до rename. Directory fsync/полная power-loss durability после commit не обещаются: атомарная видимость и crash durability различаются.

## Матрица проверки реализации

`AtomicWritesTests` + `AtomicWriteAcceptanceTests` покрывают pre-cancel всех entry points, каждый preparation hook (включая contended lock), mid-write/pre-commit/wait cancellation, disk-full на частичной записи и flush dependency, encoder failure, ошибки after-commit/cleanup/logger, fresh bytes/ACL conflict, normalized hashes включая leading BOM, шесть encodings и untouched mixed EOL. Реальный reader observer видит только whole old/new; read-only attributes и protected DACL сохраняются; hard-link inode вне выбранного имени не меняется; неизвестный temp не удаляется.

Два настоящих protected server processes координируются named-pipe barriers (`BeforeLock`, `LockContended`, `BeforeCommit`), без sleep synchronization. Проверены one-success/one-hash_conflict, exclusive concurrent create (existing/missing parents), junction aliases, 8.3 roots и serialization distinct hard-link paths. Fault configuration существует только в managed TestHost, production CLI/API её не принимает. Subprocess/fixture cleanup ограничен собственным sandbox; watchdog — только deadline.

Windows managed evidence и конкретные totals записаны в [test README](../FilesystemMcp.Tests/README.md). Unix permission/ACL/native publication lanes и Native AOT отдельно не подтверждены на текущем Windows host; skips явно указаны. Отсутствующий Visual Studio C++ linker остаётся внешней предпосылкой AOT, повторной установки toolchain нет.
