# FS-04: Sharing violations и неполный поиск

Приоритет: P1. Источники: FileTextHelper и SearchTool.

## Дефект

Единственный файл с FileShare.None обрывает search. FileShare.ReadWrite не отменяет чужие ограничения. Отсутствие FileShare.Delete на read handles может мешать атомарному сохранению редактором. Два открытия probe/read создают окно удаления файла.

## Контракт

- Read handles используют FileShare.ReadWrite | FileShare.Delete, когда поддерживается платформой. Write sharing определяется стратегией FS-02, не копируется с read.
- Direct tool: locked файл → file_locked, недоступный по ACL → access_denied, исчезнувший → file_not_found. Системный код/HResult хранить для классификации, не путать любую IOException с lock.
- search при ожидаемой ошибке отдельного файла/директории продолжает работу; result object: matches, truncated, incomplete, skipped_count, skipped. skipped содержит ограниченный список относительных path/reason; skipped_count отражает полный счётчик.
- incomplete=true при lock/access denied/исчезновении/link skip; truncated отдельно обозначает result/budget cap. По пустому matches нельзя заключать отсутствие совпадений, если incomplete.
- Один открытый read stream на probe+decode+search. Ошибка корня не маскируется как успешный пустой поиск.
- Retry только для transient sharing violation: максимум три попытки с 50/100 ms и общим deadline. Можно отказаться от retry в search ради отзывчивости. Hash conflict/access denied не повторять. Mutation after uncertain commit не retry.

## Приёмка

Текущие FileLocksTests (Windows): запертый файл не должен скрывать найденный доступный файл, ответ должен сообщать incomplete и file_locked; контрольная проверка read с чужим FileShare.Read проходит. Fixtures открывают настоящий чужой handle, не только подставляют exception.

Дополнить: rename при собственном открытом read handle; FileShare.Read — replace file_locked; удаление между probe/read; ACL file/directory; lock released before retry; cancellation во время ожидания; несколько locked files и ограничение skipped details. Windows-specific sharing tests отмечены явно; POSIX semantics не объявлять эквивалентными.

Зависимости: FS-05 для error classifier, FS-01 для skipped links, FS-07 для deadlines, FS-09 для shared stream parser.
