using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WooCast.Protocol.Messages;

namespace WooCast.Protocol.Net;

/// <summary>
/// Слушатель обнаружения на стороне отправителя: принимает широковещательные <c>announce</c>
/// и по запросу рассылает <c>discover</c>.
/// </summary>
/// <remarks>
/// Сокет привязан к <see cref="Wcp.DiscoveryPort"/>, поэтому unicast-ответы на наш <c>discover</c>
/// приходят на тот же порт. Дополнительно выполняется вход в multicast-группу
/// <see cref="Wcp.MulticastGroup"/> — в сетях, где broadcast заблокирован, это единственный
/// способ увидеть приёмник.
/// </remarks>
public sealed class DiscoveryListener : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _devices = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _localId;
    private readonly string _localName;
    private Task? _receiveLoop;
    private Task? _maintenanceLoop;
    private bool _disposed;

    /// <param name="localId">Идентификатор нашего ПК — попадает в <c>discover</c>.</param>
    /// <param name="localName">Имя нашего ПК для отображения на телевизоре.</param>
    public DiscoveryListener(string localId, string localName)
    {
        _localId = localId;
        _localName = localName;

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.EnableBroadcast = true;
        _socket.Bind(new IPEndPoint(IPAddress.Any, Wcp.DiscoveryPort));

        try
        {
            _socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(IPAddress.Parse(Wcp.MulticastGroup)));
        }
        catch (SocketException)
        {
            // Multicast недоступен на этом интерфейсе — работаем только через broadcast.
        }
    }

    /// <summary>Устройство появилось в сети впервые.</summary>
    public event EventHandler<DiscoveredDevice>? DeviceDiscovered;

    /// <summary>Устройство прислало очередной <c>announce</c> (обновление состояния).</summary>
    public event EventHandler<DiscoveredDevice>? DeviceUpdated;

    /// <summary>Устройство перестало отвечать.</summary>
    public event EventHandler<DiscoveredDevice>? DeviceLost;

    /// <summary>Периодически рассылать <c>discover</c> без запроса пользователя.</summary>
    public bool AutoProbe { get; set; } = true;

    /// <summary>Период автоопроса.</summary>
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Текущий список найденных устройств, отсортированный по имени.</summary>
    public IReadOnlyList<DiscoveredDevice> Devices =>
        _devices.Values.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Запустить приём пакетов и фоновое обслуживание списка. Повторный вызов безопасен.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _receiveLoop ??= Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _maintenanceLoop ??= Task.Run(() => MaintenanceLoopAsync(_cts.Token));
    }

    /// <summary>Немедленно разослать <c>discover</c> по всем широковещательным адресам.</summary>
    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        var payload = WcpJson.EncodeLine(WcpMessage.Discover(_localId, _localName));

        foreach (var address in NetworkUtil.GetBroadcastAddresses())
        {
            await SendToAsync(address, Wcp.DiscoveryPort, payload, cancellationToken).ConfigureAwait(false);
        }

        if (IPAddress.TryParse(Wcp.MulticastGroup, out var multicast))
        {
            await SendToAsync(multicast, Wcp.DiscoveryPort, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Опросить конкретный адрес — используется при ручном вводе IP.</summary>
    public Task ProbeAsync(IPAddress target, int port = Wcp.DiscoveryPort, CancellationToken cancellationToken = default)
    {
        var payload = WcpJson.EncodeLine(WcpMessage.Discover(_localId, _localName));
        return SendToAsync(target, port, payload, cancellationToken);
    }

    /// <summary>Забыть устройство — например, если пользователь удалил его из списка вручную.</summary>
    public void Forget(string deviceId)
    {
        if (_devices.TryRemove(deviceId, out var device))
        {
            DeviceLost?.Invoke(this, device);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket
                    .ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, remote, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // ICMP port unreachable и подобные ошибки не должны останавливать приём.
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (result.RemoteEndPoint is not IPEndPoint sender)
            {
                continue;
            }

            HandleDatagram(buffer.AsSpan(0, result.ReceivedBytes), sender.Address);
        }
    }

    private void HandleDatagram(ReadOnlySpan<byte> datagram, IPAddress senderAddress)
    {
        WcpMessage? message;
        try
        {
            message = WcpJson.Deserialize(Encoding.UTF8.GetString(datagram));
        }
        catch (WcpProtocolException)
        {
            return; // Чужой или повреждённый пакет на нашем порту — молча игнорируем.
        }

        if (message?.T != Wcp.MessageTypes.Announce)
        {
            return;
        }

        DiscoveredDevice device;
        try
        {
            device = DiscoveredDevice.FromAnnounce(message, senderAddress, DateTimeOffset.UtcNow);
        }
        catch (WcpProtocolException)
        {
            return;
        }

        var isNew = false;
        _devices.AddOrUpdate(
            device.Id,
            _ =>
            {
                isNew = true;
                return device;
            },
            (_, _) => device);

        if (isNew)
        {
            DeviceDiscovered?.Invoke(this, device);
        }
        else
        {
            DeviceUpdated?.Invoke(this, device);
        }
    }

    private async Task MaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await SafeWaitAsync(timer, cancellationToken).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var (id, device) in _devices)
            {
                if (now - device.LastSeen > Wcp.DeviceTimeout && _devices.TryRemove(id, out var lost))
                {
                    DeviceLost?.Invoke(this, lost);
                }
            }

            if (AutoProbe && now - _lastProbe >= ProbeInterval)
            {
                _lastProbe = now;
                try
                {
                    await ProbeAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    // Сеть недоступна — попробуем на следующем тике.
                }
            }
        }
    }

    private DateTimeOffset _lastProbe = DateTimeOffset.MinValue;

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SendToAsync(IPAddress address, int port, byte[] payload, CancellationToken cancellationToken)
    {
        try
        {
            await _socket
                .SendToAsync(payload, SocketFlags.None, new IPEndPoint(address, port), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // Интерфейс мог уйти в offline — не считаем это ошибкой уровня протокола.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _cts.Cancel();
        _socket.Dispose();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
