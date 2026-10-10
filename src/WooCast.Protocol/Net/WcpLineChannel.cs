using System.Text;
using WooCast.Protocol.Messages;

namespace WooCast.Protocol.Net;

/// <summary>
/// Канал управляющих сообщений: по одной JSON-строке на сообщение, разделитель — <c>\n</c>.
/// </summary>
/// <remarks>
/// Чтение ведётся вручную, а не через <see cref="StreamReader"/>, чтобы жёстко ограничить
/// длину строки (<see cref="Wcp.MaxJsonLineBytes"/>): иначе некорректный или враждебный peer
/// может заставить процесс выделить произвольный объём памяти.
/// </remarks>
public sealed class WcpLineChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly byte[] _buffer = new byte[8 * 1024];
    private readonly MemoryStream _accumulator = new(1024);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _available;
    private int _position;

    public WcpLineChannel(Stream stream, bool ownsStream = true)
    {
        _stream = stream;
        _ownsStream = ownsStream;
    }

    /// <summary>Нижележащий поток — например, чтобы достать удалённый адрес из <see cref="System.Net.Sockets.NetworkStream"/>.</summary>
    public Stream BaseStream => _stream;

    /// <summary>Прочитать следующее сообщение. <see langword="null"/> означает корректное закрытие потока.</summary>
    /// <exception cref="WcpProtocolException">Строка слишком длинная или не является JSON-объектом.</exception>
    public async ValueTask<WcpMessage?> ReadAsync(CancellationToken cancellationToken)
    {
        var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return line is null ? null : WcpJson.Deserialize(line);
    }

    /// <summary>Отправить сообщение одной строкой. Потокобезопасно.</summary>
    public async ValueTask WriteAsync(WcpMessage message, CancellationToken cancellationToken)
    {
        var bytes = WcpJson.EncodeLine(message);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        _accumulator.SetLength(0);

        while (true)
        {
            if (_position < _available)
            {
                var newline = Array.IndexOf(_buffer, (byte)'\n', _position, _available - _position);
                if (newline >= 0)
                {
                    _accumulator.Write(_buffer, _position, newline - _position);
                    _position = newline + 1;
                    return DecodeLine();
                }

                _accumulator.Write(_buffer, _position, _available - _position);
                _position = _available;

                if (_accumulator.Length > Wcp.MaxJsonLineBytes)
                {
                    throw new WcpProtocolException(
                        $"Строка управляющего канала превышает {Wcp.MaxJsonLineBytes} байт");
                }
            }

            _available = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            _position = 0;

            if (_available == 0)
            {
                // Поток закрыт: отдаём последнюю недочитанную строку, если она есть.
                return _accumulator.Length == 0 ? null : DecodeLine();
            }
        }
    }

    private string DecodeLine()
    {
        var text = Encoding.UTF8.GetString(_accumulator.GetBuffer(), 0, (int)_accumulator.Length);
        return text.TrimEnd('\r');
    }

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        if (_ownsStream)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
