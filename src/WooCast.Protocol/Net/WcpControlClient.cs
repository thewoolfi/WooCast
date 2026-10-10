using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using WooCast.Protocol.Messages;

namespace WooCast.Protocol.Net;

/// <summary>
/// Клиент управляющего канала: соединение с приёмником, корреляция ответов и приём
/// незапрошенных уведомлений.
/// </summary>
/// <remarks>
/// Ответы сопоставляются с запросами по полю <c>re</c>. Идентификатор запроса берётся из
/// <see cref="WcpMessage.Id"/>; для <c>hello</c> он совпадает с идентификатором устройства —
/// так требует протокол, и это же значение приёмник возвращает в <c>hello_ack.re</c>.
/// </remarks>
public sealed class WcpControlClient : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly WcpLineChannel _channel;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WcpMessage>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private int _requestCounter;
    private bool _disposed;

    private WcpControlClient(TcpClient client, WcpLineChannel channel)
    {
        _client = client;
        _channel = channel;
    }

    /// <summary>Незапрошенное сообщение от приёмника (телеметрия, запрос ключевого кадра, остановка).</summary>
    public event EventHandler<WcpMessage>? Notification;

    /// <summary>Соединение потеряно или закрыто.</summary>
    public event EventHandler<Exception?>? Closed;

    /// <summary>Удалённый адрес приёмника.</summary>
    public string RemoteEndPoint => _client.Client.RemoteEndPoint?.ToString() ?? "?";

    public bool IsConnected => !_disposed && _client.Connected;

    /// <summary>Установить управляющее соединение.</summary>
    public static async Task<WcpControlClient> ConnectAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient(address.AddressFamily)
        {
            // Управляющие сообщения должны уходить мгновенно, без ожидания Нагла.
            NoDelay = true,
        };

        await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);

        var channel = new WcpLineChannel(client.GetStream());
        var instance = new WcpControlClient(client, channel);
        instance.StartReceiveLoop();
        return instance;
    }

    /// <summary>
    /// Отправить запрос и дождаться ответа. Идентификатор присваивается автоматически,
    /// если он не задан (для <c>hello</c> он обязан быть идентификатором устройства).
    /// </summary>
    /// <exception cref="TimeoutException">Ответ не пришёл за отведённое время.</exception>
    /// <exception cref="IOException">Соединение разорвано.</exception>
    public async Task<WcpMessage> RequestAsync(
        WcpMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        request.Id ??= Interlocked.Increment(ref _requestCounter).ToString();

        var completion = new TaskCompletionSource<WcpMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.Id, completion))
        {
            throw new InvalidOperationException($"Запрос с идентификатором {request.Id} уже выполняется");
        }

        try
        {
            await _channel.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Приёмник не ответил на «{request.T}» за {timeout.TotalSeconds:F1} с");
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    /// <summary>Отправить сообщение без ожидания ответа.</summary>
    public ValueTask SendAsync(WcpMessage message, CancellationToken cancellationToken = default) =>
        _channel.WriteAsync(message, cancellationToken);

    private void StartReceiveLoop() => _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await _channel.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    break; // Приёмник закрыл соединение.
                }

                if (!string.IsNullOrEmpty(message.Re) && _pending.TryRemove(message.Re, out var completion))
                {
                    completion.TrySetResult(message);
                    continue;
                }

                // Ответ на запрос, которого мы уже не ждём (таймаут) — игнорируем.
                if (!string.IsNullOrEmpty(message.Re) && message.T is Wcp.MessageTypes.StreamReady
                    or Wcp.MessageTypes.HelloAck or Wcp.MessageTypes.Started or Wcp.MessageTypes.Pong
                    or Wcp.MessageTypes.StreamReject or Wcp.MessageTypes.Stopped)
                {
                    continue;
                }

                Notification?.Invoke(this, message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            // Все ожидающие запросы должны узнать о разрыве, иначе они провисят до таймаута.
            foreach (var (_, completion) in _pending)
            {
                completion.TrySetException(failure ?? new IOException("Управляющее соединение закрыто"));
            }

            _pending.Clear();
            Closed?.Invoke(this, failure);
        }
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
            await _channel.WriteAsync(WcpMessage.Bye(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Приёмник уже мог закрыть соединение.
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();

        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}
