using System.Net;
using System.Net.Sockets;
using System.Text;
using WooCast.Protocol.Media;

namespace WooCast.Protocol.Net;

/// <summary>Счётчики отправленного медиапотока.</summary>
public sealed class MediaSenderCounters
{
    private long _frames;
    private long _keyframes;
    private long _bytes;
    private long _ptsMicros;
    private int _lastPayloadBytes;
    private long _audioFrames;
    private long _audioBytes;

    public long Frames => Interlocked.Read(ref _frames);

    public long Keyframes => Interlocked.Read(ref _keyframes);

    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Кадров звука отправлено.</summary>
    public long AudioFrames => Interlocked.Read(ref _audioFrames);

    /// <summary>Байт звука отправлено (вместе с заголовками пакетов).</summary>
    public long AudioBytes => Interlocked.Read(ref _audioBytes);

    /// <summary>Метка времени последнего отправленного кадра.</summary>
    public long LastPtsMicros => Interlocked.Read(ref _ptsMicros);

    /// <summary>Размер последнего отправленного кадра в байтах.</summary>
    public int LastPayloadBytes => Volatile.Read(ref _lastPayloadBytes);

    internal void OnSent(int payloadBytes, long ptsMicros, bool keyframe)
    {
        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _bytes, payloadBytes + MediaPacketHeader.Size);
        Interlocked.Exchange(ref _ptsMicros, ptsMicros);
        Volatile.Write(ref _lastPayloadBytes, payloadBytes);

        if (keyframe)
        {
            Interlocked.Increment(ref _keyframes);
        }
    }

    internal void OnAudioSent(int payloadBytes)
    {
        Interlocked.Increment(ref _audioFrames);
        Interlocked.Add(ref _audioBytes, payloadBytes + MediaPacketHeader.Size);
    }
}

