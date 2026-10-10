using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WooCast.Protocol;
using WooCast.Protocol.Media;
using WooCast.Protocol.Messages;
using WooCast.Protocol.Net;

namespace WooCast.Cli;

/// <summary>
/// Команда <c>receive</c>: тестовый приёмник.
/// </summary>
/// <remarks>
/// Нужен, чтобы проверять отправитель и протокол без телевизора: принимает управляющую сессию,
/// поднимает медиа-порт, считает кадры и умеет сохранять поток в файл для проверки через ffprobe.
/// </remarks>
internal static class ReceiveCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = CliArgs.Parse(args);
        var controlPort = options.GetInt("port", Wcp.ControlPort);
        var seconds = options.GetInt("seconds", 0);
        var dumpPath = options.GetString("dump");
        var audioDumpPath = options.GetString("dump-audio");
        var announce = !options.GetBool("no-announce");
        var name = options.GetString("name", "WooCast (тестовый приёмник)")!;

        // Имитация слабого декодера: приёмник сообщает заниженную частоту кадров,
        // чтобы проверить адаптивную подстройку на стороне ПК.
        var simulateFps = options.GetInt("simulate-fps", 0);
        var simulateSeconds = options.GetInt("simulate-seconds", 0);
        var simulateLatency = options.GetDouble("simulate-latency", 0);

        // Ограничения декодера, попадающие в announce.caps.
        var (maxWidth, maxHeight, maxFps) = ParseCaps(options.GetString("caps", "3840x2160@60")!);

        ConsoleUi.Header("Тестовый приёмник WooCast");
        ConsoleUi.Detail("Управление", $"порт {controlPort}");
        ConsoleUi.Detail("Обнаружение", announce ? $"включено (порт {Wcp.DiscoveryPort})" : "выключено");
        ConsoleUi.Detail("Возможности", $"до {maxWidth}×{maxHeight} @ {maxFps} к/с");
        if (dumpPath is not null)
        {
            ConsoleUi.Detail("Запись потока", dumpPath);
        }

        if (audioDumpPath is not null)
        {
            ConsoleUi.Detail("Запись звука", audioDumpPath);
        }

        if (simulateFps > 0)
        {
            ConsoleUi.Detail(
                "Имитация декодера",
                simulateSeconds > 0
                    ? $"{simulateFps} к/с первые {simulateSeconds} с, дальше настоящая"
                    : $"{simulateFps} к/с (телеметрия занижена)");
        }

        var deviceId = Guid.NewGuid().ToString("N")[..16];
        await using var announcer = new DiscoveryAnnouncer(
            new AnnounceDescriptor
            {
                Id = deviceId,
                Name = name,
                Model = "Тестовый приёмник",
                ControlPort = controlPort,
                MediaPort = Wcp.MediaPort,
                Caps = new DeviceCaps
                {
                    Codecs = ["h264"],
                    MaxWidth = maxWidth,
                    MaxHeight = maxHeight,
                    MaxFps = maxFps,
                    LowLatencyDecode = maxFps >= 60,
                },
            },
            bindDiscoveryPort: announce);

        var listener = new TcpListener(IPAddress.Any, controlPort);
        listener.Start();
        var actualControlPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        if (announce)
        {
            announcer.Start();
        }

        ConsoleUi.Success($"Слушаю управляющий порт {actualControlPort}");
        ConsoleUi.Info("Ожидание подключения с ПК… (Ctrl+C — выход)");
        ConsoleUi.Info();

        FileStream? dump = null;
        if (dumpPath is not null)
        {
            dump = new FileStream(dumpPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        }

        FileStream? audioDump = null;
        if (audioDumpPath is not null)
        {
            audioDump = new FileStream(audioDumpPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        }

        try
        {
            using var acceptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var client = await listener.AcceptTcpClientAsync(acceptTimeout.Token);
            client.NoDelay = true;

            var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
            ConsoleUi.Success($"Подключение с {remote}");

            return await HandleSessionAsync(
                client,
                announcer,
                dump,
                audioDump,
                seconds,
                simulateFps,
                simulateSeconds,
                simulateLatency,
                options.GetBool("gamepad-test"),
                cancellationToken);
        }
        finally
        {
            listener.Stop();

            if (dump is not null)
            {
                await dump.FlushAsync(CancellationToken.None);
                await dump.DisposeAsync();
                ConsoleUi.Info($"Поток сохранён: {dumpPath}");
            }

            if (audioDump is not null)
            {
                await audioDump.FlushAsync(CancellationToken.None);
                await audioDump.DisposeAsync();
                ConsoleUi.Info($"Звук сохранён: {audioDumpPath}");
            }
        }
    }

    /// <summary>Проверить, что пакет — целый ADTS-кадр: так его будет разбирать приставка.</summary>
    private static bool IsValidAdtsFrame(byte[] payload)
    {
        if (payload.Length < 7 || payload[0] != 0xFF || (payload[1] & 0xF0) != 0xF0)
        {
            return false;
        }

        try
        {
            return AdtsFrameReader.ParseHeader(payload).FrameLength == payload.Length;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Разобрать строку вида <c>1280x720@30</c>.</summary>
    private static (int Width, int Height, int Fps) ParseCaps(string text)
    {
        var width = 3840;
        var height = 2160;
        var fps = 60;

        var at = text.IndexOf('@');
        var sizePart = at >= 0 ? text[..at] : text;

        if (at >= 0 && int.TryParse(text[(at + 1)..], out var parsedFps) && parsedFps > 0)
        {
            fps = parsedFps;
        }

        var parts = sizePart.Split(['x', 'х', 'X'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var parsedWidth) &&
            int.TryParse(parts[1], out var parsedHeight) &&
            parsedWidth > 0 && parsedHeight > 0)
        {
            width = parsedWidth;
            height = parsedHeight;
        }

        return (width, height, fps);
    }

    private static async Task<int> HandleSessionAsync(
        TcpClient client,
        DiscoveryAnnouncer announcer,
        FileStream? dump,
        FileStream? audioDump,
        int seconds,
        int simulateFps,
        int simulateSeconds,
        double simulateLatency,
        bool simulateGamepad,
        CancellationToken cancellationToken)
    {
        await using var channel = new WcpLineChannel(client.GetStream());
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        MediaStreamReceiver? mediaReceiver = null;
        Task? mediaTask = null;
        var token = 0L;

        // Счётчик отправленных состояний управления при имитации контроллера.
        var gamepadSeq = 0L;
        var frameCount = 0L;
        var byteCount = 0L;
        var keyFrames = 0L;
        var missingAud = 0L;
        var audioFrames = 0L;
        var audioBytes = 0L;
        var audioBadFrames = 0L;

        // Метки времени: по ним видно, лежат ли звук и видео на одной шкале.
        var firstVideoPts = 0L;
        var lastVideoPts = 0L;
        var firstAudioPts = 0L;
        var lastAudioPts = 0L;
        var finished = false;
        var simulationEnded = false;
        var stopwatch = Stopwatch.StartNew();
        var firstFrameAt = TimeSpan.Zero;

        // Телеметрия приёмника: ПК использует её для адаптивной подстройки.
        var statsTask = Task.Run(async () =>
        {
            var reportedFrames = 0L;
            var reportedBytes = 0L;
            var lastAt = stopwatch.Elapsed;

            try
            {
                while (!sessionCts.IsCancellationRequested)
                {
                    await Task.Delay(1000, sessionCts.Token);

                    var now = stopwatch.Elapsed;
                    var elapsed = (now - lastAt).TotalSeconds;
                    lastAt = now;

                    var frames = Interlocked.Read(ref frameCount);
                    var bytes = Interlocked.Read(ref byteCount);
                    var deltaFrames = frames - reportedFrames;
                    var deltaBytes = bytes - reportedBytes;
                    reportedFrames = frames;
                    reportedBytes = bytes;

                    var measuredFps = elapsed > 0 ? deltaFrames / elapsed : 0;
                    var measuredKbps = elapsed > 0 ? (int)(deltaBytes * 8 / 1000 / elapsed) : 0;

                    // Занижение частоты имитирует слабый декодер приставки.
                    // С --simulate-seconds имитация длится ограниченное время: так проверяется
                    // и снижение параметров, и их возврат, когда «декодер» снова успевает.
                    var simulate = simulateFps > 0
                                   && (simulateSeconds <= 0 || stopwatch.Elapsed.TotalSeconds < simulateSeconds);

                    if (simulateFps > 0 && !simulate && !simulationEnded)
                    {
                        simulationEnded = true;
                        ConsoleUi.Info($"  Имитация слабого декодера снята на {stopwatch.Elapsed.TotalSeconds:F0} с");
                    }

                    var reportedFps = simulate ? simulateFps : measuredFps;

                    // Управление с контроллера: имитация нужна, чтобы проверить весь путь
                    // до виртуального геймпада на компьютере без настоящего контроллера.
                    if (simulateGamepad)
                    {
                        gamepadSeq++;

                        // Первая половина проверки — кнопка и стик нажаты, вторая — отпущены.
                        var pressing = gamepadSeq % 20 < 10;

                        await channel.WriteAsync(
                            WcpMessage.InputState(new InputSpec
                            {
                                Seq = gamepadSeq,
                                Buttons = pressing ? Wcp.InputButtons.A : 0,
                                Lx = pressing ? 30000 : 0,
                                Lt = pressing ? 200 : 0,
                                Device = "проверка (искусственный приёмник)",
                            }),
                            sessionCts.Token);
                    }

                    await channel.WriteAsync(
                        WcpMessage.Stats(
                            fps: Math.Round(reportedFps, 1),
                            kbps: measuredKbps,
                            dropped: 0,
                            queueMs: 0,
                            late: 0,
                            latencyMs: simulateLatency > 0 ? simulateLatency : null),
                        sessionCts.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }, sessionCts.Token);

        try
        {
            while (!sessionCts.IsCancellationRequested)
            {
                var message = await channel.ReadAsync(sessionCts.Token);
                if (message is null)
                {
                    ConsoleUi.Warn("ПК закрыл соединение");
                    break;
                }

                switch (message.T)
                {
                    case Wcp.MessageTypes.Hello:
                        token = Random.Shared.NextInt64(1, long.MaxValue);
                        ConsoleUi.Command($"hello от «{message.Name}» ({message.Id}), версия {message.Proto}");
                        await channel.WriteAsync(
                            WcpMessage.HelloAck(
                                message.Id ?? string.Empty,
                                session: Guid.NewGuid().ToString("N")[..8],
                                token: token,
                                receiver: new PeerInfo { Id = "test", Name = "Тестовый приёмник", Version = "1.0.0" }),
                            sessionCts.Token);
                        break;

                    case Wcp.MessageTypes.StreamRequest:
                        var video = message.Video;
                        ConsoleUi.Command(
                            $"stream_request: {video?.Codec} {video?.Width}×{video?.Height}@{video?.Fps}, " +
                            $"{video?.Bitrate} кбит/с, источник «{message.Source?.Title}»");

                        mediaReceiver = new MediaStreamReceiver(0, token);
                        ConsoleUi.Success($"Медиа-порт открыт: {mediaReceiver.Port}");

                        mediaTask = Task.Run(
                            () => PumpMediaAsync(mediaReceiver, dump, audioDump, sessionCts.Token),
                            sessionCts.Token);

                        announcer.State = Wcp.ReceiverStates.Busy;
                        await channel.WriteAsync(
                            WcpMessage.StreamReady(message.Id, mediaReceiver.Port, token),
                            sessionCts.Token);
                        break;

                    case Wcp.MessageTypes.StreamStart:
                        ConsoleUi.Success("ПК начал передачу");
                        await channel.WriteAsync(WcpMessage.Started(message.Id), sessionCts.Token);
                        break;

                    case Wcp.MessageTypes.Stats:
                        if (frameCount > 0 && firstFrameAt > TimeSpan.Zero)
                        {
                            var elapsed = (stopwatch.Elapsed - firstFrameAt).TotalSeconds;
                            var fps = elapsed > 0 ? frameCount / elapsed : 0;
                            var kbps = elapsed > 0 ? byteCount * 8 / 1000 / elapsed : 0;

                            ConsoleUi.Status(
                                $"  кадров {frameCount}  {fps:F1} к/с  {kbps / 1000.0:F1} Мбит/с  " +
                                $"ключевых {keyFrames}  без AUD {missingAud}  " +
                                $"звук {Interlocked.Read(ref audioFrames)} кадров");
                        }
                        break;

                    case Wcp.MessageTypes.Ping:
                        await channel.WriteAsync(WcpMessage.Pong(message.Id, message.Ts), sessionCts.Token);
                        break;

                    case Wcp.MessageTypes.Stop:
                        ConsoleUi.EndStatus();
                        ConsoleUi.Info("ПК остановил трансляцию");
                        await channel.WriteAsync(WcpMessage.Stopped(message.Id), sessionCts.Token);

                        // Не выходим сразу: итоговую сводку нужно напечатать и в этом случае.
                        finished = true;
                        break;

                    case Wcp.MessageTypes.Bye:
                        ConsoleUi.EndStatus();
                        ConsoleUi.Info("ПК завершил сессию");
                        finished = true;
                        break;
                }

                if (finished)
                {
                    break;
                }

                if (seconds > 0 && stopwatch.Elapsed.TotalSeconds > seconds)
                {
                    ConsoleUi.EndStatus();
                    ConsoleUi.Info($"Ограничение по времени ({seconds} с) достигнуто");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or WcpProtocolException)
        {
            ConsoleUi.EndStatus();
            ConsoleUi.Error(ex.Message);
            return 1;
        }
        finally
        {
            sessionCts.Cancel();

            try
            {
                await statsTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or AggregateException)
            {
            }

            if (mediaReceiver is not null)
            {
                await mediaReceiver.DisposeAsync();
            }

            if (mediaTask is not null)
            {
                try
                {
                    await mediaTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or AggregateException)
                {
                }
            }
        }

        ConsoleUi.EndStatus();
        ConsoleUi.Header("Итоги");
        ConsoleUi.Detail("Кадров принято", frameCount.ToString());
        ConsoleUi.Detail("Ключевых кадров", keyFrames.ToString());
        ConsoleUi.Detail("Байт видео", byteCount.ToString());

        if (missingAud > 0)
        {
            ConsoleUi.Warn($"Access unit'ов без AUD: {missingAud} — проверьте битстрим-фильтры ffmpeg");
        }
        else if (frameCount > 0)
        {
            ConsoleUi.Success("Каждый access unit содержит AUD — разбор на стороне приёмника корректен");
        }

        if (audioFrames > 0)
        {
            ConsoleUi.Detail("Кадров звука", audioFrames.ToString());
            ConsoleUi.Detail("Байт звука", audioBytes.ToString());

            var audioSeconds = audioFrames * 1024.0 / 48_000;
            if (audioSeconds > 0)
            {
                ConsoleUi.Detail(
                    "Средний битрейт звука",
                    $"{audioBytes * 8 / 1000 / audioSeconds:F0} кбит/с");
            }

            if (audioBadFrames > 0)
            {
                ConsoleUi.Warn($"Кадров звука с нарушенным ADTS: {audioBadFrames}");
            }
            else
            {
                ConsoleUi.Success("Все кадры звука — целые ADTS-кадры");
            }

            // Синхронизация: важно не абсолютное смещение (оно складывается из задержек
            // кодировщиков и компенсируется приёмником), а то, что обе шкалы идут с одной
            // скоростью — иначе звук и видео разъедутся за время просмотра.
            if (firstVideoPts > 0 && firstAudioPts > 0 && frameCount > 30 && audioFrames > 30)
            {
                var startOffsetMs = (firstAudioPts - firstVideoPts) / 1000.0;
                ConsoleUi.Detail("Смещение звука на старте", $"{startOffsetMs:F0} мс");

                var videoCadenceMs = (lastVideoPts - firstVideoPts) / 1000.0 / (frameCount - 1);
                var audioCadenceMs = (lastAudioPts - firstAudioPts) / 1000.0 / (audioFrames - 1);

                // Ожидаемый темп звука: 1024 сэмпла на кадр при 48 кГц.
                var expectedAudioCadenceMs = 1024.0 * 1000 / 48_000;

                ConsoleUi.Detail("Темп видео", $"{videoCadenceMs:F2} мс/кадр");
                ConsoleUi.Detail(
                    "Темп звука",
                    $"{audioCadenceMs:F2} мс/кадр (ожидается {expectedAudioCadenceMs:F2})");

                var rateErrorMs = Math.Abs(audioCadenceMs - expectedAudioCadenceMs);
                var driftPerMinute = rateErrorMs * 48_000 / 1024 * 60 / 1000.0;

                if (rateErrorMs < 0.5)
                {
                    ConsoleUi.Success(
                        $"Шкалы идут с одинаковой скоростью, дрейф не более {driftPerMinute:F0} мс/мин");
                }
                else
                {
                    ConsoleUi.Warn(
                        $"Темп звука отличается на {rateErrorMs:F2} мс/кадр — " +
                        $"это дрейф около {driftPerMinute:F0} мс/мин");
                }

                ConsoleUi.Info(
                    "  Абсолютное смещение — это разница задержек кодировщиков; " +
                    "приёмник компенсирует его, выравнивая видео по звуку.");
            }
        }

        return frameCount > 0 ? 0 : 3;

        // Локальная функция: считает кадры и, при необходимости, пишет Annex-B в файл.
        async Task PumpMediaAsync(
            MediaStreamReceiver receiver,
            FileStream? output,
            FileStream? audioOutput,
            CancellationToken ct)
        {
            try
            {
                var handshake = await receiver.AcceptAsync(ct);
                ConsoleUi.Success(
                    $"Медиа-хендшейк принят: версия {handshake.Version}, кодек {handshake.Codec}, " +
                    $"звук {(handshake.HasAudio ? "есть" : "нет")}, токен совпал");

                await foreach (var frame in receiver.ReadFramesAsync(ct))
                {
                    // Звук и видео идут в одном соединении и различаются флагом пакета.
                    if (frame.Header.IsAudio)
                    {
                        Interlocked.Increment(ref audioFrames);
                        Interlocked.Add(ref audioBytes, frame.Header.PayloadLength);

                        // Проверяем, что кадр действительно ADTS с корректной длиной:
                        // именно так его будет разбирать приставка.
                        if (!IsValidAdtsFrame(frame.Payload))
                        {
                            Interlocked.Increment(ref audioBadFrames);
                        }

                        if (Interlocked.Read(ref audioFrames) == 1)
                        {
                            firstAudioPts = frame.Header.PtsMicros;
                        }

                        lastAudioPts = frame.Header.PtsMicros;

                        if (audioOutput is not null)
                        {
                            await audioOutput.WriteAsync(frame.Payload, ct);
                        }

                        continue;
                    }

                    Interlocked.Increment(ref frameCount);
                    Interlocked.Add(ref byteCount, frame.Header.PayloadLength);

                    if (Interlocked.Read(ref frameCount) == 1)
                    {
                        firstVideoPts = frame.Header.PtsMicros;
                    }

                    lastVideoPts = frame.Header.PtsMicros;

                    if (frame.Header.IsKeyframe)
                    {
                        Interlocked.Increment(ref keyFrames);
                    }

                    if (!H264Util.HasAud(frame.Payload))
                    {
                        Interlocked.Increment(ref missingAud);
                    }

                    if (firstFrameAt == TimeSpan.Zero)
                    {
                        firstFrameAt = stopwatch.Elapsed;
                    }

                    if (output is not null)
                    {
                        await output.WriteAsync(frame.Payload, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ConsoleUi.EndStatus();
                ConsoleUi.Error($"Медиа-канал: {ex.Message}");
            }
        }
    }
}
