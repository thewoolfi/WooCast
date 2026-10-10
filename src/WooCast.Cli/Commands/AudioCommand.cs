using System.Diagnostics;
using WooCast.Capture;

namespace WooCast.Cli;

/// <summary>
/// Команда <c>audio</c>: проверка захвата системного звука без трансляции.
/// </summary>
/// <remarks>
/// Отвечает на вопрос «слышит ли WooCast звук с этого компьютера»: показывает формат
/// устройства вывода, объём захваченных данных и уровень сигнала. Если во время работы
/// команды играет музыка, а уровень остаётся нулевым — значит, звук идёт мимо
/// устройства по умолчанию (например, через эксклюзивный режим или другой интерфейс).
/// </remarks>
internal static class AudioCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = CliArgs.Parse(args);
        var seconds = options.GetInt("seconds", 8);
        var dumpPath = options.GetString("dump");
        var bitrate = options.GetInt("bitrate", 160);

        ConsoleUi.Header("Проверка захвата системного звука");

        var ffmpeg = FfmpegLocator.Find();
        if (ffmpeg is null)
        {
            ConsoleUi.Error("ffmpeg не найден — запустите tools/fetch-ffmpeg.ps1");
            return 1;
        }

        var audioOptions = new AudioOptions { Enabled = true, BitrateKbps = bitrate };
        await using var pipeline = AudioCapturePipeline.TryStart(audioOptions, ffmpeg);

        if (pipeline is null)
        {
            ConsoleUi.Error("Системный звук недоступен");
            ConsoleUi.Info("  • проверьте, что в системе выбрано устройство воспроизведения");
            ConsoleUi.Info("  • если играет музыка, а захвата нет, устройство может быть занято монопольно");
            return 2;
        }

        FileStream? dump = null;
        if (dumpPath is not null)
        {
            dump = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        }

        pipeline.LogLine += (_, line) => ConsoleUi.Info($"  [ffmpeg] {line}");
        pipeline.Faulted += (_, message) => ConsoleUi.Warn($"Захват прерван: {message}");
        pipeline.Start();

        ConsoleUi.Success("Захват запущен");
        ConsoleUi.Info($"  Кодировщик: {pipeline.CommandLine}");
        ConsoleUi.Info();
        ConsoleUi.Info("  Включите музыку или любой звук — уровни должны ожить.");
        ConsoleUi.Info();

        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var frames = 0L;
        var bytes = 0L;
        var peakSeen = 0f;
        long dumped = 0;

        try
        {
            await foreach (var frame in pipeline.ReadFramesAsync(cancellationToken))
            {
                frames++;
                bytes += frame.AdtsData.Length;
                peakSeen = Math.Max(peakSeen, pipeline.PeakLevel);

                if (dump is not null)
                {
                    await dump.WriteAsync(frame.AdtsData, cancellationToken);
                    dumped += frame.AdtsData.Length;
                }

                if (stopwatch.Elapsed - lastReport >= TimeSpan.FromMilliseconds(500))
                {
                    lastReport = stopwatch.Elapsed;
                    var secondsPassed = stopwatch.Elapsed.TotalSeconds;
                    var kbps = secondsPassed > 0 ? bytes * 8 / 1000 / secondsPassed : 0;

                    ConsoleUi.Status(
                        $"  кадров {frames,5}  {kbps,4:F0} кбит/с  " +
                        $"уровень {LevelBar(pipeline.PeakLevel)}");
                }

                if (seconds > 0 && stopwatch.Elapsed.TotalSeconds >= seconds)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            ConsoleUi.EndStatus();

            if (dump is not null)
            {
                await dump.FlushAsync(CancellationToken.None);
                await dump.DisposeAsync();
                ConsoleUi.Info($"  Записано в {dumpPath} ({dumped / 1024.0:F0} КБ)");
            }
        }

        ConsoleUi.Header("Итоги");
        ConsoleUi.Detail("Кадров AAC", frames.ToString());
        ConsoleUi.Detail("Байт", bytes.ToString());
        ConsoleUi.Detail("Пиковый уровень", $"{peakSeen * 100:F1} %");

        if (frames == 0)
        {
            ConsoleUi.Error("Звук не захвачен");
            return 3;
        }

        if (peakSeen < 0.001f)
        {
            ConsoleUi.Warn("Захвачена тишина: похоже, на устройстве по умолчанию ничего не играло");
            ConsoleUi.Info("  Это не ошибка: поток формируется всегда, чтобы держать шкалу времени.");
            return 0;
        }

        ConsoleUi.Success($"Звук захватывается, пиковый уровень {peakSeen * 100:F0} %");

        if (dumpPath is not null)
        {
            ConsoleUi.Info($"  Проверить запись: ffmpeg -i \"{dumpPath}\" -af volumedetect -f null -");
        }

        return 0;
    }

    private static string LevelBar(float level)
    {
        var filled = (int)Math.Round(Math.Clamp(level, 0, 1) * 12);
        return "[" + new string('#', filled) + new string('.', 12 - filled) + "]";
    }
}
