# Сборка, отладка и публикация Windows x64

## Инструменты разработчика (не требования к игроку)

- Windows 10/11 x64.
- .NET 8 SDK, feature band 8.0.400+; `global.json` не переключается на .NET 9/10.
- Visual Studio 2022 / Build Tools, workload **Desktop development with C++**,
  MSVC v143 x64, Windows SDK **10.0.22621 или новее** (C++/WinRT headers).
- CMake 3.25+ в PATH. WPF .NET desktop tools полезны для отладки в Visual Studio.
- PowerShell 5.1+ или PowerShell 7.
- Интернет только для подготовки зависимостей / restore на **машине сборки**.

Откройте обычный PowerShell в корне checkout. Сборка не правит HKLM и не требует
UAC. Не используйте старые `install.bat` / `run.bat`: они относятся к Python.

```powershell
dotnet --info
cmake --version
powershell -ExecutionPolicy Bypass -File .\tools\Build.ps1
```

### Последовательность Build.ps1

1. SHA-256 и `Get-AuthenticodeSignature` оригинальной `bin/MessageBus.dll`.
   Несоответствие не обходится флагом «ignore signature».
2. `dotnet run --project tests/HuntReplay.Tests -c Release` и
   `dotnet run --project tests/HuntReplay.WindowsTests -c Release` — Core/IPC и
   настоящие WPF binding-регрессии. GPU, игра, микрофон и изменение реестра не нужны.
3. `tools/PrepareFFmpeg.ps1`: URL и SHA-256 из `tools/ffmpeg.lock.json`, проверка
   архива до распаковки. MinGW shared DLLs используются как DLLs, а MSVC `.lib`
   создаются из export tables через `dumpbin.exe` / `lib.exe` из Visual Studio.
4. CMake C++20 / x64 / static MSVC CRT (`/MT`). WGC, D3D11 и WASAPI — системные
   Windows API; установленный GPU-драйвер предоставляет аппаратный runtime.
5. В `artifacts/runtime/` копируются DLL FFmpeg и `HuntReplay.Media.dll`.
   `.exe` FFmpeg/ffprobe туда не копируются. Создаётся `native.sha256`.
6. `dotnet publish`: self-contained WPF .NET 8, win-x64, single file, compression,
   без trimming. Native payload и signed MessageBus находятся в ресурсах.
7. В output добавляются инструкции, лицензии и checksum exe. Эти текстовые
   файлы не являются runtime-зависимостью, но нужны для корректной поставки.

**Результат:** `artifacts\publish\HuntReplayStudio.exe`.

## Offline / точный архив

BtbN autobuild-assets могут удаляться со временем. Для воспроизводимой сборки
сохраните **именно этот** архив в своём artifact storage. Скрипт не заменяет
исчезнувшую версию на `latest`.

```powershell
.\tools\Build.ps1 -FFmpegArchive D:\BuildCache\ffmpeg-n8.1.3-14-g330caae0c1-win64-gpl-shared-8.1.zip -Offline
```

`-Offline` запрещает **загрузку FFmpeg**; .NET packages/runtime packs должны уже
находиться в NuGet cache либо в вашем локальном NuGet feed. При пустом cache
restore всё ещё требует источника пакетов. Не отключайте проверку SHA-256 ради
другой сборки FFmpeg: обновите lock/ABI осознанно, изучите лицензии и прогоните QA.

## Отдельные команды

```powershell
# Core / IPC — без Windows SDK и без GPU:
dotnet run --project tests/HuntReplay.Tests -c Release

# WPF selectors: только Windows, но без GPU / NVIDIA runtime:
dotnet run --project tests/HuntReplay.WindowsTests -c Release

# Нативные библиотеки + managed debug build, без single-file publish:
.\tools\Build.ps1 -Configuration Debug -NoPublish

# После подготовки artifacts/runtime можно открывать solution:
# HuntReplayStudio.sln → HuntReplay.App → Debug / x64

dotnet build .\src\HuntReplay.App -c Debug

# Повторная публикация с уже готовым НАТИВНЫМ payload:
dotnet publish .\src\HuntReplay.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o .\artifacts\publish
```

