// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.net

/**
 * Разбор ADTS — формата, в котором по сети приходит звук.
 *
 * Зеркало `src/WooCast.Protocol/Media/AdtsFrameReader.cs`. Приёмник получает кадры
 * целиком, вместе с заголовком: из него берётся конфигурация для MediaCodec,
 * а сам заголовок перед подачей в декодер отрезается.
 */
object Adts {

    /** Частоты дискретизации по индексу из заголовка ADTS. */
    val SAMPLE_RATES = intArrayOf(
        96000, 88200, 64000, 48000, 44100, 32000, 24000,
        22050, 16000, 12000, 11025, 8000, 7350,
    )

    /** Сколько сэмплов содержит один кадр AAC. */
    const val SAMPLES_PER_FRAME = 1024

    /** Минимальная длина заголовка ADTS без CRC. */
    const val MIN_HEADER_LENGTH = 7

    /**
     * Разобрать заголовок ADTS.
     *
     * @return заголовок или `null`, если данных мало или потеряна синхронизация.
     */
    fun parseHeader(data: ByteArray, offset: Int = 0): AdtsHeader? {
        if (offset < 0 || data.size - offset < MIN_HEADER_LENGTH) return null

        // Синхрослово: 12 единичных бит.
        if (data[offset].toInt() and 0xFF != 0xFF) return null
        if (data[offset + 1].toInt() and 0xF0 != 0xF0) return null

        val protectionAbsent = (data[offset + 1].toInt() and 0x01) != 0
        val profile = (data[offset + 2].toInt() and 0xC0) shr 6
        val sampleRateIndex = (data[offset + 2].toInt() and 0x3C) shr 2
        val channelConfiguration =
            ((data[offset + 2].toInt() and 0x01) shl 2) or ((data[offset + 3].toInt() and 0xC0) shr 6)
        val frameLength =
            ((data[offset + 3].toInt() and 0x03) shl 11) or
                (data[offset + 4].toInt() shl 3) or
                ((data[offset + 5].toInt() and 0xE0) shr 5)

        if (frameLength < MIN_HEADER_LENGTH) return null
        if (sampleRateIndex > 12) return null

        return AdtsHeader(
            frameLength = frameLength,
            headerLength = if (protectionAbsent) 7 else 9,
            sampleRateIndex = sampleRateIndex,
            channelConfiguration = channelConfiguration,
            audioObjectType = profile + 1,
        )
    }

    /** Проверить, что пакет — целый ADTS-кадр (заголовок согласован с длиной). */
    fun isCompleteFrame(data: ByteArray): Boolean {
        val header = parseHeader(data) ?: return false
        return header.frameLength == data.size
    }

    /** Вырезать сжатые данные без заголовка — их принимает MediaCodec. */
    fun extractPayload(frame: ByteArray): ByteArray? {
        val header = parseHeader(frame) ?: return null
        if (header.frameLength > frame.size) return null
        return frame.copyOfRange(header.headerLength, header.frameLength)
    }
}

/**
 * Заголовок одного ADTS-кадра.
 *
 * @param frameLength полная длина кадра вместе с заголовком.
 * @param headerLength длина заголовка: 7 байт или 9 при наличии CRC.
 * @param sampleRateIndex индекс частоты дискретизации.
 * @param channelConfiguration конфигурация каналов.
 * @param audioObjectType тип аудиообъекта: 2 — AAC-LC.
 */
data class AdtsHeader(
    val frameLength: Int,
    val headerLength: Int,
    val sampleRateIndex: Int,
    val channelConfiguration: Int,
    val audioObjectType: Int,
) {
    /** Частота дискретизации в герцах. */
    val sampleRate: Int get() = Adts.SAMPLE_RATES[sampleRateIndex.coerceIn(0, 12)]

    /** Число каналов; для конфигурации 7 закодировано 8 каналов. */
    val channels: Int
        get() = when (channelConfiguration) {
            0 -> 2
            7 -> 8
            else -> channelConfiguration
        }

    /** Длительность кадра в микросекундах. */
    val durationMicros: Long get() = Adts.SAMPLES_PER_FRAME * 1_000_000L / sampleRate

    /**
     * AudioSpecificConfig — те два байта, которые MediaCodec ожидает в `csd-0`.
     *
     * Раскладка: 5 бит типа объекта, 4 бита индекса частоты, 4 бита каналов
     * и 3 нулевых бита GASpecificConfig.
     */
    fun buildAudioSpecificConfig(): ByteArray {
        val config = (audioObjectType and 0x1F) shl 11 or
            ((sampleRateIndex and 0x0F) shl 7) or
            ((channelConfiguration and 0x0F) shl 3)

        return byteArrayOf((config shr 8).toByte(), (config and 0xFF).toByte())
    }
}
