namespace WooCast.Capture;

/// <summary>Что именно транслируем.</summary>
public enum CaptureSourceKind
{
    /// <summary>Весь экран (монитор целиком).</summary>
    Display,

    /// <summary>Отдельное окно приложения.</summary>
    Window,
}

/// <summary>Какой кодировщик использовать.</summary>
public enum EncoderKind
{
    /// <summary>Выбрать лучший доступный автоматически.</summary>
    Auto,

    Nvenc,
    QuickSync,
    Amf,
    Software,
}

/// <summary>Параметры передачи системного звука.</summary>
public sealed class AudioOptions
{
    /// <summary>Передавать ли звук вместе с изображением.</summary>
    public bool Enabled { get; set; }

    /// <summary>Битрейт AAC в килобитах в секунду. 160 кбит/с достаточно для качества, близкого к исходному.</summary>
    public int BitrateKbps { get; set; } = 160;

    /// <summary>Частота дискретизации на выходе кодировщика.</summary>
    public int SampleRate { get; set; } = 48_000;

    /// <summary>
    /// Ручная поправка синхронизации, миллисекунды. Положительная — звук позже картинки.
    /// </summary>
    /// <remarks>
    /// Передаётся приёмнику в <c>stream_request.audio.delay</c> и прибавляется к его
    /// базовой задержке вывода звука. Выравнивание делается на стороне звука: у него
    /// большой буфер, тогда как придержка видео лишает декодер выходных буферов
    /// и приводит к потере кадров.
    /// </remarks>
    public int DelayMs { get; set; }

    public AudioOptions Clone() => new()
    {
        Enabled = Enabled,
        BitrateKbps = BitrateKbps,
        SampleRate = SampleRate,
        DelayMs = DelayMs,
    };
}

/// <summary>Окно, выбранное для трансляции.</summary>
public sealed record WindowTarget
{
    /// <summary>Дескриптор окна — по нему отслеживается перемещение.</summary>
    public nint Handle { get; init; }

    public required string Title { get; init; }

    public required string ProcessName { get; init; }

    /// <summary>Границы окна в координатах рабочего стола.</summary>
    public int X { get; init; }

    public int Y { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Монитор, на котором находится окно.</summary>
    public int DisplayIndex { get; init; }

    /// <summary>Начало координат этого монитора — нужно для пересчёта в область обрезки.</summary>
    public int DisplayOriginX { get; init; }

    public int DisplayOriginY { get; init; }

    /// <summary>Область обрезки внутри кадра монитора (для <c>ddagrab</c> + <c>crop</c>).</summary>
    public int CropX => Math.Max(0, X - DisplayOriginX);

    public int CropY => Math.Max(0, Y - DisplayOriginY);

    /// <summary>Описание для интерфейса и OSD на телевизоре.</summary>
    public string Describe() => $"{ProcessName} — {Title}";

    /// <summary>Собрать цель по текущим границам окна (используется при автослежении).</summary>
    public WindowTarget WithBounds(int x, int y, int width, int height) => this with
    {
        X = x,
        Y = y,
        Width = width,
        Height = height,
    };
}

/// <summary>Профиль качества трансляции.</summary>
public enum QualityPreset
{
    /// <summary>Максимум качества: 0.32 бита на пиксель за кадр. Для гигабитной проводной сети.</summary>
    Maximum,

    /// <summary>Высокое качество: 0.18 бита на пиксель. Запас на Wi-Fi 5 ГГц.</summary>
    High,

    /// <summary>Сбалансированный: 0.10 бита на пиксель.</summary>
    Balanced,

    /// <summary>Экономичный: 0.05 бита на пиксель. Для слабого Wi-Fi.</summary>
    Economy,

    /// <summary>Битрейт задан вручную в <see cref="CaptureOptions.BitrateKbps"/>.</summary>
    Custom,
}

/// <summary>Параметры захвата и кодирования.</summary>
public sealed class CaptureOptions
{
    public CaptureSourceKind SourceKind { get; set; } = CaptureSourceKind.Display;

