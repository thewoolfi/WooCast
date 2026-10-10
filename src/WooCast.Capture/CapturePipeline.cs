using System.Diagnostics;
using System.Threading.Channels;
using WooCast.Protocol;
using WooCast.Protocol.Media;

namespace WooCast.Capture;

/// <summary>Замер производительности конвейера за период между двумя выборками.</summary>
/// <param name="Fps">Измеренная частота кадров.</param>
/// <param name="Kbps">Битрейт за тот же период, кбит/с.</param>
/// <param name="Frames">Всего кадров с начала захвата.</param>
/// <param name="Bytes">Всего байт с начала захвата.</param>
/// <param name="Keyframes">Всего ключевых кадров.</param>
/// <param name="Restarts">Сколько раз кодировщик перезапускался ради ключевого кадра.</param>
public readonly record struct CaptureRate(
    double Fps,
    int Kbps,
    long Frames,
    long Bytes,
    long Keyframes,
    int Restarts);

/// <summary>Телеметрия конвейера захвата.</summary>
/// <remarks>
/// Частота кадров и битрейт считаются как «сколько кадров и байт прошло за окно измерения»,
/// а не как среднее мгновенных значений. Усреднять обратные интервалы нельзя: ffmpeg отдаёт
/// кадры неравномерными пачками, и среднее от <c>1/dt</c> оказывается на порядки выше правды.
/// </remarks>
public sealed class CaptureStats
{
    private long _frames;
    private long _keyframes;
    private long _bytes;
    private long _windowFrames;
    private long _windowBytes;
    private long _windowStart;
    private double _lastFps;
    private int _lastKbps;
    private int _restarts;

    public long Frames => Interlocked.Read(ref _frames);

    public long Keyframes => Interlocked.Read(ref _keyframes);

    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Частота кадров по последнему окну измерения.</summary>
    public double Fps => Volatile.Read(ref _lastFps);

    /// <summary>Битрейт по последнему окну измерения, кбит/с.</summary>
    public int Kbps => Volatile.Read(ref _lastKbps);

    /// <summary>Сколько раз поток пришлось перезапустить (принудительный ключевой кадр).</summary>
    public int Restarts => Volatile.Read(ref _restarts);

    internal void OnFrame(int bytes, bool keyframe)
    {
        Interlocked.Increment(ref _frames);
        Interlocked.Increment(ref _windowFrames);
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Add(ref _windowBytes, bytes);

        if (keyframe)
        {
            Interlocked.Increment(ref _keyframes);
        }
    }

    internal void OnRestart() => Interlocked.Increment(ref _restarts);

    /// <summary>
    /// Снять показания за прошедшее окно. Вызывается раз в секунду из цикла телеметрии,
    /// поэтому окно совпадает с периодом обновления интерфейса.
    /// </summary>
    public CaptureRate Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var frames = Interlocked.Exchange(ref _windowFrames, 0);
        var bytes = Interlocked.Exchange(ref _windowBytes, 0);
        var start = Interlocked.Exchange(ref _windowStart, now);

        if (start != 0)
        {
            var seconds = Stopwatch.GetElapsedTime(start, now).TotalSeconds;
            if (seconds > 0.05)
            {
                Volatile.Write(ref _lastFps, frames / seconds);
                Volatile.Write(ref _lastKbps, (int)(bytes * 8 / 1000 / seconds));
            }
        }

        return new CaptureRate(
            Fps,
            Kbps,
            Frames,
            Bytes,
            Keyframes,
            Restarts);
    }
}

/// <summary>
/// Конвейер «ffmpeg → access unit'ы»: читает stdout кодировщика, режет поток по AUD,
/// проставляет метки времени и отдаёт кадры через ограниченную очередь.
/// </summary>
/// <remarks>
/// Очередь ограничена четырьмя кадрами намеренно. Если потребитель (сеть) не успевает,
/// запись в очередь блокируется, читатель перестаёт вычитывать stdout, pipe забивается,
/// и ffmpeg начинает пропускать кадры захвата. Задержка при этом не растёт — снижается
/// только частота кадров, что для зеркалирования экрана правильнее накопления буфера.
/// </remarks>
public sealed class CapturePipeline : IAsyncDisposable
{
    private const int QueueCapacity = 4;
    private const int ReadBufferSize = 256 * 1024;

