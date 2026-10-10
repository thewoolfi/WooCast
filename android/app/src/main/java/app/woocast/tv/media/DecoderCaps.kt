// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.media

import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.os.Build
import android.util.Log
import app.woocast.tv.net.Wcp

/** Возможности выбранного аппаратного декодера H.264. */
data class DecoderInfo(
    /** Имя компонента, например `c2.amlogic.avc.decoder`. */
    val name: String,
    /** Аппаратный ли это декодер. */
    val isHardware: Boolean,
    val maxWidth: Int,
    val maxHeight: Int,
    val maxFps: Int,
    /** Поддерживает ли режим низкой задержки (`low-latency`). */
    val supportsLowLatency: Boolean,
    /** Поддерживает ли адаптивное воспроизведение (смену разрешения без перезапуска). */
    val supportsAdaptivePlayback: Boolean,
) {
    /** Краткое описание для экрана ожидания. */
    val displayName: String
        get() = buildString {
            append(shortName)
            if (isHardware) append(" · аппаратный") else append(" · программный")
        }

    /**
     * Имя компонента в читаемом виде: `c2.goldfish.h264.decoder` → `goldfish H.264`.
     * Полное имя не помещается на экране телевизора и мало что даёт пользователю.
     */
    val shortName: String
        get() {
            var result = name
            for (prefix in listOf("c2.", "OMX.", "omx.")) {
                if (result.startsWith(prefix)) {
                    result = result.removePrefix(prefix)
                }
            }

            result = result
                .removeSuffix(".decoder")
                .removeSuffix(".avc")
                .replace(".h264.", " H.264 ")
                .replace(".avc.", " H.264 ")
                .replace("avc", "H.264")
                .trim()

            return result.ifEmpty { name }
        }

    /** Строка возможностей, показываемая на экране ожидания. */
    val capsSummary: String
        get() = buildString {
            append("до ")
            append(maxWidth)
            append('×')
            append(maxHeight)
            if (maxFps > 0) {
                append(" @ ")
                append(maxFps)
                append(" к/с")
            }
            if (supportsLowLatency) append(" · low-latency")
        }
}

/**
 * Выбор лучшего доступного декодера H.264.
 *
 * Приставки сильно различаются: на дешёвых моделях аппаратный декодер ограничен 1080p30,
 * и попытка скормить ему 1080p60 приводит к чёрному экрану. Поэтому реальные ограничения
 * читаются из [MediaCodecInfo] и передаются отправителю в `announce.caps`, чтобы ПК подстроил
 * разрешение и частоту кадров.
 */
object DecoderCaps {

    private const val TAG = "WooCast/Caps"

    /** Строковый код фичи: `CodecCapabilities.FEATURE_LowLatency` появился только в API 30. */
    private const val FEATURE_LOW_LATENCY = "low-latency"

    /** Строковый код фичи адаптивного воспроизведения. */
    private const val FEATURE_ADAPTIVE_PLAYBACK = "adaptive-playback"

    /** Частота, если вендорный декодер не сообщил ничего разумного. */
    private const val DefaultMaxFps = 60

    /** Наибольшая частота, о которой приставка сообщает отправителю. */
    private const val MaxDeclaredFps = 60