    /// <summary>Индекс монитора (соответствует <c>ddagrab=output_idx</c>).</summary>
    public int DisplayIndex { get; set; }

    /// <summary>Заголовок окна — используется, если не задан <see cref="Window"/>.</summary>
    public string? WindowTitle { get; set; }

    /// <summary>Выбранное окно. Заполняется для <see cref="CaptureSourceKind.Window"/>.</summary>
    public WindowTarget? Window { get; set; }

    /// <summary>Передача системного звука.</summary>
    public AudioOptions Audio { get; set; } = new();

    /// <summary>Ширина выходного кадра.</summary>
    public int Width { get; set; } = 1920;

    /// <summary>Высота выходного кадра.</summary>
    public int Height { get; set; } = 1080;

    public int Fps { get; set; } = 60;

    /// <summary>Профиль качества. При <see cref="QualityPreset.Custom"/> используется <see cref="BitrateKbps"/>.</summary>
    public QualityPreset Quality { get; set; } = QualityPreset.High;

    /// <summary>Целевой битрейт в килобитах в секунду.</summary>
    public int BitrateKbps { get; set; } = 8000;

    /// <summary>Интервал между ключевыми кадрами в секундах.</summary>
    /// <remarks>
    /// Три секунды: ключевой кадр в разы больше обычного (измерено 190 КБ против 10 КБ
    /// при 720p60), и каждая его передача — всплеск в канале, поэтому реже лучше. Но
    /// слишком редко тоже нельзя: приёмник, подключившийся посреди потока, ждёт
    /// ближайшего ключевого кадра, и на пяти секундах ожидание первого кадра становится
    /// заметным. Три — компромисс.
    /// </remarks>
    public int KeyframeIntervalSeconds { get; set; } = 3;

    /// <summary>Рисовать ли курсор в захваченном изображении.</summary>
    public bool DrawCursor { get; set; } = true;

    /// <summary>
    /// Принимать ли управление с контроллера, подключённого к приёмнику.
    /// </summary>
    /// <remarks>
    /// Когда выключено, виртуальный геймпад в системе не создаётся вовсе, и поведение
    /// приложения остаётся прежним. Включается пользователем.
    /// </remarks>
    public bool GamepadEnabled { get; set; }

    /// <summary>Каким геймпадом притворяется компьютер.</summary>
    public GamepadKind Gamepad { get; set; } = GamepadKind.Xbox360;

    /// <summary>
    /// Запас сглаживания на приёмнике, миллисекунды.
    /// </summary>
    /// <remarks>
    /// Выбор между плавностью и задержкой: ключевой кадр в разы больше обычного, и его
    /// передача занимает сотни миллисекунд. Без запаса это видно как замирание картинки,
    /// с запасом — как задержка на ту же величину. Значение задаёт пользователь.
    ///
    /// По умолчанию ноль: картинка должна быть ровной сама по себе, а не за счёт
    /// добавленной задержки. На исправных кодеках так и есть — проверено на приставке
    /// при значениях от нуля до двухсот. Запас нужен только как страховка от
    /// неровностей сети, и его всегда можно поднять ползунком.
    /// </remarks>
    public int BufferMs { get; set; }

    /// <summary>
    /// Профиль H.264. <c>high</c> даёт лучшее качество, но старые ТВ-приставки иногда
    /// поддерживают только <c>main</c> или <c>baseline</c> — приёмник сообщает о поддержке
    /// в <c>announce.caps</c>.
    /// </summary>
    public string Profile { get; set; } = "high";

    public EncoderKind Encoder { get; set; } = EncoderKind.Auto;

    /// <summary>Использовать ddagrab (Desktop Duplication), если он доступен.</summary>
    public bool PreferDdagrab { get; set; } = true;

    /// <summary>Явный путь к ffmpeg; <see langword="null"/> — искать автоматически.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Интервал ключевых кадров в кадрах.</summary>
    public int KeyframeIntervalFrames => Math.Max(1, Fps * Math.Max(1, KeyframeIntervalSeconds));