    /// <summary>Как часто проверяется положение окна.</summary>
    private const int FollowIntervalMs = 700;

    /// <summary>Сдвиг меньше этого числа пикселей считается дрожанием и игнорируется.</summary>
    private const int FollowTolerancePx = 8;

    /// <summary>Сколько проверок подряд изменение должно устояться, прежде чем перезапускать захват.</summary>
    private const int FollowStableChecks = 2;

    /// <summary>Сколько проверок подряд окно может отсутствовать, прежде чем считать его закрытым.</summary>
    private const int WindowGoneChecks = 3;

    /// <summary>Параметры захвата. Меняются при автослежении за окном.</summary>
    private CaptureOptions _options;

    private readonly CaptureCapabilities _capabilities;
    private readonly Channel<AccessUnit> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly CaptureStats _stats = new();

    private FfmpegCaptureSource? _source;
    private Task? _reader;
    private Task? _follow;
    private bool _disposed;

    private (int X, int Y, int Width, int Height)? _pendingBounds;
    private int _pendingCount;
    private int _goneCount;
    private bool _windowMinimized;

    private CapturePipeline(CaptureOptions options, CaptureCapabilities capabilities)
    {
        _options = options;
        _capabilities = capabilities;
        _channel = Channel.CreateBounded<AccessUnit>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    /// <summary>Строки stderr от ffmpeg.</summary>
    public event EventHandler<string>? LogLine;

    /// <summary>Поток неожиданно завершился: код возврата и последние строки журнала.</summary>
    public event EventHandler<int>? Faulted;

    /// <summary>Захват перестроен под новое положение окна.</summary>
    public event EventHandler<WindowTarget>? WindowFollowed;

    /// <summary>Окно закрыто или свёрнуто — транслировать больше нечего.</summary>
    public event EventHandler<string>? WindowLost;

    /// <summary>Источник сменил размер: поток нужно пересобрать.</summary>
    public event EventHandler<string>? Resized;

    public CaptureStats Stats => _stats;

    /// <summary>Сколько кадров ждёт отправки. Растущая очередь означает нехватку сети.</summary>
    public int QueueDepth => _channel.Reader.Count;

    /// <summary>Командная строка активного процесса ffmpeg.</summary>
    public string CommandLine => _source?.CommandLine ?? string.Empty;

    /// <summary>Действующие параметры захвата.</summary>
    public CaptureOptions CurrentOptions => _options;

    /// <summary>
    /// Применить новые параметры: кодировщик перезапускается, медиа-соединение сохраняется.
    /// Используется адаптивной подстройкой, когда приёмник перестаёт успевать.
    /// </summary>
    public async Task ApplyOptionsAsync(CaptureOptions options, CancellationToken cancellationToken)
    {
        _options = options;
        await RestartSourceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Запустить конвейер.</summary>
    public static CapturePipeline Start(CaptureOptions options, CaptureCapabilities capabilities)
    {
        var pipeline = new CapturePipeline(options, capabilities);
        pipeline.StartSource();
        pipeline._reader = Task.Run(() => pipeline.ReadLoopAsync(pipeline._cts.Token));

        if (options.SourceKind == CaptureSourceKind.Window && options.Window is not null)
        {
            pipeline._follow = Task.Run(() => pipeline.FollowWindowLoopAsync(pipeline._cts.Token));
        }

        return pipeline;
    }

    /// <summary>Читать access unit'ы по мере поступления.</summary>
    public IAsyncEnumerable<AccessUnit> ReadFramesAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Форсировать ключевой кадр. Реализовано перезапуском кодировщика: ffmpeg не позволяет
    /// запросить IDR в работающем процессе без внешнего API, а перезапуск занимает ~150 мс
    /// и не разрывает медиа-соединение.
    /// </summary>
    public Task ForceKeyframeAsync(CancellationToken cancellationToken) =>
        RestartSourceAsync(cancellationToken);

    /// <summary>Перезапустить кодировщик, сохранив медиа-соединение.</summary>
    private async Task RestartSourceAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var previous = _source;
        _source = null;

        if (previous is not null)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        _stats.OnRestart();
        StartSource();
    }

    /// <summary>
    /// Слежение за окном: при перемещении или изменении размера захват перестраивается,
    /// при закрытии — трансляция завершается с понятной причиной.
    /// </summary>
    /// <remarks>
    /// Изменение должно устояться несколько проверок подряд: иначе перетаскивание окна
    /// превратилось бы в непрерывную череду перезапусков кодировщика.
    /// </remarks>
    private async Task FollowWindowLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FollowIntervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var target = _options.Window;
            if (target is null)
            {
                continue;
            }

            // При системном захвате следить за прямоугольником окна не нужно: захват идёт
            // по содержимому, а не по области экрана. Перемещение окна ничего не меняет,
            // и перезапускать поток из-за него — только терять кадры на новом ключевом.
            if (WantsWindowCapture(_options))
            {
                // Свёрнутое окно система не отрисовывает, и кадры прекращаются. Сообщаем
                // об этом один раз на каждое изменение: молча замершая картинка выглядит
                // как поломка, а на самом деле достаточно развернуть окно.
                var minimized = Windows.IsMinimized(target.Handle);
                if (minimized != _windowMinimized)
                {
                    _windowMinimized = minimized;
                    LogLine?.Invoke(
                        this,
                        minimized
                            ? "Окно свёрнуто: система его не отрисовывает, картинка приостановлена до разворота"
                            : "Окно развёрнуто: захват продолжился");
                }

                continue;
            }

            if (!Windows.IsWindowUsable(target.Handle))
            {
                if (++_goneCount >= WindowGoneChecks)
                {
                    WindowLost?.Invoke(this, target.Describe());
                    _channel.Writer.TryComplete();
                    return;
                }

                continue;
            }

            _goneCount = 0;

            var bounds = Windows.GetBounds(target.Handle);
            if (bounds is null)
            {
                continue;
            }

            var (x, y, width, height) = bounds.Value;

            var unchanged = Math.Abs(x - target.X) <= FollowTolerancePx
                            && Math.Abs(y - target.Y) <= FollowTolerancePx
                            && Math.Abs(width - target.Width) <= FollowTolerancePx
                            && Math.Abs(height - target.Height) <= FollowTolerancePx;

            if (unchanged)
            {
                _pendingCount = 0;
                _pendingBounds = null;
                continue;
            }

            if (_pendingBounds == (x, y, width, height))
            {
                _pendingCount++;
            }
            else
            {
                _pendingBounds = (x, y, width, height);
                _pendingCount = 1;
            }

            if (_pendingCount < FollowStableChecks)
            {
                continue;
            }

            _pendingCount = 0;
            _pendingBounds = null;

            var displays = Displays.Enumerate();
            var info = new WindowInfo(target.Handle, target.Title, target.ProcessName, x, y, width, height);
            var (displayIndex, _, _, _, _) = Windows.MapToDisplay(info, displays);
            var display = displays.FirstOrDefault(d => d.Index == displayIndex, displays[0]);

            var updated = target with
            {
                X = x,
                Y = y,
                Width = width,
                Height = height,
                DisplayIndex = display.Index,
                DisplayOriginX = display.X,
                DisplayOriginY = display.Y,
            };

            _options = _options.WithWindow(updated);

            try
            {
                await RestartSourceAsync(cancellationToken).ConfigureAwait(false);
                WindowFollowed?.Invoke(this, updated);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
            {
                Faulted?.Invoke(this, -1);
                _channel.Writer.TryComplete();
                return;
            }
        }
    }

