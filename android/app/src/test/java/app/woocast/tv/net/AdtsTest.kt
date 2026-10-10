// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.net

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Проверка разбора ADTS на настоящем кадре из потока WooCast.
 *
 * Эталон снят с работающей связки «ПК → приёмник»: первый кадр записи звука
 * (AAC-LC, 48 кГц, стерео, 425 байт). Тесты выполняются на JVM и не требуют
 * ни устройства, ни эмулятора — вся логика разбора здесь чистая.
 *
 * Длина кадра берётся не «на глаз», а из самого заголовка: поле `aac_frame_length`
 * занимает 13 бит и включает заголовок. Сверка этой длины с размером реального
 * пакета — главный тест: именно по ней приёмник делит поток на кадры.
 */
class AdtsTest {

    /**
     * Первый кадр реального потока: заголовок ADTS и сжатые данные.
     *
     * Записан hex-строкой, потому что в Kotlin литерал вроде `0xFF` не помещается
     * в `Byte` и потребовал бы `.toByte()` у каждого из 425 значений.
     */
    private val realFrame: ByteArray = hexToBytes(
        "FFF14C80353FFCDE02004C61766336332E31362E313030004257949FFEFFFFFFF1F456921586306088CCBCB5200DEAE5" +
            "5B1D0C07181C962F1669A51AE4B1E305BD5B7C57B24C92ECA32A3B3C65BFC5EFD3E5DBBBE3140CF64A4C391F7D0E84D2" +
            "C4405D394FF2E8A26A51932D837F4157CB2CA7B916BBDE4622CD6670052540C71F6006D21FFCF87D4C3E1E1EB114002B" +
            "ADD43A3A0B745F1CE507CA32B1D0408EE6141455940B28C8E12E6E97F9C1F945F33E5F3FBFDF874D0DBFD0346DE704BA" +
            "47E763407CDF83FA425FE3676B6D00686DFB8F88D00340005B68D014002C02210575823142206FD85CAD0D7BDEEA5F29" +
            "6042255A967E3C39311C847A442ECFBAE893A50A42E3D21053E324C0499C3D699F3FD1E23CA5D61617CA5CE794BB1F81" +
            "7727F4DDD4A6A5E2FD5FF87FE06F5E64E68F90C980FE4C95A97EF77241F8C3CE3E33E0FBAF6FB67ACFC13EFFF5473ED0" +
            "E4FC1CBECDCD3D9FDA9E9173C21CAE352EE2F4888AC38EE3B09A511B3B814F0332A2C69922B2978176715E8F50E62E35" +
            "1314267A7A2CB3CA9E25775582A0317752C2F7D350592E560D851010A45919CB1192A67BD86C788B38",
    )

    @Test
    fun `реальный кадр разбирается в ожидаемые параметры`() {
        val header = Adts.parseHeader(realFrame)

        assertTrue("заголовок должен разбираться", header != null)
        header!!

        assertEquals("длина кадра совпадает с размером пакета", realFrame.size, header.frameLength)
        assertEquals("заголовок без CRC", 7, header.headerLength)
        assertEquals("AAC-LC", 2, header.audioObjectType)
        assertEquals("индекс частоты 48 кГц", 3, header.sampleRateIndex)
        assertEquals("48 кГц", 48_000, header.sampleRate)
        assertEquals("стерео", 2, header.channels)
        assertEquals("длительность кадра", 21_333L, header.durationMicros)
    }

    @Test
    fun `AudioSpecificConfig совпадает с каноническим для AAC-LC 48 кГц стерео`() {
        val header = Adts.parseHeader(realFrame)!!
        val config = header.buildAudioSpecificConfig()

        // 0x11 0x90 — эталонное значение: тип объекта 2, частота 48 кГц, 2 канала.
        assertArrayEquals(byteArrayOf(0x11, 0x90.toByte()), config)
    }

    @Test
    fun `полезная нагрузка отделяется от заголовка`() {
        val payload = Adts.extractPayload(realFrame)

        assertTrue("нагрузка должна извлекаться", payload != null)
        assertEquals("длина без заголовка", realFrame.size - 7, payload!!.size)
        assertEquals("первый байт нагрузки", realFrame[7], payload[0])
        assertEquals("последний байт нагрузки", realFrame[realFrame.size - 1], payload[payload.size - 1])
    }

    @Test
    fun `целый кадр распознаётся, обрезанный — нет`() {
        assertTrue("полный кадр", Adts.isCompleteFrame(realFrame))
        assertFalse("обрезанный кадр", Adts.isCompleteFrame(realFrame.copyOfRange(0, 200)))
        assertFalse("слишком короткий", Adts.isCompleteFrame(realFrame.copyOfRange(0, 4)))
    }

    @Test
    fun `без синхрослова заголовок не разбирается`() {
        val broken = realFrame.copyOf()
        broken[0] = 0x00

        assertNull("нет синхрослова", Adts.parseHeader(broken))
    }

    @Test
    fun `кадр с CRC учитывает девятибайтовый заголовок`() {
        // Снимаем бит protection_absent: заголовок становится длиннее на два байта CRC.
        val withCrc = realFrame.copyOf()
        withCrc[1] = (withCrc[1].toInt() and 0xFE).toByte()

        val header = Adts.parseHeader(withCrc)!!

        assertEquals("заголовок с CRC", 9, header.headerLength)
        assertEquals("длина кадра не изменилась", realFrame.size, header.frameLength)
    }

    @Test
    fun `поток из подряд идущих кадров разбирается целиком`() {
        // Склеиваем три кадра и убеждаемся, что каждый опознаётся по своим границам.
        val stream = realFrame + realFrame + realFrame
        var offset = 0
        var count = 0

        while (offset < stream.size) {
            val header = Adts.parseHeader(stream, offset)
            assertTrue("кадр номер $count должен разбираться", header != null)
            assertEquals("длина кадра $count", realFrame.size, header!!.frameLength)
            offset += header.frameLength
            count++
        }

        assertEquals("число кадров в потоке", 3, count)
    }

    @Test
    fun `кадр в середине потока находится по смещению`() {
        // Между кадрами вставляем мусор — приёмник должен опознать кадр по синхрослову,
        // а не полагаться на то, что поток начинается ровно с заголовка.
        val stream = byteArrayOf(0x11, 0x22, 0x33) + realFrame
        val header = Adts.parseHeader(stream, 3)

        assertTrue("кадр после мусора должен разбираться", header != null)
        assertEquals("длина кадра", realFrame.size, header!!.frameLength)
    }

    /** Превратить hex-строку в массив байт. */
    private fun hexToBytes(hex: String): ByteArray {
        require(hex.length % 2 == 0) { "Длина hex-строки должна быть чётной" }

        return ByteArray(hex.length / 2) { index ->
            hex.substring(index * 2, index * 2 + 2).toInt(16).toByte()
        }
    }
}
