using System.Diagnostics;
using WooCast.Protocol.Net;

namespace WooCast.Cli;

/// <summary>Команда <c>discover</c>: поиск приставок в локальной сети.</summary>
internal static class DiscoverCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = CliArgs.Parse(args);
        var seconds = options.GetInt("seconds", 0);

        ConsoleUi.Header("Поиск приставок WooCast");
        ConsoleUi.Info("Нажмите Ctrl+C для выхода");
        ConsoleUi.Info();

        await using var listener = new DiscoveryListener(LocalDevice.Id, LocalDevice.Name);
        var found = new Dictionary<string, DiscoveredDevice>(StringComparer.Ordinal);

        listener.DeviceDiscovered += (_, device) =>
        {
            lock (found)
            {
                found[device.Id] = device;
            }

            ConsoleUi.EndStatus();
            ConsoleUi.Success($"{device.Name}");
            ConsoleUi.Info($"    {device.Subtitle}");

            // Версия приложения приставки: приёмник обновляется отдельно от ПК,
            // и по ней видно, надо ли обновлять вторую часть.
            if (!string.IsNullOrWhiteSpace(device.Version))
            {
                ConsoleUi.Info($"    версия приложения: {device.Version}");
            }
            ConsoleUi.Info($"    управление: {device.Address}:{device.ControlPort}, медиа: {device.MediaPort}");
            if (device.Caps is not null)
            {
                ConsoleUi.Info(
                    $"    декодер до {device.Caps.MaxWidth}×{device.Caps.MaxHeight} @ {device.Caps.MaxFps} к/с" +
                    (device.Caps.LowLatencyDecode == true ? ", low-latency" : string.Empty));
            }
        };

        listener.DeviceUpdated += (_, device) =>
        {
            lock (found)
            {
                found[device.Id] = device;
            }
        };

        listener.DeviceLost += (_, device) =>
        {
            lock (found)
            {
                found.Remove(device.Id);
            }

            ConsoleUi.EndStatus();
            ConsoleUi.Warn($"Пропало: {device.Name}");
        };

        listener.Start();

        var stopwatch = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(500, cancellationToken);
            ConsoleUi.Status(
                $"  Найдено устройств: {found.Count}   прошло {stopwatch.Elapsed.TotalSeconds:F0} с");

            if (seconds > 0 && stopwatch.Elapsed.TotalSeconds >= seconds)
            {
                break;
            }
        }

        ConsoleUi.EndStatus();

        if (found.Count == 0)
        {
            ConsoleUi.Warn("Устройства не найдены");
            ConsoleUi.Info("  • Убедитесь, что WooCast TV запущен на приставке");
            ConsoleUi.Info("  • Проверьте, что ПК и приставка в одной подсети");
            ConsoleUi.Info("  • Если broadcast запрещён, подключитесь вручную: woocast send --to <ip>");
            return 2;
        }

        ConsoleUi.Info();
        ConsoleUi.Info($"Итого устройств: {found.Count}");
        ConsoleUi.Info($"Трансляция: woocast send --to {found.Values.First().Address}");
        return 0;
    }
}
