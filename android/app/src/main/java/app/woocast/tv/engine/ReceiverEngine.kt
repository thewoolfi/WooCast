// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.engine

import android.annotation.SuppressLint
import android.content.Context
import android.os.Build
import android.os.SystemClock
import android.provider.Settings
import android.util.Log
import android.view.Surface
import android.view.KeyEvent
import android.view.MotionEvent
import androidx.annotation.StringRes
import app.woocast.tv.BuildConfig
import app.woocast.tv.R
import app.woocast.tv.media.AudioDecoder
import app.woocast.tv.media.DecoderCaps
import app.woocast.tv.media.DecoderInfo
import app.woocast.tv.media.VideoDecoder
import app.woocast.tv.input.GamepadState
import app.woocast.tv.net.AccessUnit
import app.woocast.tv.net.ControlServer
import app.woocast.tv.net.ControlSession
import app.woocast.tv.net.DeviceCaps
import app.woocast.tv.net.DiscoveryResponder
import app.woocast.tv.net.MediaFrameHeader
import app.woocast.tv.net.MediaHandshake
import app.woocast.tv.net.MediaProtocolException
import app.woocast.tv.net.MediaServer
import app.woocast.tv.net.PeerInfo
import app.woocast.tv.net.Wcp
import app.woocast.tv.net.WcpMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.security.SecureRandom
import java.util.Locale

/** Состояние приёмника, отображаемое на экране. */
sealed interface ReceiverUiState {

    /** Ожидание подключения: приставка видна в сети. */
    data class Waiting(
        val deviceName: String,
        val address: String,
        val controlPort: Int,
        val mediaPort: Int,
        val discoveryPort: Int,
        val decoder: DecoderInfo?,
        val hasNetwork: Boolean,
    ) : ReceiverUiState

    /** ПК запросил трансляцию, готовим поверхность и декодер. */
    data class Preparing(val sourceName: String, val width: Int, val height: Int) : ReceiverUiState

    /** Идёт трансляция. */
    data class Streaming(
        val sourceName: String,
        val width: Int,
        val height: Int,
        val fps: Double,
        val kbps: Int,
        val latencyMs: Double,
        val dropped: Int,
        val /** Телеметрия от отправителя: очередь отправки и RTT. */ senderQueueMs: Double,
        val senderRttMs: Double,
        val waitingForKeyframe: Boolean,
        /** Передаётся ли звук и с каким битрейтом. */
        val audioEnabled: Boolean = false,
        val audioKbps: Int = 0,
    ) : ReceiverUiState

    /** Трансляция прервана. */
    data class Failed(
        val code: String,
        @StringRes val reasonRes: Int,
        val detail: String,
    ) : ReceiverUiState
}

/**
 * Движок приёмника: владеет обнаружением, управляющим каналом, медиа-сервером и декодером.
 *
 * Живёт в [app.woocast.tv.WooCastApp] как единственный экземпляр на процесс, поэтому
 * пересоздание Activity (например, при смене конфигурации) не прерывает трансляцию:
 * новая поверхность просто подключается к тому же движку.
 */
@SuppressLint("HardwareIds")
class ReceiverEngine(private val context: Context) {

