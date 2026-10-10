using System.Diagnostics;
using System.Threading.Channels;
using WooCast.Protocol;
using WooCast.Protocol.Media;

namespace WooCast.Capture;

/// <summary>Один закодированный кадр звука, готовый к отправке.</summary>
/// <param name="AdtsData">Полный ADTS-кадр вместе с заголовком.</param>
/// <param name="PtsMicros">Метка времени на общих с видео монотонных часах.</param>
public readonly record struct AudioFrame(byte[] AdtsData, long PtsMicros);

/// <summary>Телеметрия звукового конвейера.</summary>
public sealed class AudioStats
{
    private long _frames;
    private long _bytes;

    public long Frames => Interlocked.Read(ref _frames);

    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Частота дискретизации закодированного потока.</summary>
    public int SampleRate { get; internal set; } = 48_000;

    /// <summary>Пиковый уровень исходного звука, 0..1.</summary>
    public float PeakLevel { get; internal set; }

    internal void OnFrame(int bytes)
    {
        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _bytes, bytes);
    }
}

/// <summary>
/// Захват и кодирование системного звука: WASAPI loopback → ffmpeg (AAC-LC) → кадры ADTS.
/// </summary>
/// <remarks>
/// Отдельный процесс ffmpeg, а не общий с видео: видео пишется в stdout как raw H.264,
/// и примешать к нему звук можно было бы только через контейнер, который пришлось бы
/// разбирать на стороне приёмника. Два независимых потока проще и не мешают друг другу.
/// <para>
/// Метки времени считаются по счётчику сэмплов, а не по моменту чтения: один кадр AAC —
/// это всегда 1024 сэмпла, поэтому шкала получается ровной, без дрожания от буферизации.
/// Начало шкалы привязано к тем же монотонным часам, что и видео, — это и есть основа
/// синхронизации звука с изображением.
/// </para>
/// </remarks>
public sealed class AudioCapturePipeline : IAsyncDisposable
{
    private const int QueueCapacity = 24;

    private readonly AudioOptions _options;
    private readonly WasapiLoopbackSource _source;
    private readonly Process _process;
    private readonly Channel<AudioFrame> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly AudioStats _stats = new();

    private Task? _feedTask;
    private Task? _readTask;
    private Task? _logTask;
    private bool _disposed;

    private AudioCapturePipeline(
        AudioOptions options,
        WasapiLoopbackSource source,
        Process process)
    {
        _options = options;
        _source = source;
        _process = process;

        _channel = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    /// <summary>Строки stderr от ffmpeg.</summary>
    public event EventHandler<string>? LogLine;

    /// <summary>Захват звука прервался: устройство вывода исчезло или сменилось.</summary>
    public event EventHandler<string>? Faulted;

    public AudioStats Stats => _stats;

    /// <summary>Пиковый уровень звука — для индикатора в интерфейсе.</summary>
    public float PeakLevel => _source.PeakLevel;

    /// <summary>Командная строка кодировщика звука.</summary>
    public string CommandLine { get; private set; } = string.Empty;

    /// <summary>
    /// Запустить захват и кодирование звука.
    /// </summary>
    /// <returns>
    /// Конвейер или <see langword="null"/>, если системный звук недоступен — например,
    /// нет устройства вывода или формат не поддерживается.
    /// </returns>
    public static AudioCapturePipeline? TryStart(AudioOptions options, string ffmpegPath)
    {
        if (!options.Enabled)
        {
            return null;
        }

        var source = WasapiLoopbackSource.TryCreate();
        if (source is null)
        {
            return null;
        }

        string pcmFormat;
        int sampleRate;
        int channels;

        try
        {
            (pcmFormat, sampleRate, channels) = source.DescribePcmFormat();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            source.Dispose();
            return null;
        }

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",

            // Вход: сырой PCM из захвата. Взаимодействие на stdin ffmpeg отключает сам,
            // потому что stdin занят данными.
            "-f", pcmFormat,
            "-ar", sampleRate.ToString(),
            "-ac", channels.ToString(),
            "-i", "-",

            // Выход: AAC-LC с заголовками ADTS — ровно то, что описано в протоколе.
            "-c:a", "aac",
            "-profile:a", "aac_low",
            "-b:a", $"{Math.Clamp(options.BitrateKbps, 32, 320)}k",
            "-ar", Math.Clamp(options.SampleRate, 8_000, 48_000).ToString(),
            "-ac", "2",
            "-flush_packets", "1",
            "-f", "adts",
            "-",
        };

        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffmpeg не запустился");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            source.Dispose();
            return null;
        }

        var pipeline = new AudioCapturePipeline(options, source, process)
        {
            CommandLine = BuildCommandLine(ffmpegPath, arguments),
        };

        // Захват намеренно не запускается здесь: вызывающий должен сначала подписаться
        // на события, иначе первые сообщения об ошибках кодировщика потеряются.
        return pipeline;
    }

    /// <summary>Читать закодированные кадры по мере поступления.</summary>
    public IAsyncEnumerable<AudioFrame> ReadFramesAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Начать захват и кодирование. Повторный вызов безопасен.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_feedTask is not null)
        {
            return;
        }

        _stats.SampleRate = _options.SampleRate;

        _source.Stopped += (_, message) => Faulted?.Invoke(this, message);
        _source.Start();

        _feedTask = Task.Run(() => FeedEncoderAsync(_cts.Token));
        _readTask = Task.Run(() => ReadEncoderAsync(_cts.Token));
        _logTask = Task.Run(() => PumpLogAsync(_cts.Token));
    }

    /// <summary>Строки stderr кодировщика — помогают понять, почему звук не идёт.</summary>
    private async Task PumpLogAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    LogLine?.Invoke(this, line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Перекачка PCM из захвата в кодировщик.</summary>
    private async Task FeedEncoderAsync(CancellationToken cancellationToken)
    {
        var stdin = _process.StandardInput.BaseStream;

        try
        {
            await foreach (var buffer in _source.Buffers.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await stdin.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Кодировщик закрылся — это штатное завершение при остановке.
        }
        finally
        {
            try
            {
                await stdin.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stdin.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Чтение ADTS-потока и нарезка на кадры.</summary>
    private async Task ReadEncoderAsync(CancellationToken cancellationToken)
    {
        var reader = new AdtsFrameReader();
        var buffer = new byte[16 * 1024];
        var stdout = _process.StandardOutput.BaseStream;

        // Начало шкалы времени звука — те же монотонные часы, что и у видео.
        var startMicros = MonotonicClock.TimestampMicros;
        long frameIndex = 0;
        var frameDurationMicros = AdtsFrameReader.SamplesPerFrame * 1_000_000L / Math.Max(8_000, _options.SampleRate);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stdout.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                reader.Append(buffer.AsSpan(0, read));

                while (reader.TryDequeue(out var frame))
                {
                    if (reader.LastHeader is { } header)
                    {
                        _stats.SampleRate = header.SampleRate;
                        frameDurationMicros = AdtsFrameReader.SamplesPerFrame * 1_000_000L / header.SampleRate;
                    }

                    var pts = startMicros + (frameIndex * frameDurationMicros);
                    frameIndex++;

                    _stats.OnFrame(frame.Raw.Length);
                    _channel.Writer.TryWrite(new AudioFrame(frame.Raw, pts));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string> arguments) =>
        $"\"{executable}\" {string.Join(' ', arguments)}";

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        _source.Stop();

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
        }

        foreach (var task in new[] { _feedTask, _readTask, _logTask })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        _source.Dispose();
        _process.Dispose();
        _channel.Writer.TryComplete();
        _cts.Dispose();
    }
}
