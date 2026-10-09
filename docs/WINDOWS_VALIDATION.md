# Windows / GPU acceptance checklist

**Ни один пункт ниже не отмечен выполненным автоматически.** Linux regression
suite не является тестом Windows, аппаратных драйверов или Hunt/EAC. Сохраняйте
отчёты диагностики, модель GPU, driver version, OS build, выбранные параметры и
SHA-256 проверяемого exe. Не называйте сборку production-ready до этих проверок.

## 1. Сборка и чистый ПК

- [ ] Windows CI / локальный `tools/Build.ps1` завершается без ошибок C++/C#/XAML.
- [ ] На чистом Windows x64 без установленного .NET, Python, OBS и FFmpeg запускается
      один exe; никакой сторонний рекордер или ffmpeg.exe не появляется в процессах.
- [ ] Без сети первый запуск, извлечение ресурсов и запись работают при уже
      установленном драйвере и системных сертификатах. Изменённая native DLL в cache
      восстанавливается из embedded payload либо приводит к явной ошибке.
- [ ] Оригинальная MessageBus имеет ожидаемый SHA-256 и Valid/NVIDIA signature.
- [ ] 100% / 125% / 150% / 200% DPI; другой монитор; минимальный размер окна;
      настройки/кнопки остаются доступны через scroll.
- [ ] Нет демонстрационных клипов, фальшивого «GPU активен» или hardcoded RAM.
- [ ] Apply / повторный refresh сохраняет Default и явные ID монитора, output, mic.
      Unplug не подменяет выбранное устройство другим: видна unavailable опция;
      replug возвращает название. `null` из config 0.1.0 восстанавливается как Default.
- [ ] `dotnet run --project tests/HuntReplay.WindowsTests -c Release` — 18 PASS,
      без обращения к GPU/микрофону/реестру.

## 2. UAC, реестр и восстановление (желательно VM snapshot)

- [ ] Стандартный пользователь: GUI не elevated, UAC только у helper.
- [ ] UAC Cancel: нет частичных ключей/файлов, доступен ручной режим.
- [ ] Fresh install: обе registry views, точные четыре значения; защищённые ACL DLL
      и backup, NVIDIA debug logging=0 и log directory защищён от подмены;
      собственный live log Studio доступен обычному пользователю в LocalAppData;
      перезапуск не требует повторного UAC.
- [ ] Существующая NVIDIA registration: без отдельного согласия не заменяется;
      backup сохраняет типы/старые значения; неизвестная DLL не затирается.
- [ ] Access denied / AV lock / ошибочная подпись / отказ диска посреди установки:
      rollback либо явная ошибка с сохранённой информацией для восстановления.
- [ ] Reparse/junction `C:\NvidiaShim` или ProgramData: отказ, а не elevated запись
      в произвольную цель. Подложенный не-admin backup не доверяется.
- [ ] Восстановление возвращает исходные значения, не удаляет NVIDIA parent keys,
      другие ModuleMap entries, клипы и последующие изменения другого installer.
- [ ] Заблокированная игрой DLL при uninstall: ошибка видна; игру нужно закрыть.

## 3. Pipe / protocol

- [ ] Studio занимает `NvMessageBusBroadcast` до запуска игры; JOIN + peer присутствуют
      в live log; `createSDKInstanceRequest` даёт корректное имя игры и scopes.
- [ ] После JOIN служебный ACK не вызывает disconnect / цикл ответов; в логе
      появляется `IPC SDK · CreateSDKInstanceRequest`, затем `SDK авторизован`.
      Повторное подключение проходит ту же последовательность.
- [ ] Игра регистрирует `HUNTER_KILLED`, `PLAYER_DOWNED`, открывает/закрывает match group.
- [ ] Старый Python / NVIDIA server уже держит pipe: понятный статус конфликта,
      никакого убийства чужого процесса или ложного «IPC online».
- [ ] Обычная игра под тем же пользователем подключается; чужая user session — нет.
- [ ] Переподключение, DestroySDK, несколько clients не смешивают группы/счётчики.
- [ ] Разрыв в середине frame / malicious length / неверный PID не роняют приложение.
- [ ] Закрытие группы и выход из игры не отменяют уже принятый highlight.
- [ ] 0.1.2: SDK-запросы с токеном, оканчивающимся на NUL (форма поля 0.1.1, длина 37),
      принимаются: `dotnet run --project tests/HuntReplay.Tests -c Release -- --pipe-smoke --multi --control-id`
      при запущенном буфере. Ожидается `OK CreateSDKInstanceRequest` и далее без disconnect.
      В `studio.log`: `IPC · BusMessage.uniqueid содержит управляющие символы (U+0000@36)`;
      сырого токена в логе нет. Это **синтетический** клиент, не перехват игры.

