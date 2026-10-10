using System.Diagnostics;
using System.Globalization;
using WooCast.Capture;

namespace WooCast.Cli;

/// <summary>
/// Команда <c>capture-window</c>: проверка системного захвата окна.
/// </summary>
/// <remarks>
/// Захватывает выбранное окно через Windows.Graphics.Capture и сохраняет кадр в файл
/// средствами ffmpeg. Это проверка того же пути, которым пойдёт трансляция: пиксели
/// снимаются системой, копируются в память и уходят в кодировщик потоком.
/// <para>
/// Смысл проверки в том, что окно можно перекрыть другим — правильный захват покажет
/// содержимое выбранного окна, а прежний способ (обрезка снимка экрана) показал бы то,
/// что лежит сверху.
/// </para>
/// </remarks>
internal static class CaptureWindowCommand
{
    public static int Run(string[] args)
    {
        var options = ParseArguments(args);

        if (options is null)
        {
            PrintUsage();
            return 2;
        }

        ConsoleUi.Header("Проверка системного захвата окна");

        var windows = Capture.Windows.Enumerate(Environment.ProcessId);
        if (windows.Count == 0)
        {
            ConsoleUi.Error("Окна не найдены");
            return 2;
        }

        var target = FindWindow(windows, options);
        if (target is null)
        {
            ConsoleUi.Error($"Окно не найдено: {options.Title ?? options.Index?.ToString(CultureInfo.InvariantCulture)}");
            PrintWindows(windows);
            return 2;
        }

        var window = target.Value;
        ConsoleUi.Detail("Окно", $"{window.ProcessName} — {window.Title}");
        ConsoleUi.Detail("Размер окна", $"{window.Width}×{window.Height}");
        ConsoleUi.Detail("Системный захват", WindowCaptureSupport.IsSupported() ? "доступен" : "недоступен");

        var frames = new List<WindowFrame>();
        var closed = false;

        using var capture = WindowCapture.TryStart(
            window.Handle,
            frame =>
            {
                lock (frames)
                {
                    // Для проверки достаточно первого кадра, но считаем все: по их числу
                    // видно, идёт ли поток вообще.
                    if (frames.Count == 0)
                    {
                        frames.Add(frame);
                    }
                    else
                    {
                        frames[0] = frames[0] with { ArrivedAt = frame.ArrivedAt };
                        frames.Add(frame);
                    }
                }
            },
            out var error,
            _ => closed = true,
            warning => ConsoleUi.Warn(warning));

        if (capture is null)
        {
            ConsoleUi.Error($"Не удалось начать захват: {error}");
            return 1;
        }

        ConsoleUi.Success("Захват начат");

        var deadline = DateTime.UtcNow.AddSeconds(options.Seconds);
        WindowFrame? first = null;

        while (DateTime.UtcNow < deadline && !closed)
        {
            lock (frames)
            {
                if (frames.Count > 0 && first is null)
                {
                    first = frames[0];
                }
            }

            Thread.Sleep(50);
        }

        var taken = capture.FramesTaken;
        var skipped = capture.FramesSkipped;

        ConsoleUi.Detail("Событий кадра", capture.FrameEvents.ToString(CultureInfo.InvariantCulture));
        ConsoleUi.Detail("Пустых опросов", capture.EmptyPolls.ToString(CultureInfo.InvariantCulture));
        ConsoleUi.Detail("Кадров получено", taken.ToString(CultureInfo.InvariantCulture));
        ConsoleUi.Detail("Кадров пропущено", skipped.ToString(CultureInfo.InvariantCulture));

        if (skipped > 0 && taken > 0)
        {
            var rate = (double)taken / (taken + skipped) * 100;
            ConsoleUi.Detail("Доля принятых", $"{rate:F1} %");
        }

        if (first is null)
        {
            if (capture.LastError is { Length: > 0 } lastError)
            {
                ConsoleUi.Error($"Обработка кадра падает: {lastError}");
            }
            else
            {
                ConsoleUi.Error("Ни одного кадра не получено");
            }

            return 1;
        }

        var frame0 = first.Value;
        ConsoleUi.Detail("Размер кадра", $"{frame0.Width}×{frame0.Height}");

        if (options.Output is not null)
        {
            return SaveFrame(frame0, options.Output) ? 0 : 1;
        }

        return 0;
    }

