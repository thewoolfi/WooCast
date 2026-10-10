using System.Diagnostics.CodeAnalysis;

namespace WooCast.Protocol.Media;

/// <summary>
/// Делит непрерывный поток байт H.264 (Annex-B) на access unit'ы по границам AUD (NAL тип 9).
/// </summary>
/// <remarks>
/// Отправитель обязан включать AUD — это позволяет делить поток без разбора slice-заголовков
/// (см. <c>docs/protocol.md</c>, п. 3.3). Класс устойчив к произвольному дроблению входных
/// данных: старт-код, разорванный между двумя вызовами <see cref="Append"/>, будет найден.
/// <para>
/// Если AUD в потоке нет, память не растёт бесконечно: при превышении лимита буфер принудительно
/// режется по последнему найденному старт-коду, а кадр помечается <see cref="AccessUnit.ForcedSplit"/>.
/// </para>
/// </remarks>
public sealed class AnnexBSplitter
{
    private readonly int _maxAccessUnitBytes;
    private readonly Queue<AccessUnit> _ready = new();

    private byte[] _buffer;
    private int _length;
    private int _scanPosition;
    private int _accessUnitStart = -1;

    /// <param name="maxAccessUnitBytes">
    /// Предел размера одного access unit'а. Должен совпадать с ограничением медиа-пакета.
    /// </param>
    public AnnexBSplitter(int maxAccessUnitBytes = Wcp.MaxMediaPayload)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAccessUnitBytes, 64 * 1024);

        _maxAccessUnitBytes = maxAccessUnitBytes;
        _buffer = new byte[Math.Min(maxAccessUnitBytes, 512 * 1024)];
    }

    /// <summary>Сколько access unit'ов готово к выдаче.</summary>
    public int ReadyCount => _ready.Count;

    /// <summary>Признак того, что в потоке ещё ни разу не встретился AUD.</summary>
    public bool AudMissing => _accessUnitStart >= 0 && _lastAudSeen == 0;

    private long _lastAudSeen;

    /// <summary>Добавить очередную порцию байт из потока кодировщика.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        EnsureCapacity(_length + data.Length);
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;

        Scan();
    }

    /// <summary>Забрать готовый access unit, если он есть.</summary>
    public bool TryDequeue([NotNullWhen(true)] out AccessUnit? unit)
    {
        if (_ready.Count > 0)
        {
            unit = _ready.Dequeue();
            return true;
        }

        unit = null;
        return false;
    }

    /// <summary>Завершить поток: отдать последний незакрытый access unit.</summary>
    public void Flush()
    {
        if (_accessUnitStart >= 0 && _length > _accessUnitStart)
        {
            Emit(_accessUnitStart, _length);
            _accessUnitStart = -1;
        }
    }

    private void Scan()
    {
        // Отступаем на 3 байта назад: старт-код мог разорваться между двумя Append.
        var i = Math.Max(0, _scanPosition - 3);

        while (i + 3 <= _length)
        {
            var startCode = H264Util.StartCodeLength(_buffer.AsSpan(0, _length), i);
            if (startCode == 0)
            {
                i++;
                continue;
            }

            var headerIndex = i + startCode;
            if (headerIndex >= _length)
            {
                break; // Заголовок NAL ещё не пришёл — подождём следующих байт.
            }

            if ((_buffer[headerIndex] & 0x1F) == H264Util.NalTypeAud)
            {
                if (_accessUnitStart < 0)
                {
                    // Первый AUD: всё, что было до него (обычно SPS/PPS), относим к этому же кадру.
                    _accessUnitStart = 0;
                }
                else if (i > _accessUnitStart)
                {
                    Emit(_accessUnitStart, i);
                    _accessUnitStart = i;
                }

                _lastAudSeen++;
            }

            i = headerIndex + 1;
        }

        _scanPosition = _length;
        GuardAgainstRunaway();
        Compact();
    }

    /// <summary>
    /// Страховка от потока без AUD: не даём буферу расти без ограничений.
    /// </summary>
    private void GuardAgainstRunaway()
    {
        if (_length <= _maxAccessUnitBytes)
        {
            return;
        }

        var cut = _accessUnitStart < 0 ? 0 : _accessUnitStart;

        // Ищем последний старт-код, чтобы не разрезать NAL посередине.
        var boundary = -1;
        for (var i = _length - 4; i > cut; i--)
        {
            if (H264Util.StartCodeLength(_buffer.AsSpan(0, _length), i) > 0)
            {
                boundary = i;
                break;
            }
        }

        if (boundary <= cut)
        {
            boundary = _length; // Совсем нет старт-кодов — отдаём как есть.
        }

        Emit(cut, boundary, forced: true);
        _accessUnitStart = boundary < _length ? boundary : -1;
    }

    private void Emit(int from, int to, bool forced = false)
    {
        if (to <= from)
        {
            return;
        }

        var data = _buffer.AsSpan(from, to - from).ToArray();
        H264Util.Analyze(data, out var isKeyframe, out var hasParameterSets);

        _ready.Enqueue(new AccessUnit
        {
            Data = data,
            IsKeyframe = isKeyframe,
            HasParameterSets = hasParameterSets,
            ForcedSplit = forced,
        });
    }

    private void Compact()
    {
        if (_accessUnitStart <= 0)
        {
            return;
        }

        var remaining = _length - _accessUnitStart;
        if (remaining > 0)
        {
            Buffer.BlockCopy(_buffer, _accessUnitStart, _buffer, 0, remaining);
        }

        _length = remaining;
        _scanPosition = Math.Max(0, _scanPosition - _accessUnitStart);
        _accessUnitStart = 0;
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
            size = (int)Math.Min((long)size * 2, _maxAccessUnitBytes + 1024L * 1024);
        }

        Array.Resize(ref _buffer, size);
    }
}