### Реальный smoke test без Hunt

Явно выберите **монитор** в настройках и запустите буфер. На экране не должно
быть данных, которые вы не хотите записывать. Подождите минимум 10 секунд.

```powershell
dotnet run --project tests/HuntReplay.Tests -c Release -- --pipe-smoke --multi
```

Два события приходят через **12 секунд**, затем group close. Проверяйте:

- [ ] В логе два реальных accepted события, но **один** `MULTI_KILL_x2` MP4.
- [ ] Конец видео ~7 с после второго события (или configured/game endDelta).
- [ ] Файл появляется после coalesce horizon (~15 с от последнего события),
      а не слишком рано на 7-й секунде. Эти лишние 8 с не входят в сам ролик.
- [ ] Реальные video + audio, читабельные timestamps, thumbnail и sidecar.

Этот tool не подменяет GPU smoke test: ответ IPC означает приём события, а не
подтверждение физически записанного и воспроизводимого MP4.

## 4. Матрица аппаратных энкодеров

Для каждого доступного физического GPU заполните таблицу. `N/A — hardware does
not support` допустимо; не выдавайте unsupported за PASS.

| GPU / driver / OS | H.264 1080p60 | HEVC 1440p60 | AV1 (если есть) | 4K60 | 1080p120 | A/V ≥30 мин |
|---|---|---|---|---|---|---|
| AMD / AMF | не проверено | не проверено | не проверено | не проверено | не проверено | не проверено |
| NVIDIA / NVENC | не проверено | не проверено | не проверено | не проверено | не проверено | не проверено |
| Intel / QSV | не проверено | не проверено | не проверено | не проверено | не проверено | не проверено |
| CPU x264 (explicit) | не проверено | N/A | N/A | зависит от CPU | зависит от CPU | не проверено |

Для каждой комбинации:

- [ ] Автоопределение выбирает GPU **монитора**, включая ноутбук iGPU+dGPU.
- [ ] Диагностика действительно выдаёт encoded GPU packets и открывает WASAPI/AAC;
      unsupported codec показывает ошибку, не скрытый CPU fallback.
- [ ] В 0.1.1 старт использует отдельные NV12 GPU textures, без ошибки создания
      12-slice render-target array (`80070057`). Проверить также Native размеры
      и QSV 32-pixel alignment; успешный H.264 probe не подтверждает HEVC/AV1.
- [ ] MP4 начинается с декодируемого ключевого кадра, корректны SPS/extradata,
      color range, aspect ratio; нет зелёных кадров, stale/reused GPU surfaces.
- [ ] Реальное source size / selected output / частота кадров совпадают с UI.
- [ ] Длительное видео не ускоряется после пропусков кадров; drop counter меняется.
- [ ] 30, 60 и 120 FPS; monitor 60 Hz при 120 encoding не выдаётся за 120 уникальных
      источниковых кадров. Статичный WGC source может повторяться.
- [ ] Window resize, alt-tab, minimize, fullscreen/borderless, monitor disconnect,
      lock/unlock, sleep/wake и driver reset: восстановление либо честная ошибка.
- [ ] SDR используется явно. HDR не считается пройденным тестом этой версии.

Проверяйте записанные файлы отдельным decoder/probe на **машине тестирования**
(например, FFmpeg/ffprobe из build SDK). Это QA-инструмент, не зависимость приложения.

```powershell
.\native\vendor\ffmpeg\bin\ffprobe.exe -v error -show_streams -show_format "D:\Clips\Hunt_....mp4"
.\native\vendor\ffmpeg\bin\ffmpeg.exe -v error -i "D:\Clips\Hunt_....mp4" -f null -
```

Для проверки старта/монотонности:

```powershell
.\native\vendor\ffmpeg\bin\ffprobe.exe -v error -select_streams v:0 -show_packets `
  -show_entries packet=pts_time,dts_time,flags "D:\Clips\Hunt_....mp4"
