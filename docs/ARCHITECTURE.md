# Архитектура HuntReplay Studio

## 1. Границы компонентов

```text
                      ORIGINAL NVIDIA GfeSDK (в игре, без модификаций)
                                        │
                              подписанная MessageBus.dll
                                        │ byte-mode named pipe / protobuf
                          \\.\pipe\NvMessageBusBroadcast
                                        │
                     HighlightsPipeServer → HighlightsSession
                                        │ timestamp из capture epoch
                                        ▼
   WPF / F9 ────────────────► RecorderService / HighlightPlanner
                                        │ Pin + Snapshot
                                        ▼
                                   PacketRingBuffer ◄────────────────────┐
                                        │ immutable compressed packets │
                                        ▼                              │
                         bounded single-reader export queue             │
                                        │                              │
                           libavformat MP4 stream-copy                  │
                                        │                              │
                        atomic rename + metadata + thumbnail            │
                                        │                              │
                              Gallery / Toast / retention               │
                                                                       │
          Windows.Graphics.Capture ──► D3D11 BGRA texture                │
                    OS / DWM            │                              │
                                GPU VideoProcessor → NV12               │
                                        │                              │
                         AMF / NVENC / QSV (FFmpeg API)                 │
                                        └──── encoded AVPacket ─────────┤
                                                                       │
              WASAPI loopback + optional mic                            │
                         │ QPC-timestamped PCM                          │
                 resample + timeline + mix / separate                   │
                         │ AAC 48 kHz stereo                            │
                         └──────── encoded AVPacket ────────────────────┘
```

**Ни OBS WebSocket, ни клавиатурных макросов, ни процессов FFmpeg в этом графе нет.**
Старый Python-код — источник совместимого поведения, не runtime-зависимость.

### Managed проекты

- **Core / net8.0:** зависит только от BCL, проверяется без Windows.
- **Protocol / net8.0:** Core + Google.Protobuf; C# генерируется Grpc.Tools из
  оригинальных proto2-схем. Единственное изменение схем — `csharp_namespace`,
  которое не меняет wire-номера или сообщения Python.
- **App / net8.0-windows10.0.19041.0 / x64:** WPF, desktop integration и orchestration.
  Все пользовательские параметры редактируются в отдельной копии, затем проходят
  валидацию, атомарное сохранение и перезапуск capture session.

### Native / C++20 DLL

Версионированный C ABI: `native/include/huntreplay_media.h`. Никаких C++ объектов
или исключений на managed-границе. `hr_start` возвращает opaque session;
`hr_stop` завершает capture workers, но **сохраняет codec parameters** до
`hr_destroy`, чтобы можно было дописать ожидающие клипы.

`RuntimeAssets` извлекает все DLL по встроенному манифесту SHA-256; загрузка
основной DLL использует `LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | DEFAULT_DIRS`.
Callback-пакет действителен только до возврата callback. C# делает одну копию
сжатого payload в принадлежащий буферу `byte[]`; делегаты удерживаются до join
нативных потоков. Исключения из callback не пересекают unmanaged ABI.

## 2. Захват и аппаратное кодирование

1. Выбор DXGI adapter по `HMONITOR`, соответствующему окну/монитору. В гибридной
   системе это адаптер **захватываемого дисплея**, не просто первая запись WMI.
2. D3D11 device с BGRA/video support и multithread protection. WARP не выдаётся
   за аппаратную карту.
3. `IGraphicsCaptureItemInterop.CreateForWindow/CreateForMonitor`, затем
   `Direct3D11CaptureFramePool.CreateFreeThreaded`. Не нужен UI DispatcherQueue.
   Курсор выключен; системный индикатор WGC не обходится.
4. Capture worker берёт свежие frames из pool, копирует их в собственную GPU
   texture и закрывает WinRT frame. При resize pool пересоздаётся. Canvas
   энкодера фиксирован на сессию: это не даёт смешивать несовместимые SPS/форматы
   в одном кольце. Новый Native canvas — после перезапуска буфера.
