namespace WooCast.Protocol;

using System;

/// <summary>
/// Константы протокола WooCast (WCP/1). Единственное место, где заданы «магические» числа,
/// порты и строковые идентификаторы — и отправитель, и приёмник обязаны брать их отсюда.
/// </summary>
public static class Wcp
{
    /// <summary>Версия протокола. Поле <c>wc</c> в JSON и <c>version</c> в медиа-заголовке.</summary>
    public const int Version = 1;

    /// <summary>Порт UDP-обнаружения.</summary>
    public const int DiscoveryPort = 47810;

    /// <summary>Порт TCP-управления.</summary>
    public const int ControlPort = 47811;

    /// <summary>Порт TCP-медиа по умолчанию.</summary>
    public const int MediaPort = 47812;

    /// <summary>Группа multicast для сетей, где широковещательные пакеты ограничены.</summary>
    public const string MulticastGroup = "239.255.77.87";

    /// <summary>Период рассылки <c>announce</c> приёмником.</summary>
    public static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(3);

    /// <summary>Через сколько без <c>announce</c> устройство считается офлайн.</summary>
    public static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Сколько ждать медиа-соединение после <c>stream_ready</c>.</summary>
    public static readonly TimeSpan MediaConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Интервал телеметрии <c>stats</c>.</summary>
    public static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(1);

    /// <summary>Максимальная длина одной строки JSON в управляющем канале.</summary>
    public const int MaxJsonLineBytes = 64 * 1024;

    /// <summary>Максимальная длина полезной нагрузки одного медиа-пакета (4 МиБ).</summary>
    public const int MaxMediaPayload = 4 * 1024 * 1024;

    /// <summary>Размер заголовка медиа-пакета в байтах.</summary>
    public const int MediaPacketHeaderSize = 16;

    /// <summary>Размер приветственного заголовка медиа-соединения в байтах.</summary>
    public const int MediaHandshakeSize = 20;

    /// <summary>ASCII-сигнатура начала медиа-соединения.</summary>
    public static ReadOnlySpan<byte> MediaMagic => "WCM1"u8;

    /// <summary>Типы сообщений управляющего канала.</summary>
    public static class MessageTypes
    {
        // Обнаружение
        public const string Announce = "announce";
        public const string Discover = "discover";

        // Управление: отправитель -> приёмник
        public const string Hello = "hello";
        public const string StreamRequest = "stream_request";
        public const string StreamStart = "stream_start";
        public const string Stats = "stats";
        public const string Ping = "ping";
        public const string Stop = "stop";
        public const string Bye = "bye";

        // Управление: приёмник -> отправитель
        public const string HelloAck = "hello_ack";
        public const string StreamReady = "stream_ready";
        public const string StreamReject = "stream_reject";
        public const string Started = "started";
        public const string RequestKeyframe = "request_keyframe";
        public const string Pong = "pong";
        public const string Stopped = "stopped";
        public const string Error = "error";

        /// <summary>
        /// Состояние управления с приёмника: кнопки и оси геймпада, подключённого к нему.
        /// </summary>
        /// <remarks>
        /// Отправляет приёмник, а не компьютер: контроллер подключён к приставке или
        /// телефону, и его события нужно передать на ПК, где игра видит виртуальный
        /// геймпад. Частота — до 60 раз в секунду и только при изменениях состояния.
        /// </remarks>
        public const string Input = "input";
    }

    /// <summary>
    /// Кнопки геймпада в сообщении <see cref="MessageTypes.Input"/> — битовая маска.
    /// </summary>
    /// <remarks>
    /// Порядок и значения общие для обеих сторон: приёмник собирает маску из событий
    /// Android, компьютер разбирает её в виртуальный геймпад. Названия нейтральные,
    /// потому что Android приводит любой контроллер к общему виду: крест DualShock
    /// приходит как <see cref="A"/>, круг — как <see cref="B"/> и так далее.
    /// </remarks>
    public static class InputButtons
    {
        public const int A = 1 << 0;
        public const int B = 1 << 1;
        public const int X = 1 << 2;
        public const int Y = 1 << 3;
        public const int LeftBumper = 1 << 4;
        public const int RightBumper = 1 << 5;
        public const int Back = 1 << 6;
        public const int Start = 1 << 7;
        public const int LeftStick = 1 << 8;
        public const int RightStick = 1 << 9;
        public const int Guide = 1 << 10;
        public const int DpadUp = 1 << 11;
        public const int DpadDown = 1 << 12;
        public const int DpadLeft = 1 << 13;
        public const int DpadRight = 1 << 14;
    }

    /// <summary>Пределы осей и курков в сообщении управления.</summary>
    public static class InputAxes
    {
        /// <summary>Стики: от <see cref="StickMin"/> до <see cref="StickMax"/>, ноль в покое.</summary>
        public const int StickMin = -32768;
        public const int StickMax = 32767;

        /// <summary>Курки: от нуля до <see cref="TriggerMax"/>.</summary>
        public const int TriggerMin = 0;
        public const int TriggerMax = 255;

        /// <summary>Привести значение стика к допустимым пределам.</summary>
        public static int ClampStick(int value) => Math.Clamp(value, StickMin, StickMax);

        /// <summary>Привести значение курка к допустимым пределам.</summary>
        public static int ClampTrigger(int value) => Math.Clamp(value, TriggerMin, TriggerMax);
    }

    /// <summary>Причины отказа в старте трансляции (<c>stream_reject.reason</c>).</summary>
    public static class RejectReasons
    {
        public const string Busy = "busy";
        public const string UnsupportedCodec = "unsupported_codec";
        public const string UnsupportedResolution = "unsupported_resolution";
        public const string Declined = "declined";
        public const string Internal = "internal";
    }

    /// <summary>Коды ошибок протокола (<c>error.code</c>).</summary>
    public static class ErrorCodes
    {
        public const string ProtocolError = "protocol_error";
        public const string BadToken = "bad_token";
        public const string BadState = "bad_state";
        public const string Internal = "internal";
    }

    /// <summary>Состояния приёмника в <c>announce.state</c>.</summary>
    public static class ReceiverStates
    {
        public const string Idle = "idle";
        public const string Busy = "busy";
    }

    /// <summary>Типы источника захвата (<c>stream_request.source.type</c>).</summary>
    public static class SourceTypes
    {
        public const string Display = "display";
        public const string Window = "window";
    }

    /// <summary>Кодеки видео (<c>stream_request.video.codec</c> и поле <c>codec</c> медиа-заголовка).</summary>
    public static class Codecs
    {
        public const string H264 = "h264";

        /// <summary>Числовой идентификатор для медиа-заголовка.</summary>
        public const byte H264Id = 1;
    }
}
