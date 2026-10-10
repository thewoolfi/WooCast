using System.Net;
using WooCast.Protocol;
using WooCast.Protocol.Messages;
using WooCast.Protocol.Net;

namespace WooCast.Capture;

/// <summary>Этап жизненного цикла сессии отправки.</summary>
public enum SenderPhase
{
    Idle,
    Connecting,
    Handshake,
    Negotiating,
    Streaming,
    Stopping,
    Failed,
}

/// <summary>Снимок состояния сессии отправки для интерфейса и журнала.</summary>
public sealed record SenderStatus
{
    public SenderPhase Phase { get; init; } = SenderPhase.Idle;

    /// <summary>Пояснение для пользователя.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Частота кадров на выходе кодировщика.</summary>
    public double Fps { get; init; }

    /// <summary>Фактический битрейт, кбит/с.</summary>
    public int Kbps { get; init; }

    /// <summary>Целевой битрейт, кбит/с.</summary>
    public int TargetKbps { get; init; }

    /// <summary>Круговое время до приёмника, мс.</summary>
    public double RttMs { get; init; }

    /// <summary>Задержка, измеренная приёмником на своей стороне, мс.</summary>
    public double ReceiverLatencyMs { get; init; }

    /// <summary>Кадры, отброшенные приёмником из-за нехватки времени на декодирование.</summary>
    public int ReceiverDropped { get; init; }

    /// <summary>Сколько кадров ждёт отправки — растущее значение означает узкое место в сети.</summary>
    public int QueueDepth { get; init; }

    /// <summary>Передаётся ли системный звук.</summary>
    public bool AudioEnabled { get; init; }

    /// <summary>Битрейт звука, кбит/с.</summary>
    public int AudioKbps { get; init; }

    /// <summary>Пиковый уровень звука, 0..1 — по нему видно, что звук действительно идёт.</summary>
    public float AudioLevel { get; init; }

    /// <summary>
    /// Самая длинная пауза между показанными кадрами за последнюю секунду, миллисекунды.
    /// </summary>
    /// <remarks>
    /// Это единственная метрика, которая соответствует тому, что человек видит как рывок.
    /// Потери кадров и задержка о рывках молчат: кадры могут приходить все, но неравномерно.
    /// </remarks>
    public double ReceiverMaxGapMs { get; init; }

    /// <summary>Сколько раз за последнюю секунду пауза между кадрами была вдвое длиннее нормы.</summary>
    public int ReceiverStutters { get; init; }

    /// <summary>Насколько звук на приставке записан вперёд воспроизведения, миллисекунды.</summary>
    public int ReceiverAudioBacklogMs { get; init; }

    /// <summary>Текст последней ошибки, полученной от приставки. Показывается постоянно.</summary>
    public string? ReceiverError { get; init; }

    public long FramesSent { get; init; }

    public long BytesSent { get; init; }

    /// <summary>Сколько раз пришлось перезапускать кодировщик ради ключевого кадра.</summary>
    public int EncoderRestarts { get; init; }

    /// <summary>Что и почему изменено адаптивной подстройкой; <see langword="null"/>, если всё в норме.</summary>
    public string? AdaptationNote { get; init; }

    public string? Error { get; init; }

    public bool IsActive => Phase == SenderPhase.Streaming;
}