    /// <summary>
    /// Битрейт, соответствующий профилю качества.
    /// </summary>
    /// <remarks>
    /// Считается как «бит на пиксель за кадр» (bpppf) — это устойчивая метрика, не зависящая
    /// от разрешения и частоты кадров. Для рабочего стола, где много мелкого текста, требуется
    /// заметно больше бит, чем для видео: 0.18 bpppf при 1080p60 даёт ~22 Мбит/с и визуально
    /// неотличимо от оригинала, что для локальной сети совершенно необременительно.
    /// </remarks>
    public int EffectiveBitrateKbps()
    {
        if (Quality == QualityPreset.Custom)
        {
            return Math.Clamp(BitrateKbps, 500, 200_000);
        }

        var bitsPerPixel = Quality switch
        {
            QualityPreset.Maximum => 0.32,
            QualityPreset.High => 0.18,
            QualityPreset.Balanced => 0.10,
            _ => 0.05,
        };

        var raw = Width * (double)Height * Math.Max(1, Fps) * bitsPerPixel / 1000.0;
        return (int)Math.Clamp(raw, 1500, 200_000);
    }

    /// <summary>Человекочитаемое описание профиля качества для интерфейса.</summary>
    public string DescribeQuality() => Quality switch
    {
        QualityPreset.Maximum => "Максимум (проводная сеть)",
        QualityPreset.High => "Высокое",
        QualityPreset.Balanced => "Сбалансированное",
        QualityPreset.Economy => "Экономичное",
        _ => $"{BitrateKbps} кбит/с",
    };

    /// <summary>Человекочитаемое имя источника для OSD на телевизоре.</summary>
    public string DescribeSource() => SourceKind switch
    {
        CaptureSourceKind.Window => Window?.Describe()
            ?? (string.IsNullOrWhiteSpace(WindowTitle) ? "Окно приложения" : WindowTitle!),
        _ => $"Экран {DisplayIndex + 1}",
    };

    /// <summary>Копия параметров с другим окном — для автослежения за перемещением.</summary>
    public CaptureOptions WithWindow(WindowTarget window)
    {
        var copy = Clone();
        copy.SourceKind = SourceKind;
        copy.DisplayIndex = window.DisplayIndex;
        copy.WindowTitle = window.Title;
        copy.Window = window;
        return copy;
    }

    /// <summary>Копия параметров с другим битрейтом — для адаптивной подстройки.</summary>
    public CaptureOptions WithBitrate(int bitrateKbps)
    {
        var copy = Clone();
        copy.Quality = QualityPreset.Custom;
        copy.BitrateKbps = Math.Clamp(bitrateKbps, 500, 200_000);
        return copy;
    }

    /// <summary>Копия параметров с другой частотой кадров — для адаптивной подстройки.</summary>
    public CaptureOptions WithFps(int fps)
    {
        var copy = Clone();
        copy.Fps = Math.Clamp(fps, 5, 240);
        return copy;
    }

    private CaptureOptions Clone() => new()
    {
        SourceKind = SourceKind,
        DisplayIndex = DisplayIndex,
        WindowTitle = WindowTitle,
        Window = Window,
        Audio = Audio.Clone(),
        Width = Width,
        Height = Height,
        Fps = Fps,
        Quality = Quality,
        BitrateKbps = BitrateKbps,
        KeyframeIntervalSeconds = KeyframeIntervalSeconds,
        DrawCursor = DrawCursor,
        BufferMs = BufferMs,
        Profile = Profile,
        Encoder = Encoder,
        PreferDdagrab = PreferDdagrab,
        FfmpegPath = FfmpegPath,
        GamepadEnabled = GamepadEnabled,
        Gamepad = Gamepad,
    };

    /// <summary>Копия параметров с другим состоянием управления.</summary>
    public CaptureOptions WithGamepadEnabled(bool enabled)
    {
        var copy = Clone();
        copy.GamepadEnabled = enabled;
        return copy;
    }
}