/// <summary>
/// Отправляющая сторона медиа-канала: TCP-соединение с приёмником и упаковка access unit'ов.
/// </summary>
/// <remarks>
/// Потокобезопасность записи обеспечивается семафором: конвейер захвата пишет кадры из одной
/// задачи, но <c>request_keyframe</c> и остановка могут прийти из другой.
/// </remarks>
public sealed class MediaStreamSender : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly MediaSenderCounters _counters = new();
    private bool _disposed;

    private MediaStreamSender(TcpClient client, NetworkStream stream)
    {
        _client = client;
        _stream = stream;
    }

    public MediaSenderCounters Counters => _counters;

    /// <summary>Установить медиа-соединение и отправить приветственный заголовок.</summary>
    public static async Task<MediaStreamSender> ConnectAsync(
        IPAddress address,
        int port,
        long token,
        bool hasAudio,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient(address.AddressFamily)
        {
            // Зеркалирование экрана не терпит ожидания накопления пакетов (алгоритм Нагла).
            NoDelay = true,
            SendBufferSize = 1 << 20,
        };

        await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);

        var stream = client.GetStream();
        var handshake = MediaHandshake.ForH264(token, hasAudio);
        var buffer = new byte[MediaHandshake.Size];
        handshake.WriteTo(buffer);

        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        return new MediaStreamSender(client, stream);
    }

    /// <summary>Отправить один access unit.</summary>
    /// <exception cref="IOException">Соединение разорвано.</exception>
    public async ValueTask SendAsync(AccessUnit unit, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var flags = MediaFrameFlags.None;
        if (unit.IsKeyframe)
        {
            flags |= MediaFrameFlags.Keyframe;
        }

        if (unit.HasParameterSets)
        {
            flags |= MediaFrameFlags.Config;
        }

        await WritePacketAsync(unit.Data, unit.PtsMicros, flags, cancellationToken).ConfigureAwait(false);
        _counters.OnSent(unit.Length, unit.PtsMicros, unit.IsKeyframe);
    }

    /// <summary>
    /// Отправить один кадр звука: полный ADTS-кадр с меткой времени на общих с видео часах.
    /// </summary>
    /// <exception cref="IOException">Соединение разорвано.</exception>
    public async ValueTask SendAudioAsync(
        ReadOnlyMemory<byte> adtsFrame,
        long ptsMicros,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await WritePacketAsync(adtsFrame, ptsMicros, MediaFrameFlags.Audio, cancellationToken)
            .ConfigureAwait(false);

        _counters.OnAudioSent(adtsFrame.Length);
    }

    /// <summary>Собрать и записать один медиа-пакет. Порядок записи защищён семафором.</summary>
    private async ValueTask WritePacketAsync(
        ReadOnlyMemory<byte> payload,
        long ptsMicros,
        MediaFrameFlags flags,
        CancellationToken cancellationToken)
    {
        var packet = new byte[MediaPacketHeader.Size + payload.Length];
        new MediaPacketHeader(payload.Length, ptsMicros, flags).WriteTo(packet);
        payload.CopyTo(packet.AsMemory(MediaPacketHeader.Size));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _writeLock.Dispose();
        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Кадр, принятый тестовым приёмником.</summary>
/// <param name="Header">Заголовок пакета.</param>
/// <param name="Payload">Полезная нагрузка (access unit).</param>
public readonly record struct ReceivedFrame(MediaPacketHeader Header, byte[] Payload);

/// <summary>
/// Приёмник медиапотока для отладки и автотестов: слушает порт, принимает одно соединение
/// и выдаёт кадры по мере поступления. Используется командой <c>woocast receive</c>.
/// </summary>
public sealed class MediaStreamReceiver : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly long _expectedToken;
    private TcpClient? _client;

    /// <param name="port">Порт прослушивания; 0 — выбрать свободный автоматически.</param>
    /// <param name="expectedToken">Ожидаемый токен сессии.</param>
    public MediaStreamReceiver(int port, long expectedToken)
    {
        _expectedToken = expectedToken;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>Фактический порт прослушивания.</summary>
    public int Port { get; }

    /// <summary>Принять соединение и проверить хендшейк.</summary>
    /// <exception cref="InvalidDataException">Хендшейк неверен или токен не совпал.</exception>
    public async Task<MediaHandshake> AcceptAsync(CancellationToken cancellationToken)
    {
        _client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        _client.NoDelay = true;

        var handshake = await MediaHandshake
            .ReadAsync(_client.GetStream(), cancellationToken)
            .ConfigureAwait(false);

        if (handshake.Token != _expectedToken)
        {
            throw new InvalidDataException(
                $"Медиа-соединение: неверный токен (получено {handshake.Token:X16})");
        }

        return handshake;
    }

    /// <summary>Читать кадры до закрытия соединения.</summary>
    public async IAsyncEnumerable<ReceivedFrame> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Сначала вызовите AcceptAsync");
        }

        var stream = _client.GetStream();
        var headerBuffer = new byte[MediaPacketHeader.Size];

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await stream.ReadExactlyAsync(headerBuffer, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                yield break;
            }
            catch (IOException)
            {
                yield break;
            }

            var header = MediaPacketHeader.Parse(headerBuffer);
            var payload = new byte[header.PayloadLength];
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

            yield return new ReceivedFrame(header, payload);
        }
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }

    /// <summary>Краткое текстовое описание пакета — для логов отладки.</summary>
    public static string Describe(ReceivedFrame frame)
    {
        H264Util.Analyze(frame.Payload, out _, out var hasConfig);
        var builder = new StringBuilder(64);
        builder.Append(frame.Header.IsKeyframe ? "IDR" : "P");
        builder.Append("  ").Append(frame.Header.PayloadLength.ToString().PadLeft(7)).Append(" Б");
        builder.Append("  pts=").Append((frame.Header.PtsMicros / 1000.0).ToString("F1")).Append(" мс");
        if (hasConfig)
        {
            builder.Append("  [SPS/PPS]");
        }

        return builder.ToString();
    }
}
