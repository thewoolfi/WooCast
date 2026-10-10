// SPDX-License-Identifier: MIT
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.net

import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket

/**
 * Одно управляющее соединение: обмен сообщениями по одной JSON-строке.
 *
 * Чтение реализовано вручную, чтобы жёстко ограничить длину строки: некорректный
 * или враждебный отправитель не должен заставить приставку выделить произвольный объём памяти.
 */
class ControlSession(val socket: Socket) {

    private companion object {
        const val TAG = "WooCast/Control"
    }

    private val input = BufferedInputStream(socket.getInputStream(), 16 * 1024)
    private val output = BufferedOutputStream(socket.getOutputStream(), 16 * 1024)

    private val line = StringBuilder(512)

    /** Удалённый адрес отправителя. */
    val remoteAddress: String
        get() = (socket.remoteSocketAddress as? InetSocketAddress)?.address?.hostAddress ?: "?"

    /** Прочитать следующее сообщение. `null` — соединение закрыто корректно. */
    @Throws(IOException::class)
    fun read(): WcpMessage? {
        line.setLength(0)
        var totalBytes = 0

        while (true) {
            val b = input.read()
            if (b < 0) {
                return if (line.isEmpty()) null else decode(line)
            }

            totalBytes++
            if (totalBytes > Wcp.MAX_JSON_LINE_BYTES) {
                throw IOException("Строка управляющего канала превышает ${Wcp.MAX_JSON_LINE_BYTES} байт")
            }

            if (b == '\n'.code) {
                return decode(line)
            }

            if (b != '\r'.code) {
                line.append(b.toChar())
            }
        }
    }

    private fun decode(text: StringBuilder): WcpMessage? {
        val raw = text.toString().trim()
        if (raw.isEmpty()) return null

        return try {
            WcpJson.decodeFromString(WcpMessage.serializer(), raw)
        } catch (e: Exception) {
            Log.w(TAG, "Некорректное сообщение: $raw", e)
            throw IOException("Некорректный JSON в управляющем канале", e)
        }
    }

    /** Отправить сообщение. */
    @Throws(IOException::class)
    fun send(message: WcpMessage) {
        val payload = WcpJson.encodeToString(WcpMessage.serializer(), message).toByteArray(Charsets.UTF_8)
        output.write(payload)
        output.write('\n'.code)
        output.flush()
    }

    fun close() {
        try {
            socket.close()
        } catch (e: IOException) {
            Log.w(TAG, "Ошибка закрытия управляющего соединения", e)
        }
    }
}

/**
 * Сервер управляющего канала. В каждый момент обслуживается только одна сессия —
 * телевизор показывает то, что транслирует один ПК.
 */
class ControlServer(
    private val requestedPort: Int,
    private val onSession: suspend (ControlSession) -> Unit,
) {

    private companion object {
        const val TAG = "WooCast/ControlServer"
    }

    private var serverSocket: ServerSocket? = null
    private var job: Job? = null

    /** Фактический порт прослушивания. */
    @Volatile
    var boundPort: Int = -1
        private set

    /**
     * Запустить сервер. Если запрошенный порт занят, берётся свободный —
     * фактическое значение всегда сообщается отправителю в `announce`.
     */
    fun start(scope: CoroutineScope) {
        if (job != null) return

        job = scope.launch(Dispatchers.IO) {
            val server = try {
                ServerSocket().apply {
                    reuseAddress = true
                    bind(InetSocketAddress(requestedPort))
                }
            } catch (e: Exception) {
                Log.w(TAG, "Порт $requestedPort занят, выбираем свободный", e)
                try {
                    ServerSocket().apply {
                        reuseAddress = true
                        bind(InetSocketAddress(0))
                    }
                } catch (inner: Exception) {
                    Log.e(TAG, "Не удалось запустить управляющий сервер", inner)
                    return@launch
                }
            }

            serverSocket = server
            boundPort = server.localPort
            Log.i(TAG, "Управляющий сервер слушает порт $boundPort")

            while (isActive) {
                val client = try {
                    server.accept()
                } catch (e: Exception) {
                    if (!isActive) break
                    Log.w(TAG, "Ошибка accept", e)
                    continue
                }

                // Отключаем алгоритм Нагла: управляющие сообщения должны уходить мгновенно.
                try {
                    client.tcpNoDelay = true
                } catch (e: IOException) {
                    Log.w(TAG, "Не удалось выставить TCP_NODELAY", e)
                }

                try {
                    withContext(Dispatchers.IO) {
                        onSession(ControlSession(client))
                    }
                } catch (e: Exception) {
                    Log.w(TAG, "Сессия завершилась с ошибкой", e)
                } finally {
                    try {
                        client.close()
                    } catch (e: IOException) {
                        // Соединение уже могло быть закрыто обработчиком.
                    }
                }
            }
        }
    }

    fun stop() {
        job?.cancel()
        job = null
        try {
            serverSocket?.close()
        } catch (e: IOException) {
            Log.w(TAG, "Ошибка закрытия сервера", e)
        }
        serverSocket = null
        boundPort = -1
    }
}
