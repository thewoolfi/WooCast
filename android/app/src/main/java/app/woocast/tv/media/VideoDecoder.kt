// SPDX-License-Identifier: MIT
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.media

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import android.util.Log
import android.view.Surface
import app.woocast.tv.net.AccessUnit
import app.woocast.tv.net.AnnexB
import app.woocast.tv.net.ParameterSets
import app.woocast.tv.net.Wcp
import java.nio.ByteBuffer
import java.util.concurrent.LinkedBlockingDeque
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * Аппаратный декодер H.264, выводящий изображение напрямую в [Surface].
 *
 * Ключевые решения, обеспечивающие минимальную задержку и отсутствие «накопления» картинки:
 *  * декодирование в режиме Surface, а не ByteBuffer — кадры не копируются в память приложения;
 *  * каждый готовый кадр отдаётся на экран немедленно (`releaseOutputBuffer(index, true)`),
 *    без планирования по PTS: кадры и так приходят в реальном времени, а очередь рендера
 *    только добавила бы задержку;
 *  * если очередь входных кадров переполнена, выбрасывается самый старый кадр — задержка
 *    остаётся постоянной, а не растёт (при потере кадра запрашивается ключевой кадр);
 *  * при смене SPS/PPS кодек пересоздаётся на лету под то же самое окно вывода.
 */
