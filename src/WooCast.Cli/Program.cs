using System.Text;
using WooCast.Protocol;

namespace WooCast.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // Вывод перенаправлен — кодировка не важна.
        }

        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var rest = args.Skip(1).ToArray();

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return command switch
            {
                "probe" => await ProbeCommand.RunAsync(rest, cancellation.Token),
                "report" => await ReportCommand.RunAsync(rest, cancellation.Token),
                "audio" => await AudioCommand.RunAsync(rest, cancellation.Token),
                "discover" => await DiscoverCommand.RunAsync(rest, cancellation.Token),
                "windows" => WindowsCommand.Run(rest),
                "capture-window" => CaptureWindowCommand.Run(rest),
                "gamepad-test" => GamepadTestCommand.Run(rest),
                "gamepad-watch" => GamepadWatchCommand.Run(rest),
                "update-check" => await UpdateCheckCommand.RunAsync(rest, cancellation.Token),
                "receive" => await ReceiveCommand.RunAsync(rest, cancellation.Token),
                "send" => await SendCommand.RunAsync(rest, cancellation.Token),
                "help" or "--help" or "-h" => PrintHelp(),
                _ => UnknownCommand(command),
            };
        }
        catch (OperationCanceledException)
        {
            ConsoleUi.EndStatus();
            ConsoleUi.Info("Прервано пользователем");
            return 130;
        }
        catch (Exception ex)
        {
            ConsoleUi.EndStatus();
            ConsoleUi.Error(ex.Message);

            if (ex.InnerException is not null)
            {
                ConsoleUi.Info($"  {ex.InnerException.Message}");
            }

            return 1;
        }
    }

    private static int UnknownCommand(string command)
    {
        ConsoleUi.Error($"Неизвестная команда: {command}");
        return PrintHelp();
    }

    private static int PrintHelp()
    {
        ConsoleUi.Header("WooCast — трансляция экрана ПК на Android TV");
        ConsoleUi.Info();
        ConsoleUi.Command("woocast probe");
        ConsoleUi.Info("    проверить окружение: ffmpeg, аппаратные кодировщики, сетевые адреса");
        ConsoleUi.Info();
        ConsoleUi.Command("woocast discover [--seconds N]");
        ConsoleUi.Info("    найти приставки в локальной сети");
        ConsoleUi.Info();
        ConsoleUi.Command("woocast windows");
        ConsoleUi.Info("    список окон, доступных для трансляции (с номерами)");
        ConsoleUi.Info();
        ConsoleUi.Command("woocast audio [--seconds N] [--dump file.aac]");
        ConsoleUi.Info("    проверить захват системного звука и показать уровень сигнала");
        ConsoleUi.Info();
        ConsoleUi.Command("woocast report");
        ConsoleUi.Info("    сводка о системе, сети и приставках — для разбора проблем с качеством");
        ConsoleUi.Info();
        ConsoleUi.Command("woocast send --to <ip[:порт]> [параметры]");
        ConsoleUi.Info("    транслировать экран или окно на приставку");
        ConsoleUi.Info("    --fps N               частота кадров (по умолчанию 60)");
        ConsoleUi.Info("    --size WxH            разрешение (по умолчанию 1920x1080)");
        ConsoleUi.Info("    --quality <профиль>   maximum | high | balanced | economy (по умолчанию high)");
        ConsoleUi.Info("    --bitrate N           битрейт в кбит/с (включает профиль custom)");
        ConsoleUi.Info("    --encoder <имя>       auto | nvenc | qsv | amf | software");
        ConsoleUi.Info("    --profile <профиль>   high | main | baseline");
        ConsoleUi.Info("    --display N           номер монитора, начиная с 0");
        ConsoleUi.Info("    --window-id N         транслировать окно с номером из `woocast windows`");
        ConsoleUi.Info("    --window \"фрагмент\"   найти окно по заголовку или имени процесса");
        ConsoleUi.Info("    --ffmpeg <путь>       явный путь к ffmpeg.exe");
        ConsoleUi.Info("    --gdigrab             отключить ddagrab и использовать gdigrab");
        ConsoleUi.Info("    --no-audio            не передавать системный звук");
        ConsoleUi.Info("    --audio-bitrate N     битрейт звука, кбит/с (по умолчанию 160)");
        ConsoleUi.Info("    --audio-delay N       сдвиг звука, мс: больше нуля — звук позже картинки");
        ConsoleUi.Info("    --seconds N           остановиться через N секунд");
        ConsoleUi.Info();
        ConsoleUi.Command("woocast receive [--port N] [--dump file.h264] [--dump-audio file.aac] [--seconds N]");
        ConsoleUi.Info("    тестовый приёмник: проверяет протокол без телевизора");
        ConsoleUi.Info("    --caps WxH@FPS        какие возможности декодера сообщать (по умолчанию 3840x2160@60)");
        ConsoleUi.Info("    --simulate-fps N      сообщать заниженную частоту декодирования");
        ConsoleUi.Info("    --simulate-seconds N  длить имитацию только N секунд, потом сообщать настоящую");
        ConsoleUi.Info("    --simulate-latency N  сообщать заданную задержку, мс");
        ConsoleUi.Info();
        ConsoleUi.Detail("Протокол", "WCP/1 — см. docs/protocol.md");
        ConsoleUi.Detail(
            "Порты",
            $"{Wcp.DiscoveryPort}/udp, {Wcp.ControlPort}/tcp, {Wcp.MediaPort}/tcp");
        ConsoleUi.Info();
        return 0;
    }
}
