// SPDX-License-Identifier: MIT
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.net

/**
 * Константы протокола WooCast (WCP/1) на стороне приёмника.
 *
 * Зеркало `src/WooCast.Protocol/Wcp.cs`. Любое расхождение здесь ломает совместимость,
 * поэтому значения продублированы дословно, а не «выведены» из чего-либо.
 */
object Wcp {

    /** Версия протокола: поле `wc` в JSON и `version` в медиа-заголовке. */
    const val VERSION = 1

    /** Порт UDP-обнаружения. */
    const val DISCOVERY_PORT = 47810

    /** Порт TCP-управления. */
    const val CONTROL_PORT = 47811

    /** Порт TCP-медиа, запрашиваемый по умолчанию. */
    const val MEDIA_PORT = 47812

    /** Группа multicast для сетей, где широковещательные пакеты ограничены. */
    const val MULTICAST_GROUP = "239.255.77.87"

    /** Период рассылки `announce`. */
    const val ANNOUNCE_INTERVAL_MS = 3_000L

    /** Сколько ждать медиа-соединение после `stream_ready`. */
    const val MEDIA_CONNECT_TIMEOUT_MS = 5_000L

    /** Максимальная длина строки JSON в управляющем канале. */
    const val MAX_JSON_LINE_BYTES = 64 * 1024

    /** Максимальная длина полезной нагрузки медиа-пакета (4 МиБ). */
    const val MAX_MEDIA_PAYLOAD = 4 * 1024 * 1024

    /** Размер заголовка медиа-пакета. */
    const val MEDIA_PACKET_HEADER_SIZE = 16

    /** Размер приветственного заголовка медиа-соединения. */
    const val MEDIA_HANDSHAKE_SIZE = 20

    /** ASCII-сигнатура начала медиа-соединения: «WCM1». */
    val MEDIA_MAGIC = byteArrayOf(0x57, 0x43, 0x4D, 0x31)

    /** Числовой идентификатор кодека H.264 в медиа-заголовке. */
    const val CODEC_H264_ID = 1

    /** Имя кодека H.264 в сообщениях `stream_request` / `announce.caps`. */
    const val CODEC_H264 = "h264"

    /** MIME-тип кодека для Android MediaCodec. */
    const val MIME_H264 = "video/avc"

    /** Типы сообщений управляющего канала. */
    object Types {
        const val ANNOUNCE = "announce"
        const val DISCOVER = "discover"

        const val HELLO = "hello"
        const val STREAM_REQUEST = "stream_request"
        const val STREAM_START = "stream_start"
        const val STATS = "stats"
        const val PING = "ping"
        const val STOP = "stop"
        const val BYE = "bye"

        const val HELLO_ACK = "hello_ack"
        const val STREAM_READY = "stream_ready"
        const val STREAM_REJECT = "stream_reject"
        const val STARTED = "started"
        const val REQUEST_KEYFRAME = "request_keyframe"
        const val PONG = "pong"
        const val STOPPED = "stopped"
        const val ERROR = "error"

        /**
         * Состояние управления с приёмника: кнопки и оси геймпада, подключённого к нему.
         *
         * Отправляет приёмник: контроллер подключён к приставке или телефону, а игра
         * работает на компьютере, где состояние подаётся в виртуальный геймпад.
         */
        const val INPUT = "input"
    }

    /** Причины отказа в старте трансляции. */
    object RejectReasons {
        const val BUSY = "busy"
        const val UNSUPPORTED_CODEC = "unsupported_codec"
        const val UNSUPPORTED_RESOLUTION = "unsupported_resolution"
        const val DECLINED = "declined"
        const val INTERNAL = "internal"
    }

    /** Коды ошибок протокола. */
    object ErrorCodes {
        const val PROTOCOL_ERROR = "protocol_error"
        const val BAD_TOKEN = "bad_token"
        const val BAD_STATE = "bad_state"
        const val INTERNAL = "internal"
    }

    /** Состояния приёмника в `announce.state`. */
    object States {
        const val IDLE = "idle"
        const val BUSY = "busy"
    }

    /** Типы источника захвата. */
    object SourceTypes {
        const val DISPLAY = "display"
        const val WINDOW = "window"
    }

    /** Флаги медиа-пакета. */
    object FrameFlags {
        const val NONE = 0
        const val KEYFRAME = 1
        const val CONFIG = 2
        const val AUDIO = 4
    }
}