    private void StartSource()
    {
        var source = StartSourceCore();

        source.LogLine += (_, line) => LogLine?.Invoke(this, line);
        source.Exited += (_, code) =>
        {
            // Завершение по нашей инициативе (перезапуск/остановка) сюда не попадает:
            // к этому моменту _source уже заменён или обнулён.
            if (ReferenceEquals(_source, source))
            {
                Faulted?.Invoke(this, code);
            }
        };

        // Окно изменило размер: кодировщику размер сообщён при запуске, поэтому поток
        // пересобирается с нуля — так же, как при смене области захвата.
        source.SourceResized += (_, reason) => Resized?.Invoke(this, reason);

        // Окно закрыто: системный захват сообщает об этом сам, следить за прямоугольником
        // (как в прежнем способе) не нужно.
        source.SourceClosed += (_, reason) => WindowLost?.Invoke(this, reason);

        _source = source;
    }

    /// <summary>
    /// Выбрать способ захвата: системный для окна, прежний — во всех прочих случаях.
    /// </summary>
    /// <remarks>
    /// Системный захват отдаёт содержимое окна независимо от перекрытия, поэтому для
    /// отдельного приложения он предпочтителен. Но он доступен не всегда: его может
    /// не быть в системе, окно может его не поддерживать, а первый кадр может не прийти.
    /// Любой из этих случаев — не ошибка, а повод вернуться к обрезке снимка экрана.
    /// </remarks>
    private FfmpegCaptureSource StartSourceCore()
    {
        if (WantsWindowCapture(_options))
        {
            var windowSource = FfmpegCaptureSource.TryStartWindow(_options, _capabilities, out var error);

            if (windowSource is not null)
            {
                // Курсор при системном захвате рисует система, а не ffmpeg: если
                // настройка не применилась, в кадре его не будет ни при каких условиях.
                LogLine?.Invoke(
                    this,
                    "Захват окна: системный (содержимое окна целиком), " +
                    $"курсор {(_options.DrawCursor ? "показывается" : "скрыт")}");

                return windowSource;
            }

            LogLine?.Invoke(this, $"Системный захват окна недоступен ({error}) — работаю прежним способом");
        }

        var source = FfmpegCaptureSource.Start(_options, _capabilities);
        LogLine?.Invoke(this, $"Захват окна: {FfmpegArguments.DescribeCapture(_options, _capabilities)}");
        return source;
    }

