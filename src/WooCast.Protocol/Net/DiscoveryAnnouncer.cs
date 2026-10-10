using System.Net;
using System.Net.Sockets;
using System.Text;
using WooCast.Protocol.Messages;

namespace WooCast.Protocol.Net;

/// <summary>Описание приёмника для широковещательных <c>announce</c>.</summary>
public sealed record AnnounceDescriptor
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string Model { get; init; } = "Android TV";

    public string Version { get; init; } = "1.0.0";

    public int ControlPort { get; init; } = Wcp.ControlPort;

    public int MediaPort { get; init; } = Wcp.MediaPort;

    public DeviceCaps Caps { get; init; } = new() { Codecs = ["h264"] };
}

/// <summary>
/// Служба оповещения для приёмника: рассылает <c>announce</c> и отвечает на <c>discover</c>.
/// </summary>
/// <remarks>
/// Нужна приёмной стороне. В C# реализована для отладочного приёмника
/// (<c>woocast receive</c>) и автотестов; на телевизоре ту же логику реализует Kotlin-код.
/// </remarks>
public sealed class DiscoveryAnnouncer : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly AnnounceDescriptor _descriptor;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _boundToDiscoveryPort;
    private Task? _loop;
    private volatile string _state = Wcp.ReceiverStates.Idle;
    private bool _disposed;

    /// <param name="descriptor">Сведения о приёмнике.</param>
    /// <param name="bindDiscoveryPort">
    /// Слушать ли <see cref="Wcp.DiscoveryPort"/> ради ответов на <c>discover</c>.
    /// Если порт занят (например, рядом запущен отправитель), служба переходит в режим
    /// только широковещательных оповещений.
    /// </param>
    public DiscoveryAnnouncer(AnnounceDescriptor descriptor, bool bindDiscoveryPort = true)
    {
        _descriptor = descriptor;

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.EnableBroadcast = true;

        if (bindDiscoveryPort)
        {
            try
            {
                _socket.Bind(new IPEndPoint(IPAddress.Any, Wcp.DiscoveryPort));
                _boundToDiscoveryPort = true;
            }
            catch (SocketException)
            {
                _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            }
        }
        else
        {
            _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
    }

    /// <summary>Фактический порт сокета оповещения.</summary>
    public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    /// <summary>Отвечает ли служба на запросы <c>discover</c>.</summary>
    public bool RespondsToDiscover => _boundToDiscoveryPort;

    /// <summary>Текущее состояние, попадающее в <c>announce.state</c>.</summary>
    public string State
    {
        get => _state;
        set => _state = value;
    }

    /// <summary>Запустить периодические оповещения и приём <c>discover</c>.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _loop ??= Task.WhenAll(AnnounceLoopAsync(_cts.Token), ReceiveLoopAsync(_cts.Token));
    }

    /// <summary>Разослать <c>announce</c> немедленно — например, сразу после старта.</summary>
    public async Task AnnounceNowAsync(CancellationToken cancellationToken = default)
    {
        var payload = WcpJson.EncodeLine(BuildAnnounce());

        foreach (var address in NetworkUtil.GetBroadcastAddresses())
        {
            await SendAsync(address, Wcp.DiscoveryPort, payload, cancellationToken).ConfigureAwait(false);
        }

        if (IPAddress.TryParse(Wcp.MulticastGroup, out var multicast))
        {
            await SendAsync(multicast, Wcp.DiscoveryPort, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private WcpMessage BuildAnnounce() => WcpMessage.Announce(
        _descriptor.Id,
        _descriptor.Name,
        _descriptor.Model,
        _descriptor.Version,
        _descriptor.ControlPort,
        _descriptor.MediaPort,
        _state,
        _descriptor.Caps);

    private async Task AnnounceLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Wcp.AnnounceInterval);

        try
        {
            await AnnounceNowAsync(cancellationToken).ConfigureAwait(false);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await AnnounceNowAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (!_boundToDiscoveryPort)
        {
            return;
        }

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
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                continue;
            }

            if (result.RemoteEndPoint is not IPEndPoint sender)
            {
                continue;
            }

            WcpMessage? message;
            try
            {
                message = WcpJson.Deserialize(Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes));
            }
            catch (WcpProtocolException)
            {
                continue;
            }

            if (message?.T == Wcp.MessageTypes.Discover)
            {
                // Отвечаем строго unicast — иначе получится лавина ответов.
                var payload = WcpJson.EncodeLine(BuildAnnounce());
                await SendAsync(sender.Address, sender.Port, payload, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SendAsync(IPAddress address, int port, byte[] payload, CancellationToken cancellationToken)
    {
        try
        {
            await _socket
                .SendToAsync(payload, SocketFlags.None, new IPEndPoint(address, port), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
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
