using System.Text.Json.Serialization;

namespace WooCast.Protocol.Messages;

/// <summary>
/// Универсальное сообщение управляющего канала WCP/1.
/// </summary>
/// <remarks>
/// Намеренно сделано «плоским» DTO со всеми возможными полями вместо иерархии типов:
/// это упрощает разбор на стороне Kotlin, автоматически обеспечивает совместимость вперёд
/// (неизвестные поля игнорируются) и избавляет от полиморфной десериализации.
/// Каждое поле соответствует ключу JSON в camelCase.
/// <para>
/// Токен сессии — знаковое 64-битное число. В медиа-заголовке он передаётся как есть,
/// в сетевом порядке байт; знаковость выбрана потому, что Kotlin (в отличие от C#)
/// не имеет удобного беззнакового 64-битного типа, и отрицательное значение,
/// записанное приёмником, ломало разбор на стороне ПК.
/// </para>
/// </remarks>
public sealed class WcpMessage
{
    /// <summary>Версия протокола, всегда <see cref="Wcp.Version"/>.</summary>
    public int Wc { get; set; } = Wcp.Version;

    /// <summary>Тип сообщения, см. <see cref="Wcp.MessageTypes"/>.</summary>
    public string? T { get; set; }

    /// <summary>Идентификатор запроса. Ответ обязан вернуть его в <see cref="Re"/>.</summary>
    public string? Id { get; set; }

    /// <summary>Идентификатор запроса, на который отвечает это сообщение.</summary>
    public string? Re { get; set; }

    // ---- обнаружение и идентификация ----

    /// <summary>Имя устройства: компьютера или приставки.</summary>
    public string? Name { get; set; }

    public string? Model { get; set; }

    public string? Version { get; set; }

    public int? Proto { get; set; }

    public int? ControlPort { get; set; }

    public int? MediaPort { get; set; }

    /// <summary>Состояние приёмника, см. <see cref="Wcp.ReceiverStates"/>.</summary>
    public string? State { get; set; }

    public DeviceCaps? Caps { get; set; }

    public AppInfo? App { get; set; }

    public PeerInfo? Receiver { get; set; }

    // ---- параметры трансляции ----

    public StreamVideoSpec? Video { get; set; }

    public StreamAudioSpec? Audio { get; set; }

    public StreamSourceSpec? Source { get; set; }

    /// <summary>Состояние управления с приёмника (<c>input</c>).</summary>
    public InputSpec? Input { get; set; }

    /// <summary>Идентификатор сессии, выданный приёмником.</summary>
    public string? Session { get; set; }

    /// <summary>Токен для медиа-хендшейка.</summary>
    public long? Token { get; set; }

    /// <summary>Причина отказа, см. <see cref="Wcp.RejectReasons"/>.</summary>
    public string? Reason { get; set; }

    /// <summary>Код ошибки, см. <see cref="Wcp.ErrorCodes"/>.</summary>
    public string? Code { get; set; }

    /// <summary>Пояснение для пользователя (может показываться в интерфейсе).</summary>
    public string? Message { get; set; }

    // ---- телеметрия ----

    public double? Fps { get; set; }

    /// <summary>Битрейт в килобитах в секунду.</summary>
    public int? Kbps { get; set; }

    /// <summary>Кадры, потерянные из-за нехватки пропускной способности.</summary>
    public int? Dropped { get; set; }

    /// <summary>Глубина очереди отправки в миллисекундах.</summary>
    public double? QueueMs { get; set; }

    /// <summary>Круговое время до приёмника в миллисекундах.</summary>
    public double? RttMs { get; set; }

    /// <summary>Кадры, отброшенные приёмником как просроченные.</summary>
    public int? Late { get; set; }

    /// <summary>Оценка сквозной задержки в миллисекундах.</summary>
    public double? LatencyMs { get; set; }

    /// <summary>
    /// Самая длинная пауза между показанными кадрами за последний интервал, миллисекунды.
    /// </summary>
    /// <remarks>
    /// Это то, что человек видит как рывок. Потери кадров и задержка о таком молчат:
    /// кадры могут приходить все, но неравномерно — пачкой после ключевого кадра,
    /// и тогда картинка дёргается при формально идеальной телеметрии.
    /// </remarks>
    public double? MaxGapMs { get; set; }

    /// <summary>Сколько раз пауза между кадрами вдвое превысила нормальную.</summary>
    public int? Stutters { get; set; }

    /// <summary>Насколько звук записан вперёд фактического воспроизведения, миллисекунды.</summary>
    public int? AudioBacklogMs { get; set; }