5. `ID3D11VideoProcessor` масштабирует с сохранением пропорций и переводит
   BGRA → NV12 BT.709 limited range. Это SDR pipeline, **не HDR tone mapper**.
6. FFmpeg D3D11 hwdevice/hwframes используют тот же D3D11 device. NVENC/AMF получают
   D3D11 surfaces; QSV — derived device/frames и hardware map. Для QSV pool
   выровнен, output canvas остаётся пользовательского размера.
7. `h264/hevc/av1_{amf,nvenc,qsv}`; при явном CPU выборе — `libx264` с readback.
   Global headers, no B frames, closed GOP, periodic I/IDR requests. Начало
   экспортируемого диапазона дополнительно проверяется по реальному key flag,
   а не по предположению, что каждый N-й пакет — IDR. Video codec parameters
   замораживаются перед публикацией первого video packet: учитываются late global
   headers, `NEW_EXTRADATA` и in-band sequence headers через `extract_extradata`.
   Mux получает защищённую deep copy, а не live `AVCodecContext` другого потока.
8. У кадров общая QPC-based capture epoch. Пропуск кадра не сжимает время:
   PTS отражает elapsed time. При статичном источнике последний кадр повторяется.
   Число кадров/пропусков и отсутствие новых source frames видны в UI.

`hr_probe` не ограничивается `avcodec_find_encoder_by_name`: открывает выбранный
энкодер, создаёт GPU texture, подаёт несколько кадров и требует выходные пакеты.
Также на 200 ms открываются выбранные WASAPI endpoints (микрофон только если
он включён), проверяется AAC; данные пробы отбрасываются, файл не создаётся.
Проба подтверждает выбранную комбинацию, а не все кодеки всех GPU. Полноценная
проверка WGC/audio/A/V выполняется отдельным smoke/QA сценарием.

## 3. Аудио и временная шкала

- WASAPI shared event-driven loopback выбранного render endpoint.
- Микрофон — отдельный capture endpoint и **opt-in**.
- PCM float / PCM8/16/24/32, downmix/resample к 48 kHz stereo.
- `IAudioCaptureClient.GetBuffer` QPC-position и video используют одну epoch.
- Каждый источник пишет в ограниченную PCM timeline с метками абсолютных
  sample positions. Пропуски/тишина заполняются нулями; они не сдвигают звук.
- 120 ms jitter buffer, компенсация небольшого device-clock drift, сброс
  ресемплера при discontinuity. На остановке дочитывается доступный хвост,
  затем AAC flush. Это **не обещание нулевой задержки аудио**.
- Микс = clamp(game + gain × mic); separate mode = game AAC track + mic AAC track.
- Default endpoint проверяется раз в секунду, если выбран `Default`. Смена
  устройства завершает текущую capture session с явной ошибкой; coordinator
  может запустить новую. Тишина не маскируется статусом «успешное переключение».

Loopback не отделяет Hunt от Discord/браузера и может захватить звуки самой Studio.
Чтобы избежать этого, выбирайте соответствующий endpoint / отключайте сигнал.

## 4. Ring buffer: инварианты

`EncodedPacket`:

- stream 0 — video; 1 — game/mix audio; 2 — optional microphone;
- PTS/DTS/duration в **микросекундах общей capture epoch**;
- immutable после передачи payload; никакого несжатого видеокольца в managed RAM;
- строго возрастающий DTS внутри потока (глобальный порядок callback не требуется).

`PacketRingBuffer` держит per-stream FIFO под короткой блокировкой. Обычное
вытеснение сохраняет ключевой кадр **не позже** временной границы. При pin
используется более ранняя граница для незавершённого эпизода. При превышении
байтового лимита удаляется целый самый старый GOP и соответствующее аудио;
если даже один GOP не помещается, кольцо очищается до следующего IDR.

