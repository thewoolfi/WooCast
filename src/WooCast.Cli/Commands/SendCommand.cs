using WooCast.Capture;
using WooCast.Protocol;
using WooCast.Protocol.Net;

namespace WooCast.Cli;

/// <summary>Команда <c>send</c>: трансляция экрана или окна на приставку.</summary>
internal static class SendCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = CliArgs.Parse(args);

        var target = parsed.GetString("to");
        if (target is null)
        {
            ConsoleUi.Error("Не указан адрес приставки: --to <ip[:порт]>");
            ConsoleUi.Info("  Найти приставку: woocast discover");
            return 1;
        }

        if (!NetworkUtil.TryParseEndpoint(target, Wcp.ControlPort, out var address, out var controlPort))
        {
            ConsoleUi.Error($"Некорректный адрес: {target}");
            return 1;
        }

        var (width, height) = parsed.GetSize("size", 1920, 1080);

        // Источник: весь экран либо конкретное окно.
        var sourceKind = CaptureSourceKind.Display;
        WindowTarget? windowTarget = null;

        var windowIdText = parsed.GetString("window-id");
        var windowQuery = parsed.GetString("window");

        if (windowIdText is not null)
        {
            if (!int.TryParse(windowIdText, out var windowIndex))
            {
                ConsoleUi.Error($"Некорректный номер окна: {windowIdText}");
                return 1;
            }

            var (resolved, lookupError) = WindowsCommand.ResolveByIndex(windowIndex);
            if (resolved is null)
            {
                ConsoleUi.Error(lookupError!);
                ConsoleUi.Info("  Список окон: woocast windows");
                return 1;
            }

            windowTarget = resolved;
            sourceKind = CaptureSourceKind.Window;
        }
        else if (windowQuery is not null)
        {
            var (resolved, lookupError) = WindowsCommand.ResolveByTitle(windowQuery);
            if (resolved is null)
            {
                ConsoleUi.Error(lookupError!);
                ConsoleUi.Info("  Список окон: woocast windows");
                return 1;
            }

            windowTarget = resolved;
            sourceKind = CaptureSourceKind.Window;
        }

        var options = new CaptureOptions
        {
            Width = width,
            Height = height,
            Fps = parsed.GetInt("fps", 60),
            DisplayIndex = windowTarget?.DisplayIndex ?? parsed.GetInt("display", 0),
            Quality = ParseQuality(parsed.GetString("quality")),
            Encoder = ParseEncoder(parsed.GetString("encoder")),
            FfmpegPath = parsed.GetString("ffmpeg"),
            SourceKind = sourceKind,
            Window = windowTarget,
            WindowTitle = windowTarget?.Title ?? windowQuery,

            // Принудительный откат на gdigrab — для проверки резервного пути захвата.
            PreferDdagrab = !parsed.GetBool("gdigrab"),

            // Курсор принадлежит рабочему столу, а не окну, поэтому для окна он по умолчанию выключен.
            DrawCursor = parsed.GetBool("cursor") || (windowTarget is null && !parsed.GetBool("no-cursor")),

            // Управление с контроллера, подключённого к приёмнику: включается по запросу,
            // иначе виртуальный геймпад в системе не появляется.
            GamepadEnabled = parsed.GetBool("gamepad"),
            Gamepad = parsed.GetBool("gamepad-ds4") ? GamepadKind.DualShock4 : GamepadKind.Xbox360,

            Audio = new AudioOptions
            {
                Enabled = !parsed.GetBool("no-audio"),
                BitrateKbps = parsed.GetInt("audio-bitrate", 160),
                DelayMs = parsed.GetInt("audio-delay", 0),
            },
        };

        var explicitBitrate = parsed.GetInt("bitrate", 0);
        if (explicitBitrate > 0)
        {
            options.Quality = QualityPreset.Custom;
            options.BitrateKbps = explicitBitrate;
        }

        // Старые приставки иногда умеют только Main или Baseline, поэтому профиль
        // можно задать вручную; приёмник сообщает свои ограничения в announce.caps.
        var profile = parsed.GetString("profile", "high")!.ToLowerInvariant();
        options.Profile = profile is "high" or "main" or "baseline" ? profile : "high";

        var seconds = parsed.GetInt("seconds", 0);

        ConsoleUi.Header("WooCast — трансляция");
        ConsoleUi.Detail("Приёмник", $"{address}:{controlPort}");
        ConsoleUi.Detail("Источник", options.DescribeSource());

        if (windowTarget is { } selectedWindow)
        {
            ConsoleUi.Detail(
                "Область окна",
                $"{selectedWindow.Width}×{selectedWindow.Height}+{selectedWindow.CropX}+{selectedWindow.CropY} " +
                $"на экране {selectedWindow.DisplayIndex + 1}");
        }

        ConsoleUi.Detail("Параметры", $"{width}×{height} @ {options.Fps} к/с");
        ConsoleUi.Detail("Качество", $"{options.DescribeQuality()} — {options.EffectiveBitrateKbps() / 1000.0:F1} Мбит/с");
        ConsoleUi.Detail("Звук", options.Audio.Enabled
            ? $"AAC {options.Audio.BitrateKbps} кбит/с, {options.Audio.SampleRate / 1000.0:F0} кГц" +
              (options.Audio.DelayMs != 0 ? $", задержка {options.Audio.DelayMs:+0;-0} мс" : string.Empty)
            : "выключен");
        ConsoleUi.Detail("Ключевые кадры", $"каждые {options.KeyframeIntervalSeconds} с");

        ConsoleUi.Info();
        ConsoleUi.Info("Проверяю кодировщики…");
        var capabilities = await EncoderProbe.DetectAsync(options.FfmpegPath, cancellationToken);

        if (capabilities.WorkingEncoders.Count == 0)
        {
            ConsoleUi.Error("Ни один кодировщик H.264 не заработал");
            return 1;
        }

        var encoder = FfmpegArguments.ResolveEncoder(options.Encoder, capabilities);
        ConsoleUi.Detail("Кодировщик", CaptureCapabilities.Describe(encoder));
        ConsoleUi.Detail("Захват", FfmpegArguments.DescribeCapture(options, capabilities));

        ConsoleUi.Info();

        await using var session = await SenderSession.StartAsync(
            address,
            controlPort,
            options,
            capabilities,
            cancellationToken);

        ConsoleUi.Success("Трансляция началась. Ctrl+C — остановить");
        ConsoleUi.Info();
        ConsoleUi.Info($"  {session.EncoderCommandLine}");
        ConsoleUi.Info();

        session.Log += (_, line) => ConsoleUi.Info($"  {line}");

        session.StatusChanged += (_, status) =>
        {
            if (!status.IsActive)
            {
                return;
            }

            var note = status.AdaptationNote is null ? string.Empty : $"  ⚠ {status.AdaptationNote}";
            var audio = status.AudioEnabled
                ? $"  звук {status.AudioKbps,3} кбит/с {LevelBar(status.AudioLevel)}"
                : "  без звука";

            ConsoleUi.Status(
                $"  {status.Fps,5:F1} к/с  {status.Kbps / 1000.0,5:F1} Мбит/с{audio}  " +
                $"кадров {status.FramesSent,7}  очередь {status.QueueDepth}  " +
                $"потери {status.ReceiverDropped}  " +

                // Рывки и отставание звука — то, что человек видит и слышит.
                // Потери и задержка о них молчат.
                $"рывок {status.ReceiverMaxGapMs,4:F0} мс x{status.ReceiverStutters}  " +
                $"звук+{status.ReceiverAudioBacklogMs} мс  " +
                $"RTT {status.RttMs,4:F1} мс{note}");
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (seconds > 0)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        }

        try
        {
            await session.Completion.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Пользователь прервал или истекло время.
        }

        ConsoleUi.EndStatus();
        await session.StopAsync();

        var final = session.Status;
        ConsoleUi.Header("Итоги");
        ConsoleUi.Detail("Отправлено кадров", final.FramesSent.ToString());
        ConsoleUi.Detail("Отправлено данных", $"{final.BytesSent / 1024.0 / 1024.0:F1} МиБ");
        ConsoleUi.Detail("Перезапусков кодировщика", final.EncoderRestarts.ToString());

        if (final.Kbps > 0)
        {
            ConsoleUi.Detail("Средний битрейт", $"{final.Kbps / 1000.0:F1} Мбит/с");
        }

        if (final.ReceiverLatencyMs > 0)
        {
            ConsoleUi.Success($"Задержка на телевизоре: {final.ReceiverLatencyMs:F0} мс");
        }

        if (final.ReceiverDropped > 0)
        {
            ConsoleUi.Warn($"Приёмник отбросил кадров: {final.ReceiverDropped}");
        }

        return final.Phase == SenderPhase.Failed ? 1 : 0;
    }

    /// <summary>Полоска уровня звука: по ней сразу видно, идёт ли звук на самом деле.</summary>
    private static string LevelBar(float level)
    {
        var filled = (int)Math.Round(Math.Clamp(level, 0, 1) * 8);
        return "[" + new string('#', filled) + new string('.', 8 - filled) + "]";
    }

    private static QualityPreset ParseQuality(string? value) => value?.ToLowerInvariant() switch    {
        "maximum" or "max" or "максимум" => QualityPreset.Maximum,
        "balanced" or "сбалансированное" => QualityPreset.Balanced,
        "economy" or "экономичное" => QualityPreset.Economy,
        "custom" or "вручную" => QualityPreset.Custom,
        _ => QualityPreset.High,
    };

    private static EncoderKind ParseEncoder(string? value) => value?.ToLowerInvariant() switch
    {
        "nvenc" or "nvidia" => EncoderKind.Nvenc,
        "qsv" or "intel" => EncoderKind.QuickSync,
        "amf" or "amd" => EncoderKind.Amf,
        "software" or "x264" or "cpu" => EncoderKind.Software,
        _ => EncoderKind.Auto,
    };
}
