// SPDX-License-Identifier: MIT
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.net

import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface

/**
 * Служба обнаружения на стороне приёмника: отвечает на `discover` и периодически
 * рассылает `announce`, чтобы ПК видел приставку без ручного ввода адреса.
 */
class DiscoveryResponder(
    private val deviceId: String,
    private val deviceName: String,
    private val model: String,
    private val version: String,
) {

    private companion object {
        const val TAG = "WooCast/Discovery"
        const val SOCKET_TIMEOUT_MS = 1_000
    }

    /** Порт управления, сообщаемый отправителю. */
    @Volatile
    var controlPort: Int = Wcp.CONTROL_PORT

    /** Порт медиа, сообщаемый отправителю. */
    @Volatile
    var mediaPort: Int = Wcp.MEDIA_PORT

    /** Текущее состояние: `idle` или `busy`. */
    @Volatile
    var state: String = Wcp.States.IDLE

    /** Возможности приёмника, попадающие в `announce.caps`. */
    @Volatile
    var caps: DeviceCaps = DeviceCaps(codecs = listOf("h264"))

    /** Адрес, на котором слушает сокет обнаружения. */
    @Volatile
    var boundPort: Int = -1
        private set

    private var socket: DatagramSocket? = null
    private var job: Job? = null

    /** Локальный IPv4-адрес приставки (для показа на экране ожидания). */
    @Volatile
    var localAddress: String = "—"
        private set

    /** Запустить службу. Повторный вызов безопасен. */
    fun start(scope: CoroutineScope) {
        if (job != null) return

        job = scope.launch(Dispatchers.IO) {
            val udp = try {
                DatagramSocket(null).apply {
                    reuseAddress = true
                    broadcast = true
                    soTimeout = SOCKET_TIMEOUT_MS
                    bind(InetSocketAddress(Wcp.DISCOVERY_PORT))
                }
            } catch (e: Exception) {
                Log.e(TAG, "Не удалось занять порт ${Wcp.DISCOVERY_PORT}", e)
                // Резервный вариант: работаем без приёма `discover`, только широковещательные announce.
                try {
                    DatagramSocket(null).apply {
                        reuseAddress = true
                        broadcast = true
                        soTimeout = SOCKET_TIMEOUT_MS
                        bind(InetSocketAddress(0))
                    }
                } catch (inner: Exception) {
                    Log.e(TAG, "Служба обнаружения недоступна", inner)
                    return@launch
                }
            }

            socket = udp
            boundPort = udp.localPort
            localAddress = resolveLocalAddress() ?: "—"
            Log.i(TAG, "Обнаружение запущено на порту $boundPort, адрес $localAddress")

            val receiveJob = launch { receiveLoop(udp) }
            val announceJob = launch { announceLoop(udp) }

            try {
                receiveJob.join()
                announceJob.join()
            } finally {
                udp.close()
                socket = null
            }
        }
    }

    /** Остановить службу. */
    fun stop() {
        job?.cancel()
        job = null
        socket?.close()
        socket = null
        boundPort = -1
    }

    /** Отправить `announce` немедленно (например, сразу при старте). */
    suspend fun announceNow() {
        val udp = socket ?: return
        withContext(Dispatchers.IO) {
            sendAnnounce(udp, null, 0)
        }
    }

    private suspend fun receiveLoop(udp: DatagramSocket) {
        val buffer = ByteArray(8 * 1024)

        while (kotlinx.coroutines.currentCoroutineContext().isActive) {
            try {
                val packet = DatagramPacket(buffer, buffer.size)
                udp.receive(packet)

                val message = try {
                    WcpJson.decodeFromString<WcpMessage>(
                        WcpMessage.serializer(),
                        String(packet.data, 0, packet.length, Charsets.UTF_8),
                    )
                } catch (e: Exception) {
                    continue
                }

                if (message.t == Wcp.Types.DISCOVER) {
                    // Строго unicast: широковещательный ответ породил бы лавину пакетов.
                    sendAnnounce(udp, packet.address, packet.port)
                }
            } catch (e: java.net.SocketTimeoutException) {
                // Нормальная ситуация: проверяем флаг отмены и продолжаем.
            } catch (e: Exception) {
                if (!kotlinx.coroutines.currentCoroutineContext().isActive) return
                Log.w(TAG, "Ошибка приёма пакета обнаружения", e)
            }
        }
    }

    private suspend fun announceLoop(udp: DatagramSocket) {
        while (kotlinx.coroutines.currentCoroutineContext().isActive) {
            try {
                sendAnnounce(udp, null, 0)
            } catch (e: Exception) {
                Log.w(TAG, "Не удалось разослать announce", e)
            }

            delay(Wcp.ANNOUNCE_INTERVAL_MS)
        }
    }

    private fun sendAnnounce(udp: DatagramSocket, target: InetAddress?, targetPort: Int) {
        val payload = WcpJson
            .encodeToString(
                WcpMessage.serializer(),
                WcpMessage.announce(
                    id = deviceId,
                    name = deviceName,
                    model = model,
                    version = version,
                    controlPort = controlPort,
                    mediaPort = mediaPort,
                    state = state,
                    caps = caps,
                ),
            )
            .toByteArray(Charsets.UTF_8)

        if (target != null) {
            udp.send(DatagramPacket(payload, payload.size, target, targetPort))
            return
        }

        for (address in broadcastAddresses()) {
            try {
                udp.send(DatagramPacket(payload, payload.size, address, Wcp.DISCOVERY_PORT))
            } catch (e: Exception) {
                Log.w(TAG, "Не удалось отправить announce на $address", e)
            }
        }
    }

    /** Широковещательные адреса всех активных IPv4-интерфейсов. */
    private fun broadcastAddresses(): List<InetAddress> {
        val result = mutableListOf<InetAddress>()
        result.add(InetAddress.getByName("255.255.255.255"))

        try {
            for (nic in NetworkInterface.getNetworkInterfaces()) {
                if (!nic.isUp || nic.isLoopback) continue

                for (interfaceAddress in nic.interfaceAddresses) {
                    val address = interfaceAddress.address
                    if (address is Inet4Address) {
                        interfaceAddress.broadcast?.let { broadcast ->
                            if (result.none { it.hostAddress == broadcast.hostAddress }) {
                                result.add(broadcast)
                            }
                        }
                    }
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "Не удалось перечислить интерфейсы", e)
        }

        return result
    }

    /** Первый подходящий локальный IPv4-адрес — показывается на экране ожидания. */
    private fun resolveLocalAddress(): String? = try {
        NetworkInterface.getNetworkInterfaces()
            .asSequence()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { it.interfaceAddresses.asSequence() }
            .map { it.address }
            .filterIsInstance<Inet4Address>()
            .firstOrNull { it.isSiteLocalAddress }
            ?.hostAddress
            ?: NetworkInterface.getNetworkInterfaces()
                .asSequence()
                .filter { it.isUp && !it.isLoopback }
                .flatMap { it.interfaceAddresses.asSequence() }
                .map { it.address }
                .filterIsInstance<Inet4Address>()
                .firstOrNull()
                ?.hostAddress
    } catch (e: Exception) {
        null
    }
}