Невозможно одновременно гарантировать произвольную длину серии, точное прошлое
и фиксированный RAM budget. Здесь приоритет — ограничение памяти; snapshots
явно возвращают `TruncatedStart` / `TruncatedEnd`.

Бюджет учитывает длину буфера, максимальную серию, post-roll и coalesce gap,
но ограничен четвертью доступного managed memory budget (минимум 64 MiB).
Вне серии временное вытеснение по-прежнему держит обычные 60 с + GOP margin.
Snapshots ссылаются на существующие массивы. Экспортируемые snapshots могут
удерживать уже вытесненные пакеты; очередь экспорта ограничена двумя
snapshots суммарно (включая исполняемый). Остальные готовые планы остаются
в planner под pin, пока не появится место; graceful stop дописывает их волнами. Это не глобальный лимит working set процесса.

## 5. Multi-kill и lossless smart trim

Алгоритм перенесён из идеи `recorder.py`, но исправляет конфликт `post-roll=7`
и заявленного `coalesce gap=15`:

```text
kill A at 100s:   desired start=80s, video end=107s, finalize no earlier than 115s
kill B at 112s:   desired start=80s, video end=119s, finalize no earlier than 127s
result:          один MULTI_KILL_x2, ~39s + keyframe margin, не два отдельных файла
```

- Coalesce только внутри одного клиента и одной **эпохи группы**. Повторное
  `openGroup` после `closeGroup` с тем же groupId начинает новый матч.
- Максимальная длина цепочки, до 8 pending plans, до 256 events/plan;
  bounded de-duplication по request ID. Повторный pipe request получает
  кэшированный ответ, но не новый фраг.
- `startDelta < 0` задаёт предзапись с clamp к длине кольца; иначе применяется
  fallback 20 с. Post-roll = max(user delay, bounded game `endDelta`).
- `baseTimeStamp` не смешивается с Unix/QPC: его clock domain из этих схем
  однозначно неизвестен. Как и в исходном сервере, deltas отсчитываются от
  получения события; высокая IPC-задержка повлияет на точность.
- Smart trim OFF — запросить полный replay pre-window. ON — окно события.
  «Умная» здесь **event-driven**, не motion/audio/AI detection.
- На готовность добавлено 250 ms на AAC/jitter drain. Эта задержка не добавляется
  к целевому концу клипа. Ручной snapshot тоже удерживает свой start pin.
- Остановка игры/приложения force-flush'ит доступную часть ожидающего эпизода;
  недостающий post-roll отмечается, а не придумывается.

MP4: `avcodec_parameters_copy`, отдельные AVStreams, rebasing timestamps,
`av_interleaved_write_frame`, `av_write_trailer`, `+faststart`. Ни фильтра decode
видео, ни `avcodec_send_frame` на этапе экспорта. Audio срезается на границе AAC
packet (обычно около 21.3 ms), video start — до 1 GOP раньше запрошенного.

## 6. IPC и модель доверия

- Byte pipe: 4 bytes unsigned big-endian length + protobuf payload.
- `ReadExactlyAsync` для partial reads; максимум 4 MiB/frame; 10-секундный deadline
  после первого байта frame; idle соединение не рвётся просто из-за тихого матча.
- `FirstPipeInstance` обнаруживает конфликт с NVIDIA/Python. Последующий listener
  создаётся до передачи connected instance клиентскому task; владение не имеет
  промежутка с нулём server handles.
- `CurrentUserOnly`, максимум 16 instances / 14 обслуживаемых clients.
- PID создания SDK сверяется с `GetNamedPipeClientProcessId`; нельзя авторизовать
  произвольный чужой PID одним полем Protobuf. Это **не криптографическая
  аутентификация игры**: собственный процесс пользователя может генерировать
  события, что и делает live smoke tool.
