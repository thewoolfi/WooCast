namespace WooCast.Capture;

/// <summary>
/// Сборка командной строки ffmpeg для захвата и кодирования H.264.
/// </summary>
/// <remarks>
/// Вынесено в отдельный статический класс без побочных эффектов: аргументы можно проверить
/// в отладочном выводе и в тестах, не запуская процесс.
/// <para>
/// Ключевые требования к потоку (см. <c>docs/protocol.md</c>, п. 3.3):
/// без B-кадров, AUD перед каждым access unit'ом, SPS/PPS перед каждым ключевым кадром,
/// нулевая задержка кодирования.
/// </para>
/// </remarks>
public static class FfmpegArguments
{
    /// <summary>
    /// Будет ли использован <c>ddagrab</c>. Единственное место с этим решением:
    /// и сборка аргументов, и интерфейс должны показывать одно и то же.
    /// </summary>
    public static bool UsesDdagrab(CaptureOptions options, CaptureCapabilities capabilities)
    {
        if (!options.PreferDdagrab || !capabilities.HasDdagrab)
        {
            return false;
        }

        // Для режима окна нужна известная область обрезки: без неё снимок монитора
        // не к чему обрезать, и остаётся только gdigrab.
        return options.SourceKind == CaptureSourceKind.Display || options.Window is not null;
    }

    /// <summary>Человекочитаемое имя способа захвата для интерфейса и журнала.</summary>
    public static string DescribeCapture(CaptureOptions options, CaptureCapabilities capabilities) =>
        UsesDdagrab(options, capabilities)
            ? "ddagrab (Desktop Duplication)"
            : "gdigrab";

    /// <summary>Собрать полный набор аргументов для запуска потока.</summary>
    /// <param name="options">Параметры захвата.</param>
    /// <param name="capabilities">Что доступно на этой машине.</param>
    /// <returns>Аргументы без имени исполняемого файла.</returns>
    public static IReadOnlyList<string> Build(CaptureOptions options, CaptureCapabilities capabilities)
    {
        var encoder = ResolveEncoder(options.Encoder, capabilities);
        var window = options.SourceKind == CaptureSourceKind.Window ? options.Window : null;

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-nostdin", // иначе ffmpeg попытается читать наш stdin и «съест» ввод
        };

        // Окно тоже захватывается через ddagrab: снимок монитора обрезается до его области.
        // Это заметно дешевле, чем gdigrab, и не требует точного совпадения заголовка.
        var useDdagrab = UsesDdagrab(options, capabilities);

        var filter = BuildVideoFilter(options, useDdagrab);

        if (useDdagrab)
        {
            arguments.AddRange(["-filter_complex", filter]);
            arguments.AddRange(["-map", "[v]"]);
        }
        else
        {
            arguments.AddRange(["-f", "gdigrab"]);
            arguments.AddRange(["-framerate", options.Fps.ToString()]);
            arguments.AddRange(["-draw_mouse", options.DrawCursor ? "1" : "0"]);

            if (window is not null)
            {
                // gdigrab умеет область: смещение и размер задаются в координатах рабочего стола.
                arguments.AddRange(["-offset_x", window.X.ToString()]);
                arguments.AddRange(["-offset_y", window.Y.ToString()]);
                arguments.AddRange([
                    "-video_size",
                    $"{FfmpegArguments.MakeEven(window.Width)}x{FfmpegArguments.MakeEven(window.Height)}",
                ]);
                arguments.AddRange(["-i", "desktop"]);
            }
            else if (options.SourceKind == CaptureSourceKind.Window && !string.IsNullOrWhiteSpace(options.WindowTitle))
            {
                arguments.AddRange(["-i", $"title={options.WindowTitle}"]);
            }
            else
            {
                arguments.AddRange(["-i", "desktop"]);
            }

            arguments.AddRange(["-vf", filter]);
        }

        arguments.AddRange(["-c:v", CaptureCapabilities.FfmpegName(encoder)]);
        arguments.AddRange(EncoderTuning(encoder, options.EffectiveBitrateKbps(), options.KeyframeIntervalFrames, options.Profile));

