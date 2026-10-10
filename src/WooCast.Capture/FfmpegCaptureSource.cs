using System.Diagnostics;
using System.Text;

namespace WooCast.Capture;

/// <summary>Запущенный процесс ffmpeg, отдающий H.264 в стандартный вывод.</summary>
/// <remarks>
/// Кадры могут попадать в кодировщик двумя путями: ffmpeg захватывает их сам
/// (<c>ddagrab</c>/<c>gdigrab</c>) либо мы подаём их в стандартный вход. Второй путь
/// используется для системного захвата окна: он отдаёт содержимое окна независимо
/// от того, что его перекрывает.
/// </remarks>
public sealed class FfmpegCaptureSource : IAsyncDisposable
{
    /// <summary>Сколько ждать первый кадр окна, прежде чем считать захват несостоявшимся.</summary>
    private const int FirstFrameTimeoutMs = 2500;

    private readonly Process _process;
    private readonly CancellationTokenSource _logCts = new();
    private readonly object _pumpLock = new();

    private Task? _logPump;
    private bool _disposed;

    private WindowCapture? _windowCapture;
    private Stream? _input;
    private WindowFrame? _pendingFirst;
    private bool _pumpReady;
    private int _frameWidth;
    private int _frameHeight;
    private long _framesWritten;
    private string? _writeError;

    private FfmpegCaptureSource(Process process, string commandLine)
    {
        _process = process;
        CommandLine = commandLine;
    }

    /// <summary>Полная командная строка — для журнала и диагностики.</summary>
    public string CommandLine { get; }

    /// <summary>Поток с H.264 в формате Annex-B.</summary>
    public Stream Output => _process.StandardOutput.BaseStream;

    /// <summary>Строки из stderr: предупреждения и ошибки ffmpeg.</summary>
    public event EventHandler<string>? LogLine;

    /// <summary>Процесс завершился.</summary>
    public event EventHandler<int>? Exited;

    /// <summary>Захватываемое окно изменило размер: поток нужно перезапустить.</summary>
    public event EventHandler<string>? SourceResized;

    /// <summary>Захватываемое окно закрыто — транслировать больше нечего.</summary>
    public event EventHandler<string>? SourceClosed;

    /// <summary>Код возврата, если процесс уже завершился.</summary>
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    /// <summary>Сколько кадров окна подано в кодировщик.</summary>
    public long WindowFrames => Interlocked.Read(ref _framesWritten);

    /// <summary>Причина, по которой кадры перестали подаваться, если она была.</summary>
    public string? WriteError => _writeError;

    /// <summary>Запустить захват и кодирование.</summary>
    /// <exception cref="InvalidOperationException">ffmpeg не удалось запустить.</exception>
    public static FfmpegCaptureSource Start(CaptureOptions options, CaptureCapabilities capabilities)
    {
        var executable = options.FfmpegPath ?? capabilities.FfmpegPath;
        var arguments = FfmpegArguments.Build(options, capabilities);

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Не удалось запустить {executable}");
        }

