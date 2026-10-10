// SPDX-License-Identifier: MIT
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.net

import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * Одно сообщение управляющего канала WCP/1.
 *
 * Плоский DTO со всеми возможными полями — сознательное повторение решения на стороне C#:
 * неизвестные поля игнорируются, совместимость вперёд не требует полиморфной десериализации,
 * а разбор остаётся тривиальным.
 */
@Serializable
data class WcpMessage(
    val wc: Int = Wcp.VERSION,
    val t: String? = null,
    val id: String? = null,
    val re: String? = null,

    // Обнаружение и идентификация
    val name: String? = null,
    val model: String? = null,
    val version: String? = null,
    val proto: Int? = null,
    val controlPort: Int? = null,
    val mediaPort: Int? = null,
    val state: String? = null,
    val caps: DeviceCaps? = null,
    val app: AppInfo? = null,
    val receiver: PeerInfo? = null,

    // Параметры трансляции
    val video: VideoSpec? = null,
    val audio: AudioSpec? = null,
    val source: SourceSpec? = null,
    val session: String? = null,
    val token: Long? = null,
    val reason: String? = null,
    val code: String? = null,
    val message: String? = null,

    /** Состояние управления: кнопки и оси геймпада, подключённого к приёмнику. */
    val input: InputSpec? = null,

    // Телеметрия
    val fps: Double? = null,
    val kbps: Int? = null,
    val dropped: Int? = null,
    val queueMs: Double? = null,
    val rttMs: Double? = null,
    val late: Int? = null,
    val latencyMs: Double? = null,
    /** Самая длинная пауза между показанными кадрами, мс — то, что видно как рывок. */
    val maxGapMs: Double? = null,
    /** Сколько раз пауза вдвое превысила нормальную. */
    val stutters: Int? = null,
    /** Насколько звук записан вперёд воспроизведения, мс. */
    val audioBacklogMs: Int? = null,
    val ts: Long? = null,
    val event: String? = null,
) {
    companion object {
        fun announce(
            id: String,
            name: String,
            model: String,
            version: String,
            controlPort: Int,
            mediaPort: Int,
            state: String,
            caps: DeviceCaps,
        ) = WcpMessage(
            t = Wcp.Types.ANNOUNCE,
            id = id,
            name = name,
            model = model,
            version = version,
            proto = Wcp.VERSION,
            controlPort = controlPort,
            mediaPort = mediaPort,
            state = state,
            caps = caps,
        )

        fun helloAck(re: String?, session: String, token: Long, receiver: PeerInfo) = WcpMessage(
            t = Wcp.Types.HELLO_ACK,
            re = re,
            session = session,
            token = token,
            receiver = receiver,
            proto = Wcp.VERSION,
        )

        fun streamReady(re: String?, mediaPort: Int, token: Long) = WcpMessage(
            t = Wcp.Types.STREAM_READY,
            re = re,
            mediaPort = mediaPort,
            token = token,
        )

        fun streamReject(re: String?, reason: String, message: String? = null) = WcpMessage(
            t = Wcp.Types.STREAM_REJECT,
            re = re,
            reason = reason,
            message = message,
        )

        fun started(re: String? = null) = WcpMessage(t = Wcp.Types.STARTED, re = re)

        fun requestKeyframe() = WcpMessage(t = Wcp.Types.REQUEST_KEYFRAME)

        fun pong(re: String?, ts: Long?) = WcpMessage(t = Wcp.Types.PONG, re = re, ts = ts)

        fun stopped(re: String? = null) = WcpMessage(t = Wcp.Types.STOPPED, re = re)

        /** Телеметрия приёмника: темп декодирования, потери, рывки и отставание звука. */
        fun stats(
            fps: Double,
            dropped: Int,
            late: Int,
            latencyMs: Double?,
            maxGapMs: Double? = null,
            stutters: Int? = null,
            audioBacklogMs: Int? = null,
        ) = WcpMessage(
            t = Wcp.Types.STATS,
            fps = fps,
            dropped = dropped,
            late = late,
            latencyMs = latencyMs,
            maxGapMs = maxGapMs,
            stutters = stutters,
            audioBacklogMs = audioBacklogMs,
        )

        fun error(re: String?, code: String, message: String) =
            WcpMessage(t = Wcp.Types.ERROR, re = re, code = code, message = message)

        /** Состояние управления с приёмника: кнопки и оси геймпада. */
        fun input(state: InputSpec) = WcpMessage(t = Wcp.Types.INPUT, input = state)
    }
}

