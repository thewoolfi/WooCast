using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using WooCast.Capture;
using WooCast.Protocol;
using WooCast.Protocol.Net;
namespace WooCast.Cli;

/// <summary>
/// Команда <c>report</c>: сводка о системе и сети для разбора проблем с качеством.
/// </summary>
/// <remarks>
/// Отвечает на вопросы, которые иначе приходится выяснять перепиской: какой экран
/// и какое у него родное разрешение, какие кодировщики реально работают, с какой
/// скоростью соединён сетевой адаптер и что о себе сообщает приставка. Вывод
/// рассчитан на то, чтобы его целиком скопировали в обращение.
/// </remarks>
internal static class ReportCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = CliArgs.Parse(args);

        ConsoleUi.Header("WooCast — сводка о системе");

        ConsoleUi.Detail("Система", RuntimeInformation.OSDescription);
        ConsoleUi.Detail("Процессор", DescribeCpu());
        ConsoleUi.Detail("Версия WooCast", typeof(ReportCommand).Assembly.GetName().Version?.ToString() ?? "—");

        // --- Экраны ---------------------------------------------------------

        ConsoleUi.Header("Экраны");

        foreach (var display in Displays.Enumerate())
        {
            ConsoleUi.Detail(
                display.Name,
                $"{display.Width}×{display.Height} в точке {display.X},{display.Y}" +
                (display.IsPrimary ? ", основной" : string.Empty));
        }

        // --- Кодировщики ----------------------------------------------------

        ConsoleUi.Header("Кодировщики");

        var ffmpeg = FfmpegLocator.Find();
        if (ffmpeg is null)
        {
            ConsoleUi.Error("ffmpeg не найден — запустите tools/fetch-ffmpeg.ps1");
            return 1;
        }

        ConsoleUi.Detail("ffmpeg", ffmpeg);

        var capabilities = await EncoderProbe.DetectAsync(ffmpeg, cancellationToken);
        ConsoleUi.Detail("Версия", capabilities.FfmpegVersion);
        ConsoleUi.Detail("Захват", capabilities.HasDdagrab ? "ddagrab (Desktop Duplication)" : "gdigrab");
        ConsoleUi.Detail("Захват окна", WindowCaptureSupport.Describe());
        ConsoleUi.Detail("Драйвер геймпада", GamepadDriver.Describe());
        ConsoleUi.Detail("Установщик драйвера", GamepadDriver.FindInstaller() ?? "рядом с программой не найден");

        if (capabilities.WorkingEncoders.Count == 0)
        {
            ConsoleUi.Error("Ни один кодировщик H.264 не заработал");
        }
        else
        {
            foreach (var encoder in capabilities.WorkingEncoders)
            {
                var marker = encoder == capabilities.RecommendedEncoder ? " ← будет использован" : string.Empty;
                ConsoleUi.Detail(CaptureCapabilities.Describe(encoder), $"{CaptureCapabilities.FfmpegName(encoder)}{marker}");
            }
        }

        // --- Сеть -----------------------------------------------------------

        ConsoleUi.Header("Сеть");

        foreach (var adapter in DescribeNetworkAdapters())
        {
            ConsoleUi.Detail(adapter.Name, adapter.Description);
        }

        foreach (var address in NetworkUtil.GetLocalAddresses())
        {
            ConsoleUi.Detail(address.ToString(), NetworkUtil.IsPrivate(address) ? "локальный" : "внешний");
        }

        // --- Приставки ------------------------------------------------------

        ConsoleUi.Header("Приставки в сети");

        await using var listener = new DiscoveryListener(LocalDevice.Id, LocalDevice.Name);
        var found = new List<DiscoveredDevice>();

        listener.DeviceDiscovered += (_, device) =>
        {
            lock (found)
            {
                found.Add(device);
            }
        };

        listener.Start();
        await listener.ProbeAsync(cancellationToken);

        // Даём время на ответы: приставки отвечают на запрос в течение пары секунд.
        await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);

        // Снимок списка под замком, а измерения — уже без него: ждать внутри lock нельзя.
        List<DiscoveredDevice> devices;
        lock (found)
        {
            devices = [.. found];
        }

        if (devices.Count == 0)
        {
            ConsoleUi.Warn("Никто не отозвался");
            ConsoleUi.Info("  Проверьте, что приложение запущено на приставке и сеть не изолирует устройства.");
        }
        else
        {
            foreach (var device in devices)
            {
                ConsoleUi.Success($"{device.Name} — {device.EndPointText}");
                ConsoleUi.Detail("  модель", device.Subtitle);
                ConsoleUi.Detail("  версия", device.Version ?? "не сообщена");
                ConsoleUi.Detail("  состояние", device.IsBusy ? "занята" : "свободна");

                if (device.Caps is { } caps)
                {
                    ConsoleUi.Detail("  заявляет до", $"{caps.MaxWidth}×{caps.MaxHeight} @ {caps.MaxFps} к/с");

                    // Приставки сообщают пределы из системного списка возможностей декодера,
                    // и там встречаются числа вроде 4096×4096 при 960 к/с. Верить им как
                    // обещанию нельзя — поэтому так и написано.
                    if (caps.MaxFps > 120 || caps.MaxWidth > 3840)
                    {
                        ConsoleUi.Detail(
                            "  на деле",
                            "пределы завышены — реальная скорость подбирается по ходу трансляции");
                    }

                    var codecs = caps.Codecs;
                    ConsoleUi.Detail(
                        "  кодеки",
                        codecs is { Count: > 0 } ? string.Join(", ", codecs) : "не указаны");
                }
                else
                {
                    ConsoleUi.Detail("  заявляет до", "не сообщено");
                }

                await DescribeLatencyAsync(device, cancellationToken);
            }
        }

        ConsoleUi.Header("Что это значит");
        ConsoleUi.Info("  Скопируйте весь вывод целиком — по нему видно, где узкое место.");
        ConsoleUi.Info();

        return 0;
    }

    /// <summary>
    /// Измерить задержку до приставки.
    /// </summary>
    /// <remarks>
    /// Разброс задержки важнее среднего значения: ровные 5 мс и 5 мс «в среднем» при
    /// скачках до 200 — это разные каналы. Скачки означают Wi-Fi с помехами или
    /// загруженный участок, и именно они дают рывки картинки.
    /// </remarks>
    private static async Task DescribeLatencyAsync(DiscoveredDevice device, CancellationToken cancellationToken)
    {
        var samples = new List<long>();

        try
        {
            using var ping = new Ping();

            // Первый запрос часто теряется при разрешении адреса — он не в счёт.
            await ping.SendPingAsync(device.Address, TimeSpan.FromSeconds(2));

            for (var i = 0; i < 10 && !cancellationToken.IsCancellationRequested; i++)
            {
                var reply = await ping.SendPingAsync(device.Address, TimeSpan.FromSeconds(2));
                if (reply.Status == IPStatus.Success)
                {
                    samples.Add(reply.RoundtripTime);
                }

                await Task.Delay(120, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            ConsoleUi.Detail("  задержка", $"не измерить: {ex.Message}");
            return;
        }

        if (samples.Count == 0)
        {
            ConsoleUi.Detail("  задержка", "нет ответов на ping — приставка блокирует ICMP");
            return;
        }

        var min = samples.Min();
        var max = samples.Max();
        var average = samples.Average();
        var jitter = max - min;

        var verdict = jitter switch
        {
            <= 15 => "ровный канал",
            <= 60 => "заметные скачки — возможен Wi-Fi",
            _ => "сильные скачки — канал перегружен",
        };

        ConsoleUi.Detail(
            "  задержка",
            $"средняя {average:F0} мс, разброс {min}…{max} мс ({samples.Count} из 10) — {verdict}");
    }

    private static string DescribeCpu()
    {
        try
        {
            var cores = Environment.ProcessorCount;
            var name = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "неизвестен";
            return $"{name}, ядер: {cores}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            return "неизвестен";
        }
    }

    /// <summary>
    /// Сетевые адаптеры со скоростью соединения. Скорость важнее названия: адаптер
    /// на 100 Мбит/с не пропустит поток 20 Мбит/с вместе со служебным трафиком,
    /// а Wi-Fi даёт всплески задержки, которых не видно по средней скорости.
    /// </summary>
    private static List<(string Name, string Description)> DescribeNetworkAdapters()
    {
        var result = new List<(string, string)>();

        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                // В списке полно служебных мини-портов (фильтры WFP, планировщик QoS):
                // у них есть скорость, но нет адреса. Настоящий адаптер адрес имеет.
                var hasAddress = adapter.GetIPProperties().UnicastAddresses.Any(
                    address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                if (!hasAddress)
                {
                    continue;
                }

                var speed = adapter.Speed > 0
                    ? adapter.Speed >= 1_000_000_000
                        ? $"{adapter.Speed / 1_000_000_000.0:F1} Гбит/с"
                        : $"{adapter.Speed / 1_000_000.0:F0} Мбит/с"
                    : "скорость неизвестна";

                var kind = adapter.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "провод",
                    _ => adapter.NetworkInterfaceType.ToString(),
                };

                var warning = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                    ? "  ← Wi-Fi даёт всплески задержки"
                    : adapter.Speed is > 0 and <= 100_000_000
                        ? "  ← 100 Мбит/с мало для высокого битрейта"
                        : string.Empty;

                result.Add((adapter.Name, $"{kind}, {speed}{warning}"));
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            result.Add(("ошибка", ex.Message));
        }

        return result;
    }
}
