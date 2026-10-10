using System.Diagnostics;
using System.Text;

namespace WooCast.Capture;

/// <summary>Возможности локальной сборки ffmpeg: что вообще доступно на этой машине.</summary>
/// <param name="FfmpegPath">Путь к исполняемому файлу.</param>
/// <param name="FfmpegVersion">Первая строка вывода <c>ffmpeg -version</c>.</param>
/// <param name="HasDdagrab">Доступен ли фильтр <c>ddagrab</c> (Desktop Duplication API).</param>
/// <param name="WorkingEncoders">Кодировщики, которые реально запустились с тестовым кадром.</param>
public sealed record CaptureCapabilities(
    string FfmpegPath,
    string FfmpegVersion,
    bool HasDdagrab,
    IReadOnlyList<EncoderKind> WorkingEncoders)
{
    /// <summary>Лучший доступный кодировщик в порядке предпочтения.</summary>
    public EncoderKind RecommendedEncoder =>
        WorkingEncoders.Count > 0 ? WorkingEncoders[0] : EncoderKind.Software;

    /// <summary>Человекочитаемое имя кодировщика.</summary>
    public static string Describe(EncoderKind kind) => kind switch
    {
        EncoderKind.Nvenc => "NVIDIA NVENC",
        EncoderKind.QuickSync => "Intel Quick Sync",
        EncoderKind.Amf => "AMD AMF",
        EncoderKind.Software => "libx264 (CPU)",
        _ => "авто",
    };

    /// <summary>Имя кодировщика в терминах ffmpeg (<c>-c:v ...</c>).</summary>
    public static string FfmpegName(EncoderKind kind) => kind switch
    {
        EncoderKind.Nvenc => "h264_nvenc",
        EncoderKind.QuickSync => "h264_qsv",
        EncoderKind.Amf => "h264_amf",
        _ => "libx264",
    };
}

/// <summary>
/// Определяет, какие кодировщики H.264 работоспособны на этой машине, и доступен ли <c>ddagrab</c>.
/// </summary>
/// <remarks>
/// Наличие кодировщика в списке <c>ffmpeg -encoders</c> ничего не значит: сборка содержит все
/// аппаратные кодировщики, а конкретная видеокарта может их не поддерживать (или драйвер
/// отсутствовать). Поэтому каждый кандидат проверяется реальным кодированием пяти кадров.
/// Результат кэшируется на время жизни процесса: проверка занимает около секунды.
/// </remarks>
public static class EncoderProbe
{
    private static readonly EncoderKind[] Candidates =
    [
        EncoderKind.Nvenc,
        EncoderKind.QuickSync,
        EncoderKind.Amf,
        EncoderKind.Software,
    ];

    private static CaptureCapabilities? _cached;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Определить возможности (с кэшированием).</summary>
    public static async Task<CaptureCapabilities> DetectAsync(string? ffmpegPath = null, CancellationToken cancellationToken = default)
    {
        if (_cached is not null && ffmpegPath is null)
        {
            return _cached;
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null && ffmpegPath is null)
            {
                return _cached;
            }

            var path = ffmpegPath ?? FfmpegLocator.Require();
            var version = await GetFirstVersionLineAsync(path, cancellationToken).ConfigureAwait(false);
            var hasDdagrab = await HasFilterAsync(path, "ddagrab", cancellationToken).ConfigureAwait(false);

            var working = new List<EncoderKind>();
            foreach (var candidate in Candidates)
            {
                if (await CanEncodeAsync(path, candidate, cancellationToken).ConfigureAwait(false))
                {
                    working.Add(candidate);
                }
            }

            var capabilities = new CaptureCapabilities(path, version, hasDdagrab, working);
            if (ffmpegPath is null)
            {
                _cached = capabilities;
            }

            return capabilities;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Сбросить кэш — например, если пользователь сменил путь к ffmpeg.</summary>
    public static void Reset() => _cached = null;

    private static async Task<string> GetFirstVersionLineAsync(string ffmpegPath, CancellationToken cancellationToken)
    {
        var result = await RunAsync(ffmpegPath, ["-hide_banner", "-version"], TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);

        var line = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();

        return string.IsNullOrEmpty(line) ? "ffmpeg (версия неизвестна)" : line;
    }

    private static async Task<bool> HasFilterAsync(string ffmpegPath, string filter, CancellationToken cancellationToken)
    {
        var result = await RunAsync(ffmpegPath, ["-hide_banner", "-filters"], TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);

        return result.StandardOutput.Contains(filter, StringComparison.Ordinal);
    }

    private static async Task<bool> CanEncodeAsync(string ffmpegPath, EncoderKind kind, CancellationToken cancellationToken)
    {
        var name = CaptureCapabilities.FfmpegName(kind);
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-f", "lavfi",
            "-i", "color=c=black:s=640x360:r=30",
            "-frames:v", "5",
            "-c:v", name,
        };

        arguments.AddRange(FfmpegArguments.EncoderTuning(kind, bitrateKbps: 2000, gopFrames: 30));
        arguments.AddRange(["-f", "null", "-"]);

        try
        {
            var result = await RunAsync(ffmpegPath, arguments, TimeSpan.FromSeconds(25), cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Не удалось запустить {fileName}");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"{fileName} не ответил за {timeout.TotalSeconds:F0} с");
        }

        return new ProcessResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
        }
    }

    private readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>Собрать краткую сводку для журнала приложения.</summary>
    public static string Summarize(CaptureCapabilities capabilities)
    {
        var builder = new StringBuilder();
        builder.AppendLine(capabilities.FfmpegVersion);
        builder.AppendLine($"ddagrab: {(capabilities.HasDdagrab ? "доступен" : "недоступен")}");
        builder.Append("Кодировщики: ");
        builder.Append(capabilities.WorkingEncoders.Count == 0
            ? "ни один не заработал"
            : string.Join(", ", capabilities.WorkingEncoders.Select(CaptureCapabilities.Describe)));
        return builder.ToString();
    }
}