        var source = new FfmpegCaptureSource(process, BuildCommandLine(executable, arguments));
        source.StartLogPump();
        return source;
    }

    /// <summary>
    /// Запустить кодирование кадров окна, снятых системным захватом.
    /// </summary>
    /// <remarks>
    /// Размер кадра кодировщику сообщают при запуске, поэтому сначала берётся первый
    /// кадр окна и только затем запускается ffmpeg. Если окно не отдаёт кадров —
    /// это не ошибка приложения, а повод вернуться к прежнему способу захвата.
    /// </remarks>
    /// <param name="options">Параметры захвата.</param>
    /// <param name="capabilities">Что доступно на этой машине.</param>
    /// <param name="error">Причина отказа, если начать не удалось.</param>
    public static FfmpegCaptureSource? TryStartWindow(
        CaptureOptions options,
        CaptureCapabilities capabilities,
        out string error)
    {
        error = string.Empty;

        var window = options.Window;
        if (window is null)
        {
            error = "окно не выбрано";
            return null;
        }

        var firstFrame = new TaskCompletionSource<WindowFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Закрытие окна приходит раньше, чем создан источник, поэтому уведомление
        // передаётся через изменяемую ссылку.
        Action<string>? closedRelay = null;

        var capture = WindowCapture.TryStart(
            window.Handle,
            frame => firstFrame.TrySetResult(frame),
            out var captureError,
            onClosed: reason => closedRelay?.Invoke(reason),
            onWarning: line => { },

            // Курсор при системном захвате рисует сама система: ffmpeg здесь только
            // кодирует готовые кадры и ничего не добавляет.
            showCursor: options.DrawCursor);

        if (capture is null)
        {
            error = captureError;
            return null;
        }

        WindowFrame first;
        try
        {
            if (!firstFrame.Task.Wait(FirstFrameTimeoutMs))
            {
                capture.Dispose();
                error = "окно не отдало ни одного кадра за отведённое время";
                return null;
            }

            first = firstFrame.Task.Result;
        }
        catch (AggregateException ex)
        {
            capture.Dispose();
            error = ex.InnerException?.Message ?? ex.Message;
            return null;
        }

        var source = StartRawProcess(options, capabilities, first.Width, first.Height);
        closedRelay = source.NotifyClosed;
        source.AttachWindowCapture(capture, first);
        return source;
    }

    private void NotifyClosed(string reason) => SourceClosed?.Invoke(this, reason);

    private static FfmpegCaptureSource StartRawProcess(
        CaptureOptions options,
        CaptureCapabilities capabilities,
        int width,
        int height)
    {
        var executable = options.FfmpegPath ?? capabilities.FfmpegPath;
        var arguments = FfmpegArguments.BuildForRawInput(options, capabilities, width, height);

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Не удалось запустить {executable}");
        }

        var source = new FfmpegCaptureSource(process, BuildCommandLine(executable, arguments));
        source.StartLogPump();
        return source;
    }

    private void AttachWindowCapture(WindowCapture capture, WindowFrame first)
    {
        _windowCapture = capture;
        _frameWidth = first.Width;
        _frameHeight = first.Height;

        var input = _process.StandardInput.BaseStream;

        lock (_pumpLock)
        {
            _input = input;
            _pendingFirst = first;
            _pumpReady = true;
        }

        capture.SetFrameHandler(OnWindowFrame);

        // Первый кадр отправляем сразу: кодировщик ждёт данные, а он уже снят.
        WriteFrame(first);
    }

    private void OnWindowFrame(WindowFrame frame)
    {
        if (_disposed)
        {
            return;
        }

        // Изменение размера окна ломает поток: кодировщику размер сообщён при запуске.
        // Перезапуск делает вышестоящий уровень — он умеет пересоздавать источник.
        if (frame.Width != _frameWidth || frame.Height != _frameHeight)
        {
            SourceResized?.Invoke(
                this,
                $"окно изменило размер: {_frameWidth}×{_frameHeight} → {frame.Width}×{frame.Height}");

            return;
        }

        if (!_pumpReady)
        {
            // Кодировщик ещё не запущен: держим только последний кадр, чтобы не копить память.
            lock (_pumpLock)
            {
                _pendingFirst = frame;
            }

            return;
        }

        WriteFrame(frame);
    }

    private void WriteFrame(WindowFrame frame)
    {
        var input = _input;
        if (input is null)
        {
            return;
        }

        try
        {
            // Запись блокирующая: если кодировщик не успевает, кадры начнут отбрасываться
            // на стороне захвата — это и есть нужное поведение, очередь не растёт.
            input.Write(frame.Pixels, 0, frame.Pixels.Length);
            Interlocked.Increment(ref _framesWritten);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Interlocked.CompareExchange(ref _writeError, $"{ex.GetType().Name}: {ex.Message}", null);
        }
    }

    /// <summary>Дождаться завершения процесса.</summary>
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    private void StartLogPump()
    {
        _logPump = Task.Run(async () =>
        {
            try
            {
                while (!_logCts.IsCancellationRequested)
                {
                    var line = await _process.StandardError.ReadLineAsync(_logCts.Token).ConfigureAwait(false);
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
            finally
            {
                if (_process.HasExited)
                {
                    Exited?.Invoke(this, _process.ExitCode);
                }
            }
        });
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        builder.Append('"').Append(executable).Append('"');

        foreach (var argument in arguments)
        {
            builder.Append(' ');
            builder.Append(argument.Contains(' ') || argument.Contains('"')
                ? '"' + argument.Replace("\"", "\\\"") + '"'
                : argument);
        }

        return builder.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logCts.Cancel();

        // Захват окна останавливается первым: иначе он продолжит писать в закрытый вход.
        _windowCapture?.Dispose();
        _windowCapture = null;

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.StandardInput.Close();
                }
                catch (Exception)
                {
                    // Вход мог быть уже закрыт.
                }

                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
        }

        _process.Dispose();
        _logCts.Dispose();
    }
}