Обычный `dotnet build` **не компилирует C++ автоматически**. Без payload UI может
собраться для работы над XAML, но захват честно покажет «нативный движок отсутствует».
`dotnet publish` без payload по умолчанию завершается ошибкой. Не выпускайте сборку
с `SkipNativePayloadCheck=true` — это только техническое свойство для tooling.

После изменения C++ всегда заново запускайте Build.ps1: ABI, DLL, manifest и
embedded resources должны обновиться вместе. Не копируйте новую DLL в cache вручную.

## GitHub Actions

`.github/workflows/studio.yml` содержит два job:

- Linux + **.NET 8**: executable tests и static checks.
- Windows 2022 + .NET 8 + MSVC: полный native/WPF build, тесты и artifact
  `HuntReplayStudio-win-x64-unsigned-QA` (хранится 7 дней).

Workflow запускается при push/PR либо вручную. Предыдущий опубликованный QA artifact —
0.1.1, [run 37856493230](https://github.com/Mlovo/HuntOpenHighlights/actions/runs/37856493230).
Для 0.1.2 результат фиксируется в `VALIDATION.md` только после успешного прогона;
до этого сборка 0.1.2 не считается опубликованной. Точные границы проверки — там же.
Hosted runner не подтверждает реальное аппаратное кодирование, интерактивный
WGC или совместимость с EAC.

## Подпись и лицензии перед релизом

1. Пройдите `WINDOWS_VALIDATION.md` на физических AMD/NVIDIA/Intel системах.
2. Проверьте права на распространение proprietary NVIDIA payload. Его цифровая
   подпись не выдаёт разработчику нового приложения лицензию на распространение.
3. Default FFmpeg build — GPL, включая x264. Подготовьте полный corresponding
   source всех включённых компонентов и выполнение GPL-обязательств. Один URL
   на upstream недостаточен. См. `THIRD_PARTY_NOTICES.md`.
4. Если подписываете native DLL, делайте это **до** создания `native.sha256` и
   embedding. Для обычного выпуска достаточно подписать финальный exe вашим
   легитимным code-signing сертификатом; оригинальную NVIDIA DLL не изменяйте.
5. Убедитесь через `Get-AuthenticodeSignature`, что publisher exe — ваш настоящий
   издатель, а не NVIDIA. Секретные ключи/пароли не помещайте в Git или config.
6. Обновите checksum **после** подписи. Неподписанный dev exe может показывать
   SmartScreen / «неизвестный издатель» — код проекта этого не обходит.

## Переносимость на ПК игрока

Для запуска нужен только exe и системные условия: Windows x64 19041+, D3D11 GPU,
соответствующий драйвер, доступный аудио endpoint. .NET и CRT включены. После
первого запуска программа извлекает native runtime в LocalAppData; для
автоматических SDK-событий устанавливает MessageBus с UAC. Это single-file
**доставка**, а не обещание работы без записи каких-либо служебных файлов.

Захват/кодирование не требует Windows HEVC/AV1 extensions. Однако WPF-плеер
использует Windows media stack: HEVC/AV1 playback зависит от установленных
системных декодеров; Windows N может потребовать Media Feature Pack. H.264 —
предустановленный совместимый вариант. Превью генерирует встроенный FFmpeg.
Если встроенный плеер не открыл клип, Studio показывает HRESULT и подсказку, а
клип остаётся неизменным. Кнопка «Открыть в проигрывателе» передаёт уже сохранённый
MP4 приложению Windows по умолчанию; перекодирования и смены кодека при этом нет.

## Удаление / восстановление

1. Закройте Hunt и остановите буфер.
2. Диагностика → **«Восстановить реестр»** → UAC. Восстанавливаются лишь конкретные
   still-owned значения из protected backup; чужой `ModuleMap` целиком не удаляется.
3. Отключите автозапуск в настройках и завершите приложение через трей.
4. Удалите exe; при необходимости вручную удалите пользовательский cache/config.
   Клипы удаляются только по вашему отдельному решению.

Для администрирования доступны фиксированные CLI действия:

```powershell
# Вызываются GUI через runas; сами требуют elevated process.
.\HuntReplayStudio.exe --install-shim
.\HuntReplayStudio.exe --uninstall-shim
```

`--replace-nvidia` — только осознанная замена существующей регистрации с backup.
GUI запрашивает отдельное согласие. Legacy `uninstall.bat` **не использовать**.