- Отдельные definitions, groups, counts, bounded response cache на соединение.
- JOIN confirmation и presence `NvNode:SDKAPI` повторяют `server.py`.
- Неподдерживаемые видео-состояния/версии/операции возвращают отрицательный retval,
  а не ложный SUCCESS. Disabled пользовательское событие — валидный no-op.
- `closeGroup.destroyHighlights` удаляет **виртуальную** SDK-группу, не MP4 пользователя.
- Screenshot/overlay/OPS/encrypted messaging не входят в эту реализацию. Они
  диагностируются/отклоняются. Права scope не создают настоящую NVIDIA overlay.

## 7. Установка без постоянно elevated GUI

Startup `--install-shim` / `--uninstall-shim` обрабатывается **до** запуска capture,
трея, нативных ресурсов и пользовательского config. Elevated process принимает
только фиксированное действие и explicit replace flag, не путь к произвольной DLL.

1. Проверка administrator token, install mutex, запрет reparse paths.
2. Existing unknown MessageBus.dll не заменяется. Чужая регистрация NVIDIA
   требует отдельного согласия в мастере.
3. Снимок **четырёх конкретных значений** в Registry64 и Registry32, включая тип,
   наличие значения и родительского ключа. Protected backup в ProgramData.
4. Embedded DLL → temporary file → hash + `WinVerifyTrust` + signer → fixed path.
   ACL: Administrators/SYSTEM write, Users read/execute. Backup также защищён и
   проверяет владельца перед восстановлением. Native engine никуда в HKLM не
   регистрируется.
5. Значения: InstallPath, LogPath, LogLevel, `ModuleMap\MessageBus.dll`.
   LogLevel=0: bus-debug logging выключен. Даже каталог NVIDIA logs защищён
   от записи пользователями, чтобы elevated vendor process не смог пройти через
   подложенный log junction. Собственный live log Studio находится в per-user
   LocalAppData, не в elevated directory.
6. При ошибке — rollback к состоянию непосредственно перед этой попыткой; неудачный
   repair сохраняет исходный backup успешной установки. При восстановлении — только всё ещё принадлежащие
   установке значения; последующие изменения другого NVIDIA installer не затираются.
   Удаляется только собственная DLL с ожидаемым хэшем и только без оставшихся ссылок.
   NVIDIA parent keys и произвольное содержимое папок не удаляются.

32-bit registry view дублируется для SDK lookup, **но payload x64**. 32-битные игры
не поддерживаются этой сборкой. Отдельный легальный x86 payload потребовал бы
другой схемы регистрации и тестирования.

## 8. Жизненный цикл / shutdown

- UI/автоматика сериализуются через operation semaphore; native lifecycle —
  через отдельный semaphore RecorderService.
- Игровые callbacks не ждут UI или дискового mux.
- Stop: закрыть WGC → остановить/flush audio и video → force plans → дождаться
  export queue → уничтожить native session → освободить ring.
- Settings apply всегда создаёт новую codec session; нельзя смешивать HEVC/H264,
  resolution/audio-track topology в одном старом кольце.
- Export failures не становятся SUCCESS toast. `.partial.mp4` не показываются
  в галерее; успешный trailer предшествует финальному имени и sidecar.
- Авария между rename MP4 и sidecar может оставить **готовый, но не индексированный
  MP4**. Он не удаляется автоматически; его можно открыть из папки. Транзакция
  нескольких файлов не выдаётся за атомарную файловую транзакцию.
- Галерея/retention работают только с validated ownership sidecars. Selected
  player clip и только что сохранённый clip защищены от фоновой очистки.

## 9. Что ещё требует реального Windows QA

C++/WinRT ABI, MSVC linking с pinned FFmpeg, AMF/NVENC/QSV hardware paths, GPU
synchronization, WASAPI formats/drift, DPI/player/tray/toast, UAC cancellation,
registry recovery, hardware loss и совместимость с конкретным GfeSDK/Hunt/EAC.
Никакой mock не подменяет эти проверки. См. `WINDOWS_VALIDATION.md`.