/**
 * Состояние управления: кнопки и оси геймпада, подключённого к приёмнику.
 *
 * Значения кнопок — из [InputButtons], пределы осей — из [InputAxes]. Отправляется
 * при изменениях, поэтому [seq] позволяет заметить пропуск.
 */
@Serializable
data class InputSpec(
    val seq: Long = 0,
    val buttons: Int = 0,
    val lx: Int = 0,
    val ly: Int = 0,
    val rx: Int = 0,
    val ry: Int = 0,
    val lt: Int = 0,
    val rt: Int = 0,
    val device: String? = null,
)

/** Кнопки геймпада в сообщении управления — битовая маска. Общая для обеих сторон. */
object InputButtons {
    const val A = 1 shl 0
    const val B = 1 shl 1
    const val X = 1 shl 2
    const val Y = 1 shl 3
    const val LEFT_BUMPER = 1 shl 4
    const val RIGHT_BUMPER = 1 shl 5
    const val BACK = 1 shl 6
    const val START = 1 shl 7
    const val LEFT_STICK = 1 shl 8
    const val RIGHT_STICK = 1 shl 9
    const val GUIDE = 1 shl 10
    const val DPAD_UP = 1 shl 11
    const val DPAD_DOWN = 1 shl 12
    const val DPAD_LEFT = 1 shl 13
    const val DPAD_RIGHT = 1 shl 14
}

/** Пределы осей и курков в сообщении управления. */
object InputAxes {
    const val STICK_MIN = -32768
    const val STICK_MAX = 32767
    const val TRIGGER_MAX = 255

    fun clampStick(value: Int): Int = value.coerceIn(STICK_MIN, STICK_MAX)

    fun clampTrigger(value: Int): Int = value.coerceIn(0, TRIGGER_MAX)

    /**
     * Перевести значение оси Android в диапазон стика.
     *
     * У Android «вверх» — это отрицательное значение оси, а у геймпада Xbox и в нашей
     * нумерации «вверх» положительное. Без разворота стик работал бы наоборот.
     *
     * Диапазон стика несимметричен (−32768…32767), поэтому умножение разное для
     * разных знаков: иначе крайнее положение слева не достигалось бы, и стик
     * упирался бы в −32767.
     */
    fun fromAxis(value: Float, invert: Boolean = false): Int {
        val clamped = value.coerceIn(-1f, 1f)
        val signed = if (invert) -clamped else clamped

        val scaled = if (signed < 0f) signed * 32768f else signed * STICK_MAX
        return clampStick(scaled.toInt())
    }

    /**
     * Перевести значение курка Android в диапазон курка геймпада.
     *
     * Все оси курков в Android лежат в пределах от нуля до единицы, поэтому
     * переноса диапазона не требуется — нужен только предел значений.
     */
    fun fromTrigger(value: Float): Int = clampTrigger((value.coerceIn(0f, 1f) * TRIGGER_MAX).toInt())
}

@Serializable
data class DeviceCaps(
    val codecs: List<String>? = null,
    val maxWidth: Int? = null,
    val maxHeight: Int? = null,
    val maxFps: Int? = null,
    val audio: List<String>? = null,
    val lowLatencyDecode: Boolean? = null,
)

@Serializable
data class AppInfo(
    val name: String? = null,
    val version: String? = null,
)

@Serializable
data class PeerInfo(
    val id: String? = null,
    val name: String? = null,
    val model: String? = null,
    val version: String? = null,
)

@Serializable
data class VideoSpec(
    val codec: String? = null,
    val width: Int = 0,
    val height: Int = 0,
    val fps: Int = 0,
    val bitrate: Int = 0,
    val profile: String? = null,
    /** Запас сглаживания на приёмнике, мс: выбор между плавностью и задержкой. */
    val bufferMs: Int = 0,
)

@Serializable
data class AudioSpec(
    val enabled: Boolean = false,
    val codec: String? = null,
    val bitrate: Int = 0,
    /** Поправка выравнивания звука, мс: положительная — звук позже картинки. */
    val delay: Int = 0,
)

@Serializable
data class SourceSpec(
    val type: String? = null,
    @SerialName("id") val sourceId: String? = null,
    val title: String? = null,
)

/**
 * Настройки JSON управляющего канала.
 *
 * `ignoreUnknownKeys` обязателен: отправитель может быть новее приёмника.
 * `explicitNulls = false` вместе с `encodeDefaults = true` даёт компактные сообщения,
 * в которых всегда присутствуют значимые поля (например `wc`), но нет `null`-полей.
 */
@OptIn(ExperimentalSerializationApi::class)
val WcpJson: Json = Json {
    ignoreUnknownKeys = true
    isLenient = true
    encodeDefaults = true
    explicitNulls = false
    coerceInputValues = true
}
