// SPDX-License-Identifier: MIT
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.net

/** Разобранный access unit H.264 в формате Annex-B. */
class AccessUnit(
    /** Байты access unit'а вместе с ведущим старт-кодом. */
    val data: ByteArray,
    /** Содержит IDR-слайс — кадр декодируется независимо. */
    val isKeyframe: Boolean,
    /** Содержит SPS или PPS. */
    val hasParameterSets: Boolean,
    /** Метаданные потока: наборы параметров, извлечённые из этого access unit'а. */
    val parameterSets: ParameterSets? = null,
)

/** Наборы параметров H.264 в формате Annex-B (со старт-кодами). */
class ParameterSets(val sps: ByteArray, val pps: ByteArray) {
    override fun equals(other: Any?): Boolean {
        if (other !is ParameterSets) return false
        return sps.contentEquals(other.sps) && pps.contentEquals(other.pps)
    }

    override fun hashCode(): Int = 31 * sps.contentHashCode() + pps.contentHashCode()
}

/** Вспомогательные операции разбора Annex-B. */
object AnnexB {

    const val NAL_IDR = 5
    const val NAL_SPS = 7
    const val NAL_PPS = 8
    const val NAL_AUD = 9

    /**
     * Длина старт-кода в позиции [offset] или 0, если старт-кода нет.
     * Предпочитается четырёхбайтовый вариант — так же, как в C#-реализации.
     */
    fun startCodeLength(data: ByteArray, offset: Int, length: Int = data.size): Int {
        if (offset < 0 || offset + 3 > length) return 0
        if (data[offset] != 0.toByte() || data[offset + 1] != 0.toByte()) return 0
        if (data[offset + 2] == 1.toByte()) return 3
        if (offset + 4 <= length && data[offset + 2] == 0.toByte() && data[offset + 3] == 1.toByte()) return 4
        return 0
    }

    /** Определить, является ли access unit ключевым и есть ли в нём SPS/PPS. */
    fun analyze(data: ByteArray, length: Int = data.size): Pair<Boolean, Boolean> {
        var isKeyframe = false
        var hasParameterSets = false
        var offset = 0

        while (offset < length) {
            val startCode = startCodeLength(data, offset, length)
            if (startCode == 0) {
                offset++
                continue
            }

            val headerIndex = offset + startCode
            if (headerIndex >= length) break

            when (data[headerIndex].toInt() and 0x1F) {
                NAL_IDR -> isKeyframe = true
                NAL_SPS, NAL_PPS -> hasParameterSets = true
            }

            offset = headerIndex + 1
        }

        return isKeyframe to hasParameterSets
    }

    /**
     * Извлечь SPS и PPS в формате Annex-B — именно в таком виде их принимает MediaCodec
     * в качестве `csd-0` и `csd-1`.
     */
    fun extractParameterSets(data: ByteArray, length: Int = data.size): ParameterSets? {
        var sps: ByteArray? = null
        var pps: ByteArray? = null
        var offset = 0

        while (offset < length) {
            val startCode = startCodeLength(data, offset, length)
            if (startCode == 0) {
                offset++
                continue
            }

            val headerIndex = offset + startCode
            if (headerIndex >= length) break

            val nalType = data[headerIndex].toInt() and 0x1F
            if (nalType == NAL_SPS || nalType == NAL_PPS) {
                val end = findNextStartCode(data, headerIndex + 1, length)
                val nal = data.copyOfRange(offset, end)

                if (nalType == NAL_SPS && sps == null) sps = nal
                if (nalType == NAL_PPS && pps == null) pps = nal
            }

            offset = headerIndex + 1
        }

        val s = sps ?: return null
        val p = pps ?: return null
        return ParameterSets(s, p)
    }

    /**
     * Вырезать из access unit'а всё, кроме слайсов: AUD (тип 9), SPS (7) и PPS (8).
     *
     * MediaCodec получает параметры кодирования отдельно, через `csd-0`/`csd-1`, и многие
     * аппаратные декодеры (в том числе эмуляторный goldfish) не переносят, когда те же
     * NAL'ы приходят повторно внутри входного буфера. AUD декодеру не нужен вовсе —
     * он служил только для деления потока на стороне приёмника.
     *
     * @return полезная нагрузка для кодека или `null`, если в кадре нет слайсов
     *         (например, пришёл только набор параметров).
     */
    fun extractSlicePayload(data: ByteArray, length: Int = data.size): ByteArray? {
        // Быстрый путь: границы NAL'ов.
        val nalStarts = ArrayList<Int>(8)
        var offset = 0

        while (offset < length) {
            val startCode = startCodeLength(data, offset, length)
            if (startCode == 0) {
                offset++
                continue
            }

            nalStarts.add(offset)
            offset += startCode
        }

        if (nalStarts.isEmpty()) {
            return null
        }

        var hasSlices = false
        for (index in nalStarts.indices) {
            val start = nalStarts[index]
            val headerIndex = start + startCodeLength(data, start, length)
            if (headerIndex >= length) continue

            val nalType = data[headerIndex].toInt() and 0x1F
            if (nalType != NAL_AUD && nalType != NAL_SPS && nalType != NAL_PPS) {
                hasSlices = true
                break
            }
        }

        if (!hasSlices) {
            return null
        }

        // Собираем только нужные NAL'ы, сохраняя старт-коды как есть.
        var total = 0
        for (index in nalStarts.indices) {
            val start = nalStarts[index]
            val headerIndex = start + startCodeLength(data, start, length)
            if (headerIndex >= length) continue

            val nalType = data[headerIndex].toInt() and 0x1F
            if (nalType == NAL_AUD || nalType == NAL_SPS || nalType == NAL_PPS) continue

            val end = if (index + 1 < nalStarts.size) nalStarts[index + 1] else length
            total += end - start
        }

        if (total == 0) {
            return null
        }

        val result = ByteArray(total)
        var cursor = 0
        for (index in nalStarts.indices) {
            val start = nalStarts[index]
            val headerIndex = start + startCodeLength(data, start, length)
            if (headerIndex >= length) continue

            val nalType = data[headerIndex].toInt() and 0x1F
            if (nalType == NAL_AUD || nalType == NAL_SPS || nalType == NAL_PPS) continue

            val end = if (index + 1 < nalStarts.size) nalStarts[index + 1] else length
            System.arraycopy(data, start, result, cursor, end - start)
            cursor += end - start
        }

        return result
    }