        // dump_extra повторяет SPS/PPS перед каждым ключевым кадром, h264_metadata добавляет AUD —
        // без AUD приёмник не сможет делить поток на access unit'ы.
        arguments.AddRange(["-bsf:v", "dump_extra=freq=k,h264_metadata=aud=insert"]);

        arguments.AddRange(["-flush_packets", "1"]);
        arguments.AddRange(["-f", "h264", "-"]);

        return arguments;
    }

    /// <summary>
    /// Собрать аргументы для кодирования кадров, которые подаём мы сами.
    /// </summary>
    /// <remarks>
    /// Отличие от <see cref="Build"/> только во входе: вместо захвата средствами ffmpeg
    /// кадры приходят потоком сырых пикселей в стандартный вход. Ключ <c>-nostdin</c>
    /// здесь недопустим — он заставляет ffmpeg игнорировать вход, из которого мы пишем.
    /// Всё остальное (кодировщик, его настройки, фильтры, вставка AUD и SPS/PPS)
    /// совпадает с обычным путём: поток для приёмника должен быть одинаковым.
    /// </remarks>
    /// <param name="options">Параметры захвата.</param>
    /// <param name="capabilities">Что доступно на этой машине.</param>
    /// <param name="sourceWidth">Ширина подаваемых кадров.</param>
    /// <param name="sourceHeight">Высота подаваемых кадров.</param>
    /// <param name="pixelFormat">Формат пикселей во входном потоке.</param>
    public static IReadOnlyList<string> BuildForRawInput(
        CaptureOptions options,
        CaptureCapabilities capabilities,
        int sourceWidth,
        int sourceHeight,
        string pixelFormat = "bgra")
    {
        var encoder = ResolveEncoder(options.Encoder, capabilities);

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "warning",
            "-f", "rawvideo",
            "-pix_fmt", pixelFormat,
            "-s", $"{MakeEven(sourceWidth)}x{MakeEven(sourceHeight)}",
            "-framerate", options.Fps.ToString(),
            "-i", "-",
            "-vf", BuildVideoFilter(options, useDdagrab: false),
        };

        arguments.AddRange(["-c:v", CaptureCapabilities.FfmpegName(encoder)]);
        arguments.AddRange(EncoderTuning(encoder, options.EffectiveBitrateKbps(), options.KeyframeIntervalFrames, options.Profile));
        arguments.AddRange(["-bsf:v", "dump_extra=freq=k,h264_metadata=aud=insert"]);
        arguments.AddRange(["-flush_packets", "1"]);
        arguments.AddRange(["-f", "h264", "-"]);

        return arguments;
    }

    /// <summary>
    /// Параметры кодировщика. Набор намеренно минимален: всё, что не поддерживается конкретной
    /// сборкой или драйвером, отсеивается на этапе <see cref="EncoderProbe"/>.
    /// </summary>
    public static IEnumerable<string> EncoderTuning(
        EncoderKind kind,
        int bitrateKbps,
        int gopFrames,
        string profile = "high")
    {
        var rate = $"{Math.Max(500, bitrateKbps)}k";

        // Буфер оставлен равным битрейту. Попытка уменьшить его, чтобы кодировщик
        // размазывал ключевой кадр по времени, на измерении ничего не дала: NVENC
        // выдаёт IDR целиком независимо от буфера (178 КБ против 190 КБ до правки).
        // Меньший буфер только ухудшал бы качество на сложных сценах, поэтому откачено.
        var buffer = $"{Math.Max(500, bitrateKbps)}k";

        switch (kind)
        {
            case EncoderKind.Nvenc:
                return
                [
                    "-preset", "p1",
                    "-tune", "ull",
                    "-rc", "cbr",
                    "-zerolatency", "1",
                    "-rc-lookahead", "0",
                    "-bf", "0",
                    "-g", gopFrames.ToString(),
                    "-profile:v", profile,
                    "-b:v", rate,
                    "-maxrate", rate,
                    "-bufsize", buffer,
                ];

            case EncoderKind.QuickSync:
                return
                [
                    "-preset", "veryfast",
                    "-look_ahead", "0",
                    "-bf", "0",
                    "-g", gopFrames.ToString(),
                    "-profile:v", profile,
                    "-b:v", rate,
                    "-maxrate", rate,
                    "-bufsize", buffer,
                ];

            case EncoderKind.Amf:
                return
                [
                    "-usage", "lowlatency",
                    "-quality", "speed",
                    "-rc", "cbr",
                    "-bf", "0",
                    "-g", gopFrames.ToString(),
                    "-profile:v", profile,
                    "-b:v", rate,
                    "-maxrate", rate,
                    "-bufsize", buffer,
                ];

            default:
                return
                [
                    "-preset", "ultrafast",
                    "-tune", "zerolatency",
                    "-x264-params", $"repeat-headers=1:keyint={gopFrames}:min-keyint={gopFrames}:scenecut=0",
                    "-bf", "0",
                    "-g", gopFrames.ToString(),
                    "-profile:v", profile,
                    "-b:v", rate,
                    "-maxrate", rate,
                    "-bufsize", buffer,
                ];
        }
    }

    /// <summary>Цепочка фильтров: захват → системная память → [обрезка окна] → масштаб → yuv420p.</summary>
    /// <param name="options">Параметры захвата.</param>
    /// <param name="useDdagrab">Использовать ли Desktop Duplication API.</param>
    public static string BuildVideoFilter(CaptureOptions options, bool useDdagrab)
    {
        var width = MakeEven(options.Width);
        var height = MakeEven(options.Height);

        var scale =
            $"scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bilinear," +
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black," +
            "format=yuv420p";

        if (useDdagrab)
        {
            // ddagrab отдаёт кадры в D3D11-текстуре; hwdownload переносит их в обычную память,
            // после чего работает обычный программный энкодер.
            var displayIndex = options.SourceKind == CaptureSourceKind.Window && options.Window is { } window
                ? Math.Max(0, window.DisplayIndex)
                : Math.Max(0, options.DisplayIndex);

            var capture =
                $"ddagrab=output_idx={displayIndex}" +
                $":framerate={options.Fps}" +
                $":draw_mouse={(options.DrawCursor ? 1 : 0)}" +
                ",hwdownload,format=bgra,";

            // Обрезка до области окна идёт после переноса в память: фильтр crop
            // работает с обычными кадрами, а не с D3D11-текстурами.
            var crop = string.Empty;
            if (options.SourceKind == CaptureSourceKind.Window && options.Window is { } target)
            {
                var cropWidth = MakeEvenSize(target.Width);
                var cropHeight = MakeEvenSize(target.Height);
                crop = $"crop={cropWidth}:{cropHeight}:{MakeEvenOffset(target.CropX)}:{MakeEvenOffset(target.CropY)},";
            }

            return capture + crop + scale + "[v]";
        }

        return scale;
    }

    /// <summary>Выбрать фактический кодировщик с учётом доступности.</summary>
    public static EncoderKind ResolveEncoder(EncoderKind requested, CaptureCapabilities capabilities)
    {
        if (requested != EncoderKind.Auto && capabilities.WorkingEncoders.Contains(requested))
        {
            return requested;
        }

        return capabilities.RecommendedEncoder;
    }

    /// <summary>Привести размер к чётному значению — требование формата yuv420p.</summary>
    public static int MakeEven(int value)
    {
        var clamped = Math.Clamp(value, 2, 7680);
        return clamped % 2 == 0 ? clamped : clamped - 1;
    }

    /// <summary>Привести размер области обрезки к чётному значению, не менее 2.</summary>
    public static int MakeEvenSize(int value) => MakeEven(value);

    /// <summary>Привести координату обрезки к чётному значению; ноль остаётся нулём.</summary>
    public static int MakeEvenOffset(int value)
    {
        var clamped = Math.Clamp(value, 0, 7680);
        return clamped % 2 == 0 ? clamped : clamped - 1;
    }
}
