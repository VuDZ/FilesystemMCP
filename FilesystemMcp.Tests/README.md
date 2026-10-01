# Regression tests FilesystemMcp

Отдельный xUnit-проект на .NET 10, включён в FilesystemMCP.sln. Покрывает воспроизведения FS-01…FS-13 и контрольные Unicode/stdio/hash сценарии. [Спецификации и backlog](../docs/README.md).

## Статус

Исправления сервера пока не реализованы. Status=KnownDefect означает проверку **желаемого исправленного поведения**, а не ожидание ошибочного результата. Эти тесты активны и не skipped. Полный запуск сейчас намеренно красный; passing Baseline не означает полной исправности сервера.

Тесты новых CLI контрактов (allowSymLinks, logDirectory, budgets) тоже активны: текущий сервер игнорирует дополнительные аргументы. Контрольный allowSymLinks=true/internal-link тест проверяет доступ к внутренней цели, но сам по себе не доказывает наличие parser настройки.

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

Количество учитывает отдельные InlineData cases. Snapshot до реализации исправлений:

| Спека | KnownDefect | Baseline | Покрыто сейчас |
|---|---:|---:|---|
| FS-01 | 3 | 2 | Default/explicit false, true internal/external junction, traversal, неизменность внешнего файла |
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
| **Всего** | **46** | **15** | **61 cases** |

Проверено 2026-10-01 на Windows, SDK 10.0.401 / runtime 10.0.12: сборка без ошибок и предупреждений; полный запуск через защищённый host — 15 passed, 46 failed, 0 skipped. Host control отдельно подтвердил перехват exception с exit code 1 и stderr; аварийный logger test видит EOF вместо ожидания системного диалога. Это snapshot версии, а не утверждение, что каждый failed case — независимый дефект.

Fault injection на commit, межпроцессные гонки, ACL, rename callbacks, cancellation/отзывчивость dispatcher, memory bounds, cycles/TOCTOU, backup recovery и полный protocol table пока не автоматизированы. Они перечислены в приёмке спек и обязательны перед их закрытием. Несколько repro-tests не являются полной приёмкой.

## Окружение и изоляция

- Fixtures создаются только под temp/filesystemmcp-tests-<guid>. Внешний относительно workspace файл тоже лежит в этом sandbox. Настоящие repo files/user configs не редактируются.
- Cleanup проверяет абсолютную границу temp, удаляет собственные junction entries без следования целям и затем собственный sandbox.
- WindowsFact явно skipped вне Windows. Junction создаётся PowerShell без symlink privileges/Developer Mode. Ошибка создания junction — fixture failure, не тихий skip.
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
