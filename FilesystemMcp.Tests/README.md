# Regression tests FilesystemMcp

Отдельный xUnit-проект на .NET 10, включён в FilesystemMCP.sln. Покрывает воспроизведения FS-01…FS-13 и контрольные Unicode/stdio/hash сценарии. [Спецификации и backlog](../docs/README.md).

## Статус

FS-01 реализован по пользовательскому override (default true, external links через workspace route) и принят независимым ревью в третьем раунде (версия 1.2.0). Его проверки перенесены в Baseline и расширены до 38 cases. Status=KnownDefect означает проверку **желаемого исправленного поведения**, а не ожидание ошибочного результата. Эти тесты активны и не skipped. Полный запуск сейчас намеренно красный; passing Baseline не означает полной исправности сервера.

CLI `allowSymLinks` проверяется unit и stdio startup tests. `logDirectory` и budgets ещё не реализованы и отвергаются строгим parser как unknown options; соответствующие KnownDefect tests остаются активными.

После исправления пункта перенести проходящие проверки в Status=Baseline, дополнить матрицу приёмки из спеки и выполнить весь проект без фильтра. Не подменять failing tests проверками старого дефектного поведения.

## Запуск из корня репозитория

```powershell
dotnet restore FilesystemMcp/FilesystemMcp.Tests/FilesystemMcp.Tests.csproj -p:PublishAot=false -p:PublishTrimmed=false
dotnet build FilesystemMcp/FilesystemMCP.sln --no-restore -p:PublishAot=false -p:PublishTrimmed=false

# Зелёные контрольные сценарии
dotnet test FilesystemMcp/FilesystemMcp.Tests/FilesystemMcp.Tests.csproj --no-build --no-restore -p:PublishAot=false -p:PublishTrimmed=false --filter 'Status=Baseline'

# Известные дефекты: текущий exit code 1 ожидаем и требует анализа результатов
dotnet test FilesystemMcp/FilesystemMcp.Tests/FilesystemMcp.Tests.csproj --no-build --no-restore -p:PublishAot=false -p:PublishTrimmed=false --filter 'Status=KnownDefect'

# Один пункт / полный целевой gate
dotnet test FilesystemMcp/FilesystemMcp.Tests/FilesystemMcp.Tests.csproj --no-build --no-restore --filter 'Spec=FS-01'
dotnet test FilesystemMcp/FilesystemMcp.Tests/FilesystemMcp.Tests.csproj --no-build --no-restore --logger 'trx;LogFileName=all.trx'
```

NuGet versions согласованы с существующим тестовым проектом репозитория. Production ProjectReference отключает AOT/trim для unit-тестов; build/publish production properties остаются прежними. Compile exclusion и InternalsVisibleTo в production csproj нужны для отделения тестов и доступа к internal API.

## Покрытие

Количество учитывает отдельные InlineData cases. Текущая матрица после реализации FS-01:

| Спека | KnownDefect | Baseline | Покрыто сейчас |
|---|---:|---:|---|
| FS-01 | 0 | 38 | Default true external read/create/replace/list/search с reusable alias, strict false, direct outside absolute/../ и Windows nested drive components rejected, Unix colon names, parser, symlinks/roots/chains/physical .., dangling/cycles/identity visited/8.3 aliases и create/replace, root-directory create rejection, parent swap/handle rename/Unix identities |
| FS-02 | 1 | 1 | Pre-cancelled write сохраняет bytes; stale hash сохраняет внешние изменения |
| FS-03 | 6 | 0 | CP1251 rejected; пять BOM encodings сохраняются с CRLF |
| FS-04 | 1 | 1 | Partial search при настоящем FileShare.None; чтение с чужим FileShare.Read |
| FS-05 | 4 | 0 | Missing file, stale hash, unknown tool, отсутствующий path |
| FS-06 | 2 | 0 | Недоступный log directory; secret content не попадает в диагностику |
| FS-07 | 2 | 0 | File byte cap для range; oversized frame без выполнения mutation |
| FS-08 | 2 | 1 | Сохранение settings/servers, invalid JSON, Unicode fresh install |
| FS-09 | 4 | 2 | Search UTF-16/UTF-32 LE/BE и UTF-8/BOM, line=2 |
| FS-10 | 5 | 1 | Leading/empty/trailing lines, range, truncation metadata, full-file hash |
| FS-11 | 4 | 2 | Empty deletion, delete-all, whitespace target/replacement; invalid missing/empty arguments |
| FS-12 | 8 | 1 | Шесть direct RPC methods, aliases conflict, sample append contract, create-no-overwrite |
| FS-13 | 4 | 2 | Parse error id:null, unsupported version, invalid params/id, handshake/ping/notification |
| Unicode control | 0 | 1 | Create/read/list/search/replace по Unicode пути через stdio |
| Process launch | 0 | 1 | Test host перехватывает escaping exception, сохраняет stderr и ненулевой exit |
| **Всего** | **43** | **51** | **94 cases**, включая 4 capability-dependent symlink cases, 2 Unix cases и 1 capability-dependent Windows 8.3 case |

Проверено 2026-10-01 на Windows, SDK 10.0.401 / runtime 10.0.12: сборка без ошибок и предупреждений; полный запуск через защищённый host — 15 passed, 46 failed, 0 skipped. Host control отдельно подтвердил перехват exception с exit code 1 и stderr; аварийный logger test видит EOF вместо ожидания системного диалога. Это snapshot версии, а не утверждение, что каждый failed case — независимый дефект.

