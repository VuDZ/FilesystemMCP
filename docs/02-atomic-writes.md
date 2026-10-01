# FS-02: Атомарные изменения файла и конфликтующие писатели

Приоритет: P1. Источники: FileTextHelper.WriteUtf8WithoutBomAsync, FileService.ReplaceInFileAsync, MutationService.

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
