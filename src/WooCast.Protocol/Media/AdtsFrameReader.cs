namespace WooCast.Protocol.Media;

/// <summary>Заголовок одного ADTS-кадра (AAC).</summary>
/// <param name="FrameLength">Полная длина кадра вместе с заголовком.</param>
/// <param name="HeaderLength">Длина заголовка: 7 байт или 9 при наличии CRC.</param>
/// <param name="SampleRateIndex">Индекс частоты дискретизации из таблицы ADTS.</param>
/// <param name="ChannelConfiguration">Конфигурация каналов.</param>
/// <param name="AudioObjectType">Тип аудиообъекта: 2 — AAC-LC.</param>
public readonly record struct AdtsHeader(
    int FrameLength,
    int HeaderLength,
    int SampleRateIndex,
    int ChannelConfiguration,
    int AudioObjectType)
{
    /// <summary>Частота дискретизации в герцах.</summary>
    public int SampleRate => AdtsFrameReader.SampleRates[Math.Clamp(SampleRateIndex, 0, 12)];

    /// <summary>
    /// AudioSpecificConfig — те два байта, которые MediaCodec ожидает в <c>csd-0</c>.
    /// </summary>
    /// <remarks>
    /// Раскладка битов: 5 бит типа объекта, 4 бита индекса частоты, 4 бита каналов
    /// и 3 нулевых бита GASpecificConfig.
    /// </remarks>
    public byte[] BuildAudioSpecificConfig()
    {
        var config = (AudioObjectType & 0x1F) << 11
                     | (SampleRateIndex & 0x0F) << 7
                     | (ChannelConfiguration & 0x0F) << 3;

        return [(byte)(config >> 8), (byte)(config & 0xFF)];
    }
}

/// <summary>
/// Делит непрерывный поток ADTS на отдельные кадры AAC.
/// </summary>
/// <remarks>
/// Именно в таком виде системный звук приходит от кодировщика: ffmpeg с <c>-f adts</c>
/// добавляет к каждому кадру заголовок с длиной, поэтому границы кадров определяются
/// без разбора самого аудиопотока.
/// </remarks>
public sealed class AdtsFrameReader
{
    /// <summary>Частоты дискретизации по индексу из заголовка ADTS.</summary>
    public static readonly int[] SampleRates =
    [
        96000, 88200, 64000, 48000, 44100, 32000, 24000,
        22050, 16000, 12000, 11025, 8000, 7350,
    ];

    /// <summary>Сколько сэмплов содержит один кадр AAC.</summary>
    public const int SamplesPerFrame = 1024;

    private readonly Queue<AdtsFrame> _ready = new();
    private byte[] _buffer;
    private int _length;

    public AdtsFrameReader(int initialCapacity = 64 * 1024)
    {
        _buffer = new byte[Math.Max(4096, initialCapacity)];
    }

    /// <summary>Сколько кадров готово к выдаче.</summary>
    public int ReadyCount => _ready.Count;

    /// <summary>Последний разобранный заголовок — из него берётся конфигурация для декодера.</summary>
    public AdtsHeader? LastHeader { get; private set; }

    /// <summary>Сколько байт пришлось пропустить из-за нарушения синхронизации.</summary>
    public long SkippedBytes { get; private set; }

    /// <summary>Добавить очередную порцию байт от кодировщика.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        EnsureCapacity(_length + data.Length);
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;

        Parse();
    }

    /// <summary>Забрать готовый кадр.</summary>
    public bool TryDequeue(out AdtsFrame frame)
    {
        if (_ready.Count > 0)
        {
            frame = _ready.Dequeue();
            return true;
        }

        frame = default;
        return false;
    }

    /// <summary>Завершить поток: незавершённый кадр отбрасывается.</summary>
    public void Flush()
    {
        _length = 0;
    }

    private void Parse()
    {
        var offset = 0;

        while (true)
        {
            // Ищем синхрослово 0xFFF на границе байта.
            while (offset + 1 < _length && !(_buffer[offset] == 0xFF && (_buffer[offset + 1] & 0xF0) == 0xF0))
            {
                offset++;
                SkippedBytes++;
            }

            if (offset + 7 > _length)
            {
                break;
            }

            var header = ParseHeader(_buffer.AsSpan(offset));
            if (header.FrameLength < header.HeaderLength || offset + header.FrameLength > _length)
            {
                break; // Кадр ещё не пришёл целиком.
            }

            var raw = _buffer.AsSpan(offset, header.FrameLength).ToArray();

            LastHeader = header;
            _ready.Enqueue(new AdtsFrame(header, raw));

            offset += header.FrameLength;
        }

        // Сдвигаем необработанный остаток в начало буфера.
        if (offset > 0)
        {
            var remaining = _length - offset;
            if (remaining > 0)
            {
                Buffer.BlockCopy(_buffer, offset, _buffer, 0, remaining);
            }

            _length = remaining;
        }
    }

    /// <summary>Разобрать заголовок ADTS в позиции <paramref name="data"/>[0].</summary>
    public static AdtsHeader ParseHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7)
        {
            throw new ArgumentException("Для заголовка ADTS нужно минимум 7 байт", nameof(data));
        }

        var protectionAbsent = (data[1] & 0x01) != 0;
        var profile = (data[2] & 0xC0) >> 6;
        var sampleRateIndex = (data[2] & 0x3C) >> 2;
        var channelConfiguration = ((data[2] & 0x01) << 2) | ((data[3] & 0xC0) >> 6);
        var frameLength = ((data[3] & 0x03) << 11) | (data[4] << 3) | ((data[5] & 0xE0) >> 5);

        var headerLength = protectionAbsent ? 7 : 9;

        return new AdtsHeader(
            frameLength,
            headerLength,
            sampleRateIndex,
            channelConfiguration,
            profile + 1);
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        var size = _buffer.Length;
        while (size < required)
        {
            size *= 2;
        }

        Array.Resize(ref _buffer, size);
    }
}

/// <summary>Один кадр AAC целиком, вместе с заголовком ADTS.</summary>
/// <param name="Header">Разобранный заголовок.</param>
/// <param name="Raw">Полный кадр: заголовок и сжатые данные.</param>
/// <remarks>
/// По сети кадр передаётся целиком — так требует <c>docs/protocol.md</c> (п. 3.6).
/// Приёмник сам вырезает заголовок и строит из него <c>csd-0</c> для MediaCodec.
/// </remarks>
public readonly record struct AdtsFrame(AdtsHeader Header, byte[] Raw)
{
    /// <summary>Сжатые данные без заголовка — то, что принимает MediaCodec.</summary>
    public ReadOnlySpan<byte> Payload => Raw.AsSpan(Header.HeaderLength);

    /// <summary>Длительность кадра в микросекундах при данной частоте дискретизации.</summary>
    public long DurationMicros => AdtsFrameReader.SamplesPerFrame * 1_000_000L / Header.SampleRate;
}