    /// <summary>Нужен ли системный захват окна для текущих параметров.</summary>
    private static bool WantsWindowCapture(CaptureOptions options) =>
        options.SourceKind == CaptureSourceKind.Window &&
        options.Window is { Handle: not 0 } &&
        WindowCaptureSupport.IsSupported();

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var splitter = new AnnexBSplitter();
        var buffer = new byte[ReadBufferSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var source = _source;
                if (source is null)
                {
                    // Идёт перезапуск ради ключевого кадра — ждём новый процесс.
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                int read;
                try
                {
                    read = await source.Output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    read = 0;
                }

                if (read == 0)
                {
                    if (ReferenceEquals(source, _source))
                    {
                        break; // Поток закрылся и процесс не перезапускается.
                    }

                    continue;
                }

                splitter.Append(buffer.AsSpan(0, read));

                while (splitter.TryDequeue(out var unit))
                {
                    // Метка времени берётся с монотонных часов в момент получения кадра от
                    // кодировщика. Приёмник сравнивает её со своими часами (расхождение
                    // оценивается через ping/pong) и так показывает реальную сквозную задержку.
                    unit.PtsMicros = MonotonicClock.TimestampMicros;

                    _stats.OnFrame(unit.Length, unit.IsKeyframe);
                    await _channel.Writer.WriteAsync(unit, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();
        _cts.Cancel();

        if (_source is not null)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var task in new[] { _reader, _follow })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}