class VideoDecoder(
    private val surface: Surface,
    private val listener: Listener,
    private val decoderInfo: DecoderInfo?,
    expectedWidth: Int = 0,
    expectedHeight: Int = 0,
) {

    interface Listener {
        /** Изменился размер видео (с учётом crop-прямоугольника). */
        fun onVideoSizeChanged(width: Int, height: Int)

        /** Кадр отдан на экран; [arrivalNanos] — момент прихода access unit'а. */
        fun onFrameRendered(ptsMicros: Long, arrivalNanos: Long)

        /** Первый кадр успешно отрисован. */
        fun onFirstFrame()

        /** Ошибка декодера. [recoverable] — можно ли продолжить после пересоздания. */
        fun onDecoderError(recoverable: Boolean, message: String)
    }

    private companion object {
        const val TAG = "WooCast/Decoder"

        /** Глубина очереди входных кадров. Больше — больше задержка, меньше — риск потери кадров. */
        /**
         * Предел очереди кадров — предохранитель, а не рабочий размер.
         *
         * Фактическую глубину задаёт запас сглаживания: кадр подаётся в декодер
         * не раньше положенного срока, поэтому очередь сама держится на нужном уровне.
         * Предел нужен лишь на случай, если поток пойдёт быстрее, чем мы успеваем.
         *
         * Шестьдесят четыре кадра — с запасом на самую большую настройку (600 мс
         * при 60 к/с это 36 кадров). Меньший предел обрезал бы запас: очередь
         * переполнялась бы, кадры выбрасывались, и картинка замирала — ровно то,
         * что и наблюдалось при значениях выше 200 мс.
         */
        const val QUEUE_CAPACITY = 64

        /**
         * Запас сглаживания перед подачей кадра в декодер, наносекунды.
         *
         * Двести пятьдесят миллисекунд — примерно пятнадцать кадров при 60 к/с.
         * Столько требует главная неровность потока: ключевой кадр в разы больше
         * обычного (измерено 190 КБ против 10 КБ), и его передача занимает сотни
         * миллисекунд, в течение которых кадры не приходят вовсе. На замерах это
         * давало замирание картинки на 662 мс раз в несколько секунд и постоянное
         * дёргание до 100 мс между замираниями.
         *
         * Запас меньше всплеска бесполезен: он не покрывает паузу. Двести пятьдесят
         * миллисекунд покрывают её с избытком, а задержка остаётся в пять раз меньше,
         * чем у OBS или YouTube — там запас измеряется секундами.
         */
        const val JitterBufferMs = 250L

        /** Таймаут ожидания входного буфера, микросекунды. */
        const val INPUT_TIMEOUT_US = 4_000L

        /** Таймаут ожидания выходного буфера, микросекунды. */
        const val OUTPUT_TIMEOUT_US = 2_000L

        /**
         * Если кадры не отрисовывались дольше этого времени, картинка считается «вставшей».
         * Порог сознательно больше интервала ключевых кадров (2 с): принудительный IDR требует
         * перезапуска кодировщика на ПК, и слишком частая его просьба превращается в порочный
         * круг — пауза на перезапуске снова выглядит как замирание.
         */
        const val STALL_THRESHOLD_NANOS = 2_500_000_000L
    }

    private val queue = LinkedBlockingDeque<PendingFrame>(QUEUE_CAPACITY)

    /** Запас сглаживания в наносекундах — из него считается момент подачи кадра. */
    @Volatile
    private var jitterBufferNanos = JitterBufferMs * 1_000_000L

    /**
     * Задать запас сглаживания в миллисекундах.
     *
     * Значение приходит от пользователя: это его выбор между плавностью и задержкой.
     * Ноль означает «показывать сразу» — минимальная задержка, но пачка кадров после
     * ключевого будет видна рывком.
     */
    fun setBufferMs(milliseconds: Int) {
        jitterBufferNanos = milliseconds.coerceAtLeast(0).toLong() * 1_000_000L
    }
    private val running = AtomicBoolean(false)

    @Volatile
    private var codec: MediaCodec? = null

    private var thread: Thread? = null

    @Volatile
    private var parameterSets: ParameterSets? = null

    @Volatile
    private var pendingReconfigure = false

    @Volatile
    private var streamWidth = expectedWidth

    @Volatile
    private var streamHeight = expectedHeight

    /** Смена набора параметров кодирования: кадры прошлого поколения декодировать нельзя. */
    private val generation = AtomicInteger(0)

    private val droppedFrames = AtomicInteger(0)

    /** Экземпляр кодека в процессе создания: нужен, чтобы освободить его при сбое. */
    private var createdCodec: MediaCodec? = null

    /**
     * Освободить экземпляр, который не удалось настроить.
     *
     * Без этого он остаётся занятым в системе: кодиеков у приставки несколько,
     * и после нескольких неудач новые не выдаются вообще.
     */
    private fun releaseCreatedCodec() {
        val leaked = createdCodec ?: return
        createdCodec = null

        try {
            leaked.release()
            Log.w(TAG, "Неудачно созданный кодек освобождён")
        } catch (e: Exception) {
            Log.w(TAG, "Не удалось освободить кодек", e)
        }
    }

    /** Самая длинная пауза между показанными кадрами за текущее окно, наносекунды. */
    private val maxGapNanos = AtomicLong(0)

    /** Сколько раз пауза вдвое превысила нормальный интервал кадра. */
    private val stutterCount = AtomicInteger(0)

    /** Момент вывода предыдущего кадра — по нему считается пауза. */
    private var lastShownNanos = 0L

    /** Ожидаемый интервал между кадрами, наносекунды: 0, пока частота неизвестна. */
    @Volatile
    private var nominalGapNanos = 0L

    /** Опорная точка выравнивания вывода: локальное время и метка первого кадра. */
    private var anchorLocalNanos = 0L
    private var anchorPtsMicros = 0L
    private val fedFrames = AtomicInteger(0)
    private val drainedFrames = AtomicInteger(0)
    private val renderedFrames = AtomicLong(0)
    private val firstFrameReported = AtomicBoolean(false)
    private val keyframeRequested = AtomicBoolean(false)

    /** Момент прихода последнего поданного кадра — для оценки «свежести» изображения. */
    @Volatile
    private var lastArrivalNanos = 0L

    /** Последний ключевой кадр — запас на случай пересоздания кодека. */
    @Volatile
    private var pendingKeyframe: PendingFrame? = null

    /** Момент последней отрисовки — по нему определяется «замирание» картинки. */
    @Volatile
    private var lastRenderNanos = 0L

    /** Кадры, выброшенные из очереди из-за нехватки времени на декодирование. */
    val dropped: Int get() = droppedFrames.get()

    /** Сколько кадров реально отрисовано. */
    val rendered: Long get() = renderedFrames.get()

    /**
     * Забрать измерения рывков за прошедшее окно и начать новое.
     *
     * @return самая длинная пауза между показанными кадрами в миллисекундах и число пауз,
     * вдвое превысивших нормальный интервал.
     */
    fun takeGapStats(): Pair<Double, Int> {
        val gap = maxGapNanos.getAndSet(0)
        val stutters = stutterCount.getAndSet(0)
        return Pair(gap / 1_000_000.0, stutters)
    }

    /** Сообщить ожидаемую частоту кадров — по ней определяется, что считать рывком. */
    fun setFrameRate(fps: Int) {
        if (fps > 0) {
            nominalGapNanos = 1_000_000_000L / fps
        }
    }

    /** Выброшен ли хотя бы один кадр с момента последнего запроса ключевого кадра. */
    fun consumeKeyframeRequest(): Boolean = keyframeRequested.getAndSet(false)

    /** Поставить access unit в очередь на декодирование. Вызывается из сетевого потока. */
    fun submit(unit: AccessUnit, ptsMicros: Long) {
        val sets = unit.parameterSets

        if (sets != null && sets != parameterSets) {
            // Параметры кодирования изменились: кодек будет пересоздан под них.
            // Очередь чистим здесь, из потока-отправителя: кадры старого разрешения
            // декодировать уже нельзя, а ключевой кадр нового поколения придёт следом
            // и должен попасть в свежий кодек.
            generation.incrementAndGet()
            queue.clear()
            parameterSets = sets
            pendingReconfigure = true
        }

        if (parameterSets == null && !unit.hasParameterSets) {
            // До первого SPS/PPS декодировать нечего.
            return
        }

        // AUD и наборы параметров декодеру не нужны: csd-0/csd-1 уже переданы при настройке.
        val payload = AnnexB.extractSlicePayload(unit.data) ?: return
        val frame = PendingFrame(payload, unit.isKeyframe, System.nanoTime(), ptsMicros, generation.get())

        if (!queue.offerLast(frame)) {
            if (frame.isKeyframe) {
                // Новый ключевой кадр обесценивает всё, что накопилось до него:
                // кадры до IDR декодировать нельзя, а сам IDR терять нельзя ни в коем случае —
                // без него декодер не выдаст ни одного изображения.
                queue.clear()
                queue.offerLast(frame)
                keyframeRequested.set(false)
            } else {
                // Обычный кадр: освобождаем место, выбрасывая самый старый.
                val evicted = queue.pollFirst()
                if (evicted != null) {
                    droppedFrames.incrementAndGet()

                    // Ключевой кадр нужен только тогда, когда картинка встала.
                    // Если декодер просто не успевает за потоком, перезапуск кодировщика
                    // на ПК только добавит рывок и ничего не исправит.
                    val sinceLastRender = System.nanoTime() - lastRenderNanos
                    if (lastRenderNanos == 0L || sinceLastRender > STALL_THRESHOLD_NANOS) {
                        keyframeRequested.set(true)
                    }
                }
                queue.offerLast(frame)
            }
        }

        // Последний ключевой кадр храним отдельно от очереди: он нужен, чтобы сразу
        // запустить декодирование после (пере)создания кодека, даже если очередь
        // успела переполниться за время его инициализации.
        if (frame.isKeyframe) {
            pendingKeyframe = frame
        }

        lastArrivalNanos = frame.arrivalNanos
    }

    /** Запустить поток декодирования. */
    fun start() {
        if (!running.compareAndSet(false, true)) return

        val worker = Thread({ runLoop() }, "woocast-decoder")
        worker.priority = Thread.MAX_PRIORITY
        thread = worker
        worker.start()
    }

    /** Остановить декодирование и освободить кодек. */
    fun stop() {
        if (!running.compareAndSet(true, false)) return

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
            while (running.get()) {
                if (codec == null || pendingReconfigure) {
                    if (!recreateCodec()) {
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
                    handleCodecException(e)
                } catch (e: IllegalStateException) {
                    Log.e(TAG, "Некорректное состояние кодека", e)
                    releaseCodec()
                }
            }
        } catch (e: InterruptedException) {
            Thread.currentThread().interrupt()
        } finally {
            releaseCodec()
        }
    }

    /** Создать (или пересоздать) кодек под текущие наборы параметров. */
    private fun recreateCodec(): Boolean {
        val sets = parameterSets ?: return false

        releaseCodec()
        pendingReconfigure = false

        // Очередь здесь не очищается: кадр, который принёс новые параметры, должен
        // попасть в новый кодек. Кадры прошлых поколений отсеиваются в feedInput.

        val width = if (streamWidth > 0) streamWidth else 1920
        val height = if (streamHeight > 0) streamHeight else 1080

        return try {
            val format = MediaFormat.createVideoFormat(Wcp.MIME_H264, width, height)
            format.setByteBuffer("csd-0", ByteBuffer.wrap(sets.sps))
            format.setByteBuffer("csd-1", ByteBuffer.wrap(sets.pps))

            // Ограничение входного буфера: один access unit 1080p редко превышает 1 МиБ.
            format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 1 shl 20)

            if (decoderInfo?.supportsAdaptivePlayback == true) {
                format.setInteger(MediaFormat.KEY_MAX_WIDTH, maxOf(width, 1920))
                format.setInteger(MediaFormat.KEY_MAX_HEIGHT, maxOf(height, 1080))
            }

            // Низкая задержка: без этого кодек держит внутри несколько кадров.
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && decoderInfo?.supportsLowLatency == true) {
                format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            }

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                // 0 — максимальный приоритет потока декодера.
                format.setInteger(MediaFormat.KEY_PRIORITY, 0)
            }

            val created = MediaCodec.createDecoderByType(Wcp.MIME_H264)
            createdCodec = created

            // Пробуем включить низкую задержку; если прошивка не приняла флаг — настраиваемся без него.
            try {
                created.configure(format, surface, null, 0)
            } catch (e: Exception) {
                Log.w(TAG, "Не удалось настроить кодек с низкой задержкой, пробуем обычный режим", e)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    format.removeKey(MediaFormat.KEY_LOW_LATENCY)
                }
                created.configure(format, surface, null, 0)
            }

            created.start()
            createdCodec = null

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                try {
                    created.setVideoScalingMode(MediaCodec.VIDEO_SCALING_MODE_SCALE_TO_FIT)
                } catch (e: Exception) {
                    Log.w(TAG, "setVideoScalingMode не поддержан", e)
                }
            }

            codec = created
            fedFrames.set(0)
            drainedFrames.set(0)
            Log.i(
                TAG,
                "Декодер запущен: ${created.name}, ${width}×$height, " +
                    "csd-0=${sets.sps.size} Б, csd-1=${sets.pps.size} Б",
            )

            // Без ключевого кадра декодер молчит. Если он есть в запасе — начинаем с него,
            // иначе просим у отправителя.
            if (queue.none { it.isKeyframe }) {
                val stashed = pendingKeyframe

                if (stashed != null && stashed.generation == generation.get()) {
                    // Кадры без предшествующего IDR декодировать нельзя, поэтому очередь
                    // можно смело очистить.
                    queue.clear()
                    queue.offerFirst(stashed)
                } else {
                    keyframeRequested.set(true)
                }
            }

            true
        } catch (e: Exception) {
            Log.e(TAG, "Не удалось создать декодер", e)

            // Освобождаем экземпляр, если он успел создаться: у приставки кодиеков всего
            // несколько, и каждая неудачная попытка без освобождения съедает один.
            // После нескольких таких попыток система перестаёт выдавать кодеки вообще —
            // и тогда не работает уже ни одна версия приложения до перезагрузки приставки.
            // Именно это и произошло: «раньше работало» перестало работа́ть навсегда.
            releaseCreatedCodec()

            // Здесь чаще всего и кроется причина: кодек отказывается настраиваться
            // на предложенный поток. Сообщение исключения бывает пустым, поэтому
            // называем и тип исключения тоже.
            val reason = e.message?.takeIf { it.isNotBlank() } ?: e.javaClass.simpleName
            listener.onDecoderError(true, "настройка: $reason")
            releaseCodec()
            // Небольшая пауза, чтобы не крутить цикл намертво при постоянной ошибке.
            Thread.sleep(200)
            false
        }
    }

    private fun feedInput(active: MediaCodec) {
        val currentGeneration = generation.get()

        // Отбрасываем кадры, закодированные с другими параметрами.
        var frame = queue.pollFirst() ?: return
        while (frame.generation != currentGeneration) {
            frame = queue.pollFirst() ?: return
        }

        // Запас сглаживания: кадр подаётся не в момент прихода, а спустя JitterBufferMs.
        // Без него видна любая неровность: кадры приходят пачками после ключевого,
        // и каждая пачка показывается рывком. Так устроены все потоковые системы —
        // у OBS и YouTube запас измеряется секундами, у нас он минимальный, чтобы
        // задержка осталась в пределах сотен миллисекунд.
        val waitNanos = (frame.arrivalNanos + jitterBufferNanos) - System.nanoTime()
        if (waitNanos > 0) {
            queue.offerFirst(frame)

            // Спим короткими долями: за это время может прийти ключевой кадр,
            // который обесценивает накопленное.
            Thread.sleep(minOf(waitNanos / 1_000_000L, 8L).coerceAtLeast(1L))
            return
        }

        val inputIndex = try {
            active.dequeueInputBuffer(INPUT_TIMEOUT_US)
        } catch (e: MediaCodec.CodecException) {
            throw e
        }

        if (inputIndex < 0) {
            // Входной буфер недоступен — возвращаем кадр в начало очереди и попробуем позже.
            queue.offerFirst(frame)
            return
        }

        try {
            val buffer = active.getInputBuffer(inputIndex) ?: return
            buffer.clear()

            if (buffer.capacity() < frame.data.size) {
                // Кадр больше буфера: это признак того, что кодировщик на ПК задал
                // слишком маленький входной размер. Кадр пропускаем, но не падаем.
                droppedFrames.incrementAndGet()
                keyframeRequested.set(true)
                active.queueInputBuffer(inputIndex, 0, 0, frame.ptsMicros, 0)
                return
            }

            buffer.put(frame.data)
            active.queueInputBuffer(inputIndex, 0, frame.data.size, frame.ptsMicros, 0)

            val fed = fedFrames.incrementAndGet()
            if (fed <= 3 || fed % 300 == 0) {
                Log.i(
                    TAG,
                    "Подано кадров: $fed, последний ${frame.data.size} Б, " +
                        "ключевой=${frame.isKeyframe}, буфер=${buffer.capacity()} Б",
                )
            }
        } catch (e: IllegalStateException) {
            Log.w(TAG, "Не удалось подать кадр в декодер", e)
        }
    }

    private fun drainOutput(active: MediaCodec) {
        val info = MediaCodec.BufferInfo()

        while (running.get()) {
            val outputIndex = try {
                active.dequeueOutputBuffer(info, OUTPUT_TIMEOUT_US)
            } catch (e: MediaCodec.CodecException) {
                throw e
            }

            when {
                outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER -> return

                outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                    applyOutputFormat(active.outputFormat)
                }

                outputIndex == MediaCodec.INFO_OUTPUT_BUFFERS_CHANGED -> {
                    // Устаревшее событие, актуально только для API < 21.
                }

                outputIndex >= 0 -> {
                    // В режиме Surface поле size часто равно нулю: размер кадра известен
                    // только самому кодеку. Поэтому признаком «пустого» буфера служит
                    // флаг конфигурации, а не size — иначе изображение не появится вовсе.
                    val isConfig = (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0

                    if (isConfig) {
                        active.releaseOutputBuffer(outputIndex, false)
                    } else {
                        releaseFrame(active, outputIndex, info.presentationTimeUs)
                        onRendered(info.presentationTimeUs, info.size)
                    }
                }
            }
        }
    }

    /**
     * Вывести кадр на поверхность — немедленно.
     *
     * Здесь была попытка выравнивать вывод по меткам времени, чтобы сгладить пачки
     * кадров после ключевого. Она оказалась хуже болезни: любая придержка занимает
     * выходные буферы декодера, а их всего несколько — картинка либо дёргается сильнее,
     * либо пропадает вовсе. Простое «показать сразу» работает надёжно, и пока оно
     * не доказано обратное, здесь остаётся именно оно.
     */
    private fun releaseFrame(active: MediaCodec, outputIndex: Int, ptsMicros: Long) {
        active.releaseOutputBuffer(outputIndex, true)
    }

    private fun onRendered(ptsMicros: Long, size: Int) {
        val now = System.nanoTime()
        lastRenderNanos = now

        // Рывок — это пауза между показанными кадрами, а не потеря кадра.
        // Кадры могут приходить все, но неравномерно, и тогда картинка дёргается
        // при формально идеальной телеметрии. Именно это и нужно измерять.
        if (lastShownNanos != 0L) {
            val gap = now - lastShownNanos
            if (gap > maxGapNanos.get()) {
                maxGapNanos.set(gap)
            }

            val nominal = nominalGapNanos
            if (nominal > 0 && gap > nominal * 2) {
                stutterCount.incrementAndGet()
            }
        }

        lastShownNanos = now

        val drained = drainedFrames.incrementAndGet()
        if (drained <= 3 || drained % 300 == 0) {
            Log.i(TAG, "Отрисовано кадров: $drained, размер буфера=$size, pts=${ptsMicros / 1000} мс")
        }

        renderedFrames.incrementAndGet()

        // Точная сквозная задержка считается в движке по синхронизации часов через ping/pong;
        // здесь передаётся момент прихода последнего поданного кадра.
        listener.onFrameRendered(ptsMicros, lastArrivalNanos)

        if (firstFrameReported.compareAndSet(false, true)) {
            listener.onFirstFrame()
        }

        if (size <= 0) {
            Log.w(TAG, "Пустой выходной кадр")
        }
    }

    @Suppress("DEPRECATION")
    private fun applyOutputFormat(format: MediaFormat) {
        var width = format.getInteger(MediaFormat.KEY_WIDTH, streamWidth)
        var height = format.getInteger(MediaFormat.KEY_HEIGHT, streamHeight)

        // Кодировщик может добавить выравнивающие поля — их нужно вычесть,
        // иначе пропорции на экране будут неверными.
        if (format.containsKey("crop-left") && format.containsKey("crop-right")) {
            width = format.getInteger("crop-right") - format.getInteger("crop-left") + 1
        }
        if (format.containsKey("crop-top") && format.containsKey("crop-bottom")) {
            height = format.getInteger("crop-bottom") - format.getInteger("crop-top") + 1
        }

        if (width > 0 && height > 0 && (width != streamWidth || height != streamHeight)) {
            streamWidth = width
            streamHeight = height
            Log.i(TAG, "Размер видео: ${width}×$height")
            listener.onVideoSizeChanged(width, height)

            // Если разрешение изменилось, кодек пересоздаётся под новый размер.
            pendingReconfigure = true
        }
    }

    /**
     * Собрать описание ошибки кодекa из всего, что он о себе сообщает.
     *
     * Поле `message` у MediaCodec почти всегда пустое, а всё содержательное лежит
     * в `diagnosticInfo` (текст от вендорного кодека) и в коде ошибки. Без них
     * на экран попадало «Декодер:» и ничего больше — то есть диагноз терялся ровно
     * там, где был нужен.
     */
    private fun describeCodecException(e: MediaCodec.CodecException): String {
        val parts = mutableListOf<String>()

        e.message?.takeIf { it.isNotBlank() }?.let { parts += it }
        e.diagnosticInfo?.takeIf { it.isNotBlank() }?.let { parts += it }

        parts += try {
            when (e.errorCode) {
                MediaCodec.CodecException.ERROR_INSUFFICIENT_RESOURCE -> "нехватка ресурсов"
                MediaCodec.CodecException.ERROR_RECLAIMED -> "кодек отобран системой"
                else -> "код ошибки ${e.errorCode}"
            }
        } catch (inner: Exception) {
            "код ошибки недоступен"
        }

        return parts.joinToString(" · ")
    }

    private fun handleCodecException(e: MediaCodec.CodecException) {
        val recoverable = try {
            e.isRecoverable
        } catch (inner: Exception) {
            false
        }

        Log.e(TAG, "Ошибка декодера (recoverable=$recoverable): ${e.diagnosticInfo}", e)
        listener.onDecoderError(recoverable, describeCodecException(e))

        releaseCodec()
        pendingReconfigure = true
        queue.clear()

        if (!recoverable) {
            // Полный сброс: параметры придут заново с ближайшим ключевым кадром.
            parameterSets = null
            decoderInfo?.let { /* остаёмся на том же декодере */ }
        }
    }

    private fun releaseCodec() {
        val active = codec ?: return
        codec = null

        try {
            active.stop()
        } catch (e: Exception) {
            Log.w(TAG, "Ошибка при остановке кодека", e)
        }

        try {
            active.release()
        } catch (e: Exception) {
            Log.w(TAG, "Ошибка при освобождении кодека", e)
        }
    }

    private class PendingFrame(
        val data: ByteArray,
        val isKeyframe: Boolean,
        val arrivalNanos: Long,
        val ptsMicros: Long,
        val generation: Int,
    )
}
