// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.net

import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.BufferedInputStream
import java.io.EOFException
import java.io.IOException
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** Заголовок одного медиа-пакета. */
data class MediaFrameHeader(
    val payloadLength: Int,
    val ptsMicros: Long,
    val flags: Int,
) {
    val isKeyframe: Boolean get() = (flags and Wcp.FrameFlags.KEYFRAME) != 0
    val isConfig: Boolean get() = (flags and Wcp.FrameFlags.CONFIG) != 0
    val isAudio: Boolean get() = (flags and Wcp.FrameFlags.AUDIO) != 0
}

/** Разобранный приветственный заголовок медиа-соединения. */
data class MediaHandshake(
    val version: Int,
    val codec: Byte,
    val flags: Int,
    val token: Long,
) {
    val hasAudio: Boolean get() = (flags and 1) != 0
}

/** Ошибка медиа-протокола с кодом для экрана ошибки. */
class MediaProtocolException(val code: String, message: String, cause: Throwable? = null) :
    IOException(message, cause)

/**
 * Приём медиапотока.
 *
 * Соединения обслуживаются параллельно, но активно только последнее: при подключении нового
 * предыдущее закрывается. Это важно при перезапуске трансляции на ПК — старый сокет может
 * остаться полуоткрытым, и последовательная обработка заблокировала бы приём нового потока.
 */
class MediaServer(private val requestedPort: Int) {

    private companion object {
        const val TAG = "WooCast/Media"

        /** Размер буфера чтения: ключевой кадр 1080p редко превышает 256 КиБ. */
        const val READ_BUFFER_SIZE = 256 * 1024

        /** Если данных нет дольше этого времени, соединение считается мёртвым. */
        const val SOCKET_TIMEOUT_MS = 15_000
    }

    private var serverSocket: ServerSocket? = null
    private var acceptJob: Job? = null
    private var connectionJob: Job? = null

    @Volatile
    private var currentSocket: Socket? = null

    /** Фактический порт прослушивания. */
    @Volatile
    var boundPort: Int = -1
        private set

    /** Проверка хендшейка. Вернуть `false`, чтобы разорвать соединение. */
    var onHandshake: ((MediaHandshake) -> Boolean)? = null

    /** Готовый access unit. */
    var onAccessUnit: ((MediaHandshake, AccessUnit, MediaFrameHeader) -> Unit)? = null

    /** Кадр звука: полный ADTS-кадр и метка времени на общей с видео шкале. */
    var onAudioFrame: ((MediaHandshake, ByteArray, MediaFrameHeader) -> Unit)? = null

    /** Соединение закрыто: [error] равен `null` при плановом завершении. */
    var onClosed: ((MediaHandshake?, Throwable?) -> Unit)? = null

    /** Запустить прослушивание. */
    fun start(scope: CoroutineScope) {
        if (acceptJob != null) return

        acceptJob = scope.launch {
            val server = try {
                ServerSocket().apply {
                    reuseAddress = true
                    bind(InetSocketAddress(requestedPort))
                }
            } catch (e: Exception) {
                Log.w(TAG, "Медиа-порт $requestedPort занят, выбираем свободный", e)
                try {
                    ServerSocket().apply {
                        reuseAddress = true
                        bind(InetSocketAddress(0))
                    }
                } catch (inner: Exception) {
                    Log.e(TAG, "Не удалось запустить медиа-сервер", inner)
                    return@launch
                }
            }

            serverSocket = server
            boundPort = server.localPort
            Log.i(TAG, "Медиа-сервер слушает порт $boundPort")

            while (isActive) {
                val client = try {
                    server.accept()
                } catch (e: Exception) {
                    if (!isActive) break
                    Log.w(TAG, "Ошибка accept медиа-соединения", e)
                    continue
                }

                // Новое соединение вытесняет предыдущее.
                closeCurrent()

                try {
                    client.tcpNoDelay = true
                    client.receiveBufferSize = 1 shl 20
                    client.soTimeout = SOCKET_TIMEOUT_MS
                } catch (e: IOException) {
                    Log.w(TAG, "Не удалось настроить медиа-сокет", e)
                }

                currentSocket = client
                connectionJob = launch { handleConnection(client) }
            }
        }
    }

    /** Закрыть активное соединение, не останавливая сервер. */
    fun closeCurrent() {
        try {
            currentSocket?.close()
        } catch (e: IOException) {
            // Уже закрыто.
        }
        currentSocket = null
    }

    fun stop() {
        acceptJob?.cancel()
        acceptJob = null
        connectionJob?.cancel()
        connectionJob = null
        closeCurrent()

        try {
            serverSocket?.close()
        } catch (e: IOException) {
            Log.w(TAG, "Ошибка закрытия медиа-сервера", e)
        }

        serverSocket = null
        boundPort = -1
    }

