# FS-01: Workspace jail и настройка allowSymLinks

Приоритет: P1. Источники: WorkspaceJail.cs, SearchTool.cs, все path-based tools и startup Program.cs.

Статус: выполнено.

Версия реализации: **1.2.0**. Принято независимым ревью в третьем раунде.

Реализация и regression matrix обновлены по уточнению пользователя. Пользовательский override: ссылки включены по умолчанию и могут вести наружу, но доступны через входной путь внутри workspace. `ServerOptions` задаёт immutable настройку, единый `PathPolicy` используется tools и legacy API. Запись проходит повторную policy-проверку и выполняется через открытые handles (`NtCreateFile` относительно закреплённых Windows родителей; Unix `openat`/`O_NOFOLLOW`), без последующего поиска leaf по имени при атомарной публикации. Windows проверяет окончательный физический путь anchor handle; Unix сверяет dev/inode закреплённых и текущих родителей перед открытием leaf и перед атомарной публикацией. Отказ безопасного открытия — `path_changed`; неподдерживаемая OS/ABI — `unsupported_safe_write`. Корень/существующий каталог вместо файла — `path_is_directory`, без создания ошибочного дочернего файла.

Контракт list: object с `entries`, тип ссылки `link`. Search: object с `matches`, `skipped` (relative path + code), `incomplete`; `already_visited` отмечает повторную физическую директорию. Link codes: `symlink_not_allowed`, `path_outside_workspace`, `symlink_dangling`, `symlink_cycle`, `unsupported_reparse_point`. Подробнее — [README](../README.md#workspace-links-and-result-contracts).

Граница: hard links и Windows case-sensitive directories остаются backlog; FS-02 atomic writes не реализован этой правкой. Handle guard относится к writes; Unix native identity ABI поддержан для 64-bit Linux/macOS. Полная OS sandbox гарантия при произвольном конкурентном переименовании объектов, включая rename после commit, здесь не заявляется. Разрешённая true-запись через внешнюю ссылку намеренно изменяет внешнюю цель.

## Дефект и воспроизведение

GetFullPath/StartsWith проверяют только логический путь и не реализуют отключение ссылок. При явно выбранном strict `allowSymLinks=false` junction workspace/link → соседняя директория должен блокировать read/search/replace внешнего файла. При true этот маршрут теперь разрешён пользователем. Обход циклических ссылок без visited-set остаётся дефектом независимо от режима.

## Требуемое поведение

1. Настройка allowSymLinks типа bool, по умолчанию true. CLI: FilesystemMCP.exe <workspace> --allowSymLinks=true|false. Отсутствующий аргумент эквивалентен true; false включается явно. Флаг без значения, невалидное значение, неизвестная настройка и дубликаты отклоняются в stderr с ненулевым exit и без stdout garbage.
2. false: запретить следование file/directory symlinks и Windows junction/reparse redirects. Проверять все существующие компоненты пути начиная с workspace, включая родителя нового файла. Linked workspace root отклонять при startup; его вышележащие системные каталоги учитывать при канонизации физического root, не запрещать их автоматически.
3. list_directory может перечислять имя ссылки как type=link, но не открывает цель при перечислении имени. При false search пропускает ссылки и отмечает skipped/incomplete; прямой доступ через ссылку возвращает symlink_not_allowed. При true search обходит разрешённые внутренние и внешние цели; отклонённые/dangling/циклические маршруты отмечает skipped/incomplete и продолжает.
4. true: разрешены внутренние и внешние физические цели ссылок, достижимые через входной путь внутри логического workspace. Это поддерживает срезы репозитория и подключение внешних файлов/документации. Входной absolute outside и прямой ../ escape запрещены при обоих флагах с path_outside_workspace, включая временный logical escape с последующим возвратом внутрь. Windows drive/root-changing компоненты (например dummy/C:outside) отклоняются и во входном маршруте, и в physical walker; результат всегда fully-qualified. Unix имена с ':' сохраняются. Сначала проверять depth входного маршрута, затем разрешать ссылки физически; нельзя схлопывать .. до разрешения link. False дополнительно сохраняет физический jail и запрет ссылок.
5. Разрешать внутренние/внешние цепочки ссылок при true, корректно обрабатывать родительские links, .. после физического разрешения и создание ещё не существующего листа. Dangling/циклическая ссылка — явная ошибка; search продолжает обход. Search сохраняет logical alias пути результата, чтобы его можно было передать обратно в read_file; physical directories используются только для visited-set.
6. search отслеживает идентичность посещённых физических директорий и не обходит цель повторно: Windows volume serial + 128-bit file ID, Unix dev/inode. 8.3 aliases не являются отдельными целями. Windows root и разрешённые directory link targets канонизируются по final handle path, включая 8.3 spelling; create/replace через такие aliases сохраняют разрешённую семантику. Неизвестный reparse type — fail closed. Не приводить Unicode имена к ASCII/NFC/NFD.

## Реализация и границы

Единый PathPolicy применяется ко всем операциям, включая legacy пути до их удаления FS-12. Перед commit записи повторно проверить путь; строгая защита от подмены ссылки требует проверки открытых handles/identity или отказа от неподдерживаемого сценария. Нельзя заявлять TOCTOU protection на основании одной проверки имени.

Hard links и особенности case-sensitive Windows directories — отдельные backlog задачи; документация явно сообщает эту границу. Root itself как link при true канонизируется в одну физическую базу для логического workspace; это не разрешает прямые абсолютные обращения к произвольным внешним путям.

## Приёмка и тесты

WorkspaceLinksTests/WorkspaceLinksAcceptanceTests: omitted/default true разрешает внутренние и внешние junction, explicit false запрещает оба; прямой outside absolute/../ отвергается при обоих флагах. Проверить default external read/create/replace/list/search и обратное чтение logical alias search result. Отклонённые записи и parent-swap сохраняют outside bytes; разрешённые true-записи изменяют только ожидаемую внешнюю цель.

Матрица: file symlinks, linked parent create, цепочки, dangling, цикл, visited-set, linked root, startup parser default/explicit false/invalid option, list type=link, search metadata, deterministic parent swap before commit, root/directory create rejection через tools/legacy/native guard. Windows nested drive-component test запускает server с cwd sandbox и проверяет tools/legacy обоих режимов; outside bytes неизменны. Windows 8.3 capability test проверяет один обход физического root, reusable logical result и create/replace через internal/external short aliases; при отсутствии 8.3 support явно skipped. Unix identity test перемещает закреплённый parent наружу и создаёт новый обычный каталог по тому же имени: запись должна отказать, оба файла неизменны. Unix colon-name test сохраняет ':' как обычный символ имени. Windows junction тест не требует Developer Mode; Linux/macOS symlink/identity tests исполняются на своих OS. Unsupported OS-функцию явно пропускать, а не считать passed.

Зависимости: FS-05 для ошибок, FS-07 для общего deadline; tests не должны зависать на цикле.
