using WooCast.Capture;
using WooCast.Protocol.Net;

namespace WooCast.Cli;

/// <summary>Команда <c>probe</c>: проверка окружения перед первой трансляцией.</summary>
internal static class ProbeCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        ConsoleUi.Header("Окружение");

        var ffmpeg = FfmpegLocator.Find();
        if (ffmpeg is null)
        {
            ConsoleUi.Error("ffmpeg не найден");
            ConsoleUi.Info("  Запустите: pwsh -File tools/fetch-ffmpeg.ps1");
            ConsoleUi.Info($"  Либо укажите путь в переменной {FfmpegLocator.EnvironmentVariable}");
            return 1;
        }

        ConsoleUi.Success($"ffmpeg: {ffmpeg}");

        ConsoleUi.Info("Проверяю кодировщики (несколько секунд)…");
        var capabilities = await EncoderProbe.DetectAsync(ffmpeg, cancellationToken);

        ConsoleUi.Detail("Версия", capabilities.FfmpegVersion);
        ConsoleUi.Detail(
            "Desktop Duplication",
            capabilities.HasDdagrab ? "доступен (ddagrab)" : "недоступен, будет gdigrab");

        if (capabilities.WorkingEncoders.Count == 0)
        {
            ConsoleUi.Error("Ни один кодировщик H.264 не заработал");
            return 1;
        }

        foreach (var encoder in capabilities.WorkingEncoders)
        {
            var marker = encoder == capabilities.RecommendedEncoder ? " ← будет использован" : string.Empty;
            ConsoleUi.Success($"{CaptureCapabilities.Describe(encoder)} ({CaptureCapabilities.FfmpegName(encoder)}){marker}");
        }

        ConsoleUi.Header("Сеть");
        var addresses = NetworkUtil.GetLocalAddresses();
        if (addresses.Count == 0)
        {
            ConsoleUi.Warn("Активных IPv4-адресов не найдено");
        }
        else
        {
            foreach (var address in addresses)
            {
                var kind = NetworkUtil.IsPrivate(address) ? "локальный" : "внешний";
                ConsoleUi.Detail(address.ToString(), kind);
            }
        }

        ConsoleUi.Detail("Широковещание", string.Join(", ", NetworkUtil.GetBroadcastAddresses()));
        ConsoleUi.Detail("Идентификатор ПК", $"{LocalDevice.Name} ({LocalDevice.Id})");

        ConsoleUi.Header("Профили качества (1080p60)");
        foreach (var quality in new[]
                 {
                     QualityPreset.Maximum, QualityPreset.High, QualityPreset.Balanced, QualityPreset.Economy,
                 })
        {
            var options = new CaptureOptions { Width = 1920, Height = 1080, Fps = 60, Quality = quality };
            ConsoleUi.Detail(
                options.DescribeQuality(),
                $"{options.EffectiveBitrateKbps() / 1000.0:F1} Мбит/с");
        }

        ConsoleUi.Info();
        return 0;
    }
}
