# FS-01: Workspace jail и настройка allowSymLinks

Приоритет: P1. Источники: WorkspaceJail.cs, SearchTool.cs, все path-based tools и startup Program.cs.

## Дефект и воспроизведение

GetFullPath/StartsWith проверяют логический путь. Junction workspace/link → соседняя директория позволяет read/search/replace внешнего файла. Подтверждено на Windows. Обход циклических ссылок не имеет visited-set.

## Требуемое поведение

1. Настройка allowSymLinks типа bool, по умолчанию false. CLI: FilesystemMCP.exe <workspace> --allowSymLinks=true|false. Отсутствующий аргумент эквивалентен false; флаг без значения и невалидное значение отклоняются.
2. false: запретить следование file/directory symlinks и Windows junction/reparse redirects. Проверять все существующие компоненты пути начиная с workspace, включая родителя нового файла. Linked workspace root отклонять при startup; его вышележащие системные каталоги учитывать при канонизации физического root, не запрещать их автоматически.
3. list_directory может перечислять имя ссылки как type=link, но не открывает цель. search пропускает ссылки и отмечает skipped/incomplete. Прямой доступ через ссылку возвращает symlink_not_allowed.
4. true: разрешены только ссылки, чья итоговая физическая цель внутри физического workspace. Ссылка наружу — path_outside_workspace независимо от флага. Это разрешение ссылок, а не отключение jail.
5. Разрешать внутреннюю цепочку ссылок, корректно обрабатывать родительские links, .. после разрешения и создание ещё не существующего листа. Dangling/циклическая ссылка — явная ошибка; search продолжает обход.
6. search отслеживает посещённые физические директории и не обходит цель повторно. Неизвестный reparse type — fail closed. Не приводить Unicode имена к ASCII/NFC/NFD.

## Реализация и границы

Единый PathPolicy применяется ко всем операциям, включая legacy пути до их удаления FS-12. Перед commit записи повторно проверить путь; строгая защита от подмены ссылки требует проверки открытых handles/identity или отказа от неподдерживаемого сценария. Нельзя заявлять TOCTOU protection на основании одной проверки имени.

Hard links и особенности case-sensitive Windows directories — отдельные backlog задачи; документация явно сообщает эту границу. root itself как link при true разрешается в одну физическую область и далее становится её базовой границей.

## Приёмка и тесты

Текущие WorkspaceLinksTests: default false запрещает внешние/внутренние junction, true разрешает внутреннюю цель и запрещает внешнюю; прямой .. отвергается. Каждая запись проверяет неизменность внешних bytes.

Добавить при реализации: file symlinks, linked parent create, цепочки, dangling, цикл, visited-set, linked root, startup parser default/explicit false/invalid option, list type=link, search metadata и детерминированная подмена родителя перед commit. Windows junction тест не требует Developer Mode; Linux/macOS symlink tests исполняются на своих OS. Unsupported OS-функцию явно пропускать, а не считать passed.

Зависимости: FS-05 для ошибок, FS-07 для общего deadline; tests не должны зависать на цикле.