/// <summary>
/// Сессия трансляции: управляющий канал, медиа-канал и конвейер захвата,
/// связанные в один объект с наблюдаемым состоянием.
/// </summary>
/// <remarks>
/// Используется и консольным отправителем, и приложением Avalonia — логика протокола
/// не должна дублироваться в интерфейсе.
/// </remarks>
public sealed class SenderSession : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NegotiationTimeout = TimeSpan.FromSeconds(10);

    private readonly WcpControlClient _control;
    private readonly MediaStreamSender _media;
    private readonly CapturePipeline _pipeline;
    private readonly AudioCapturePipeline? _audio;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _pumpLoop;
    private Task? _audioPumpLoop;
    private Task? _statsLoop;
    private Task? _pingLoop;
    private int _receiverDropped;
    private double _receiverLatencyMs;
    private double _receiverFps;
    private double _rttMs;
    private long _lastAudioBytes;
    private double _audioKbps;
    private double _receiverMaxGapMs;
    private int _receiverStutters;
    private int _receiverAudioBacklogMs;
    private string? _receiverError;

    /// <summary>Когда последний раз пересобирали поток из-за изменения размера окна.</summary>
    private long _lastResizeRestart;

    /// <summary>Виртуальный геймпад, созданный при первом состоянии управления.</summary>
    private GamepadOutput? _gamepad;

    /// <summary>Название контроллера, о котором уже сообщено.</summary>
    private string? _lastGamepadName;

    /// <summary>Не пересобирать поток чаще, чем раз в секунду: перетаскивание окна
    /// порождает десятки изменений размера подряд.</summary>
    private const int ResizeRestartIntervalMs = 1000;    private bool _disposed;
    private bool _stopping;

    // ---- адаптивная подстройка ----

    /// <summary>Действующий битрейт: может быть снижен при нехватке пропускной способности.</summary>
    private int _currentBitrateKbps;

    /// <summary>Действующая частота кадров: снижается, если приставка не успевает декодировать.</summary>
    private int _currentFps;

    private DateTimeOffset _lastAdaptation = DateTimeOffset.MinValue;
    private string? _adaptationNote;
    private DateTimeOffset _adaptationNoteAt = DateTimeOffset.MinValue;

    /// <summary>Сколько пояснение о подстройке остаётся на экране.</summary>
    private static readonly TimeSpan NoteLifetime = TimeSpan.FromSeconds(20);

    /// <summary>К чему возвращаться, когда связь наладится. За сессию не меняется.</summary>
    private int _targetBitrateKbps;
    private int _targetFps;

    /// <summary>Сколько замеров подряд всё хорошо — для возврата параметров вверх.</summary>
    private int _goodSamples;

    /// <summary>Сколько хороших замеров нужно сейчас, чтобы поднять ступень.</summary>
    private int _recoverySamples = RecoverySamples;

    /// <summary>Когда последний раз поднимали параметры — чтобы заметить «дёрганье».</summary>
    private DateTimeOffset _lastStepUp = DateTimeOffset.MinValue;

    /// <summary>Когда последний раз что-либо снижали.</summary>
    private DateTimeOffset _lastStepDown = DateTimeOffset.MinValue;

    /// <summary>Сколько замеров подряд должны быть плохими, прежде чем что-то менять.</summary>
    private const int PressureThreshold = 3;

    /// <summary>
    /// Сколько хороших замеров нужно, чтобы вернуть одну ступень.
    /// Заметно больше порога ухудшения: снижать надо быстро, а поднимать — осторожно.
    /// </summary>
    private const int RecoverySamples = 12;

    /// <summary>Предел терпения при «дёрганье»: до минуты хорошей работы перед подъёмом.</summary>
    private const int MaxRecoverySamples = 60;

    /// <summary>
    /// Множитель повышения битрейта. Ниже множителя понижения (0,7) — так качество
    /// растёт медленнее, чем падает, и система не начинает колебаться.
    /// </summary>
    private const double RecoveryBitrateFactor = 1.25;

    /// <summary>Если подъём сменился спадом быстрее этого времени, значит мы дёргаемся.</summary>
    private static readonly TimeSpan ThrashWindow = TimeSpan.FromSeconds(30);

    /// <summary>Столько спокойной работы сбрасывает терпение к исходному.</summary>
    private static readonly TimeSpan ThrashForget = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Задержка на приставке, после которой она считается не успевающей.
    /// Триста миллисекунд — это уже заметное отставание картинки от действия,
    /// и терпеть его ради высокой частоты кадров смысла нет.
    /// </summary>
    private const double LatencyPressureMs = 150;

    /// <summary>Сколько кадров было потеряно на прошлом замере — чтобы заметить рост.</summary>
    private int _lastReceiverDropped;

    /// <summary>
    /// Сколько кадров должно потеряться за секунду, чтобы считать это перегрузкой.
    /// Два кадра — это около трёх процентов при 60 к/с: заметно, но не случайный выброс.
    /// </summary>
    private const int DropPressureThreshold = 2;

    /// <summary>Последние замеры по каждому виду давления: <see langword="true"/> — плохой.</summary>
    private readonly Queue<bool> _networkSamples = new();
    private readonly Queue<bool> _decoderSamples = new();

    /// <summary>Длина окна наблюдения за давлением, в замерах (примерно секунды).</summary>
    private const int PressureWindow = 10;

    /// <summary>Сколько плохих замеров в окне достаточно, чтобы снизить параметры.</summary>
    private const int PressureHits = 3;

    /// <summary>
    /// Набралось ли достаточно плохих замеров в скользящем окне.
    /// </summary>
    /// <remarks>
    /// Окно, а не серия подряд: перегрузка на практике чередуется с нормальными
    /// замерами, и ждать непрерывной череды плохих — значит не замечать её вовсе.
    /// Три плохих из десяти при этом не срабатывают на случайный единичный всплеск.
    /// </remarks>
    private static bool CheckPressure(Queue<bool> samples, bool bad)
    {
        samples.Enqueue(bad);
        while (samples.Count > PressureWindow)
        {
            samples.Dequeue();
        }

        var badCount = 0;
        foreach (var sample in samples)
        {
            if (sample)
            {
                badCount++;
            }
        }

        return badCount >= PressureHits;
    }

    /// <summary>Минимальный интервал между изменениями параметров.</summary>
    private static readonly TimeSpan AdaptationCooldown = TimeSpan.FromSeconds(5);

    /// <summary>Ниже этого битрейта не опускаемся: изображение станет нечитаемым.</summary>
    private const int MinimumBitrateKbps = 1500;

    private SenderSession(
        WcpControlClient control,
        MediaStreamSender media,
        CapturePipeline pipeline,
        AudioCapturePipeline? audio,
        SenderStatus status)
    {
        _control = control;
        _media = media;
        _pipeline = pipeline;
        _audio = audio;
        Status = status;
    }

    /// <summary>Текущее состояние сессии.</summary>
    public SenderStatus Status { get; private set; }

    /// <summary>Состояние изменилось.</summary>
    public event EventHandler<SenderStatus>? StatusChanged;

    /// <summary>Строка журнала: диагностика ffmpeg, этапы протокола.</summary>
    public event EventHandler<string>? Log;

    /// <summary>Сессия завершилась (штатно или с ошибкой).</summary>
    public Task Completion => _completion.Task;

    /// <summary>Командная строка запущенного кодировщика.</summary>
    public string EncoderCommandLine => _pipeline.CommandLine;

    /// <summary>
    /// Подключиться к приёмнику и начать трансляцию.
    /// </summary>
    /// <exception cref="TimeoutException">Приёмник не ответил на одном из этапов.</exception>
    /// <exception cref="InvalidOperationException">Приёмник отказал в трансляции.</exception>
    public static async Task<SenderSession> StartAsync(
        IPAddress address,
        int controlPort,
        CaptureOptions options,
        CaptureCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var targetKbps = options.EffectiveBitrateKbps();
        var status = new SenderStatus
        {
            Phase = SenderPhase.Connecting,
            Message = $"Подключение к {address}:{controlPort}",
            TargetKbps = targetKbps,
        };

        var control = await WcpControlClient
            .ConnectAsync(address, controlPort, cancellationToken)
            .ConfigureAwait(false);

        MediaStreamSender? media = null;
        CapturePipeline? pipeline = null;
        AudioCapturePipeline? audio = null;

        try
        {
            // 1. Приветствие.
            var hello = await control
                .RequestAsync(
                    WcpMessage.Hello(LocalDevice.Id, LocalDevice.Name, new AppInfo { Name = "WooCast", Version = "1.0.0" }),
                    HandshakeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            if (hello.T != Wcp.MessageTypes.HelloAck || hello.Token is null)
            {
                throw new InvalidOperationException($"Неожиданный ответ на hello: {hello.T}");
            }

            // 2. Согласование параметров потока.
            var request = WcpMessage.StreamRequest(
                requestId: "stream",
                video: new StreamVideoSpec
                {
                    Codec = Wcp.Codecs.H264,
                    Width = FfmpegArguments.MakeEven(options.Width),
                    Height = FfmpegArguments.MakeEven(options.Height),
                    Fps = options.Fps,
                    Bitrate = targetKbps,
                    Profile = options.Profile,

                    // Запас сглаживания на приёмнике выбирает пользователь: это его
                    // компромисс между плавностью и задержкой.
                    BufferMs = options.BufferMs,
                },
                source: new StreamSourceSpec
                {
                    Type = options.SourceKind == CaptureSourceKind.Window
                        ? Wcp.SourceTypes.Window
                        : Wcp.SourceTypes.Display,
                    Id = options.DisplayIndex.ToString(),
                    Title = options.DescribeSource(),
                },
                audio: new StreamAudioSpec
                {
                    Enabled = options.Audio.Enabled,
                    Codec = "aac",
                    Bitrate = options.Audio.BitrateKbps,
                    Delay = options.Audio.DelayMs,
                });

            var ready = await control
                .RequestAsync(request, NegotiationTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (ready.T == Wcp.MessageTypes.StreamReject)
            {
                throw new InvalidOperationException(
                    $"Приёмник отказал: {ready.Message ?? ready.Reason ?? "без пояснения"}");
            }

            if (ready.T != Wcp.MessageTypes.StreamReady)
            {
                throw new InvalidOperationException($"Неожиданный ответ на stream_request: {ready.T}");
            }

            var token = ready.Token ?? hello.Token.Value;
            var mediaPort = ready.MediaPort ?? Wcp.MediaPort;

            // 3. Звук: доступность определяется до медиа-хендшейка, потому что признак
            // «есть звук» уходит в его заголовок.
            if (options.Audio.Enabled)
            {
                audio = AudioCapturePipeline.TryStart(options.Audio, capabilities.FfmpegPath);

                if (audio is null)
                {
                    // Не беда: трансляция продолжится без звука.
                }
            }

            var hasAudio = audio is not null;

            // 4. Медиа-канал.
            media = await MediaStreamSender
                .ConnectAsync(address, mediaPort, token, hasAudio, cancellationToken)
                .ConfigureAwait(false);

            // 5. Конвейер захвата изображения.
            pipeline = CapturePipeline.Start(options, capabilities);

            var session = new SenderSession(
                control,
                media,
                pipeline,
                audio,
                status with { Phase = SenderPhase.Negotiating, Message = "Запуск кодировщика" });

            session._currentBitrateKbps = targetKbps;
            session._currentFps = options.Fps;

            // Целевые значения не меняются за сессию: к ним возвращаемся, когда связь наладится.
            session._targetBitrateKbps = targetKbps;
            session._targetFps = options.Fps;

            if (audio is not null)
            {
                audio.LogLine += (_, line) => session.RaiseLog($"[ffmpeg/audio] {line}");
                audio.Faulted += (_, message) => session.RaiseLog($"Захват звука прерван: {message}");

                // Запускаем после подписки на события, чтобы не потерять диагностику.
                audio.Start();
            }

            pipeline.LogLine += (_, line) => session.RaiseLog($"[ffmpeg] {line}");
            pipeline.Faulted += (_, code) => session.Fail($"Кодировщик неожиданно завершился (код {code})");

            pipeline.WindowFollowed += (_, window) => session.RaiseLog(
                $"Окно переместилось: {window.Width}×{window.Height}, " +
                $"область {window.CropX}+{window.CropY} на экране {window.DisplayIndex + 1}");

            pipeline.WindowLost += (_, description) => session.Fail($"Окно закрыто или свёрнуто: {description}");

            // Окно сменило размер: кодировщику размер сообщён при запуске, поэтому поток
            // пересобирается заново — иначе кадры прекращаются совсем. Проверено на живом
            // окне: счётчик принятых кадров замирал и больше не рос.
            pipeline.Resized += (_, reason) => session.RestartForResize(reason);

            control.Notification += (_, message) => session.OnNotification(message);
            control.Closed += (_, error) => session.Fail(
                error is null ? "Приёмник закрыл соединение" : $"Управляющее соединение потеряно: {error.Message}");

            await control
                .SendAsync(WcpMessage.StreamStart(), cancellationToken)
                .ConfigureAwait(false);

            session.StartLoops(cancellationToken);
            session.PublishStatus(status with
            {
                Phase = SenderPhase.Streaming,
                Message = $"Трансляция на {address}",
            });

            session.RaiseLog(
                $"Приёмник: {hello.Receiver?.Name ?? "—"}, модель {hello.Receiver?.Model ?? "—"}");

            if (audio is not null)
            {
                session.RaiseLog(
                    $"Звук: AAC {options.Audio.BitrateKbps} кбит/с, " +
                    $"{options.Audio.SampleRate} Гц");
            }
            else if (options.Audio.Enabled)
            {
                session.RaiseLog("Системный звук недоступен — трансляция без звука");
            }
            else
            {
                session.RaiseLog("Звук выключен в настройках");
            }

            return session;
        }
        catch
        {
            if (audio is not null)
            {
                await audio.DisposeAsync().ConfigureAwait(false);
            }

            if (pipeline is not null)
            {
                await pipeline.DisposeAsync().ConfigureAwait(false);
            }

            if (media is not null)
            {
                await media.DisposeAsync().ConfigureAwait(false);
            }

            await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void StartLoops(CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        _pumpLoop = Task.Run(() => PumpFramesAsync(linked.Token));
        _statsLoop = Task.Run(() => StatsLoopAsync(linked.Token));
        _pingLoop = Task.Run(() => PingLoopAsync(linked.Token));

        if (_audio is not null)
        {
            _audioPumpLoop = Task.Run(() => PumpAudioAsync(linked.Token));
        }
    }

    /// <summary>
    /// Отправка звука. Идёт параллельно видео и в то же соединение: пакеты различаются
    /// битом <see cref="MediaFrameFlags.Audio"/>, а метки времени лежат на общей шкале.
    /// </summary>
    private async Task PumpAudioAsync(CancellationToken cancellationToken)
    {
        var audio = _audio;
        if (audio is null)
        {
            return;
        }

        try
        {
            await foreach (var frame in audio.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                await _media.SendAudioAsync(frame.AdtsData, frame.PtsMicros, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!_stopping)
            {
                // Звук пропал (например, сменилось устройство вывода), но картинку
                // прерывать из-за этого не стоит.
                RaiseLog("Поток звука прервался — продолжаю без звука");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            RaiseLog($"Звук остановлен: {ex.Message}");
        }
    }

    /// <summary>
    /// Перекачка кадров из конвейера в сокет.
    /// </summary>
    /// <remarks>
    /// Ограниченная очередь внутри конвейера обеспечивает обратное давление: если сеть
    /// не успевает, чтение stdout останавливается, ffmpeg пропускает кадры захвата,
    /// и задержка не растёт. На локальной сети при выбранном битрейте этого не происходит.
    /// </remarks>
    private async Task PumpFramesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var unit in _pipeline.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                await _media.SendAsync(unit, cancellationToken).ConfigureAwait(false);
            }

            if (!_stopping)
            {
                Fail("Поток кадров прервался");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Fail($"Ошибка отправки: {ex.Message}");
        }
    }

    private async Task StatsLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Wcp.StatsInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var stats = _pipeline.Stats;
                var rate = stats.Sample();
                var queueDepth = _pipeline.QueueDepth;

                // Битрейт звука за прошедшую секунду: цикл телеметрии идёт ровно раз в секунду.
                var audioBytes = _media.Counters.AudioBytes;
                var audioDelta = audioBytes - _lastAudioBytes;
                _lastAudioBytes = audioBytes;
                _audioKbps = audioDelta * 8 / 1000.0;

                ExpireAdaptationNote();

                PublishStatus(Status with
                {
                    Phase = SenderPhase.Streaming,
                    Fps = rate.Fps,
                    Kbps = rate.Kbps,
                    RttMs = _rttMs,
                    ReceiverLatencyMs = _receiverLatencyMs,
                    ReceiverDropped = Volatile.Read(ref _receiverDropped),
                    QueueDepth = queueDepth,
                    FramesSent = _media.Counters.Frames,
                    BytesSent = _media.Counters.Bytes,
                    EncoderRestarts = rate.Restarts,
                    AdaptationNote = _adaptationNote,
                    AudioEnabled = _audio is not null,
                    AudioKbps = (int)_audioKbps,
                    AudioLevel = _audio?.PeakLevel ?? 0,
                    ReceiverMaxGapMs = _receiverMaxGapMs,
                    ReceiverStutters = _receiverStutters,
                    ReceiverAudioBacklogMs = _receiverAudioBacklogMs,
                    ReceiverError = _receiverError,
                });

                // Телеметрия для оверлея на телевизоре.
                var frameDurationMs = rate.Fps > 0 ? 1000.0 / rate.Fps : 0;
                await _control.SendAsync(
                    WcpMessage.Stats(
                        fps: Math.Round(rate.Fps, 1),
                        kbps: rate.Kbps,
                        dropped: 0,
                        queueMs: Math.Round(queueDepth * frameDurationMs, 1),
                        rttMs: Math.Round(_rttMs, 1)),
                    cancellationToken).ConfigureAwait(false);

                await EvaluateAdaptationAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Подстройка параметров потока под то, что реально происходит на другом конце.
    /// </summary>
    /// <remarks>
    /// Различаются два узких места, и лечатся они по-разному:
    /// <list type="bullet">
    /// <item>не успевает сеть — растёт очередь отправки; снижаем битрейт;</item>
    /// <item>не успевает декодер приставки — её частота кадров ниже нашей, а очередь пуста;
    /// снижаем частоту кадров, потому что битрейт тут ни в чём не виноват.</item>
    /// </list>
    /// Решение о снижении принимается после нескольких плохих замеров подряд и не чаще,
    /// чем раз в несколько секунд: иначе система дёргалась бы на каждом всплеске.
    /// <para>
    /// Обратный ход — тоже часть работы. Без него один короткий сбой оставлял бы поток
    /// в пониженном качестве до конца сессии, хотя сеть давно свободна. Подъём идёт
    /// ступенями и осторожнее спуска: требуется заметно больше хороших замеров, а шаг
    /// вверх меньше шага вниз. Если подъём сразу сменяется спадом, система увеличивает
    /// выдержку — так она перестаёт колебаться вокруг границы возможностей сети.
    /// </para>
    /// </remarks>
    private async Task EvaluateAdaptationAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastAdaptation < AdaptationCooldown)
        {
            return;
        }

        // Долгая спокойная работа снимает накопленную осторожность.
        if (_recoverySamples > RecoverySamples && now - _lastStepDown > ThrashForget)
        {
            _recoverySamples = RecoverySamples;
        }

        var queueDepth = _pipeline.QueueDepth;
        var receiverFps = Volatile.Read(ref _receiverFps);
        var receiverLatency = Volatile.Read(ref _receiverLatencyMs);
        var receiverDropped = Volatile.Read(ref _receiverDropped);

        // Давление считается по окну последних замеров, а не по серии подряд.
        // Проблема на перегруженном канале идёт вперемешку с хорошими замерами:
        // задержка растёт пилой — 30, 26, 198, 14, 247 мс, — а потери капают по одной.
        // Требование «плохо три раза подряд» такую картину пропускало.
        var networkPressure = CheckPressure(_networkSamples, queueDepth >= 2);

        // Декодер приставки: она декодирует заметно медленнее, чем мы кодируем,
        // при этом сеть свободна — значит, дело в ней, а не в канале.
        //
        // Сравнивать нужно с той частотой, которую мы действительно отправляем, а не
        // с запрошенной. Системный захват окна отдаёт кадр только при изменении
        // содержимого: неподвижное окно даёт единицы кадров в секунду, и сравнение
        // с запрошенными шестьюдесятью принимало это за нехватку мощности приставки —
        // подстройка снижала качество там, где всё было в порядке.
        var sendingFps = _pipeline.Stats.Fps;
        var effectiveFps = sendingFps > 1 ? Math.Min(_currentFps, sendingFps) : _currentFps;

        var decoderBehind = receiverFps > 0
                            && receiverFps < effectiveFps * 0.85
                            && queueDepth == 0;

        // Приставка может успевать декодировать, но не успевать показывать: кадры
        // копятся в её буфере, отчего растёт задержка и начинаются потери. По одной
        // частоте кадров этого не видно — она остаётся высокой. Раньше такие случаи
        // проходили мимо подстройки: сеть свободна, декодер «успевает», а картинка
        // отстаёт на сотни миллисекунд.
        //
        // Считаем давлением не любую потерю, а заметную: единичные выбросы случаются
        // и на исправной приставке, а реакция на них уводила битрейт к минимуму
        // без всякой пользы для картинки.
        var dropsGrowing = false;
        if (receiverDropped > _lastReceiverDropped)
        {
            dropsGrowing = receiverDropped - _lastReceiverDropped >= DropPressureThreshold;
        }

        _lastReceiverDropped = receiverDropped;

        var receiverStruggling = queueDepth == 0
                                 && (receiverLatency > LatencyPressureMs || dropsGrowing);

        var decoderPressure = CheckPressure(_decoderSamples, decoderBehind || receiverStruggling);

        if (networkPressure)
        {
            var next = Math.Max(MinimumBitrateKbps, (int)(_currentBitrateKbps * 0.7));
            if (next < _currentBitrateKbps)
            {
                _currentBitrateKbps = next;
                _lastAdaptation = now;
                NoteStepDown(now);

                await _pipeline
                    .ApplyOptionsAsync(_pipeline.CurrentOptions.WithBitrate(next), cancellationToken)
                    .ConfigureAwait(false);

                SetAdaptationNote(
                    $"Сеть не успевает — битрейт снижен до {next / 1000.0:F1} Мбит/с" +
                    DescribeRecovery());
            }
            else
            {
                // Дошли до нижней границы: дальше снижать некуда, подсказываем выход.
                SetAdaptationNote(
                    "Сеть не справляется даже на минимальном битрейте — " +
                    "попробуйте проводное подключение или меньшее разрешение");
            }

            return;
        }

        if (decoderPressure)
        {
            var reason = receiverLatency > LatencyPressureMs
                ? $"задержка на приставке {receiverLatency:F0} мс"
                : receiverFps > 0 && receiverFps < effectiveFps * 0.85
                    ? $"приставка выдаёт {receiverFps:F0} к/с при отправке {effectiveFps:F0}"
                    : "приставка теряет кадры";

            // Порядок шагов подобран по измерениям на реальной приставке (Xiaomi MiTV):
            // сначала частота 60 → 30 — это облегчает и сеть, и декодер сразу; затем
            // битрейт, потому что снижение до 15 к/с заметно портит плавность, а декодер
            // часто ограничен не числом кадров, а объёмом данных на кадр; и только
            // в последнюю очередь 30 → 15.
            if (_currentFps > 30)
            {
                _currentFps = 30;
                _lastAdaptation = now;
                NoteStepDown(now);

                await _pipeline
                    .ApplyOptionsAsync(_pipeline.CurrentOptions.WithFps(30), cancellationToken)
                    .ConfigureAwait(false);

                SetAdaptationNote(
                    $"Приставка не успевает ({reason}) — " +
                    $"частота снижена до 30 к/с{DescribeRecovery()}");
            }
            else if (_currentBitrateKbps > MinimumBitrateKbps)
            {
                // Шаг крупнее сетевого: предел декодера обычно лежит заметно ниже,
                // и подбираться к нему по 0,7 пришлось бы слишком долго.
                var next = Math.Max(MinimumBitrateKbps, (int)(_currentBitrateKbps * 0.6));
                _currentBitrateKbps = next;
                _lastAdaptation = now;
                NoteStepDown(now);

                await _pipeline
                    .ApplyOptionsAsync(_pipeline.CurrentOptions.WithBitrate(next), cancellationToken)
                    .ConfigureAwait(false);

                SetAdaptationNote(
                    $"Приставке тяжело ({reason}) — битрейт снижен до {next / 1000.0:F1} Мбит/с" +
                    DescribeRecovery());
            }
            else if (_currentFps > 15)
            {
                _currentFps = 15;
                _lastAdaptation = now;
                NoteStepDown(now);

                await _pipeline
                    .ApplyOptionsAsync(_pipeline.CurrentOptions.WithFps(15), cancellationToken)
                    .ConfigureAwait(false);

                SetAdaptationNote(
                    $"Приставка не справляется даже на малом битрейте ({reason}) — " +
                    $"частота снижена до 15 к/с{DescribeRecovery()}");
            }
            else
            {
                SetAdaptationNote(
                    "Приставка не справляется даже на минимальных параметрах — " +
                    "выберите меньшее разрешение");
            }

            return;
        }

        // Ничего не давит. Считаем хорошие замеры и возвращаем параметры к целевым.
        // Задержка тоже должна быть в норме: иначе система поднимала бы битрейт обратно
        // в ту зону, где канал приставки начинает копить буфер.
        var belowTarget = _currentFps < _targetFps || _currentBitrateKbps < _targetBitrateKbps;
        var healthy = belowTarget
                      && queueDepth == 0
                      && receiverLatency <= LatencyPressureMs

                      // Тоже сравнение с фактической частотой отправки, а не с запрошенной:
                      // у неподвижного окна она ниже, и по запрошенной приставка никогда
                      // не выглядела бы успевающей — качество не вернулось бы.
                      && (receiverFps <= 0 || receiverFps >= effectiveFps * 0.95);

        if (!healthy)
        {
            _goodSamples = 0;
            return;
        }

        _goodSamples++;
        if (_goodSamples < _recoverySamples)
        {
            return;
        }

        _goodSamples = 0;

        // Сначала возвращаем плавность движения, потом — детализацию.
        if (_currentFps < _targetFps)
        {
            var nextFps = NextFpsUp(_currentFps, _targetFps);
            _currentFps = nextFps;
            _lastAdaptation = now;
            _lastStepUp = now;

            await _pipeline
                .ApplyOptionsAsync(_pipeline.CurrentOptions.WithFps(nextFps), cancellationToken)
                .ConfigureAwait(false);

            SetAdaptationNote($"Связь восстановилась — частота повышена до {nextFps} к/с");
            return;
        }

        if (_currentBitrateKbps < _targetBitrateKbps)
        {
            var next = Math.Min(_targetBitrateKbps, (int)(_currentBitrateKbps * RecoveryBitrateFactor));

            if (next > _currentBitrateKbps)
            {
                _currentBitrateKbps = next;
                _lastAdaptation = now;
                _lastStepUp = now;

                await _pipeline
                    .ApplyOptionsAsync(_pipeline.CurrentOptions.WithBitrate(next), cancellationToken)
                    .ConfigureAwait(false);

                SetAdaptationNote(next >= _targetBitrateKbps
                    ? $"Связь восстановилась — битрейт вернулся к {next / 1000.0:F1} Мбит/с"
                    : $"Связь восстановилась — битрейт повышен до {next / 1000.0:F1} Мбит/с");
            }
        }
    }

    /// <summary>
    /// Отметить снижение параметров. Если подъём почти сразу сменился спадом, значит
    /// система нашла границу возможностей канала — дальше поднимаемся осторожнее.
    /// </summary>
    private void NoteStepDown(DateTimeOffset now)
    {
        var wasRecentStepUp = now - _lastStepUp < ThrashWindow;

        _lastStepDown = now;

        if (!wasRecentStepUp || _recoverySamples >= MaxRecoverySamples)
        {
            return;
        }

        _recoverySamples = Math.Min(MaxRecoverySamples, _recoverySamples * 2);
        RaiseLog(
            $"Связь нестабильна: возврат к высокому качеству будет медленнее " +
            $"({_recoverySamples} с спокойной работы на ступень)");
    }

    /// <summary>Подсказка о том, сколько ждать возврата качества. Пусто, если ждать нечего.</summary>
    private string DescribeRecovery() =>
        _currentFps < _targetFps || _currentBitrateKbps < _targetBitrateKbps
            ? $" (верну, если связь продержится {_recoverySamples} с)"
            : string.Empty;

    /// <summary>Следующая ступень вверх: 15 → 30 → цель.</summary>
    private static int NextFpsUp(int current, int target)
    {
        if (current >= target)
        {
            return target;
        }

        return current < 30 && target >= 30 ? 30 : target;
    }

    private void SetAdaptationNote(string note)
    {
        if (_adaptationNote == note)
        {
            return;
        }

        _adaptationNote = note;
        _adaptationNoteAt = DateTimeOffset.UtcNow;
        RaiseLog(note);
    }

    /// <summary>
    /// Погасить пояснение, если с последнего изменения прошло достаточно времени.
    /// Пояснение описывает уже случившееся событие: держать его в интерфейсе вечно —
    /// значит показывать предупреждение там, где всё давно в порядке.
    /// </summary>
    private void ExpireAdaptationNote()
    {
        if (_adaptationNote is null || DateTimeOffset.UtcNow - _adaptationNoteAt < NoteLifetime)
        {
            return;
        }

        _adaptationNote = null;
    }

    private async Task PingLoopAsync(CancellationToken cancellationToken)    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var sentAt = MonotonicClock.TimestampMillis;
                await _control
                    .RequestAsync(WcpMessage.Ping(sentAt), TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);

                _rttMs = MonotonicClock.TimestampMillis - sentAt;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Пропущенный ping не повод рвать сессию: следующий тик попробует снова.
        }
    }

    private void OnNotification(WcpMessage message)
    {
        switch (message.T)
        {
            case Wcp.MessageTypes.Ping:
                // Приёмник тоже измеряет расхождение часов, чтобы показывать сквозную задержку.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _control
                            .SendAsync(WcpMessage.Pong(message.Id, message.Ts), _cts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                    {
                    }
                });
                break;

            case Wcp.MessageTypes.RequestKeyframe:
                RaiseLog("Приёмник запросил ключевой кадр");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _pipeline.ForceKeyframeAsync(_cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
                    {
                    }                });
                break;

            case Wcp.MessageTypes.Input:
                HandleInput(message.Input);
                break;

            case Wcp.MessageTypes.Stats:
                _receiverDropped = message.Dropped ?? _receiverDropped;
                _receiverLatencyMs = message.LatencyMs ?? _receiverLatencyMs;
                _receiverFps = message.Fps ?? _receiverFps;
                _receiverMaxGapMs = message.MaxGapMs ?? 0;
                _receiverStutters = message.Stutters ?? 0;
                _receiverAudioBacklogMs = message.AudioBacklogMs ?? 0;
                break;

            case Wcp.MessageTypes.Error:
                // Текст ошибки приставки хранится отдельно от подсказок подстройки:
                // подстройка обновляет свою строку каждую секунду и затирала его.
                _receiverError = message.Message ?? message.Code;
                RaiseLog($"Ошибка приёмника: {_receiverError}");
                break;

            case Wcp.MessageTypes.Stop:
            case Wcp.MessageTypes.Bye:
                RaiseLog("Приёмник завершил сессию");
                Fail("Трансляция остановлена на телевизоре");
                break;
        }
    }

    /// <summary>
    /// Подать состояние управления с приёмника в виртуальный геймпад.
    /// </summary>
    /// <remarks>
    /// Геймпад создаётся при первом состоянии, а не при старте трансляции: если
    /// управление не нужно, виртуальное устройство в системе не появляется вовсе.
    /// </remarks>
    private void HandleInput(InputSpec? state)
    {
        if (state is null)
        {
            return;
        }

        var options = _pipeline.CurrentOptions;

        if (!options.GamepadEnabled)
        {
            return;
        }

        if (_gamepad is null)
        {
            var pad = GamepadOutput.TryStart(options.Gamepad, out var error);

            if (pad is null)
            {
                // Сообщаем один раз: дальше повторять одну и ту же ошибку незачем.
                RaiseLog($"Управление недоступно: {error}");
                SetAdaptationNote($"Управление недоступно: {error}");
                _ = _pipeline.ApplyOptionsAsync(options.WithGamepadEnabled(false), _cts.Token);
                return;
            }

            _gamepad = pad;
            RaiseLog($"Управление: {pad.Describe} готов");

            if (!string.IsNullOrWhiteSpace(state.Device))
            {
                RaiseLog($"Контроллер на приёмнике: {state.Device}");
            }
        }

        _gamepad.Submit(state);

        var name = state.Device;
        if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, _lastGamepadName, StringComparison.Ordinal))
        {
            _lastGamepadName = name;
            SetAdaptationNote($"Управление с контроллера: {name}");
        }
    }

    /// <summary>
    /// Отпустить кнопки и убрать виртуальный геймпад из системы.
    /// </summary>
    /// <remarks>
    /// Вызывается при остановке трансляции и при разрыве связи: игра не должна
    /// остаться с зажатым стиком.
    /// </remarks>
    private void ReleaseGamepad()
    {
        var pad = _gamepad;
        _gamepad = null;

        if (pad is null)
        {
            return;
        }

        try
        {
            pad.Release();
            pad.Dispose();
            RaiseLog("Управление: виртуальный геймпад убран");
        }
        catch (Exception ex)
        {
            RaiseLog($"Не удалось убрать виртуальный геймпад: {ex.Message}");
        }

        _lastGamepadName = null;
    }

    /// <summary>Корректно остановить трансляцию.</summary>
    public async Task StopAsync()
    {
        if (_stopping || _disposed)
        {
            return;
        }

        _stopping = true;
        PublishStatus(Status with { Phase = SenderPhase.Stopping, Message = "Остановка" });

        // Виртуальный геймпад снимается до остановки потока: иначе в системе осталось бы
        // устройство с нажатыми кнопками и отклонённым стиком, и игра продолжала бы
        // считать, что игрок держит управление.
        ReleaseGamepad();

        try
        {
            // RequestAsync сам отправляет сообщение и ждёт `stopped` с тем же `re`.
            await _control
                .RequestAsync(WcpMessage.Stop(), TimeSpan.FromSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Приёмник мог уже закрыть соединение — это не мешает остановке.
        }

        _cts.Cancel();
        PublishStatus(Status with { Phase = SenderPhase.Idle, Message = "Остановлено" });
        _completion.TrySetResult();
    }

    private void Fail(string message)
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _cts.Cancel();

        // Сбой трансляции не должен оставлять в системе геймпад с зажатыми кнопками.
        ReleaseGamepad();

        RaiseLog(message);
        PublishStatus(Status with { Phase = SenderPhase.Failed, Message = message, Error = message });
        _completion.TrySetResult();
    }

    private void RaiseLog(string line) => Log?.Invoke(this, line);

    /// <summary>
    /// Пересобрать поток после изменения размера захватываемого окна.
    /// </summary>
    /// <remarks>
    /// Кодировщику размер кадра сообщают при запуске, поэтому после изменения размера
    /// он перестаёт получать данные, и кадры прекращаются вовсе — проверено на живом окне.
    /// Пересборка источника возвращает поток; медиа-соединение при этом сохраняется.
    /// <para>
    /// Повторные изменения во время перетаскивания окна пропускаются: каждый перезапуск
    /// стоит ключевого кадра и паузы, и делать их десятками в секунду нельзя.
    /// </para>
    /// </remarks>
    private void RestartForResize(string reason)
    {
        var now = Environment.TickCount64;
        if (now - _lastResizeRestart < ResizeRestartIntervalMs)
        {
            return;
        }

        _lastResizeRestart = now;
        RaiseLog($"Источник изменился ({reason}) — пересобираю поток");

        _ = Task.Run(async () =>
        {
            try
            {
                await _pipeline.ForceKeyframeAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
            {
            }
        });
    }

    private void PublishStatus(SenderStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        try
        {
            await _control.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        await _media.DisposeAsync().ConfigureAwait(false);
        await _pipeline.DisposeAsync().ConfigureAwait(false);

        if (_audio is not null)
        {
            await _audio.DisposeAsync().ConfigureAwait(false);
        }

        _completion.TrySetResult();
        _cts.Dispose();
    }
}