```

- [ ] Первый video packet keyframe, неотрицательный rebase, strictly increasing DTS
      в каждом потоке, разумная duration без длинного пустого пролога.
- [ ] Ограничение точности trim до GOP / AAC packet отражено в результатах.

## 5. Аудио

- [ ] Default headphones, явно выбранный endpoint, USB/Bluetooth, float/PCM formats.
- [ ] Тишина → звук → тишина не сдвигает video/audio timeline.
- [ ] Микрофон off действительно не открывается; on соблюдает Windows permission.
- [ ] Volume 0/50/100%; mix и два отдельных AAC tracks, первый по умолчанию — игра.
- [ ] Синхронизация визуального хлопка/выстрела и звука в начале/конце 30–60 min session;
      допустимое отклонение заранее измерено, не объявлено «нулевым».
- [ ] Unplug/default switch останавливает старую session с ошибкой; ограниченная
      автоматика либо ручной restart открывает новый endpoint; не бесконечный retry loop.
- [ ] Другие приложения слышны в loopback, как честно указано в UI.
- [ ] Если звуковой сигнал Studio попадает в replay, выключение уведомления решает это.

## 6. Буфер, события, диск

- [ ] 15/60/300 с; 10/25/80 Мбит/с; нет непрерывного video writing на SSD.
- [ ] RAM до/во время/после pinned multi-kill серии. Измерьте payload bytes **и**
      process working set/VRAM отдельно. Удерживаемые exports учитываются.
- [ ] На малом memory budget целые GOP вытесняются; сокращение отображается,
      файл остаётся декодируемым. Ни OOM-цикла, ни бесконечного роста pending jobs.
- [ ] 1/2/3 kills, gap 7/12/15/16 с; max sequence; другой match; kill + downed;
      duplicate request; оба события disabled; manual F9 при удерживаемых WASD.
- [ ] Manual clipping ждёт audio jitter drain, не теряет начало из-за eviction race.
- [ ] Выход/stop до post-roll: доступный клип сохраняется и помечается truncated.
- [ ] Disk full, read-only folder, removable drive, Unicode path, path collision,
      filename traversal, слишком много saves: без перезаписи чужого файла и
      без SUCCESS toast при ошибке.
- [ ] Auto cleanup opt-in, oldest first, лимит 15/30/60 GB, unlimited OFF;
      foreign MP4 и защищённые/проигрываемые/только что сохранённые clips остаются.
- [ ] Rename/delete, busy file, corrupted sidecar, symlink file/root — безопасный отказ.
- [ ] Crash во время mux: partial не показывается как готовый clip. RAM не обязана
      переживать crash; готовый MP4 без sidecar восстанавливается вручную из папки.

## 7. UI / automation

- [ ] Dashboard, gallery, settings, diagnostics, keyboard focus, dark dropdowns,
      sliders и редактирование чисел в системной locale.
- [ ] Settings survive restart; malformed config preserved; backup есть; native
      format/audio topology change starts a new ring, а не смешивает codec headers.
- [ ] 0.1.2 Выпадающие списки (источник, разрешение, кодировщик, кодек, GOP, F-клавиша,
      лимит библиотеки, аудиодорожки): везде подписи («Окно Hunt / …», «1920 × 1080»),
      не `Choice { … }`, и в списке, и в закрытом поле.
- [ ] 0.1.2 Монитор: «Монитор N · W × H · основной» (основной помечен). Сохранённое
      значение в `config.json` — Windows ID вида `\\.\DISPLAYn`, не подпись.
- [ ] 0.1.2 Отключённый монитор или аудиоустройство: «Недоступно · …» без `\\.\DISPLAY`
      и без GUID endpoint; после повторного подключения выбор восстанавливается, настройка
      не сбрасывается.
- [ ] Player: H.264 play/pause/seek; switching clip сбрасывает позицию; hide/close
      не оставляет фоновое воспроизведение. HEVC/AV1: проверить с/без Windows codecs,
      отсутствие decoder показывается как ограничение, запись остаётся доступна.
- [ ] 0.1.2 Клип, который встроенный плеер не открыл: подсказка с кодом (например,
      `0xC00D11B1` = NS_E_WMP_FILE_OPEN_FAILED, общая ошибка открытия), текст
      «Клип сохранён и не изменён». Ошибка не записывается как сбой экспорта; кнопка
      «Открыть в проигрывателе» открывает MP4 приложением Windows. Старый файл не
      перекодируется, кодек в настройках не меняется автоматически.
- [ ] 0.1.2 Жёлтая рамка WGC: в portable EXE она остаётся — это ожидаемое поведение, а не
      дефект. В логе строка `OS capture indicator is retained: Windows removes it only for
      packaged apps …`, запись продолжается. Исчезновение рамки не является критерием
      приёмки; реестр и настройки Windows для этого не меняются.

- [ ] Toast с настоящим preview; Windows DND; activation из трея/уведомления.
- [ ] RegisterHotKey collision сообщает ошибку и сохраняет прежнюю клавишу.
- [ ] Автостарт HKCU, `--tray`, `--portable`, закрытие в трей, второй экземпляр,
      запуск/выход Hunt, ручная пауза автоматики, полный graceful shutdown.

## Troubleshooting

| Симптом | Проверить |
|---|---|
| IPC offline | Старый `server.py`, NVIDIA App/Container, другой пользователь; restart Studio после освобождения pipe |
| Hunt виден, но SDK не подключён | Установка DLL/registry, включённые Highlights в игре, restart игры после установки, совпадение user session |
| `Не указан монитор` / `Не указаны аудиоустройства` после Apply (0.1.0) | Обновить до 0.1.1: ID больше не стираются при refresh. Config/клипы удалять не нужно; старые null IDs читаются как Default |
| `GPU NV12 frame pool ... Could not create the texture (80070057)` (0.1.0) | Полностью пересобрать `tools/Build.ps1`, включая native DLL: в 0.1.1 отдельные текстуры вместо NV12 массива. Если ошибка остаётся — новый полный текст, GPU/driver, разрешение и capture mode; CPU автоматически не включается |
| `Invalid IPC identifier: BusMessage.uniqueid … contains control characters (length 37)` (0.1.1) | 0.1.2 принимает такой токен и возвращает его точно. Если строка всё же появляется в 0.1.2, это другая причина: сохраните строку лога (в 0.1.2 она называет код символа и индекс, не токен) |
| `Плеер: 0xC00D11B1` (0.1.1) | 0.1.2 показывает подсказку вместо «кодек»; клип сохранён. Подсказка не утверждает причину. Для новых клипов можно выбрать H.264 |
| Жёлтая рамка при захвате | Официально убирается только для упакованного приложения с `graphicsCaptureWithoutBorder` и согласием пользователя. Portable EXE её сохраняет |
| `Invalid IPC identifier` сразу после JOIN (0.1.0) | 0.1.1 отличает ACK от SDK request, допускает пустой descriptive client, использует JOIN routing. Если disconnect остаётся — сохранить новое сообщение с точным полем/типом; удалять регистрацию NVIDIA не нужно |
| Unknown/unsupported encoder | Фактический GPU дисплея, современный driver, поддержка выбранного codec; explicit x264 только по выбору пользователя |
| Нет WGC кадров | Окно восстановлено, borderless вместо проблемного exclusive mode, интерактивная Windows session, нет protected capture |
| WASAPI denied / no device | Default playback device, подключение USB/BT, Windows microphone privacy, settings IDs старого устройства |
| Плеер не играет HEVC/AV1 | Системный decoder / Media Feature Pack; попробуйте H.264. Native recording и thumbnail от Windows decoder не зависят |
| После kill файла ещё нет | Дождитесь coalesce gap 15 с; 7-секундный video tail не означает finalize через 7 с |
| Короткое начало | Buffer warming, RAM cap, game startDelta, keyframe availability; смотрите truncated sidecar/log |
| Настройка не сохранилась | Красное numeric validation поле, недоступная portable directory, ошибка в filename template/config |
| Пропал legacy MP4 из галереи | Legacy файлы без .hunt.json не индексируются и не удаляются Studio; откройте папку |

## EAC / правила игры

- [ ] Изучены актуальные правила и ответы разработчика игры/античита.
- [ ] Файлы игры не меняются; не появляются injection hooks/input macros.
- [ ] Испытание на разрешённой конфигурации не выдаётся за универсальное одобрение.

Подпись NVIDIA DLL и официальный API захвата не дают стороннему разработчику
права обещать «100% EAC safe». Фиксируйте наблюдения, не выдавайте их за гарантию.
