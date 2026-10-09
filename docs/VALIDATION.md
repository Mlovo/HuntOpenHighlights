# Выполненная проверка и ограничения среды

Дата: **2026-10-09**. Ветка: `arena/83b0cc64-huntopenhighlights`.

Рабочая среда для этой версии: Linux x86-64, без Windows, Visual Studio, Windows SDK,
интерактивного рабочего стола, физического GPU, WGC, WASAPI и игры. Доступны GitHub,
npm и PyPI; NuGet и dot.net недоступны, поэтому .NET SDK в среде не было.
Это **не** испытание аппаратной записи, живой игры или античита.

## 1. Windows CI — основное подтверждение сборки и тестов

Итоговый код версии 0.1.3 — коммит `7a5aacf`. Документы добавлены следующим коммитом; путь
`docs/` не запускает CI и не меняет сборку.

- **PASS — [run 37931206688](https://github.com/Mlovo/HuntOpenHighlights/actions/runs/37931206688)**
  (коммит `7a5aacf`, оба job завершились успешно, все шаги `success`):
  - `core-and-protocol` (ubuntu-latest, .NET 8 SDK): `dotnet run --project tests/HuntReplay.Tests`
    и `python3 tools/CheckSource.py` — успешно. Набор: 117 тестов Core/IPC/view-model, из них
    12 новых регрессий библиотеки клипов.
  - `windows-build` (windows-2022, .NET 8, MSVC): `tools/Build.ps1` без `-SkipTests` выполнил
    `tests/HuntReplay.Tests` (тот же набор, 117), `tests/HuntReplay.WindowsTests` (22 WPF-регрессии,
    без изменений в 0.1.3), собрал native DLL с `capture.cpp` (запрос рамки), выполнил
    `dotnet build` и self-contained single-file publish WPF с `TreatWarningsAsErrors`, загрузил
    artifact `HuntReplayStudio-win-x64-unsigned-QA` (id **11616875561**, 154 МБ, хранение 7 дней).
  - Аннотации `windows-build`: 11 предупреждений — 1 Node.js 20 deprecation и 10 прежних
    предупреждений сторонних FFmpeg-заголовков (`libavutil/common.h`), как в 0.1.2. Предупреждений
    в изменённых файлах (`capture.cpp`, `ClipLibrary.cs`, WPF-код) нет; ошибок нет.
    Аннотации `core-and-protocol`: 1 Node.js deprecation и 1 notice о миграции `ubuntu-latest`.
  - Лог job из песочницы недоступен (хост логов Actions не разрешён), поэтому результат
    подтверждён статусами job и шагов, аннотациями и artifact. Счётчики тестов — по исходникам и
    локальному запуску того же кода; код выхода 0 означает, что ни один тест не упал.

История прогонов версии 0.1.3:

| Коммит | Run | Результат |
|---|---|---|
| `7a5aacf` | [37931206688](https://github.com/Mlovo/HuntOpenHighlights/actions/runs/37931206688) | оба job PASS — итоговая проверка кода 0.1.3 |

История прогонов версии 0.1.2:

| Коммит | Run | Результат |
|---|---|---|
| `6153ba4` | [37926574340](https://github.com/Mlovo/HuntOpenHighlights/actions/runs/37926574340) | `core-and-protocol` PASS; `windows-build` FAIL: компиляция `WindowsTests` (нет `System.IO`, отсутствовал хелпер `False`). Исправлено в `f7b8505`. |
| `f7b8505` | [37926834904](https://github.com/Mlovo/HuntOpenHighlights/actions/runs/37926834904) | оба job PASS; до отката версии CMake (см. ниже). |
| `8e5f577` | [37927275936](https://github.com/Mlovo/HuntOpenHighlights/actions/runs/37927275936) | оба job PASS — итоговая проверка кода 0.1.2, artifact 11614791506. |

## 2. Локальная проверка в песочнице (дополнительная, не замена CI)

Инструменты лежат вне репозитория и не коммитятся:

- исполнение: .NET **10.0.11** runtime из npm `@dprint/roslyn-linux-x64-glibc@0.22.1`,
  вызывается из Python через pythonnet;
- компиляция: Roslyn из того же пакета, language version C# 12, nullable enable,
  предупреждения как ошибки, implicit usings как в SDK;
- Google.Protobuf **3.36.2**, собранный из исходников upstream `v36.2` (определения net8);
- protoc **30.2** (PyPI `protoc-wheel-0`) сгенерировал C# из `proto/*.proto`;
  Python protobuf **7.35.1** — версия из докстринга генератора фикстур.

Это не официальный прогон .NET 8 SDK: среда исполнения и target-pack отличаются от CI.
Результаты ниже подтверждают логику, но не заменяют Windows CI.

- `tests/HuntReplay.Tests`: **117 passed, 0 failed** (105 прежних + 12 новых регрессий
  библиотеки). Запуск из корня репозитория: фикстуры ищутся относительно текущей папки.
- `python3 tools/CheckSource.py`: PASS — XML, pinned payloads, 45 production source files,
  15 ABI exports, golden fixtures, согласованность версии (`csproj` и футер = 0.1.3).
- Roslyn-разбор всех C#-файлов `src/` и `tests/`: 45 файлов, 0 синтаксических ошибок.
- Мутационная проверка регрессий (исходник временно портился, тест падал, исходник
  восстанавливался и тест снова проходил): превью в папке клипов → падают тесты «только MP4» и
  миграции; очистка без проверки папки владельца → падает тест «не трогает записи других папок»;
  пути хранилища для клипов 0.1.2 → падают два теста копии с дублирующим Id.

## 3. Что изменено в 0.1.3

- **Папка клипов содержит только MP4.** Раньше рядом с каждым клипом лежали `*.mp4.hunt.json`
  (метаданные) и `*.mp4.thumb.png` (превью). Теперь они хранятся в
  `%LOCALAPPDATA%\HuntReplayStudio\library\`: `records\<Id>.json` (метаданные и папка-владелец)
  и `thumbs\<Id>.png`. В режиме `--portable` каталог `library\` лежит рядом с EXE.
- **Владение.** Клип считается собственным, если есть валидная запись библиотеки с тем же Id,
  именем файла и папкой клипов, а MP4 существует и не является ссылкой. Чужие MP4 в папке не
  индексируются, не переименовываются и не удаляются.
- **Миграция 0.1.2.** При обновлении галереи (`Maintain`) валидные `*.mp4.hunt.json` с
  существующим MP4 переносятся в библиотеку, их превью переносится в `thumbs\`, а sidecar и старое
  превью удаляются из папки. В `studio.log`: `Библиотека клипов: перенесено из папки клипов N …`.
  До переноса клипы 0.1.2 остаются видимыми, переименовываются и удаляются вместе со своими
  служебными файлами.
- **Очистка сирот.** Если MP4 удалён в проводнике, при обслуживании удаляются его запись и превью,
  а также брошенные валидные sidecar и превью 0.1.2. Битые sidecar и записи не удаляются
  автоматически и не скрывают здоровые клипы. Записи других папок клипов не затрагиваются.
  Временные `*.tmp` старше 10 минут удаляются.
- **Переименование папки клипов.** Если папка переименована или перенесена и файл с тем же
  именем найден в новом месте, запись перепривязывается (в логе — `перепривязано`). Если файла
  в новой папке нет, клипы остаются связаны со старой папкой и в новой не показываются.
- **Копия клипа с тем же Id.** Клип, скопированный в другую папку вместе с sidecar (тот же Id),
  при переносе получает новый Id. Удаление и переименование такой копии до обслуживания не
  затрагивают запись оригинала (регрессионные тесты).
- **Рамка WGC: официальный запрос вместо `IsBorderRequired(false)` без согласия.** Перед стартом
  захвата вызывается `GraphicsCaptureAccess.RequestAccessAsync(Borderless)` в отдельном apartment
  с таймаутом 3 с. `IsBorderRequired(false)` выставляется **только** при статусе `Allowed`.
  Любой другой статус, таймаут, отсутствие свойства в сборке Windows или ошибка — рамка остаётся,
  запись продолжается, а лог фиксирует причину (`… consent request …`). Реестр не меняется,
  индикатор не обходится. Раздел 6 объясняет почему.
- **Тексты.** В настройках: где лежат метаданные и превью; очистка удаляет только клипы,
  записанные Studio; пояснение про рамку и его границы.
- **Версия.** 0.1.3 в `HuntReplay.App.csproj` и футере (проверяется `CheckSource.py`).
  `native/CMakeLists.txt` (VERSION 0.1.1) и `hr_abi_version()` (= 1) не менялись.

Регрессии библиотеки (12 новых, выполнены в Linux-job и в Windows-job): папка только с MP4 после
записи, переименования и удаления; превью вне папки; миграция 0.1.2 с сохранением Id и
идемпотентностью; клип 0.1.2 виден и удаляется до миграции; копия с дублирующим Id получает новый
Id; перепривязка после переименования папки; очистка записей удалённых MP4 без затрагивания
других папок; сиротские превью; сиротские sidecar 0.1.2 удаляются, а битые и чужие файлы остаются;
временные файлы; отказ переименования в занятое sidecar-ом имя; копия с дублирующим Id при
удалении и переименовании.

## 4. История: что изменено в 0.1.2

- **IPC correlation ID.** `BusMessage.uniqueid` — непрозрачный токен: непустой, не длиннее
  128 UTF-16 единиц (лимит прежний), без непарных суррогатов. Управляющие символы не удаляются и
  не нормализуются; ответ возвращает `ReferenceUniqueid` посимвольно точно; токены, отличающиеся
  только управляющими символами, — разные запросы. Смысловые поля (routing, имена событий,
  group ID, client label) по-прежнему отклоняют управляющие символы. Логи: только код символа и
  индекс (`U+0000@36`), сырой токен не печатается; SDK-запросы и отказы связываются номером `#N`
  в пределах соединения. JOIN, PID-проверка через `GetNamedPipeClientProcessId`, лимиты, кэш
  ответов и дедупликация сохранены.
- **Тесты IPC:** 13 регрессий (включая NUL-суффикс длиной 37, все классы управляющих символов,
  retransmit без второго события, неотличимость токенов без нормализации, непарные суррогаты,
  границы 128/129, безопасность логов) и сквозной сценарий по синтетической фикстуре JOIN →
  CreateSDKInstance → ConfigureHighlights → OpenGroup → PLAYER_DOWNED / HUNTER_KILLED, с проверкой
  ReferenceUniqueid, PID, JOIN, границ и планирования клипа: start −20 с, end +7 с после последнего
  события, ready +15 с.
- **Выпадающие списки.** `Choice<T>` выводит свою подпись (`ToString`). Шаблоны `ComboBox`/
  `ComboBoxItem` в `Themes/Dark.xaml` передают `ContentTemplate`, `ContentTemplateSelector` и
  `ContentStringFormat`, поэтому `DisplayMemberPath` и item-селекторы работают и в закрытом поле,
  и в popup. Монитор: «Монитор N · W × H · основной» (ID `\\.\DISPLAYn` хранится отдельно в `Id`
  и настройках). Недоступное сохранённое устройство показывается без пути и без GUID.
- **Галерея и AV1.** Подсказка при ошибке плеера содержит HRESULT. Для `0xC00D11B1` код назван
  общим `NS_E_WMP_FILE_OPEN_FAILED` и не выдаётся за конкретную причину; подсказка перечисляет
  возможные варианты. Ошибка плеера пишется предупреждением, а не сбоем экспорта; клип не
  меняется. Кнопка «Открыть в проигрывателе» передаёт сохранённый MP4 приложению Windows по
  умолчанию. Перекодирование, автоматическая смена кодека и изменение старых файлов не добавлены.
- **Версии в 0.1.2.** `native/CMakeLists.txt` и `hr_abi_version()` (= 1) не менялись.

## 5. Ограничения и что НЕ проверено

- Не проверено на железе: WGC-захват, GPU-кодирование AMF/NVENC/QSV, WASAPI, воспроизведение
  HEVC/AV1 встроенным плеером, реальная игра Hunt, живой NVIDIA IPC.
- Запрос рамки (`RequestAccessAsync(Borderless)`) не проверен на Windows: неизвестно, какой статус
  вернёт portable EXE. Упакованная сборка с capability `graphicsCaptureWithoutBorder` в 0.1.3 не
  создаётся, поэтому путь «рамка снята по согласию» не проверялся ни одним прогоном.
- Миграция 0.1.2 проверена синтетическими тестами во временных папках и в CI, но не на
  библиотеке реального пользователя. Перед обновлением рекомендуется копия папки клипов.
- **Осознанное изменение:** метаданные больше не лежат рядом с MP4. Клип, перенесённый вместе
  с папкой на другой ПК, без записи библиотеки в галерее не появится; сам MP4 при этом цел.
- Синтетические фикстуры проверяют форму протокола и поведение приложения, но не то, какие
  токены реально присылает игра.
- Live smoke `--pipe-smoke --multi --control-id` против запущенной Studio не запускался: нужна
  интерактивная сессия с буфером. Сценарий описан в `WINDOWS_VALIDATION.md`.
- WPF-тесты проверяют текст в визуальном дереве, выбранное значение и элементы popup. Они не
  сравнивают пиксели и внешний вид темы.
- Подпись монитора (порядковый номер и «основной») формируется при перечислении мониторов
  Windows; многомониторная конфигурация не проверялась.
- Открытие в проигрывателе Windows (ShellExecute) не проверялось на ПК пользователя.
- Артефакт CI — unsigned QA-сборка. Это не подписанный релиз, не проверенный на чистом ПК.
- CI не заменяет аппаратные испытания из `WINDOWS_VALIDATION.md`.

## 6. Жёлтая рамка WGC: официальные условия и поведение 0.1.3

- `GraphicsCaptureSession.IsBorderRequired` доступно начиная с Windows 10 версии 2104
  (10.0.20348.0). Рамку можно отключить, только если приложение получило согласие пользователя
  через `GraphicsCaptureAccess.RequestAccessAsync` с `GraphicsCaptureAccessKind.Borderless`. Для
  этого запроса в манифесте пакета должна быть capability `graphicsCaptureWithoutBorder`. Если
  пользователь отказал, значение игнорируется, и рамка остаётся. Рамка также остаётся, если другое
  приложение требует её для того же окна или монитора. Источники:
  [IsBorderRequired](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired),
  [RequestAccessAsync](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscaptureaccess.requestaccessasync),
  [GraphicsCaptureAccessKind](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscaptureaccesskind).
- Возможные значения `AppCapabilityAccessStatus` по
  [документации](https://learn.microsoft.com/en-us/uwp/api/windows.security.authorization.appcapabilityaccess.appcapabilityaccessstatus):
  `Allowed`, `DeniedByUser`, `DeniedBySystem`, `NotDeclaredByApp`, `UserPromptRequired`. Для
  запросов `UserPromptRequired` не возвращается. Лог Studio пишет фактический статус.
- Capabilities описаны для пакетных приложений; условия для неупакованного Win32 EXE указаны в
  [общем справочнике](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations).
- Сторонние отчёты расходятся: в одних сообщается, что `IsBorderRequired = false` работает в
  неупакованном процессе, даже когда `RequestAccessAsync` отвечает `DeniedBySystem`; в других — что
  это не работает. Такое поведение не задокументировано и не является контрактом. Поэтому Studio
  не выставляет `IsBorderRequired = false` без статуса `Allowed`.

**Что делает 0.1.3 при старте записи:**

1. Если сборка Windows не содержит `GraphicsCaptureSession.IsBorderRequired`, запрос не
   выполняется; лог: `… this Windows build has no GraphicsCaptureSession.IsBorderRequired.`
2. Иначе `RequestAccessAsync(Borderless)` выполняется в отдельном потоке с собственным
   apartment. Ждём не дольше 3 с: при таймауте рамка остаётся, лог: `… did not answer … within 3 seconds.`
3. Статус `Allowed` — единственный случай, когда вызывается `IsBorderRequired(false)`; лог:
   `Windows granted borderless capture by consent.`
4. Любой другой статус: `… Windows answered <статус> to the consent request.`; при исключении WinRT —
   `… consent request failed: <текст>.`
5. Запись в любом случае продолжается; строка `OS capture indicator is retained: …` в логе
   означает, что рамка осталась.

Реестр не меняется, индикатор не скрывается обходом, исчезновение рамки не обещается. Для
portable/неупакованного EXE ожидается отказ, но фактический статус покажет лог на конкретной
Windows.

## 7. Как воспроизвести стандартным способом

```powershell
# На машине с .NET 8 SDK; GPU, OBS и игра не нужны:
dotnet run --project tests/HuntReplay.Tests -c Release
python tools/CheckSource.py

# Только Windows / .NET 8: WPF-регрессии выбора устройств и подписей ComboBox:
dotnet run --project tests/HuntReplay.WindowsTests -c Release

# Windows native/WPF build (запускает оба набора выше):
.\tools\Build.ps1
```

Диагностика live IPC (только при запущенном буфере Studio; проверяет протокол, не запись):

```powershell
dotnet run --project tests/HuntReplay.Tests -c Release -- --pipe-smoke --multi --control-id
```

## 8. История: версия 0.1.1

- Коммит `9581710`, [run 37856493230](https://github.com/Mlovo/HuntOpenHighlights/actions/runs/37856493230):
  оба job прошли; Linux/.NET 8 — 83 регрессии Core/IPC; Windows — 83 + 18 WPF-регрессий;
  MSVC C++20/WinRT, FFmpeg import libraries, C#/XAML и single-file publish; artifact
  `HuntReplayStudio-win-x64-unsigned-QA`, версия 0.1.1.
- Исправления 0.1.1: сохранение ID устройств при refresh/unplug, отдельные NV12 GPU-текстуры,
  распознавание служебных ACK, точная диагностика SDK.
- Пользовательский отчёт с ПК после 0.1.1: ручной клип сохранился (MP4 ~60,4 с, stream copy);
  автоклип со смерти не записался из-за `Invalid IPC identifier … control characters (length 37)`,
  а галерея показала `Плеер: 0xC00D11B1`. Оба случая разобраны в разделах 4 и 6.
