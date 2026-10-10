namespace WooCast.Protocol.Messages;

/// <summary>Возможности приёмника (<c>announce.caps</c>).</summary>
public sealed class DeviceCaps
{
    /// <summary>Поддерживаемые кодеки видео, например <c>["h264"]</c>.</summary>
    public List<string>? Codecs { get; set; }

    public int? MaxWidth { get; set; }

    public int? MaxHeight { get; set; }

    public int? MaxFps { get; set; }

    /// <summary>Поддерживаемые кодеки звука; в v1 не используются.</summary>
    public List<string>? Audio { get; set; }

    /// <summary>Умеет ли приёмник включать низколатентный режим декодера (API 30+).</summary>
    public bool? LowLatencyDecode { get; set; }
}

/// <summary>Сведения о приложении-отправителе (<c>hello.app</c>).</summary>
public sealed class AppInfo
{
    public string? Name { get; set; }

    public string? Version { get; set; }
}

/// <summary>Сведения о приёмнике в ответе (<c>hello_ack.receiver</c>).</summary>
public sealed class PeerInfo
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Model { get; set; }

    public string? Version { get; set; }
}

/// <summary>Параметры видеопотока (<c>stream_request.video</c>).</summary>
public sealed class StreamVideoSpec
{
    public string? Codec { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int Fps { get; set; }

    /// <summary>Целевой битрейт в килобитах в секунду.</summary>
    public int Bitrate { get; set; }

    /// <summary>Профиль H.264: <c>baseline</c>, <c>main</c>, <c>high</c>.</summary>
    public string? Profile { get; set; }

    /// <summary>
    /// Запас сглаживания на приёмнике, миллисекунды.
    /// </summary>
    /// <remarks>
    /// Это выбор между плавностью и задержкой, и делать его должен пользователь.
    /// Ключевой кадр в разы больше обычного, и его передача занимает сотни миллисекунд:
    /// без запаса это видно как замирание картинки раз в несколько секунд, с запасом —
    /// как задержка на ту же величину. Ноль означает «минимальная задержка, возможны рывки».
    /// </remarks>
    public int BufferMs { get; set; }
}

/// <summary>Параметры звука (<c>stream_request.audio</c>).</summary>
public sealed class StreamAudioSpec
{
    public bool Enabled { get; set; }

    public string? Codec { get; set; }

    /// <summary>Битрейт в килобитах в секунду.</summary>
    public int Bitrate { get; set; }

    /// <summary>
    /// Поправка выравнивания звука, миллисекунды. Положительная — звук позже картинки.
    /// </summary>
    /// <remarks>
    /// Приёмник прибавляет её к своей базовой задержке. Выравнивание делается на стороне
    /// звука, а не видео: у звукового вывода большой буфер, и задержать его безопасно,
    /// тогда как придержка видео приводит к нехватке выходных буферов декодера.
    /// </remarks>
    public int Delay { get; set; }
}

/// <summary>
/// Состояние управления (<c>input</c>): кнопки и оси геймпада на приёмнике.
/// </summary>
/// <remarks>
/// Значения кнопок — из <see cref="Wcp.InputButtons"/>, пределы осей — из
/// <see cref="Wcp.InputAxes"/>. Отправляется только при изменениях, поэтому
/// счётчик <see cref="Seq"/> позволяет заметить пропуск.
/// </remarks>
public sealed class InputSpec
{
    /// <summary>Порядковый номер состояния: растёт на единицу с каждым отправленным.</summary>
    public long Seq { get; set; }

    /// <summary>Битовая маска нажатых кнопок.</summary>
    public int Buttons { get; set; }

    /// <summary>Левый стик, горизонталь.</summary>
    public int Lx { get; set; }

    /// <summary>Левый стик, вертикаль.</summary>
    public int Ly { get; set; }

    /// <summary>Правый стик, горизонталь.</summary>
    public int Rx { get; set; }

    /// <summary>Правый стик, вертикаль.</summary>
    public int Ry { get; set; }

    /// <summary>Левый курок.</summary>
    public int Lt { get; set; }

    /// <summary>Правый курок.</summary>
    public int Rt { get; set; }

    /// <summary>Название контроллера — для показа на компьютере.</summary>
    public string? Device { get; set; }
}

/// <summary>Источник захвата (<c>stream_request.source</c>).</summary>
public sealed class StreamSourceSpec
{
    /// <summary>Тип источника: <see cref="Wcp.SourceTypes.Display"/> или <see cref="Wcp.SourceTypes.Window"/>.</summary>
    public string? Type { get; set; }

    /// <summary>Идентификатор монитора или окна.</summary>
    public string? Id { get; set; }

    /// <summary>Человекочитаемое название источника для OSD на телевизоре.</summary>
    public string? Title { get; set; }
}