    private suspend fun handleConnection(socket: Socket) {
        var handshake: MediaHandshake? = null
        var failure: Throwable? = null

        try {
            val input = BufferedInputStream(socket.getInputStream(), READ_BUFFER_SIZE)
            handshake = readHandshake(input)

            val accepted = onHandshake?.invoke(handshake) ?: true
            if (!accepted) {
                throw MediaProtocolException(Wcp.ErrorCodes.BAD_TOKEN, "Неверный токен медиа-сессии")
            }

            Log.i(TAG, "Медиа-соединение принято: кодек=${handshake.codec}, аудио=${handshake.hasAudio}")
            pump(input, handshake)
        } catch (e: SocketTimeoutException) {
            failure = e
            Log.w(TAG, "Медиа-соединение простаивает и закрыто")
        } catch (e: Exception) {
            failure = e
            if (e is MediaProtocolException) {
                Log.w(TAG, "Медиа-протокол: ${e.message}")
            } else {
                Log.w(TAG, "Медиа-сессия завершилась", e)
            }
        } finally {
            try {
                socket.close()
            } catch (e: IOException) {
                // Уже закрыто.
            }

            if (currentSocket === socket) {
                currentSocket = null
            }

            onClosed?.invoke(handshake, failure)
        }
    }

    private inline fun pump(input: BufferedInputStream, handshake: MediaHandshake) {
        val headerBuffer = ByteArray(Wcp.MEDIA_PACKET_HEADER_SIZE)
        val splitter = AnnexBSplitter()
        var frames = 0L
        var bytes = 0L
        var audioFrames = 0L
        var audioBytes = 0L

        while (acceptJob?.isActive == true) {
            readFully(input, headerBuffer)
            val header = parseHeader(headerBuffer)

            if (header.payloadLength <= 0 || header.payloadLength > Wcp.MAX_MEDIA_PAYLOAD) {
                throw MediaProtocolException(
                    Wcp.ErrorCodes.PROTOCOL_ERROR,
                    "Недопустимая длина медиа-пакета: ${header.payloadLength}",
                )
            }

            if (header.isAudio) {
                // Звук идёт отдельными пакетами с тем же заголовком, что и видео.
                val audio = ByteArray(header.payloadLength)
                readFully(input, audio)
                audioBytes += header.payloadLength
                audioFrames++

                onAudioFrame?.invoke(handshake, audio, header)

                if (audioFrames == 1L || audioFrames % 1500L == 0L) {
                    Log.d(TAG, "Получено кадров звука: $audioFrames, ${audioBytes / 1024} КиБ")
                }

                continue
            }

            val payload = ByteArray(header.payloadLength)
            readFully(input, payload)
            bytes += header.payloadLength
            frames++

            for (unit in splitter.append(payload, 0, payload.size)) {
                onAccessUnit?.invoke(handshake, unit, header)
            }

            if (frames == 1L || frames % 600L == 0L) {
                Log.d(TAG, "Получено кадров: $frames, ${bytes / 1024} КиБ")
            }
        }
    }

    private fun readHandshake(input: BufferedInputStream): MediaHandshake {
        val buffer = ByteArray(Wcp.MEDIA_HANDSHAKE_SIZE)
        readFully(input, buffer)

        if (!buffer.copyOfRange(0, 4).contentEquals(Wcp.MEDIA_MAGIC)) {
            throw MediaProtocolException(
                Wcp.ErrorCodes.PROTOCOL_ERROR,
                "Некорректный заголовок медиа-потока (ожидалось WCM1)",
            )
        }

        val byteBuffer = ByteBuffer.wrap(buffer).order(ByteOrder.BIG_ENDIAN)
        byteBuffer.position(4)
        val version = byteBuffer.get().toInt() and 0xFF
        val codec = byteBuffer.get()
        val flags = byteBuffer.short.toInt() and 0xFFFF
        val token = byteBuffer.long

        if (version != Wcp.VERSION) {
            throw MediaProtocolException(
                Wcp.ErrorCodes.PROTOCOL_ERROR,
                "Неподдерживаемая версия медиа-протокола: $version",
            )
        }

        return MediaHandshake(version, codec, flags, token)
    }

    private fun parseHeader(buffer: ByteArray): MediaFrameHeader {
        val byteBuffer = ByteBuffer.wrap(buffer).order(ByteOrder.BIG_ENDIAN)
        val length = byteBuffer.int
        val pts = byteBuffer.long
        val flags = byteBuffer.get().toInt() and 0xFF
        return MediaFrameHeader(length, pts, flags)
    }

    /** Прочитать ровно [buffer].size байт или выбросить [EOFException]. */
    @Throws(IOException::class)
    private fun readFully(input: BufferedInputStream, buffer: ByteArray) {
        var offset = 0
        while (offset < buffer.size) {
            val read = input.read(buffer, offset, buffer.size - offset)
            if (read < 0) {
                throw EOFException("Медиа-соединение закрыто (прочитано $offset из ${buffer.size})")
            }
            offset += read
        }
    }
}