    private static bool SaveFrame(WindowFrame frame, string output)
    {
        var ffmpeg = FfmpegLocator.Find();
        if (ffmpeg is null)
        {
            ConsoleUi.Error("ffmpeg не найден — сохранить кадр нельзя");
            return false;
        }

        var fullPath = Path.GetFullPath(output);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var arguments = string.Join(
            ' ',
            "-hide_banner",
            "-loglevel error",
            "-f rawvideo",
            "-pix_fmt bgra",
            $"-s {frame.Width}x{frame.Height}",
            "-framerate 30",
            "-i -",
            "-frames:v 1",
            "-y",
            Quote(fullPath));

        var startInfo = new ProcessStartInfo(ffmpeg, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            ConsoleUi.Error("Не удалось запустить ffmpeg");
            return false;
        }

        var errors = process.StandardError.ReadToEndAsync();

        try
        {
            process.StandardInput.BaseStream.Write(frame.Pixels, 0, frame.Pixels.Length);
            process.StandardInput.BaseStream.Flush();
            process.StandardInput.Close();
        }
        catch (IOException ex)
        {
            ConsoleUi.Error($"Кодировщик закрыл вход: {ex.Message}");
        }

        process.WaitForExit(30_000);
        var errorText = errors.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            ConsoleUi.Error($"ffmpeg завершился с кодом {process.ExitCode}: {errorText.Trim()}");
            return false;
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length == 0)
        {
            ConsoleUi.Error("Файл не создан");
            return false;
        }

        ConsoleUi.Success($"Кадр сохранён: {fullPath} ({info.Length / 1024.0:F0} КБ)");
        return true;
    }

    private static string Quote(string value) => $"\"{value}\"";

    private static WindowInfo? FindWindow(IReadOnlyList<WindowInfo> windows, Options options)
    {
        if (options.Index is { } index)
        {
            return index >= 0 && index < windows.Count ? windows[index] : null;
        }

        if (options.Title is null)
        {
            return null;
        }

        foreach (var window in windows)
        {
            if (window.Title.Contains(options.Title, StringComparison.OrdinalIgnoreCase) ||
                window.ProcessName.Contains(options.Title, StringComparison.OrdinalIgnoreCase))
            {
                return window;
            }
        }

        return null;
    }

    private static void PrintWindows(IReadOnlyList<WindowInfo> windows)
    {
        ConsoleUi.Info();
        ConsoleUi.Info("Доступные окна:");

        for (var i = 0; i < windows.Count; i++)
        {
            ConsoleUi.Info($"  [{i,2}]  {windows[i].Label}");
        }
    }

    private static Options? ParseArguments(string[] args)
    {
        var options = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--title" when i + 1 < args.Length:
                    options.Title = args[++i];
                    break;

                case "--index" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], CultureInfo.InvariantCulture, out var index))
                    {
                        ConsoleUi.Error("Номер окна должен быть числом");
                        return null;
                    }

                    options.Index = index;
                    break;

                case "--seconds" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
                    {
                        ConsoleUi.Error("Длительность должна быть положительным числом");
                        return null;
                    }

                    options.Seconds = seconds;
                    break;

                case "--out" when i + 1 < args.Length:
                    options.Output = args[++i];
                    break;

                default:
                    ConsoleUi.Error($"Неизвестный параметр: {args[i]}");
                    return null;
            }
        }

        if (options.Title is null && options.Index is null)
        {
            ConsoleUi.Error("Укажите окно: --title <часть заголовка> или --index <номер>");
            return null;
        }

        return options;
    }

    private static void PrintUsage()
    {
        ConsoleUi.Info();
        ConsoleUi.Info("Проверка системного захвата окна:");
        ConsoleUi.Command("woocast capture-window --title \"Заголовок\" [--seconds 3] [--out frame.png]");
        ConsoleUi.Command("woocast capture-window --index 3 [--seconds 3] [--out frame.png]");
        ConsoleUi.Info();
        ConsoleUi.Info("Кадр сохраняется через ffmpeg — тем же путём, каким идёт трансляция.");
    }

    private sealed class Options
    {
        public string? Title { get; set; }

        public int? Index { get; set; }

        public double Seconds { get; set; } = 3;

        public string? Output { get; set; }
    }
}
