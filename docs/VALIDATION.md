# Выполненная проверка и ограничения среды

Дата: **2026-10-09**. Ветка: `arena/83b0cc64-huntopenhighlights`.

Рабочая среда для этой версии: Linux x86-64, без Windows, Visual Studio, Windows SDK,
интерактивного рабочего стола, физического GPU, WGC, WASAPI и игры. Доступны GitHub,
npm и PyPI; NuGet и dot.net недоступны, поэтому .NET SDK в среде не было.
Это **не** испытание аппаратной записи, живой игры или античита.

## 1. Windows CI — основное подтверждение сборки и WPF-тестов

Итоговый код версии 0.1.2 — коммит `8e5f577`. Документы добавлены следующим коммитом и не
меняют сборку.

- **PASS — [run 37927275936](https://github.com/Mlovo/HuntOpenHighlights/actions/runs/37927275936)**
  (коммит `8e5f577`, оба job завершились успешно):
  - `core-and-protocol` (ubuntu-latest, .NET 8 SDK): `dotnet run --project tests/HuntReplay.Tests`
    и `python3 tools/CheckSource.py` — успешно.
  - `windows-build` (windows-2022, .NET 8, MSVC): `tools/Build.ps1` выполнил
    `tests/HuntReplay.Tests` (**105** тестов Core/IPC/view-model), затем
    `tests/HuntReplay.WindowsTests` (**22** WPF-регрессии: 18 прежних на настоящих ComboBox и
    4 новых на production `Themes/Dark.xaml`), собрал native DLL, выполнил self-contained
    single-file publish и загрузил artifact `HuntReplayStudio-win-x64-unsigned-QA`
    (id 11614791506, 154 МБ, хранение 7 дней). Аннотации: 0 ошибок, 11 предупреждений:
    Node.js 20 deprecation и прежние предупреждения сторонних FFmpeg-заголовков
    (`libavutil/common.h`), уже присутствовавшие в 0.1.1.
  - Лог job из песочницы недоступен (хост логов Actions не разрешён), поэтому результат
    подтверждён статусами job и шагов, аннотациями и artifact. Счётчики WPF-тестов — по
    исходникам; код выхода 0 означает, что ни один не упал.

История прогонов версии 0.1.2:

| Коммит | Run | Результат |
|---|---|---|
| `6153ba4` | [37926574340](https://github.com/Mlovo/HuntOpenHighlights/actions/runs/37926574340) | `core-and-protocol` PASS; `windows-build` FAIL: компиляция `WindowsTests` (нет `System.IO`, отсутствовал хелпер `False`). Исправлено в `f7b8505`. |
| `f7b8505` | [37926834904](https://github.com/Mlovo/HuntOpenHighlights/actions/runs/37926834904) | оба job PASS; до отката версии CMake (см. ниже). |
| `8e5f577` | [37927275936](https://github.com/Mlovo/HuntOpenHighlights/actions/runs/37927275936) | оба job PASS — итоговая проверка кода. |

## 2. Локальная проверка в песочнице (дополнительная, не замена CI)

Инструменты лежат вне репозитория и не коммитятся:

- исполнение: .NET **10.0.11** runtime из npm `@dprint/roslyn-linux-x64-glibc@0.22.1`
  (тот же источник, что и для 0.1.1), вызывается из Python через pythonnet;
- компиляция: Roslyn из того же пакета, language version C# 12, nullable enable,
  предупреждения как ошибки, implicit usings как в SDK;
- Google.Protobuf **3.36.2**, собранный из исходников upstream `v36.2` (определения net8);
- protoc **30.2** (PyPI `protoc-wheel-0`) сгенерировал C# из `proto/*.proto`;
  Python protobuf **7.35.1** — версия из докстринга генератора фикстур.

Это не официальный прогон .NET 8 SDK: среда исполнения и target-pack отличаются от CI.
Результаты ниже подтверждают логику, но не заменяют Windows CI.

- `tests/HuntReplay.Tests`: **105 passed, 0 failed** (83 прежних + 22 новых: 13 IPC,
  5 устройств и подписей, 4 галереи/плеера).
- `python3 tools/CheckSource.py`: PASS — XML, pinned payloads, 45 production source files,
  15 ABI exports, golden fixtures, согласованность версии.
- Roslyn-разбор всех C#-файлов `src/` и `tests/`: 45 файлов, 0 синтаксических ошибок.
- `tools/GenerateProtocolFixtures.py`: два прежних golden-файла воспроизводятся побайтно;
  новый `python-synthetic-uniqueid-wire.json` создан из `proto/BusMessage_pb2.py` и
  `proto/sdk_pb2.py`, помечен `"synthetic": true`, не является перехватом игры.
- Выражение подписи монитора из `native/src/devices.cpp` проверено отдельно через g++
  (`-std=c++20`); MSVC в песочнице нет, компиляция C++ подтверждена только CI.

## 3. Что изменено в 0.1.2

- **IPC correlation ID.** `BusMessage.uniqueid` — непрозрачный токен: непустой, не длиннее
  128 UTF-16 единиц (лимит прежний), без непарных суррогатов. Управляющие символы не
  удаляются и не нормализуются; ответ возвращает `ReferenceUniqueid` посимвольно точно;
  токены, отличающиеся только управляющими символами, — разные запросы. Смысловые поля
  (routing, имена событий, group ID, client label) по-прежнему отклоняют управляющие
  символы. Логи: только код символа и индекс (`U+0000@36`), сырой токен не печатается;
  SDK-запросы и отказы связываются номером `#N` в пределах соединения. JOIN, PID-проверка
  через `GetNamedPipeClientProcessId`, лимиты, кэш ответов и дедупликация сохранены.
- **Тесты IPC:** 13 регрессий (включая NUL-суффикс длиной 37, все классы управляющих
  символов, retransmit без второго события, неотличимость токенов без нормализации,
  непарные суррогаты, границы 128/129, безопасность логов) и сквозной сценарий по
  синтетической фикстуре JOIN → CreateSDKInstance → ConfigureHighlights → OpenGroup →
  PLAYER_DOWNED / HUNTER_KILLED, с проверкой ReferenceUniqueid, PID, JOIN, границ и
  планирования клипа: start −20 с, end +7 с после последнего события, ready +15 с.
- **Выпадающие списки.** `Choice<T>` выводит свою подпись (`ToString`). Шаблоны
  `ComboBox`/`ComboBoxItem` в `Themes/Dark.xaml` передают `ContentTemplate`,
  `ContentTemplateSelector` и `ContentStringFormat`, поэтому `DisplayMemberPath` и
  item-селекторы работают и в закрытом поле, и в popup. Монитор: «Монитор N · W × H ·
  основной» (ID `\\.\DISPLAYn` хранится отдельно в `Id` и настройках). Недоступное
  сохранённое устройство показывается без пути и без GUID.
- **Галерея и AV1.** Подсказка при ошибке плеера содержит HRESULT. Для `0xC00D11B1` код
  назван общим `NS_E_WMP_FILE_OPEN_FAILED` и не выдаётся за конкретную причину; подсказка
  перечисляет возможные варианты (видеокодек, повреждённый файл, проигрыватель). Ошибка
  плеера пишется предупреждением, а не сбоем экспорта; клип не меняется. Кнопка
  «Открыть в проигрывателе» передаёт сохранённый MP4 приложению Windows по умолчанию.
  Перекодирование, автоматическая смена кодека и изменение старых файлов не добавлены.
- **Жёлтая рамка WGC.** Не отключается и не скрывается. См. раздел 5.
- **Версии.** Приложение и подпись окна — 0.1.2 (проверяется `CheckSource.py`).
  `native/CMakeLists.txt` и `hr_abi_version()` (= 1) не менялись: ABI не трогали, версия
  native не требовалась.

## 4. Ограничения и что НЕ проверено

- Не проверено на железе: WGC-захват, GPU-кодирование AMF/NVENC/QSV, WASAPI,
  воспроизведение HEVC/AV1 встроенным плеером, реальная игра Hunt, живой NVIDIA IPC.
- Синтетические фикстуры проверяют форму протокола и поведение приложения, но не то,
  какие токены реально присылает игра.
- Live smoke `--pipe-smoke --multi --control-id` против запущенной Studio не запускался:
  нужна интерактивная сессия с буфером. Сценарий описан в `WINDOWS_VALIDATION.md`.
- WPF-тесты проверяют текст в визуальном дереве, выбранное значение и элементы popup. Они
  не сравнивают пиксели и внешний вид темы.
- Подпись монитора (порядковый номер и «основной») формируется при перечислении мониторов
  Windows; многомониторная конфигурация не проверялась.
- Открытие в проигрывателе Windows (ShellExecute) не проверялось на ПК пользователя.
- Артефакт CI — unsigned QA-сборка. Это не подписанный релиз, не проверенный на чистом ПК.
- CI не заменяет аппаратные испытания из `WINDOWS_VALIDATION.md`.

## 5. Жёлтая рамка WGC: официальные условия

- `GraphicsCaptureSession.IsBorderRequired` доступно начиная с Windows 10 версии 2104
  (10.0.20348.0). Рамку можно отключить, только если приложение получило согласие
  пользователя через `GraphicsCaptureAccess.RequestAccessAsync` с
  `GraphicsCaptureAccessKind.Borderless`. Для этого запроса в манифесте пакета должна быть
  capability `graphicsCaptureWithoutBorder`. Если пользователь отказал, значение игнорируется,
  и рамка остаётся. Рамка также остаётся, если другое приложение требует её для того же окна
  или монитора. Источники:
  [IsBorderRequired](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired),
  [RequestAccessAsync](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscaptureaccess.requestaccessasync),
  [GraphicsCaptureAccessKind](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscaptureaccesskind).
- Capabilities описаны для пакетных приложений; условия для неупакованного Win32 EXE указаны
  в [общем справочнике](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations).
- Сторонние отчёты расходятся: в одних сообщается, что `IsBorderRequired = false` работает в
  неупакованном процессе, даже когда `RequestAccessAsync` отвечает `DeniedBySystem`; в других
  — что это не работает. Такое поведение не задокументировано. Studio на него не опирается и
  не выставляет `IsBorderRequired = false` без согласия.
- Поэтому portable/неупакованный EXE сохраняет рамку, запись продолжается, а лог фиксирует
  причину: `OS capture indicator is retained: Windows removes it only for packaged apps with
  the graphicsCaptureWithoutBorder capability and user consent. Recording continues with the indicator.`
  В настройках под выбором источника есть пояснение. Реестр не меняется, индикатор не
  обходится, исчезновение рамки не обещается.

## 6. Как воспроизвести стандартным способом

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

## 7. История: версия 0.1.1

- Коммит `9581710`, [run 37856493230](https://github.com/Mlovo/HuntOpenHighlights/actions/runs/37856493230):
  оба job прошли; Linux/.NET 8 — 83 регрессии Core/IPC; Windows — 83 + 18 WPF-регрессий;
  MSVC C++20/WinRT, FFmpeg import libraries, C#/XAML и single-file publish; artifact
  `HuntReplayStudio-win-x64-unsigned-QA`, версия 0.1.1.
- Исправления 0.1.1: сохранение ID устройств при refresh/unplug, отдельные NV12 GPU-текстуры,
  распознавание служебных ACK, точная диагностика SDK.
- Пользовательский отчёт с ПК после 0.1.1: ручной клип сохранился (MP4 ~60,4 с, stream copy);
  автоклип со смерти не записался из-за `Invalid IPC identifier … control characters (length 37)`,
  а галерея показала `Плеер: 0xC00D11B1`. Оба случая разобраны в разделах 3 и 5.
