using System.Net;
using WooCast.Protocol.Messages;

namespace WooCast.Protocol.Net;

/// <summary>Приёмник, найденный в локальной сети. Неизменяемый снимок состояния.</summary>
public sealed record DiscoveredDevice
{
    /// <summary>Стабильный идентификатор устройства.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Model { get; init; }

    public string? Version { get; init; }

    public required IPAddress Address { get; init; }

    public int ControlPort { get; init; } = Wcp.ControlPort;

    public int MediaPort { get; init; } = Wcp.MediaPort;

    /// <summary>Состояние приёмника, см. <see cref="Wcp.ReceiverStates"/>.</summary>
    public string State { get; init; } = Wcp.ReceiverStates.Idle;

    public DeviceCaps? Caps { get; init; }

    public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Занят ли приёмник другой трансляцией.</summary>
    public bool IsBusy => string.Equals(State, Wcp.ReceiverStates.Busy, StringComparison.OrdinalIgnoreCase);

    /// <summary>Адрес для ручного подключения, например <c>192.168.1.50:47811</c>.</summary>
    public string EndPointText => $"{Address}:{ControlPort}";

    /// <summary>Подпись устройства: модель, если известна, иначе IP.</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(Model) ? EndPointText : $"{Model} · {EndPointText}";

    /// <summary>Собрать снимок из полученного <c>announce</c>.</summary>
    /// <exception cref="WcpProtocolException">В сообщении нет обязательных полей.</exception>
    public static DiscoveredDevice FromAnnounce(WcpMessage message, IPAddress address, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(message.Id))
        {
            throw new WcpProtocolException("announce без поля id");
        }

        var controlPort = message.ControlPort ?? Wcp.ControlPort;
        if (controlPort is < 1 or > 65535)
        {
            throw new WcpProtocolException($"announce с некорректным controlPort: {controlPort}");
        }

        return new DiscoveredDevice
        {
            Id = message.Id,
            Name = string.IsNullOrWhiteSpace(message.Name) ? address.ToString() : message.Name,
            Model = message.Model,
            Version = message.Version,
            Address = address,
            ControlPort = controlPort,
            MediaPort = message.MediaPort ?? Wcp.MediaPort,
            State = message.State ?? Wcp.ReceiverStates.Idle,
            Caps = message.Caps,
            LastSeen = now,
        };
    }
}
