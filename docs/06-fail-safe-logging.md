# FS-06: Логирование не влияет на файловые операции

Приоритет: P1. Источники: McpLogger, LogSanitizer, ToolRegistry.

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