    /// <summary>Метка времени для ping/pong, миллисекунды монотонного времени.</summary>
    public long? Ts { get; set; }

    /// <summary>Произвольные данные события (зарезервировано под обратный канал).</summary>
    public string? Event { get; set; }

    [JsonIgnore]
    public bool IsAnnounce => T == Wcp.MessageTypes.Announce;

    // ------------------------------------------------------------------
    // Фабрики: единообразное создание сообщений и защита от опечаток в типах.
    // ------------------------------------------------------------------

    public static WcpMessage Discover(string deviceId, string deviceName) => new()
    {
        T = Wcp.MessageTypes.Discover,
        Id = deviceId,
        Name = deviceName,
        Proto = Wcp.Version,
    };

    public static WcpMessage Announce(
        string deviceId,
        string name,
        string model,
        string version,
        int controlPort,
        int mediaPort,
        string state,
        DeviceCaps caps) => new()
    {
        T = Wcp.MessageTypes.Announce,
        Id = deviceId,
        Name = name,
        Model = model,
        Version = version,
        Proto = Wcp.Version,
        ControlPort = controlPort,
        MediaPort = mediaPort,
        State = state,
        Caps = caps,
    };

    public static WcpMessage Hello(string deviceId, string deviceName, AppInfo app) => new()
    {
        T = Wcp.MessageTypes.Hello,
        Id = deviceId,
        Name = deviceName,
        App = app,
        Proto = Wcp.Version,
    };

    public static WcpMessage HelloAck(string? re, string session, long token, PeerInfo receiver) => new()
    {
        T = Wcp.MessageTypes.HelloAck,
        Re = re,
        Session = session,
        Token = token,
        Receiver = receiver,
        Proto = Wcp.Version,
    };

    public static WcpMessage StreamRequest(
        string requestId,
        StreamVideoSpec video,
        StreamSourceSpec source,
        StreamAudioSpec audio) => new()
    {
        T = Wcp.MessageTypes.StreamRequest,
        Id = requestId,
        Video = video,
        Source = source,
        Audio = audio,
    };

    public static WcpMessage StreamReady(string? re, int mediaPort, long token) => new()
    {
        T = Wcp.MessageTypes.StreamReady,
        Re = re,
        MediaPort = mediaPort,
        Token = token,
    };

    public static WcpMessage StreamReject(string? re, string reason, string? message = null) => new()
    {
        T = Wcp.MessageTypes.StreamReject,
        Re = re,
        Reason = reason,
        Message = message,
    };

    public static WcpMessage StreamStart() => new() { T = Wcp.MessageTypes.StreamStart };

    public static WcpMessage Started(string? re = null) => new() { T = Wcp.MessageTypes.Started, Re = re };

    public static WcpMessage RequestKeyframe() => new() { T = Wcp.MessageTypes.RequestKeyframe };

    /// <summary>Состояние управления с приёмника.</summary>
    public static WcpMessage InputState(InputSpec input) => new()
    {
        T = Wcp.MessageTypes.Input,
        Input = input,
    };

    public static WcpMessage Stats(
        double fps,
        int kbps,
        int dropped,
        double queueMs,
        double? rttMs = null,
        int? late = null,
        double? latencyMs = null,
        double? maxGapMs = null,
        int? stutters = null,
        int? audioBacklogMs = null) => new()
    {
        T = Wcp.MessageTypes.Stats,
        Fps = fps,
        Kbps = kbps,
        Dropped = dropped,
        QueueMs = queueMs,
        RttMs = rttMs,
        Late = late,
        LatencyMs = latencyMs,
        MaxGapMs = maxGapMs,
        Stutters = stutters,
        AudioBacklogMs = audioBacklogMs,
    };

    public static WcpMessage Ping(long timestampMs) => new() { T = Wcp.MessageTypes.Ping, Ts = timestampMs };

    public static WcpMessage Pong(string? re, long? timestampMs) => new()
    {
        T = Wcp.MessageTypes.Pong,
        Re = re,
        Ts = timestampMs,
    };

    public static WcpMessage Stop() => new() { T = Wcp.MessageTypes.Stop };

    public static WcpMessage Stopped(string? re = null) => new() { T = Wcp.MessageTypes.Stopped, Re = re };

    public static WcpMessage Bye() => new() { T = Wcp.MessageTypes.Bye };

    public static WcpMessage Error(string? re, string code, string message) => new()
    {
        T = Wcp.MessageTypes.Error,
        Re = re,
        Code = code,
        Message = message,
    };
}