После FS-01 и review corrections на той же Windows среде: полный проект — 45 passed, 43 failed, 6 skipped (94 cases). Все 43 failures остаются в прежних KnownDefect классах FS-02…FS-13; новых failing cases нет. FS-01 — 32 passed, 6 skipped; Baseline — 45 passed, 6 skipped. Четыре skips: текущий Windows account не разрешает symlink capability probe; два skip — Linux/macOS parent identity/rename и colon-name tests на Windows. 8.3 capability доступна: regression прошёл с одним search match и успешными create/replace через internal/external short aliases. После финализации версии 1.2.0 RID-зависимости win-x64 восстановлены; `dotnet publish -c Release -r win-x64 --no-restore` остановился с `Platform linker not found`: в среде отсутствует требуемый Visual Studio C++ toolchain. Native binary не собран и не запускался; managed build — 0 warnings/errors, handshake после обновления версии прошёл.

FS-01 добавляет deterministic parent swap before write commit, rejection реального rename закреплённого Windows parent, Unix pinned/live parent identity mismatch после перемещения наружу и создания обычного нового каталога по тому же имени, root/directory file-path rejection, link chains/cycles/visited и fail-closed неизвестного reparse tag через native validation seam. File symlink/dangling self-cycle и portable parent swap tests явно skipped при отсутствии OS/account capability; выполняются на поддерживающих Linux/macOS/Windows lanes. Unix identity test явно skipped вне Linux/macOS. Atomic commit faults, межпроцессные гонки FS-02, ACL, cancellation/отзывчивость dispatcher, memory bounds, backup recovery и полный protocol table остаются требованиями других спек.

## Окружение и изоляция

- Fixtures создаются только под temp/filesystemmcp-tests-<guid>. Внешний относительно workspace файл тоже лежит в этом sandbox. Настоящие repo files/user configs не редактируются.
- Cleanup проверяет абсолютную границу temp, удаляет собственные junction entries без следования целям и затем собственный sandbox.
- WindowsFact явно skipped вне Windows. Junction создаётся PowerShell без symlink privileges/Developer Mode. Ошибка создания junction — fixture failure, не тихий skip.
- SymlinkFact делает capability probe и явно сообщает skip, если OS/account не разрешает file/directory symlinks. Linux/macOS тесты используют реальные symlinks и `openat`; они не превращены в Windows-only tests. Cleanup обходит sandbox по entries и удаляет reparse entries без следования, включая dangling links и ссылки, перемещённые fault hooks.
- ShortNameFact делает Windows 8.3 capability probe; при отсутствии aliases явно skipped. Junction target validation использует физический handle path и сохраняет границу собственного sandbox, включая short aliases. Nested-drive regression задаёт cwd дочернего protected host внутри sandbox; global cwd не меняется.
- Installer/junction tests требуют PowerShell: Windows PowerShell 5.1 по умолчанию на Windows, pwsh на других OS. FILESYSTEM_MCP_TEST_POWERSHELL задаёт иной executable. Отсутствие PowerShell — явная ошибка окружения.
- Protocol tests запускают настоящий process, читают UTF-8 frames, постоянно дренируют bounded stderr и используют watchdog. Свой process завершается принудительно, если сам не выходит; чужие процессы не затрагиваются.
- По умолчанию отдельный FilesystemMcp.TestHost запускает настоящий production entry point в своём managed процессе. Host перехватывает escaping exceptions, пишет исходную ошибку в stderr и возвращает exit code 1 без выдуманного MCP ответа. В Windows он также задаёт SEM_NOGPFAULTERRORBOX внутри дочернего процесса после CLR startup. Настройка родителя оказалась недостаточной; CreateNoWindow скрывает только console. [Microsoft SetErrorMode](https://learn.microsoft.com/en-us/windows/win32/api/errhandlingapi/nf-errhandlingapi-seterrormode).
- TestHost не меняет protocol/tool реализацию и сохраняет InvariantGlobalization/JSON runtime settings сервера. Только infrastructure self-test использует зарезервированный одиночный аргумент --test-host-crash-probe. Глобальные Windows/registry настройки не меняются.
- Unit testhost не включает InvariantGlobalization глобально; stdio server сохраняет production runtime settings. Unit encoding fixtures не зависят от culture.

## Проверка опубликованного Native AOT сервера

```powershell
$env:FILESYSTEM_MCP_TEST_SERVER = 'C:\absolute\publish\FilesystemMCP.exe'
dotnet test FilesystemMcp/FilesystemMcp.Tests/FilesystemMcp.Tests.csproj --no-build --no-restore
Remove-Item Env:FILESYSTEM_MCP_TEST_SERVER
```

Variable указывает на реальный абсолютный путь опубликованного binary. Замена затрагивает обычные process tests; unit tests используют managed ProjectReference. Преднамеренно аварийный logger test и process-launch control всегда используют защищённый managed TestHost, даже с внешним binary: автоматический crash native binary на рабочем desktop в этой suite не выполняется. Installer использует заданный binary как config argument. AOT publish и другие OS в текущем snapshot не проверены; это будущий CI gate.