    companion object {
        private const val TAG = "WooCast/Engine"

        /** Интервал телеметрии на экране. */
        private const val METRICS_INTERVAL_MS = 500L

        /** Как часто отправляются состояния управления: шестьдесят раз в секунду. */
        private const val InputIntervalMs = 16L

        /** Не реже раза в полсекунды состояние уходит даже без изменений. */
        private const val InputKeepaliveNanos = 500_000_000L

    /** Не чаще одной ошибки декодера в две секунды — иначе канал забьётся. */
    private const val ErrorResendIntervalNanos = 2_000_000_000L

        /** Как часто проверять, не потерял ли декодер ключевой кадр. */
        private const val KEYFRAME_CHECK_INTERVAL_MS = 250L

        /** Минимальный интервал между запросами ключевого кадра: перезапуск кодировщика не бесплатен. */
        private const val KEYFRAME_REQUEST_INTERVAL_MS = 1_500L

        /** Интервал синхронизации часов через ping/pong. */
        private const val CLOCK_SYNC_INTERVAL_MS = 2_000L

        /** Сколько держать журнал событий. */
        private const val LOG_LIMIT = 60
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val random = SecureRandom()

    private val _state = MutableStateFlow<ReceiverUiState?>(null)
    val state: StateFlow<ReceiverUiState?> = _state.asStateFlow()

    private val _log = MutableStateFlow<List<String>>(emptyList())
    val log: StateFlow<List<String>> = _log.asStateFlow()

    /** Стабильный идентификатор приставки для протокола. */
    val deviceId: String by lazy { resolveDeviceId() }

    /** Имя устройства, показываемое на ПК. */
    val deviceName: String = resolveDeviceName()

    private var discovery: DiscoveryResponder? = null
    private var control: ControlServer? = null
    private var media: MediaServer? = null

    private var decoderInfo: DecoderInfo? = null
    private var decoder: VideoDecoder? = null

    /** Декодер звука. Он же служит часами для видео, когда звук играет. */
    private var audioDecoder: AudioDecoder? = null

    @Volatile
    private var surface: Surface? = null

    @Volatile
    private var session: SessionContext? = null

    private var metricsJob: Job? = null
    private var clockSyncJob: Job? = null
    private var watchdogJob: Job? = null
    private var waitingJob: Job? = null
    private var decoderWatchJob: Job? = null

    /** Счётчики для расчёта частоты кадров и битрейта. */
    private var windowFrames = 0L
    private var windowBytes = 0L
    private var windowAudioBytes = 0L
    private var lastRenderedPts = 0L

    /** Оценка расхождения часов ПК и приставки, микросекунды. */
    @Volatile
    private var clockOffsetMicros = 0L

    @Volatile
    private var clockSynced = false

    private var started = false

    /** Отправка состояний управления с геймпада. */
    private var inputJob: Job? = null

    /** Когда последний раз отправлялось состояние — для редких подтверждений связи. */
    private var lastInputSentNanos = 0L

    /** Когда последний раз отправляли ошибку декодера — чтобы не частить. */
    private var lastErrorSentNanos = 0L

    /** Запустить приёмник. Повторный вызов безопасен. */
    fun start() {
        if (started) return
        started = true

        decoderInfo = DecoderCaps.findBest()
        decoderInfo?.let { info ->
            appendLog("Декодер: ${info.displayName}")
            appendLog("Поддержка: ${info.capsSummary}")
        } ?: appendLog("Аппаратный декодер H.264 не найден")

        val announcer = DiscoveryResponder(
            deviceId = deviceId,
            deviceName = deviceName,
            model = "${Build.MANUFACTURER} ${Build.MODEL}".trim(),
            version = BuildConfig.VERSION_NAME,
        )

        announcer.caps = buildCaps()
        announcer.controlPort = Wcp.CONTROL_PORT
        announcer.mediaPort = Wcp.MEDIA_PORT
        discovery = announcer

        val mediaServer = MediaServer(Wcp.MEDIA_PORT)
        mediaServer.onHandshake = { handshake -> handshake.token == session?.token }
        mediaServer.onAccessUnit = { handshake, unit, header -> onAccessUnit(handshake, unit, header) }
        mediaServer.onAudioFrame = { handshake, frame, header -> onAudioFrame(handshake, frame, header) }
        media = mediaServer

        val controlServer = ControlServer(Wcp.CONTROL_PORT) { session -> handleControl(session) }
        control = controlServer

        // Порядок важен: сначала занимаем порты, затем сообщаем их в announce.
        mediaServer.start(scope)
        controlServer.start(scope)

        announcer.controlPort = controlServer.boundPort.takeIf { it > 0 } ?: Wcp.CONTROL_PORT
        announcer.mediaPort = mediaServer.boundPort.takeIf { it > 0 } ?: Wcp.MEDIA_PORT

        publishWaiting()
        announcer.start(scope)

        metricsJob = scope.launch { metricsLoop() }
        waitingJob = scope.launch { waitingRefreshLoop() }
        decoderWatchJob = scope.launch { decoderWatchLoop() }
        appendLog("Готов к подключению")
    }

    /**
     * Экран ожидания периодически обновляется: IP-адрес становится известен не сразу
     * (сеть поднимается асинхронно), а при переключении Wi-Fi/кабеля он меняется.
     */
    private suspend fun waitingRefreshLoop() {
        while (scope.isActive) {
            delay(2_000)
            if (_state.value is ReceiverUiState.Waiting) {
                publishWaiting()
            }
        }
    }

    /** Остановить всё. */
    fun stop() {
        if (!started) return
        started = false

        metricsJob?.cancel()
        clockSyncJob?.cancel()
        watchdogJob?.cancel()
        waitingJob?.cancel()
        decoderWatchJob?.cancel()
        releaseDecoder()
        session = null

        discovery?.stop()
        control?.stop()
        media?.stop()
        discovery = null
        control = null
        media = null
    }

    /** Подключить поверхность вывода. */
    fun attachSurface(newSurface: Surface) {
        surface = newSurface
        appendLog("Поверхность вывода готова")

        val active = session
        if (active != null) {
            ensureDecoder(active)
            // Просим ключевой кадр, чтобы картинка появилась сразу, а не через 2 секунды.
            requestKeyframe(active)
        }
    }

    /** Отключить поверхность (Activity уходит в фон). */
    fun detachSurface() {
        surface = null
        releaseDecoder()
    }

    // ------------------------------------------------------------------
    // Управление с геймпада
    // ------------------------------------------------------------------

    /** Состояние геймпада, подключённого к приёмнику. */
    val gamepad = GamepadState()

    private val _gamepadDevice = MutableStateFlow<String?>(null)

    /**
     * Название подключённого контроллера для показа на экране.
     *
     * Отдельный поток состояния, а не прямое чтение: интерфейс должен обновляться,
     * когда контроллер подключают или отключают.
     */
    val gamepadDevice: StateFlow<String?> = _gamepadDevice.asStateFlow()

    /**
     * Обработать нажатие кнопки геймпада.
     *
     * @return true, если событие принято и не должно попадать в интерфейс.
     */
    fun onGamepadKey(event: KeyEvent): Boolean {
        val accepted = gamepad.onKey(event)
        if (accepted) {
            ensureInputLoop()
        }

        return accepted
    }

    /**
     * Обработать движение геймпада: стики, курки, крестовину.
     *
     * @return true, если событие принято.
     */
    fun onGamepadMotion(event: MotionEvent): Boolean {
        val accepted = gamepad.onMotion(event)
        if (accepted) {
            ensureInputLoop()
        }

        return accepted
    }

    /** Сообщить движку о подключении или отключении контроллера. */
    fun onGamepadDeviceChanged(connected: Boolean, name: String?) {
        if (connected) {
            gamepad.onDeviceAdded(name)
            appendLog("Геймпад подключён: ${name ?: "без названия"}")
        } else {
            gamepad.onDeviceRemoved()
            appendLog("Геймпад отключён")
        }

        ensureInputLoop()
        publishInputState()
    }

    /**
     * Запустить отправку состояний управления, если она ещё не идёт.
     *
     * Отправка идёт с постоянной частотой, а не по каждому событию: стик при отклонении
     * порождает десятки событий в секунду, и отправлять их все — значит забивать канал
     * управления. Неизменённые состояния не отправляются вовсе.
     */
    private fun ensureInputLoop() {
        if (inputJob?.isActive == true) return

        inputJob = scope.launch {
            while (isActive) {
                val active = session
                val control = active?.controlSession

                if (control != null) {
                    // Раз в полсекунды состояние отправляется даже без изменений:
                    // так компьютер видит, что связь жива, и держит кнопки отпущенными.
                    val force = System.nanoTime() - lastInputSentNanos > InputKeepaliveNanos
                    val state = gamepad.take(force)

                    if (state != null) {
                        try {
                            control.send(WcpMessage.input(state))
                            lastInputSentNanos = System.nanoTime()
                        } catch (e: Exception) {
                            Log.w(TAG, "Не удалось отправить состояние управления", e)
                        }
                    }
                }

                delay(InputIntervalMs)
            }
        }
    }

    private fun publishInputState() {
        val active = session
        if (active != null) {
            active.gamepadName = gamepad.device
        }

        _gamepadDevice.value = gamepad.device
    }

    /** Вернуться к экрану ожидания после ошибки. */
    fun dismissError() {
        endSession(silent = true)
        publishWaiting()
    }

    // ------------------------------------------------------------------
    // Управляющий канал
    // ------------------------------------------------------------------

    private suspend fun handleControl(controlSession: ControlSession) {
        appendLog("Подключение с ${controlSession.remoteAddress}")
        var ownedSession: SessionContext? = null

        try {
            while (scope.isActive) {
                val message = controlSession.read() ?: break

                when (message.t) {
                    Wcp.Types.HELLO -> {
                        val newSession = SessionContext(
                            id = message.id ?: "unknown",
                            token = random.nextLong().let { if (it == 0L) 1L else it },
                            senderName = message.name ?: "ПК",
                        )
                        ownedSession = newSession
                        session = newSession

                        controlSession.send(
                            WcpMessage.helloAck(
                                re = message.re ?: message.id,
                                session = newSession.sessionId,
                                token = newSession.token,
                                receiver = PeerInfo(
                                    id = deviceId,
                                    name = deviceName,
                                    model = "${Build.MANUFACTURER} ${Build.MODEL}".trim(),
                                    version = BuildConfig.VERSION_NAME,
                                ),
                            ),
                        )

                        appendLog("Сессия с «${newSession.senderName}»")
                    }

                    Wcp.Types.STREAM_REQUEST -> {
                        val active = ownedSession
                        if (active == null) {
                            controlSession.send(
                                WcpMessage.error(message.id, Wcp.ErrorCodes.BAD_STATE, "Сначала hello"),
                            )
                            continue
                        }

                        handleStreamRequest(controlSession, active, message)
                    }

                    Wcp.Types.STREAM_START -> {
                        controlSession.send(WcpMessage.started(message.id))
                        appendLog("ПК начал передачу потока")
                    }

                    Wcp.Types.STATS -> {
                        ownedSession?.senderQueueMs = message.queueMs ?: 0.0
                        ownedSession?.senderRttMs = message.rttMs ?: 0.0
                        ownedSession?.senderDropped = message.dropped ?: 0
                    }

                    Wcp.Types.PING -> {
                        // Метка времени ПК — основа для оценки расхождения часов,
                        // без неё сквозную задержку измерить нечем.
                        message.ts?.let { senderTs ->
                            ownedSession?.senderClockSample = senderTs
                            ownedSession?.senderClockSampleAt = SystemClock.elapsedRealtime()
                        }

                        controlSession.send(WcpMessage.pong(message.id, message.ts))
                    }

                    Wcp.Types.PONG -> onPong(message.ts)

                    Wcp.Types.STOP -> {
                        controlSession.send(WcpMessage.stopped(message.id))
                        appendLog("Трансляция остановлена с ПК")
                        endSession()
                    }

                    Wcp.Types.BYE -> {
                        appendLog("ПК закрыл сессию")
                        break
                    }

                    else -> {
                        // Неизвестные типы игнорируем: совместимость вперёд.
                    }
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "Управляющая сессия прервана", e)
        } finally {
            controlSession.close()

            // Обрыв управляющего соединения немедленно останавливает трансляцию.
            if (ownedSession != null && ownedSession === session) {
                appendLog("Соединение с ПК потеряно")
                endSession(failWith = R.string.err_control_lost, code = Wcp.ErrorCodes.INTERNAL, detail = "control socket closed")
            }
        }
    }

    private suspend fun handleStreamRequest(
        controlSession: ControlSession,
        active: SessionContext,
        message: WcpMessage,
    ) {
        val video = message.video
        val info = decoderInfo

        if (video?.codec != null && !video.codec.equals(Wcp.CODEC_H264, ignoreCase = true)) {
            controlSession.send(
                WcpMessage.streamReject(message.id, Wcp.RejectReasons.UNSUPPORTED_CODEC, "Поддерживается только H.264"),
            )
            appendLog("Отказ: кодек ${video.codec} не поддерживается")
            return
        }

        if (info == null) {
            controlSession.send(
                WcpMessage.streamReject(message.id, Wcp.RejectReasons.UNSUPPORTED_CODEC, "Нет декодера H.264"),
            )
            return
        }

        val width = video?.width ?: 0
        val height = video?.height ?: 0
        val fps = video?.fps ?: 0

        if (width > 0 && height > 0 && !DecoderCaps.isSupported(info, width, height, fps)) {
            controlSession.send(
                WcpMessage.streamReject(
                    message.id,
                    Wcp.RejectReasons.UNSUPPORTED_RESOLUTION,
                    "Декодер поддерживает до ${info.maxWidth}×${info.maxHeight} @ ${info.maxFps} к/с",
                ),
            )
            appendLog("Отказ: ${width}×$height@$fps не поддерживается")
            return
        }

        active.sourceName = message.source?.title ?: active.senderName
        active.videoWidth = width
        active.videoHeight = height
        active.videoFps = fps

        // Запас сглаживания задаёт пользователь: это его выбор между плавностью
        // и задержкой. Ограничиваем разумным пределом, чтобы опечатка не превратилась
        // в многолетний буфер.
        active.videoBufferMs = (message.video?.bufferMs ?: 0).coerceIn(0, 600)
        active.mediaConnected = false
        active.audioEnabled = message.audio?.enabled == true
        active.audioDelayMs = (message.audio?.delay ?: 0).coerceIn(-1000, 1000)

        val mediaPort = media?.boundPort?.takeIf { it > 0 } ?: Wcp.MEDIA_PORT
        controlSession.send(WcpMessage.streamReady(message.id, mediaPort, active.token))

        appendLog("Запрошена трансляция: ${active.sourceName} ${width}×$height@$fps")

        // Показываем экран плеера — он создаст Surface, после чего поднимется декодер.
        _state.value = ReceiverUiState.Preparing(active.sourceName, width, height)

        discovery?.state = Wcp.States.BUSY
        publishStateToDiscovery()

        // Если ПК не подключится к медиа-порту за 5 секунд, сессия освобождается.
        watchdogJob?.cancel()
        watchdogJob = scope.launch {
            delay(Wcp.MEDIA_CONNECT_TIMEOUT_MS)
            val current = session
            if (current === active && !current.mediaConnected) {
                appendLog("ПК не подключился к медиа-порту")
                endSession(
                    failWith = R.string.err_media_timeout,
                    code = Wcp.ErrorCodes.INTERNAL,
                    detail = "media connect timeout",
                )
            }
        }

        // Часы синхронизируем сразу: без этого нельзя измерить сквозную задержку.
        startClockSync(controlSession, active)
    }

    // ------------------------------------------------------------------
    // Медиа
    // ------------------------------------------------------------------

    private fun onAccessUnit(handshake: MediaHandshake, unit: AccessUnit, header: MediaFrameHeader) {
        val active = session ?: return
        if (handshake.token != active.token) return

        if (!active.mediaConnected) {
            active.mediaConnected = true
            watchdogJob?.cancel()
            appendLog("Медиа-поток пошёл")
        }

        windowBytes += unit.data.size
        lastRenderedPts = header.ptsMicros

        // Если декодера ещё нет — пробуем создать снова. Раньше он создавался только
        // в момент появления поверхности: если возможности приставки к тому моменту
        // ещё не были определены, декодер не создавался никогда, и все кадры молча
        // выбрасывались. Снаружи это выглядело как «ожидание первого кадра»
        // при нулевых потерях — потому что до счётчика потерь кадры не доходили.
        if (decoder == null) {
            ensureDecoder(active)

            if (decoder == null) {
                discardedFrames.incrementAndGet()
                return
            }
        }

        decoder?.submit(unit, header.ptsMicros)
    }

    /** Кадры, не дошедшие до декодера. Показываются как потери: иначе они невидимы. */
    private val discardedFrames = java.util.concurrent.atomic.AtomicInteger(0)

    /** Кадр звука: полный ADTS-кадр и метка времени на общей с видео шкале. */
    private fun onAudioFrame(handshake: MediaHandshake, frame: ByteArray, header: MediaFrameHeader) {
        val active = session ?: return
        if (handshake.token != active.token) return

        if (!active.audioFlowing) {
            active.audioFlowing = true
            appendLog("Пошёл звук")
        }

        windowAudioBytes += frame.size
        audioDecoder?.submit(frame, header.ptsMicros)
    }

    // ------------------------------------------------------------------
    // Декодер
    // ------------------------------------------------------------------

    /**
     * Создать декодер, если есть и поверхность, и сведения о возможностях приставки.
     *
     * Вызывается и с потока интерфейса (появление поверхности), и с потока приёма
     * (когда приходят кадры, а декодера ещё нет), поэтому синхронизирован: иначе
     * два вызова создали бы два декодера.
     */
    @Synchronized
    private fun ensureDecoder(active: SessionContext) {
        val output = surface ?: return
        val info = decoderInfo

        if (decoder != null) return

        // Звук создаётся первым: он передаётся видео в качестве часов.
        if (active.audioEnabled && audioDecoder == null) {
            val audioListener = object : AudioDecoder.Listener {
                override fun onStarted(sampleRate: Int, channels: Int, volumePercent: Int?) {
                    appendLog("Звук: $sampleRate Гц, каналов $channels")

                    // Громкость показывается прямо: на телефоне «нет звука» чаще всего
                    // означает вывернутую в ноль громкость мультимедиа или беззвучный
                    // режим, и об этом лучше сказать словами, чем искать причину вслепую.
                    val message = when {
                        volumePercent == null -> "Громкость мультимедиа: неизвестна"

                        volumePercent == 0 ->
                            "Громкость мультимедиа 0 % — звук играет в тишину"

                        volumePercent < 15 ->
                            "Громкость мультимедиа $volumePercent % — очень тихо"

                        else -> "Громкость мультимедиа $volumePercent %"
                    }

                    appendLog(message)

                    // О нулевой громкости и о тишине сообщаем на компьютер: там на это
                    // смотрят, а в журнал на телефоне заглядывать никто не станет.
                    if (volumePercent != null && volumePercent < 15) {
                        session?.controlSession?.let { control ->
                            try {
                                control.send(WcpMessage.error(null, Wcp.ErrorCodes.INTERNAL, message))
                            } catch (e: Exception) {
                                Log.w(TAG, "Не удалось сообщить о громкости", e)
                            }
                        }
                    }
                }

                override fun onError(message: String) {
                    appendLog("Ошибка звука: $message")
                }
            }

            // Задержка выравнивания складывается из базовой (разница конвейеров на ПК,
            // около 120 мс) и поправки пользователя. Раньше значение ползунка
            // перезаписывало базовую целиком, и при нуле выравнивания не было вовсе.
            val audioDelay = AudioDecoder.DefaultStartDelayMs + active.audioDelayMs

            audioDecoder = AudioDecoder(
                listener = audioListener,
                startDelayMs = audioDelay,
                context = context.applicationContext,
            ).also { it.start() }
        }

        val listener = object : VideoDecoder.Listener {
            override fun onVideoSizeChanged(width: Int, height: Int) {
                active.videoWidth = width
                active.videoHeight = height
                appendLog("Размер видео: ${width}×$height")
            }

            override fun onFrameRendered(ptsMicros: Long, arrivalNanos: Long) {
                windowFrames++
                if (!active.firstFrameRendered) {
                    active.firstFrameRendered = true
                    active.waitingForKeyframe = false
                    appendLog("Первый кадр отрисован")
                }
            }

            override fun onFirstFrame() {
                publishStreaming(active)
            }

            override fun onDecoderError(recoverable: Boolean, message: String) {
                appendLog("Ошибка декодера: $message")

                // Ошибку отправляем на ПК даже если она поправима: её текст — единственное,
                // что объясняет причину, а в журнале на телевизоре его никто не читает.
                // Частота ограничена, чтобы не залить канал сообщениями.
                val now = System.nanoTime()
                if (now - lastErrorSentNanos > ErrorResendIntervalNanos) {
                    lastErrorSentNanos = now
                    session?.controlSession?.let { control ->
                        try {
                            control.send(
                                WcpMessage.error(
                                    re = null,
                                    code = Wcp.ErrorCodes.INTERNAL,
                                    message = (if (recoverable) "Декодер: " else "Декодер (сбой): ") + message,
                                ),
                            )
                        } catch (e: Exception) {
                            Log.w(TAG, "Не удалось отправить ошибку декодера", e)
                        }
                    }
                }

                if (!recoverable) {
                    endSession(
                        failWith = R.string.err_decoder_failed,
                        code = Wcp.ErrorCodes.INTERNAL,
                        detail = message,
                    )
                }
            }
        }

        decoder = VideoDecoder(
            surface = output,
            listener = listener,
            decoderInfo = info,
            // Размер из stream_request: кодек настраивается сразу под реальный поток,
            // а не под «безопасные» 1080p.
            expectedWidth = active.videoWidth,
            expectedHeight = active.videoHeight,
        ).also {
            // Ожидаемая частота нужна, чтобы отличать нормальный интервал от рывка.
            it.setFrameRate(active.videoFps)
            it.setBufferMs(active.videoBufferMs)
            it.start()
        }
    }

    private fun releaseDecoder() {
        decoder?.stop()
        decoder = null

        audioDecoder?.stop()
        audioDecoder = null
    }

    private fun requestKeyframe(active: SessionContext) {
        val controlSession = active.controlSession ?: return
        scope.launch {
            try {
                controlSession.send(WcpMessage.requestKeyframe())
            } catch (e: Exception) {
                Log.w(TAG, "Не удалось запросить ключевой кадр", e)
            }
        }
    }

    // ------------------------------------------------------------------
    // Синхронизация часов и телеметрия
    // ------------------------------------------------------------------

    private fun startClockSync(controlSession: ControlSession, active: SessionContext) {
        active.controlSession = controlSession

        clockSyncJob?.cancel()
        clockSyncJob = scope.launch {
            while (scope.isActive && session === active) {
                try {
                    val sentAt = SystemClock.elapsedRealtime()
                    active.lastPingSentAt = sentAt
                    controlSession.send(WcpMessage(t = Wcp.Types.PING, id = "sync", ts = sentAt))
                } catch (e: Exception) {
                    Log.w(TAG, "Не удалось отправить ping", e)
                    break
                }

                delay(CLOCK_SYNC_INTERVAL_MS)
            }
        }
    }

    /**
     * Обработка ответа на наш ping и оценка расхождения часов.
     *
     * Наш ping нужен только для измерения кругового времени; само смещение часов считается
     * по метке времени из ping'а ПК: T_приёма − T_отправки − RTT/2. Складывать эти две
     * величины в одну нельзя — иначе смещение всегда получается нулевым.
     */
    private fun onPong(ts: Long?) {
        val active = session ?: return
        val sentAt = active.lastPingSentAt
        if (sentAt == 0L) return

        val receivedAt = SystemClock.elapsedRealtime()
        val rtt = (receivedAt - sentAt).coerceAtLeast(0)

        val senderTs = active.senderClockSample
        val senderTsAt = active.senderClockSampleAt
        if (senderTs == null || senderTsAt == 0L) return

        // Часы ПК и приставки идут с разными началами отсчёта: их разность и есть смещение.
        val offsetMs = (senderTsAt - senderTs) - rtt / 2

        clockOffsetMicros = offsetMs * 1000
        clockSynced = true
    }

    private suspend fun metricsLoop() {
        while (scope.isActive) {
            delay(METRICS_INTERVAL_MS)
            val active = session ?: continue
            if (!active.firstFrameRendered) continue

            val fps = windowFrames * (1000.0 / METRICS_INTERVAL_MS)
            val kbps = (windowBytes * 8 * (1000.0 / METRICS_INTERVAL_MS) / 1000).toInt()
            val audioKbps = (windowAudioBytes * 8 * (1000.0 / METRICS_INTERVAL_MS) / 1000).toInt()

            windowFrames = 0
            windowBytes = 0
            windowAudioBytes = 0

            val latency = if (clockSynced) {
                // Сквозная задержка: сколько времени прошло с момента формирования кадра на ПК.
                val nowMicros = SystemClock.elapsedRealtime() * 1000
                ((nowMicros - clockOffsetMicros) - lastRenderedPts) / 1000.0
            } else {
                Double.NaN
            }

            _state.value = ReceiverUiState.Streaming(
                sourceName = active.sourceName,
                width = active.videoWidth,
                height = active.videoHeight,
                fps = fps,
                kbps = kbps,
                latencyMs = latency,
                dropped = decoder?.dropped ?: 0,
                senderQueueMs = active.senderQueueMs,
                senderRttMs = active.senderRttMs,
                waitingForKeyframe = active.waitingForKeyframe,
                audioEnabled = audioDecoder != null,
                audioKbps = audioKbps,
            )

            // Рывки и отставание звука — то, что человек видит и слышит. Потери кадров
            // и задержка об этом молчат: картинка может дёргаться при нулевых потерях.
            val (maxGapMs, stutters) = decoder?.takeGapStats() ?: Pair(0.0, 0)
            val audioBacklogMs = audioDecoder?.currentBacklogMs() ?: 0

            // Телеметрия уходит на ПК: он показывает её в своём интерфейсе
            // и по ней же видит, успевает ли приставка декодировать поток.
            active.controlSession?.let { control ->
                try {
                    control.send(
                        WcpMessage.stats(
                            fps = Math.round(fps * 10) / 10.0,
                            dropped = (decoder?.dropped ?: 0) + discardedFrames.get(),
                            late = 0,
                            latencyMs = if (latency.isFinite() && latency > 0) latency else null,
                            maxGapMs = Math.round(maxGapMs * 10) / 10.0,
                            stutters = stutters,
                            audioBacklogMs = audioBacklogMs,
                        ),
                    )
                } catch (e: Exception) {
                    Log.w(TAG, "Не удалось отправить телеметрию", e)
                }
            }
        }
    }

    /**
     * Отдельный цикл восстановления изображения.
     *
     * Он обязан работать независимо от того, отрисован ли уже первый кадр: именно
     * в этот момент декодер чаще всего и теряет ключевой кадр (пересоздание кодека
     * занимает сотни миллисекунд, за которые очередь успевает переполниться).
     * Запросы ограничены по частоте, чтобы не дёргать кодировщик на ПК без нужды.
     */
    private suspend fun decoderWatchLoop() {
        while (scope.isActive) {
            delay(KEYFRAME_CHECK_INTERVAL_MS)

            val active = session ?: continue
            val current = decoder ?: continue

            if (!current.consumeKeyframeRequest()) {
                active.waitingForKeyframe = false
                continue
            }

            val now = SystemClock.elapsedRealtime()
            if (now - active.lastKeyframeRequestAt < KEYFRAME_REQUEST_INTERVAL_MS) {
                continue
            }

            active.lastKeyframeRequestAt = now
            active.waitingForKeyframe = true
            appendLog("Запрашиваю ключевой кадр")
            requestKeyframe(active)
        }
    }

    // ------------------------------------------------------------------
    // Состояние
    // ------------------------------------------------------------------

    private fun publishWaiting() {
        val info = decoderInfo
        val address = discovery?.localAddress ?: "—"
        val hasNetwork = address != "—" && address.isNotEmpty()

        _state.value = ReceiverUiState.Waiting(
            deviceName = deviceName,
            address = address,
            controlPort = control?.boundPort?.takeIf { it > 0 } ?: Wcp.CONTROL_PORT,
            mediaPort = media?.boundPort?.takeIf { it > 0 } ?: Wcp.MEDIA_PORT,
            discoveryPort = discovery?.boundPort?.takeIf { it > 0 } ?: Wcp.DISCOVERY_PORT,
            decoder = info,
            hasNetwork = hasNetwork,
        )
    }

    private fun publishStreaming(active: SessionContext) {
        _state.value = ReceiverUiState.Streaming(
            sourceName = active.sourceName,
            width = active.videoWidth,
            height = active.videoHeight,
            fps = 0.0,
            kbps = 0,
            latencyMs = Double.NaN,
            dropped = 0,
            senderQueueMs = 0.0,
            senderRttMs = 0.0,
            waitingForKeyframe = false,
        )
    }

    private fun endSession(
        failWith: Int? = null,
        code: String = Wcp.ErrorCodes.INTERNAL,
        detail: String = "",
        silent: Boolean = false,
    ) {
        session = null
        watchdogJob?.cancel()
        clockSyncJob?.cancel()
        releaseDecoder()

        media?.closeCurrent()
        clockSynced = false
        windowFrames = 0
        windowBytes = 0

        discovery?.state = Wcp.States.IDLE

        if (failWith != null) {
            _state.value = ReceiverUiState.Failed(code, failWith, detail)
        } else if (!silent) {
            publishWaiting()
        }
    }

    private fun buildCaps(): DeviceCaps {
        val info = decoderInfo
        return DeviceCaps(
            codecs = listOf("h264"),
            maxWidth = info?.maxWidth,
            maxHeight = info?.maxHeight,
            maxFps = info?.maxFps,
            audio = null,
            lowLatencyDecode = info?.supportsLowLatency,
        )
    }

    private fun publishStateToDiscovery() {
        val current = _state.value
        val announcer = discovery ?: return
        announcer.caps = buildCaps()
        announcer.state = if (current is ReceiverUiState.Waiting || current == null) {
            Wcp.States.IDLE
        } else {
            Wcp.States.BUSY
        }
    }

    private fun appendLog(line: String) {
        Log.i(TAG, line)

        val stamp = java.text.SimpleDateFormat("HH:mm:ss", Locale.US).format(java.util.Date())
        _log.value = (_log.value + "$stamp  $line").takeLast(LOG_LIMIT)
    }

    private fun resolveDeviceName(): String {
        val model = Build.MODEL?.takeIf { it.isNotBlank() } ?: "Android TV"
        return "WooCast — $model"
    }

    private fun resolveDeviceId(): String {
        val androidId = try {
            Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID)
        } catch (e: Exception) {
            null
        }

        val source = androidId?.takeIf { it.isNotBlank() } ?: deviceName + Build.FINGERPRINT
        val digest = java.security.MessageDigest.getInstance("SHA-256").digest(source.toByteArray())
        return digest.take(8).joinToString("") { "%02x".format(it) }
    }

    /** Контекст одной сессии с ПК. */
    private class SessionContext(
        val id: String,
        val token: Long,
        var senderName: String,
    ) {
        val sessionId: String = java.util.UUID.randomUUID().toString().substring(0, 8)

        @Volatile var sourceName: String = senderName
        @Volatile var videoWidth: Int = 0

        /** Частота кадров из stream_request — по ней определяется, что считать рывком. */
        @Volatile var videoFps: Int = 0

        /** Запас сглаживания из stream_request, мс. Ноль — минимальная задержка. */
        @Volatile var videoBufferMs: Int = 0

        /** Название контроллера, подключённого к приёмнику. */
        @Volatile var gamepadName: String? = null
        @Volatile var videoHeight: Int = 0
        @Volatile var mediaConnected: Boolean = false
        @Volatile var audioEnabled: Boolean = false

        /** Поправка выравнивания звука из stream_request, мс. */
        @Volatile var audioDelayMs: Int = 0

        @Volatile var audioFlowing: Boolean = false
        @Volatile var firstFrameRendered: Boolean = false
        @Volatile var waitingForKeyframe: Boolean = false
        @Volatile var senderQueueMs: Double = 0.0
        @Volatile var senderRttMs: Double = 0.0
        @Volatile var senderDropped: Int = 0
        @Volatile var lastPingSentAt: Long = 0
        @Volatile var lastKeyframeRequestAt: Long = 0

        /** Метка времени из ping'а ПК и момент её приёма — для оценки смещения часов. */
        @Volatile var senderClockSample: Long? = null
        @Volatile var senderClockSampleAt: Long = 0

        @Volatile var controlSession: ControlSession? = null
    }
}
