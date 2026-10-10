using System.Diagnostics;
using System.Threading.Channels;
using NAudio.Wave;

namespace WooCast.Capture;

/// <summary>Захват системного звука через WASAPI loopback.</summary>
/// <remarks>
/// ffmpeg не умеет WASAPI loopback, а DirectShow-источник требует включённого «Стерео микшера»,
/// которого на большинстве машин нет. Поэтому звук снимается прямо с устройства вывода:
/// это работает всегда, когда что-то играет, и не требует настроек системы.
/// <para>
/// Захват идёт в «миксе» устройства — обычно это 32-битный float 48 кГц; формат передаётся
/// кодировщику как есть, без промежуточного преобразования.
/// </para>
/// </remarks>
public sealed class WasapiLoopbackSource : IDisposable
{
    private readonly WasapiLoopbackCapture? _capture;
    private readonly Channel<byte[]> _queue;
    private readonly CancellationTokenSource _cts = new();
    private long _bytesCaptured;
    private float _peakLevel;
    private bool _disposed;

    private WasapiLoopbackSource(WasapiLoopbackCapture capture, int queueCapacity)
    {
        _capture = capture;
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    /// <summary>Формат, в котором приходит звук.</summary>
    public WaveFormat? Format => _capture?.WaveFormat;

    /// <summary>Сколько байт PCM захвачено — по этому числу видно, идёт ли звук вообще.</summary>
    public long BytesCaptured => Interlocked.Read(ref _bytesCaptured);

    /// <summary>Пиковый уровень последнего буфера, 0..1. Нужен для индикатора в интерфейсе.</summary>
    public float PeakLevel => Volatile.Read(ref _peakLevel);

    /// <summary>Захват остановлен системой: устройство вывода исчезло или сменилось.</summary>
    public event EventHandler<string>? Stopped;

    /// <summary>Очередь с сырыми PCM-буферами для кодировщика.</summary>
    public ChannelReader<byte[]> Buffers => _queue.Reader;

    /// <summary>Создать источник для устройства вывода по умолчанию.</summary>
    /// <returns><see langword="null"/>, если устройства вывода нет или WASAPI недоступен.</returns>
    public static WasapiLoopbackSource? TryCreate(int queueCapacity = 32)
    {
        try
        {
            var capture = new WasapiLoopbackCapture();
            return new WasapiLoopbackSource(capture, queueCapacity);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"[WooCast] WASAPI loopback недоступен: {ex.Message}");
            return null;
        }
    }

    /// <summary>Описание формата для ffmpeg: имя формата, частота, каналы.</summary>
    /// <exception cref="NotSupportedException">Формат устройства не поддерживается.</exception>
    public (string Format, int SampleRate, int Channels) DescribePcmFormat()
    {
        var format = Format ?? throw new InvalidOperationException("Формат захвата неизвестен");

        var name = format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 32 => "f32le",
            WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 64 => "f64le",
            WaveFormatEncoding.Pcm when format.BitsPerSample == 16 => "s16le",
            WaveFormatEncoding.Pcm when format.BitsPerSample == 24 => "s24le",
            WaveFormatEncoding.Pcm when format.BitsPerSample == 32 => "s32le",
            _ => throw new NotSupportedException(
                $"Формат звука устройства не поддерживается: {format.Encoding}, {format.BitsPerSample} бит"),
        };

        return (name, format.SampleRate, format.Channels);
    }

    /// <summary>Начать захват.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _capture!.StartRecording();
    }

    /// <summary>Остановить захват.</summary>
    public void Stop()
    {
        try
        {
            _capture?.StopRecording();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            // Захват уже остановлен.
        }

        _queue.Writer.TryComplete();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        var buffer = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);

        Interlocked.Add(ref _bytesCaptured, e.BytesRecorded);
        Volatile.Write(ref _peakLevel, ComputePeak(buffer));

        // Очередь ограничена: если кодировщик не успевает, старые буферы отбрасываются.
        // Для звука это правильнее накопления — иначе появится растущая задержка.
        _queue.Writer.TryWrite(buffer);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _queue.Writer.TryComplete();

        if (e.Exception is not null)
        {
            Stopped?.Invoke(this, e.Exception.Message);
        }
    }

    /// <summary>
    /// Пиковая амплитуда буфера, 0..1. Разбор зависит от формата устройства:
    /// чаще всего это 32-битный float, но встречается и целочисленный PCM.
    /// </summary>
    private float ComputePeak(byte[] buffer)
    {
        var format = Format;
        if (format is null)
        {
            return 0;
        }

        var peak = 0f;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (var i = 0; i + 4 <= buffer.Length; i += 4)
            {
                var value = Math.Abs(BitConverter.ToSingle(buffer, i));
                if (value > peak)
                {
                    peak = value;
                }
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (var i = 0; i + 2 <= buffer.Length; i += 2)
            {
                var value = Math.Abs(BitConverter.ToInt16(buffer, i)) / 32768f;
                if (value > peak)
                {
                    peak = value;
                }
            }
        }
        else if (format.BitsPerSample == 32)
        {
            for (var i = 0; i + 4 <= buffer.Length; i += 4)
            {
                var value = Math.Abs(BitConverter.ToInt32(buffer, i)) / 2147483648f;
                if (value > peak)
                {
                    peak = value;
                }
            }
        }

        return Math.Min(1f, peak);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;

            try
            {
                _capture.StopRecording();
            }
            catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
            {
            }

            _capture.Dispose();
        }

        _queue.Writer.TryComplete();
        _cts.Dispose();
    }
}
