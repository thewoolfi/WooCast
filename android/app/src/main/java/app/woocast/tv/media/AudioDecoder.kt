// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.media

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.util.Log
import app.woocast.tv.net.Adts
import java.nio.ByteBuffer
import java.util.concurrent.LinkedBlockingDeque

/**
 * Часы медиапотока: сколько времени потока звучит прямо сейчас.
 *
 * Нужны для синхронизации: видеокадр выводится не сразу после декодирования,
 * а когда часы дойдут до его метки времени.
 */
interface MediaClock {
    /**
     * Текущее время потока в микросекундах или `null`, если часов пока нет
     * (звук выключен, ещё не начал играть или недоступен).
     */
    fun currentStreamTimeMicros(): Long?
}

/**
 * Декодирование и воспроизведение звука: ADTS → MediaCodec → AudioTrack.
 *
 * Звук выводится сразу, как только декодирован. Попытка придержать его на старте,
 * чтобы выровнять с изображением, оказалась хуже болезни: задержка ощущалась как
 * отставание звука на секунду. Простое «играть сразу» работает предсказуемо.
 */
class AudioDecoder(
    private val listener: Listener,
    /**
     * Задержка вывода звука, мс: базовая разница конвейеров плюс поправка пользователя.
     * Ниже неё отставание не выравнивается — она задана намеренно.
     */
    private val startDelayMs: Int = DefaultStartDelayMs,
    /**
     * Контекст приложения — нужен для управления звуком: без запроса фокуса часть
     * прошивок (в частности MIUI) не даёт приложению играть вовсе.
     */
    private val context: android.content.Context? = null,
) : MediaClock {

    interface Listener {
        /**
         * Звук пошёл: сообщаем параметры для интерфейса.
         *
         * @param volumePercent громкость мультимедиа в процентах, `null` если узнать
         *   не удалось. Ноль означает, что звук играет в тишину — на телефоне это
         *   самая частая причина «нет звука», и об этом нужно сказать прямо.
         */
        fun onStarted(sampleRate: Int, channels: Int, volumePercent: Int?)

        /** Ошибка декодера или вывода звука. */
        fun onError(message: String)
    }

    /**
     * Запросить фокус звука и узнать громкость мультимедиа.
     *
     * @return громкость в процентах или `null`, если управление звуком недоступно.
     */
    private fun requestAudioFocusAndDescribeVolume(): Int? {
        val manager = context?.getSystemService(android.content.Context.AUDIO_SERVICE)
            as? android.media.AudioManager ?: return null

        try {
            if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.O) {
                val request = android.media.AudioFocusRequest
                    .Builder(android.media.AudioManager.AUDIOFOCUS_GAIN)
                    .setAudioAttributes(
                        android.media.AudioAttributes.Builder()
                            .setUsage(android.media.AudioAttributes.USAGE_MEDIA)
                            .setContentType(android.media.AudioAttributes.CONTENT_TYPE_MOVIE)
                            .build(),
                    )
                    .build()

                manager.requestAudioFocus(request)
            } else {
                @Suppress("DEPRECATION")
                manager.requestAudioFocus(
                    null,
                    android.media.AudioManager.STREAM_MUSIC,
                    android.media.AudioManager.AUDIOFOCUS_GAIN,
                )
            }
        } catch (e: Exception) {
            Log.w(TAG, "Не удалось запросить фокус звука", e)
        }

        return try {
            val max = manager.getStreamMaxVolume(android.media.AudioManager.STREAM_MUSIC)
            val current = manager.getStreamVolume(android.media.AudioManager.STREAM_MUSIC)

            if (max <= 0) null else current * 100 / max
        } catch (e: Exception) {
            null
        }
    }

    companion object {
        const val TAG = "WooCast/Audio"

        /** Глубина очереди кадров: примерно 130 мс звука при 48 кГц. */
        const val QUEUE_CAPACITY = 6

        /**
         * Запас сверх заданной задержки, после которого звук начинает догонять.
         *
         * Шестьдесят миллисекунд — примерно три кадра AAC: столько можно выбросить
         * незаметно для слуха, но этого хватает, чтобы отставание не копилось.
         */
        const val BacklogHeadroomMs = 60

        const val DEQUEUE_TIMEOUT_US = 4_000L

        /**
         * Сколько звука накопить до начала воспроизведения.
         *
         * Это компенсация разницы задержек: видеоконвейер на ПК (захват экрана плюс
         * аппаратный кодировщик) отдаёт кадр позже, чем звуковой. Измеренная разница —
         * около 120 мс. Пока буфер не наполнен, вывод молчит; дальше он играет
         * непрерывно с этой задержкой.
         */
        const val DefaultStartDelayMs = 120

        /** Запас сверх задержки, чтобы AudioTrack не голодал на старте. */
        const val StartDelayMarginMs = 80
    }

    private val queue = LinkedBlockingDeque<PendingAudioFrame>(QUEUE_CAPACITY)

    @Volatile
    private var running = false

    private var thread: Thread? = null
    private var codec: MediaCodec? = null
    private var track: AudioTrack? = null

    /** Параметры из первого разобранного заголовка — из них строится csd-0. */
    @Volatile
    private var config: ByteArray? = null

    @Volatile
    private var sampleRate = 48_000

    @Volatile
    private var channels = 2

    /** Метка времени первого поданного кадра — начало шкалы звука. */
    @Volatile
    private var basePtsMicros = -1L

    /** Кадры, уже проигранные устройством: позиция головки переносится в общий счётчик. */
    private var playedFrames = 0L

    private var lastHeadPosition = 0

    private var decodedFrames = 0L

    /** Записано байт PCM до начала воспроизведения — по нему отсчитывается задержка. */
    private var writtenBytes = 0L

    /** Воспроизведение уже начато. */
    private var playbackStarted = false

    /** Сколько буферов пропущено, чтобы догнать отставание. */
    private var skippedBuffers = 0L

    /** Кадров звука декодировано — для телеметрии. */
    val framesDecoded: Long get() = decodedFrames

    /** Звук готов и играет. */
    val isPlaying: Boolean get() = track?.playState == AudioTrack.PLAYSTATE_PLAYING

    override fun currentStreamTimeMicros(): Long? {
        val active = track ?: return null
        val base = basePtsMicros
        if (base < 0) return null

        // playbackHeadPosition считает проигранные кадры, но переполняется как Int,
        // поэтому накапливаем сами.
        val head = active.playbackHeadPosition
        if (head < lastHeadPosition) {
            // Переполнение счётчика — переносим разницу через границу Int.
            playedFrames += (Int.MAX_VALUE - lastHeadPosition).toLong() + head + 1
        } else {
            playedFrames += (head - lastHeadPosition).toLong()
        }

        lastHeadPosition = head

        return base + playedFrames * 1_000_000L / sampleRate.coerceAtLeast(8_000)
    }

    /** Запустить поток декодирования. */
    fun start() {
        if (running) return
        running = true

        val worker = Thread({ runLoop() }, "woocast-audio")

        // Обычный приоритет, а не максимальный. На слабой приставке звук и видео
        // делят не только процессор, но и память, и максимальный приоритет звука
        // отбирал время у декодера изображения — это и видно как рывки картинки.
        // Запас буфера дорожки покрывает разницу в планировании.
        worker.priority = Thread.NORM_PRIORITY + 1
        thread = worker
        worker.start()
    }

    /** Поставить кадр в очередь. Вызывается из сетевого потока. */
    fun submit(adtsFrame: ByteArray, ptsMicros: Long) {
        val header = Adts.parseHeader(adtsFrame)
        if (header == null) {
            Log.w(TAG, "Пропущен кадр звука с некорректным ADTS (${adtsFrame.size} байт)")
            return
        }

        val payload = Adts.extractPayload(adtsFrame) ?: return

        if (config == null) {
            // Первый кадр задаёт конфигурацию для декодера.
            config = header.buildAudioSpecificConfig()
            sampleRate = header.sampleRate
            channels = header.channels
            Log.i(TAG, "Звук: AAC ${header.sampleRate} Гц, каналов ${header.channels}")
        }

        if (basePtsMicros < 0) {
            basePtsMicros = ptsMicros
        }

        val frame = PendingAudioFrame(payload, ptsMicros)

        if (!queue.offerLast(frame)) {
            // Звук важнее полноты: выбрасываем самый старый кадр, чтобы не копить задержку.
            queue.pollFirst()
            queue.offerLast(frame)
        }
    }

    /** Остановить декодирование и освободить ресурсы. */
    fun stop() {
        if (!running) return
        running = false

        try {
            thread?.join(1_000)
        } catch (e: InterruptedException) {
            Thread.currentThread().interrupt()
        }

        thread = null
        queue.clear()
    }

    private fun runLoop() {
        try {
            while (running) {
                if (codec == null) {
                    if (!createCodec()) {
                        Thread.sleep(10)
                        continue
                    }
                }

                val active = codec ?: continue

                try {
                    drainOutput(active)
                    feedInput(active)
                    drainOutput(active)
                } catch (e: MediaCodec.CodecException) {
                    Log.e(TAG, "Ошибка аудиодекодера: ${e.diagnosticInfo}", e)
                    listener.onError(e.message ?: "Ошибка аудиодекодера")
                    releaseCodec()
                } catch (e: IllegalStateException) {
                    Log.e(TAG, "Некорректное состояние аудиодекодера", e)
                    releaseCodec()
                }
            }
        } catch (e: InterruptedException) {
            Thread.currentThread().interrupt()
        } finally {
            releaseCodec()
        }
    }

    private fun createCodec(): Boolean {
        val csd = config ?: return false

        return try {
            val format = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_AAC, sampleRate, channels)
            format.setByteBuffer("csd-0", ByteBuffer.wrap(csd))

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                format.setInteger(MediaFormat.KEY_PRIORITY, 0)
            }

            val created = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_AUDIO_AAC)
            created.configure(format, null, null, 0)
            created.start()

            codec = created
            Log.i(TAG, "Аудиодекодер запущен: ${created.name}, ${sampleRate} Гц, каналов $channels")
            true
        } catch (e: Exception) {
            Log.e(TAG, "Не удалось создать аудиодекодер", e)
            listener.onError(e.message ?: "Аудиодекодер недоступен")
            Thread.sleep(200)
            false
        }
    }

    private fun feedInput(active: MediaCodec) {
        val frame = queue.pollFirst() ?: return

        val inputIndex = active.dequeueInputBuffer(DEQUEUE_TIMEOUT_US)
        if (inputIndex < 0) {
            queue.offerFirst(frame)
            return
        }

        try {
            val buffer = active.getInputBuffer(inputIndex) ?: return
            buffer.clear()

            if (buffer.capacity() < frame.payload.size) {
                Log.w(TAG, "Кадр звука больше входного буфера — пропущен")
                active.queueInputBuffer(inputIndex, 0, 0, frame.ptsMicros, 0)
                return
            }

            buffer.put(frame.payload)
            active.queueInputBuffer(inputIndex, 0, frame.payload.size, frame.ptsMicros, 0)
        } catch (e: IllegalStateException) {
            Log.w(TAG, "Не удалось подать кадр звука", e)
        }
    }

    private fun drainOutput(active: MediaCodec) {
        val info = MediaCodec.BufferInfo()

        while (running) {
            val outputIndex = active.dequeueOutputBuffer(info, DEQUEUE_TIMEOUT_US)

            when {
                outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER -> return

                outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                    // Формат вывода известен только сейчас: по нему создаём AudioTrack.
                    ensureTrack(active.outputFormat)
                }

                outputIndex == MediaCodec.INFO_OUTPUT_BUFFERS_CHANGED -> {
                    // Устаревшее событие, актуально только для API < 21.
                }

                outputIndex >= 0 -> {
                    val buffer = active.getOutputBuffer(outputIndex)

                    if (buffer != null && info.size > 0 && (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) == 0) {
                        writeToTrack(buffer, info)
                    }

                    active.releaseOutputBuffer(outputIndex, false)
                }
            }
        }
    }

    private fun writeToTrack(buffer: ByteBuffer, info: MediaCodec.BufferInfo) {
        val active = track ?: return

        // Отставание звука выравнивается постепенно, по одному буферу. Совсем без этого
        // оно остаётся навсегда: звук приходит с той же скоростью, что и играет, и сам
        // из буфера не выходит — на измерениях так набиралось больше сотни миллисекунд.
        //
        // Порог отсчитывается от заданной задержки выравнивания: она задана намеренно,
        // и «догонять» её нельзя — иначе смысл поправки теряется.
        if (playbackStarted && backlogMs(active) > startDelayMs + BacklogHeadroomMs) {
            skippedBuffers++
            return
        }

        buffer.position(info.offset)
        buffer.limit(info.offset + info.size)

        // MODE_STREAM: запись блокируется, когда буфер полон, — это и есть естественное
        // согласование темпа: мы не убегаем вперёд устройства вывода.
        val written = active.write(buffer, info.size, AudioTrack.WRITE_BLOCKING)
        if (written > 0) {
            decodedFrames++
            writtenBytes += written
        }
    }

    /** Сколько звука записано вперёд фактического воспроизведения, миллисекунды. */
    /** Текущее отставание звука от воспроизведения, миллисекунды. Для телеметрии. */
    fun currentBacklogMs(): Int {
        val active = track ?: return 0
        return if (playbackStarted) backlogMs(active) else 0
    }

    private fun backlogMs(active: AudioTrack): Int {
        val bytesPerSecond = sampleRate.toLong() * channels.coerceAtLeast(1) * 2
        if (bytesPerSecond <= 0) {
            return 0
        }

        val head = active.playbackHeadPosition
        if (head < lastHeadPosition) {
            // Переполнение счётчика — считаем, что отставания нет.
            return 0
        }

        val playedBytes = (head.toLong() - lastHeadPosition) * channels.coerceAtLeast(1) * 2
        val backlogBytes = writtenBytes - playedBytes

        return (backlogBytes * 1000 / bytesPerSecond).toInt().coerceAtLeast(0)
    }

    /**
     * Начать воспроизведение, когда накопилась задержка выравнивания.
     *
     * До этого момента звук только копится в буфере дорожки. Так выравнивание
     * не стоит ничего: задержка берётся один раз на старте, а не придерживает
     * каждый кадр по отдельности.
     */
    private fun startPlaybackWhenBuffered(active: AudioTrack) {
        if (playbackStarted) {
            return
        }

        // Кадр = все каналы по два байта (PCM 16 бит).
        val bytesPerSecond = sampleRate.toLong() * channels.coerceAtLeast(1) * 2
        val threshold = bytesPerSecond * startDelayMs / 1000

        if (writtenBytes < threshold) {
            return
        }

        active.play()
        playbackStarted = true
        Log.i(TAG, "Вывод звука начат с задержкой ${startDelayMs} мс (${writtenBytes} Б в буфере)")
    }

    /** Создать AudioTrack по фактическому формату вывода декодера. */
    private fun ensureTrack(outputFormat: MediaFormat) {
        if (track != null) return

        val rate = outputFormat.getInteger(MediaFormat.KEY_SAMPLE_RATE, sampleRate)
        val channelCount = outputFormat.getInteger(MediaFormat.KEY_CHANNEL_COUNT, channels)

        sampleRate = rate
        channels = channelCount

        try {
            val channelMask = if (channelCount >= 2) {
                AudioFormat.CHANNEL_OUT_STEREO
            } else {
                AudioFormat.CHANNEL_OUT_MONO
            }

            val minBuffer = AudioTrack.getMinBufferSize(rate, channelMask, AudioFormat.ENCODING_PCM_16BIT)

            // Буфер небольшой: звук выводится сразу, придерживать его не нужно,
            // а лишний объём — это лишняя задержка.
            val bufferSize = minBuffer.coerceAtLeast(4096) * 2

            val builder = AudioTrack.Builder()
                .setAudioAttributes(
                    AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_MEDIA)
                        .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
                        .build(),
                )
                .setAudioFormat(
                    AudioFormat.Builder()
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setSampleRate(rate)
                        .setChannelMask(channelMask)
                        .build(),
                )
                .setTransferMode(AudioTrack.MODE_STREAM)
                .setBufferSizeInBytes(bufferSize)

            val created = builder.build()
            created.play()

            track = created
            lastHeadPosition = 0
            playedFrames = 0
            writtenBytes = 0
            playbackStarted = true

            // Фокус звука запрашивается до начала воспроизведения: без него часть
            // прошивок молча не даёт приложению играть — снаружи это выглядит как
            // «на телевизоре звук есть, а на телефоне нет».
            val volume = requestAudioFocusAndDescribeVolume()
            if (volume != null) {
                listener.onStarted(rate, channelCount, volume)
            } else {
                listener.onStarted(rate, channelCount, null)
            }

            Log.i(
                TAG,
                "Вывод звука открыт: $rate Гц, каналов $channelCount, " +
                    "буфер $bufferSize байт, задержка выравнивания $startDelayMs мс",
            )
        } catch (e: Exception) {
            Log.e(TAG, "Не удалось открыть вывод звука", e)
            listener.onError(e.message ?: "Вывод звука недоступен")
        }
    }

    private fun releaseCodec() {
        val active = codec
        codec = null

        if (active != null) {
            try {
                active.stop()
            } catch (e: Exception) {
                Log.w(TAG, "Ошибка при остановке аудиодекодера", e)
            }

            try {
                active.release()
            } catch (e: Exception) {
                Log.w(TAG, "Ошибка при освобождении аудиодекодера", e)
            }
        }

        val trackToRelease = track
        track = null

        if (trackToRelease != null) {
            try {
                trackToRelease.pause()
                trackToRelease.flush()
                trackToRelease.release()
            } catch (e: Exception) {
                Log.w(TAG, "Ошибка при освобождении вывода звука", e)
            }
        }
    }

    private class PendingAudioFrame(val payload: ByteArray, val ptsMicros: Long)
}