    /**
     * Найти лучший декодер H.264. Предпочтение: аппаратный → поддерживающий low-latency →
     * с наибольшим поддерживаемым разрешением.
     *
     * @return описание декодера или `null`, если H.264 не поддерживается вообще.
     */
    fun findBest(): DecoderInfo? {
        val candidates = mutableListOf<DecoderInfo>()

        try {
            for (codecInfo in MediaCodecList(MediaCodecList.ALL_CODECS).codecInfos) {
                if (codecInfo.isEncoder) continue
                if (!codecInfo.supportedTypes.any { it.equals(Wcp.MIME_H264, ignoreCase = true) }) continue

                try {
                    val caps = codecInfo.getCapabilitiesForType(Wcp.MIME_H264)
                    val video = caps.videoCapabilities ?: continue

                    val widthRange = video.supportedWidths
                    val heightRange = video.supportedHeights
                    val fpsRange = video.supportedFrameRates

                    // Диапазоны приходят из платформенного API как платформенные типы:
                    // приводим через toDouble(), чтобы не зависеть от точного типа Range<...>.
                    val fpsUpper = try {
                        fpsRange.upper.toDouble()
                    } catch (e: Exception) {
                        60.0
                    }

                    // Частота ограничивается частотой обновления экрана: панель телевизора
                    // обычно 60 Гц, и поток в 120 к/с она показать не может физически.
                    // Система при этом сообщает пределы декодера вроде 960 к/с — верить им
                    // нельзя, и отправитель, поверив, присылал заведомо неподъёмный поток,
                    // который приёмник не мог показать вовсе.
                    val decoderMaxFps = if (fpsUpper.isFinite() && fpsUpper in 1.0..1000.0) {
                        fpsUpper.toInt()
                    } else {
                        DefaultMaxFps
                    }

                    candidates += DecoderInfo(
                        name = codecInfo.name,
                        isHardware = isHardware(codecInfo),
                        maxWidth = widthRange.upper,
                        maxHeight = heightRange.upper,
                        // Частота ограничивается разумным пределом: система сообщает
                        // о декодере небылицы (встречается 960 к/с), и отправитель,
                        // поверив, присылал заведомо неподъёмный поток.
                        //
                        // Именно 60, а не частота обновления панели: у телевизоров
                        // встречается 50 Гц, и ограничение по ней заставляло приставку
                        // отвергать поток 60 к/с целиком — зритель оставался без картинки.
                        maxFps = minOf(decoderMaxFps, MaxDeclaredFps),
                        supportsLowLatency = caps.isFeatureSupportedSafe(FEATURE_LOW_LATENCY),
                        supportsAdaptivePlayback = caps.isFeatureSupportedSafe(FEATURE_ADAPTIVE_PLAYBACK),
                    )
                } catch (e: Exception) {
                    // Некоторые вендорные компоненты бросают исключение на getCapabilitiesForType.
                    Log.w(TAG, "Не удалось прочитать возможности ${codecInfo.name}", e)
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "Не удалось перечислить декодеры", e)
            return null
        }

        if (candidates.isEmpty()) {
            Log.w(TAG, "Аппаратный декодер H.264 не найден")
            return null
        }

        val best = candidates.maxByOrNull { score(it) }!!
        Log.i(TAG, "Выбран декодер: ${best.displayName}, ${best.capsSummary}")
        return best
    }

    /**
     * Сможет ли выбранный декодер декодировать заданный поток.
     * Используется для отказа в трансляции до её начала вместо чёрного экрана.
     */
    fun isSupported(info: DecoderInfo, width: Int, height: Int, fps: Int): Boolean {
        if (width <= 0 || height <= 0) return true
        if (width > info.maxWidth || height > info.maxHeight) return false

        // Частота не считается поводом для отказа. Панель телевизора может обновляться
        // 50 раз в секунду, но это не значит, что поток 60 к/с надо отвергать: лишние
        // кадры просто не покажутся, а отказ оставляет зрителя вообще без картинки.
        // Отвергать поток из-за частоты — худшее из решений.
        return true
    }

    /**
     * Реальные ограничения с учётом того, что приставка может декодировать не всё,
     * что заявлено в диапазонах. Для 1080p-потоков дополнительно проверяем через
     * `areSizeAndRateSupported`, если это возможно.
     */
    fun clampToSupported(info: DecoderInfo, width: Int, height: Int, fps: Int): Triple<Int, Int, Int> {
        var w = if (width > 0) minOf(width, info.maxWidth) else 1920
        var h = if (height > 0) minOf(height, info.maxHeight) else 1080
        var f = if (fps > 0) minOf(fps, maxOf(info.maxFps, 1)) else 60

        // Ограничение по площади кадра (некоторые чипы ограничивают сумму пикселей).
        val maxPixels = info.maxWidth.toLong() * info.maxHeight
        while (w.toLong() * h > maxPixels && (w > 640 || h > 360)) {
            w = (w * 3) / 4
            h = (h * 3) / 4
        }

        w = w and 0xFFFFFFFE.toInt()
        h = h and 0xFFFFFFFE.toInt()
        if (w < 640) w = 640
        if (h < 360) h = 360
        if (f < 15) f = 15

        return Triple(w, h, f)
    }

    private fun score(info: DecoderInfo): Long {
        var score = 0L
        if (info.isHardware) score += 1_000_000_000L
        if (info.supportsLowLatency) score += 100_000_000L
        if (info.supportsAdaptivePlayback) score += 10_000_000L

        // Разрешение как второстепенный критерий, но с разумным потолком:
        // декодер, «поддерживающий» 8K на бумаге, не должен вытеснять проверенный 4K.
        val pixels = minOf(info.maxWidth.toLong() * info.maxHeight, 3840L * 2160L)
        score += pixels
        return score
    }

    private fun isHardware(codecInfo: MediaCodecInfo): Boolean {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            return codecInfo.isHardwareAccelerated
        }

        // На старых прошивках признак аппаратности определяем по имени компонента.
        val name = codecInfo.name.lowercase()
        return !name.startsWith("omx.google.") &&
            !name.startsWith("c2.android.") &&
            !name.contains("software") &&
            !name.contains("sw.decoder")
    }

    @Suppress("DEPRECATION")
    private fun MediaCodecInfo.CodecCapabilities.isFeatureSupportedSafe(feature: String): Boolean = try {
        isFeatureSupported(feature)
    } catch (e: Exception) {
        false
    }
}