    private fun findNextStartCode(data: ByteArray, from: Int, length: Int): Int {
        var i = from
        while (i + 3 <= length) {
            if (startCodeLength(data, i, length) > 0) return i
            i++
        }
        return length
    }
}

/**
 * Делит непрерывный поток байт H.264 на access unit'ы по границам AUD (NAL тип 9).
 *
 * Устойчив к произвольному дроблению входных данных: старт-код, разорванный между двумя
 * вызовами [append], будет найден. Если AUD в потоке нет, буфер принудительно режется по
 * последнему старт-коду, чтобы память не росла бесконечно.
 */
class AnnexBSplitter(private val maxAccessUnitBytes: Int = Wcp.MAX_MEDIA_PAYLOAD) {

    private var buffer = ByteArray(128 * 1024)
    private var length = 0
    private var scanPosition = 0
    private var accessUnitStart = -1

    private val ready = ArrayDeque<AccessUnit>()

    /** Добавить очередную порцию байт и получить готовые access unit'ы. */
    fun append(data: ByteArray, offset: Int, count: Int): List<AccessUnit> {
        if (count <= 0) return emptyList()

        ensureCapacity(length + count)
        System.arraycopy(data, offset, buffer, length, count)
        length += count

        scan()

        if (ready.isEmpty()) return emptyList()

        val result = ArrayList<AccessUnit>(ready.size)
        while (ready.isNotEmpty()) {
            result.add(ready.removeFirst())
        }
        return result
    }

    /** Завершить поток: отдать последний незакрытый access unit. */
    fun flush(): List<AccessUnit> {
        if (accessUnitStart in 0 until length) {
            emit(accessUnitStart, length)
            accessUnitStart = -1
        }

        if (ready.isEmpty()) return emptyList()

        val result = ArrayList<AccessUnit>(ready.size)
        while (ready.isNotEmpty()) {
            result.add(ready.removeFirst())
        }
        return result
    }

    /** Сбросить состояние — например, при переподключении. */
    fun reset() {
        length = 0
        scanPosition = 0
        accessUnitStart = -1
        ready.clear()
    }

    private fun scan() {
        // Отступаем на 3 байта: старт-код мог разорваться между двумя append.
        var i = maxOf(0, scanPosition - 3)

        while (i + 3 <= length) {
            val startCode = AnnexB.startCodeLength(buffer, i, length)
            if (startCode == 0) {
                i++
                continue
            }

            val headerIndex = i + startCode
            if (headerIndex >= length) break // заголовок NAL ещё не пришёл

            if ((buffer[headerIndex].toInt() and 0x1F) == AnnexB.NAL_AUD) {
                if (accessUnitStart < 0) {
                    // Первый AUD: всё, что было до него (SPS/PPS), относим к этому же кадру.
                    accessUnitStart = 0
                } else if (i > accessUnitStart) {
                    emit(accessUnitStart, i)
                    accessUnitStart = i
                }
            }

            i = headerIndex + 1
        }

        scanPosition = length
        guardAgainstRunaway()
        compact()
    }

    /** Страховка от потока без AUD: не даём буферу расти без ограничений. */
    private fun guardAgainstRunaway() {
        if (length <= maxAccessUnitBytes) return

        val cut = if (accessUnitStart < 0) 0 else accessUnitStart

        var boundary = -1
        var i = length - 4
        while (i > cut) {
            if (AnnexB.startCodeLength(buffer, i, length) > 0) {
                boundary = i
                break
            }
            i--
        }

        if (boundary <= cut) boundary = length

        emit(cut, boundary)
        accessUnitStart = if (boundary < length) boundary else -1
    }

    private fun emit(from: Int, to: Int) {
        if (to <= from) return

        val data = buffer.copyOfRange(from, to)
        val (isKeyframe, hasParameterSets) = AnnexB.analyze(data)
        val parameterSets = if (hasParameterSets) AnnexB.extractParameterSets(data) else null

        ready.addLast(AccessUnit(data, isKeyframe, hasParameterSets, parameterSets))
    }

    private fun compact() {
        if (accessUnitStart <= 0) return

        val remaining = length - accessUnitStart
        if (remaining > 0) {
            System.arraycopy(buffer, accessUnitStart, buffer, 0, remaining)
        }

        length = remaining
        scanPosition = maxOf(0, scanPosition - accessUnitStart)
        accessUnitStart = 0
    }

    private fun ensureCapacity(required: Int) {
        if (required <= buffer.size) return

        var size = buffer.size
        val limit = maxAccessUnitBytes + 1024 * 1024
        while (size < required) {
            size = minOf(size.toLong() * 2, limit.toLong()).toInt()
            if (size <= 0) {
                size = required
                break
            }
        }

        buffer = buffer.copyOf(size)
    }
}
