// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.ui

import android.content.Intent
import android.net.Uri
import android.view.SurfaceHolder
import android.view.SurfaceView
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.focusable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import app.woocast.tv.R
import app.woocast.tv.engine.ReceiverUiState
import kotlinx.coroutines.delay
import java.util.Locale

/** Адрес для поддержки проекта — тот же, что и в приложении для ПК. */
const val SupportUrl = "https://boosty.to/andrewwoolfi"

/**
 * Корневой экран приложения.
 *
 * Вёрстка рассчитана на 960×540 dp — это логический размер 1080p-экрана при плотности 320,
 * которая стандартна для Android TV. Поэтому содержимое разложено по двум колонкам,
 * а не в один высокий столбец: иначе нижние элементы уезжают за границу экрана.
 */
@Composable
fun WooCastRoot(
    state: ReceiverUiState?,
    log: List<String>,
    overlayVisible: Boolean,
    onDismissError: () -> Unit,
    onToggleLog: () -> Unit,
    logVisible: Boolean,
    gamepad: String?,
    onSurfaceReady: (android.view.Surface) -> Unit,
    onSurfaceDestroyed: () -> Unit,
) {
    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(WooColors.Background),
    ) {
        when (state) {
            null -> LoadingScreen()

            is ReceiverUiState.Waiting -> WaitingScreen(
                state = state,
                log = log,
                logVisible = logVisible,
                gamepad = gamepad,
                onToggleLog = onToggleLog,
            )

            is ReceiverUiState.Preparing -> PlayerScreen(
                state = null,
                preparing = state,
                overlayVisible = overlayVisible,
                onSurfaceReady = onSurfaceReady,
                onSurfaceDestroyed = onSurfaceDestroyed,
            )

            is ReceiverUiState.Streaming -> PlayerScreen(
                state = state,
                preparing = null,
                overlayVisible = overlayVisible,
                onSurfaceReady = onSurfaceReady,
                onSurfaceDestroyed = onSurfaceDestroyed,
            )

            is ReceiverUiState.Failed -> ErrorScreen(state = state, onDismiss = onDismissError)
        }
    }
}

@Composable
private fun LoadingScreen() {
    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Image(
                // Обычный рисунок, а не адаптивный значок: адаптивный — это XML,
                // и painterResource на нём падает, из-за чего приложение закрывалось
                // сразу при запуске.
                painter = painterResource(R.drawable.ic_launcher_mark),
                contentDescription = null,
                modifier = Modifier.size(104.dp),
            )

            Spacer(Modifier.height(20.dp))

            Text(
                text = "WooCast TV",
                style = MaterialTheme.typography.headlineMedium,
                color = WooColors.Muted,
            )
        }
    }
}

/** Экран ожидания: имя устройства и всё, что нужно для ручного подключения с ПК. */
@Composable
private fun WaitingScreen(
    state: ReceiverUiState.Waiting,
    log: List<String>,
    logVisible: Boolean,
    gamepad: String?,
    onToggleLog: () -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxSize()
            .padding(horizontal = 56.dp, vertical = 34.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        // Левая колонка: кто мы и что делать.
        Column(modifier = Modifier.weight(1f)) {
            // Знак приложения в начале экрана ожидания: без него приставка выглядела
            // безлико — только имя устройства и подсказки.
            Image(
                // Обычный рисунок, а не адаптивный значок: адаптивный — это XML,
                // и painterResource на нём падает, из-за чего приложение закрывалось
                // сразу при запуске.
                painter = painterResource(R.drawable.ic_launcher_mark),
                contentDescription = null,
                modifier = Modifier.size(64.dp),
            )

            Spacer(Modifier.height(18.dp))

            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    modifier = Modifier
                        .width(12.dp)
                        .height(12.dp)
                        .clip(CircleShape)
                        .background(if (state.hasNetwork) WooColors.Accent else WooColors.Danger),
                )
                Spacer(Modifier.width(12.dp))
                Text(
                    text = stringResource(
                        if (state.hasNetwork) R.string.idle_status_online else R.string.idle_status_offline,
                    ),
                    style = MaterialTheme.typography.labelLarge,
                    color = if (state.hasNetwork) WooColors.Accent else WooColors.Danger,
                )
            }

            Spacer(Modifier.height(14.dp))

            // Контроллер, подключённый к приёмнику: его состояние уходит на компьютер
            // и подаётся в игру как виртуальный геймпад.
            if (gamepad != null) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        text = "Геймпад",
                        style = MaterialTheme.typography.labelLarge,
                        color = WooColors.Accent,
                    )
                    Spacer(Modifier.width(10.dp))
                    Text(
                        text = gamepad,
                        style = MaterialTheme.typography.bodyMedium,
                        color = WooColors.Muted,
                    )
                }

                Spacer(Modifier.height(14.dp))
            }

            Text(
                text = state.deviceName,
                style = MaterialTheme.typography.displayLarge,
                color = WooColors.OnBackground,
            )

            Spacer(Modifier.height(10.dp))

            Text(
                text = stringResource(R.string.idle_hint),
                style = MaterialTheme.typography.titleLarge,
                color = WooColors.OnBackground,
            )

            Spacer(Modifier.height(4.dp))

            Text(
                text = stringResource(R.string.idle_hint_sub),
                style = MaterialTheme.typography.bodyMedium,
                color = WooColors.Muted,
            )

            Spacer(Modifier.height(22.dp))

            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                FocusableButton(
                    text = stringResource(
                        if (logVisible) R.string.idle_button_hide_log else R.string.idle_button_log,
                    ),
                    onClick = onToggleLog,
                )

                // Поддержка проекта: открывается в браузере телефона или приставки.
                // Та же ссылка, что и в приложении для ПК.
                val supportContext = LocalContext.current
                FocusableButton(
                    text = stringResource(R.string.idle_button_support),
                    onClick = {
                        try {
                            supportContext.startActivity(
                                Intent(Intent.ACTION_VIEW, Uri.parse(SupportUrl)),
                            )
                        } catch (e: Exception) {
                            // На приставке может не оказаться браузера — это не повод падать.
                        }
                    },
                )
            }
        }

        Spacer(Modifier.width(40.dp))

        // Правая колонка: технические подробности либо журнал.
        Column(modifier = Modifier.weight(1f)) {
            if (logVisible) {
                LogPanel(log)
            } else {
                InfoCard(state)
            }
        }
    }
}

/** Карточка с параметрами приёмника — их видно на экране ТВ при ручном подключении. */
@Composable
private fun InfoCard(state: ReceiverUiState.Waiting) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .background(WooColors.Surface)
            .border(1.dp, WooColors.Outline, RoundedCornerShape(18.dp))
            .padding(horizontal = 22.dp, vertical = 18.dp),
    ) {
        InfoRow(stringResource(R.string.idle_label_ip), state.address, emphasize = true)
        InfoRow(stringResource(R.string.idle_label_control_port), state.controlPort.toString())
        InfoRow(stringResource(R.string.idle_label_media_port), state.mediaPort.toString())
        InfoRow(stringResource(R.string.idle_label_discovery_port), state.discoveryPort.toString())
        InfoRow(
            stringResource(R.string.idle_label_decoder),
            state.decoder?.shortName ?: stringResource(R.string.idle_decoder_software),
        )
        InfoRow(
            stringResource(R.string.idle_label_caps),
            state.decoder?.capsSummary ?: stringResource(R.string.value_unknown),
        )
    }
}

/** Панель журнала событий — полезна при первом запуске на незнакомой приставке. */
@Composable
private fun LogPanel(log: List<String>) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .background(WooColors.Surface)
            .border(1.dp, WooColors.Outline, RoundedCornerShape(18.dp))
            .padding(horizontal = 22.dp, vertical = 18.dp),
    ) {
        Text(
            text = stringResource(R.string.idle_log_title),
            style = MaterialTheme.typography.labelMedium,
            color = WooColors.Muted,
        )

        Spacer(Modifier.height(10.dp))

        if (log.isEmpty()) {
            Text(
                text = stringResource(R.string.idle_log_empty),
                style = MaterialTheme.typography.bodyMedium,
                color = WooColors.Muted,
            )
        } else {
            // Показываем только то, что помещается на экран: журнал не должен
            // выдавливать содержимое за пределы кадра.
            log.takeLast(11).forEach { line ->
                Text(
                    text = line,
                    style = MaterialTheme.typography.bodyMedium,
                    color = WooColors.OnBackground,
                    fontFamily = FontFamily.Monospace,
                    maxLines = 1,
                )
            }
        }
    }
}

@Composable
private fun InfoRow(label: String, value: String, emphasize: Boolean = false) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyMedium,
            color = WooColors.Muted,
            modifier = Modifier.width(150.dp),
        )
        Text(
            text = value,
            style = MaterialTheme.typography.bodyMedium,
            color = if (emphasize) WooColors.Accent else WooColors.OnBackground,
            fontWeight = if (emphasize) FontWeight.SemiBold else FontWeight.Normal,
            fontFamily = FontFamily.Monospace,
            maxLines = 1,
            softWrap = false,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
    }
}

/**
 * Экран трансляции: видео на весь экран и необязательный оверлей с телеметрией.
 *
 * Пропорции сохраняются через [Modifier.aspectRatio] — телевизор сам впишет изображение
 * в свой кадр, добавив чёрные поля, вместо растягивания картинки.
 */
@Composable
private fun PlayerScreen(
    state: ReceiverUiState.Streaming?,
    preparing: ReceiverUiState.Preparing?,
    overlayVisible: Boolean,
    onSurfaceReady: (android.view.Surface) -> Unit,
    onSurfaceDestroyed: () -> Unit,
) {
    val width = state?.width ?: preparing?.width ?: 0
    val height = state?.height ?: preparing?.height ?: 0
    val ratio = if (width > 0 && height > 0) width.toFloat() / height.toFloat() else 16f / 9f

    var overlayShown by remember { mutableStateOf(true) }

    // Оверлей прячется сам, чтобы не мешать просмотру; показывается нажатием OK.
    LaunchedEffect(overlayVisible) {
        overlayShown = true
        if (overlayVisible) {
            delay(6_000)
            overlayShown = false
        }
    }

    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        AndroidView(
            // Размер выводится из соотношения сторон кадра, а не из размера экрана.
            // Раньше здесь стояло fillMaxSize().aspectRatio(ratio) — но fillMaxSize
            // уже задаёт размер во весь экран, и соотношение после него ничего не меняет:
            // поверхность растягивалась на весь экран, а изображение обрезалось.
            // На телевизоре с кадром 16:9 это совпадало с экраном и было незаметно,
            // а на телефоне в вертикальном положении обрезка видна сразу.
            modifier = Modifier.aspectRatio(ratio),
            factory = { context ->
                SurfaceView(context).apply {
                    holder.addCallback(object : SurfaceHolder.Callback {
                        override fun surfaceCreated(holder: SurfaceHolder) {
                            onSurfaceReady(holder.surface)
                        }

                        override fun surfaceChanged(
                            holder: SurfaceHolder,
                            format: Int,
                            width: Int,
                            height: Int,
                        ) {
                        }

                        override fun surfaceDestroyed(holder: SurfaceHolder) {
                            onSurfaceDestroyed()
                        }
                    })
                }
            },
        )

        if (state == null) {
            Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(
                    text = stringResource(R.string.stream_waiting_frame),
                    style = MaterialTheme.typography.titleLarge,
                    color = WooColors.Muted,
                )
            }
        } else if (state.waitingForKeyframe) {
            Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text(
                    text = stringResource(R.string.stream_reconnecting),
                    style = MaterialTheme.typography.titleLarge,
                    color = WooColors.Warning,
                )
            }
        }

        if (overlayShown && state != null) {
            MetricsOverlay(
                state = state,
                modifier = Modifier
                    .align(Alignment.TopStart)
                    .padding(24.dp),
            )
        }
    }
}

/** Оверлей с метриками: частота кадров, битрейт, задержка, потери. */
@Composable
private fun MetricsOverlay(state: ReceiverUiState.Streaming, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier
            .clip(RoundedCornerShape(18.dp))
            .background(WooColors.Surface.copy(alpha = 0.88f))
            .border(1.dp, WooColors.Outline, RoundedCornerShape(18.dp))
            .padding(horizontal = 22.dp, vertical = 16.dp),
    ) {
        Text(
            text = stringResource(R.string.stream_from) + ": " + state.sourceName,
            style = MaterialTheme.typography.titleMedium,
            color = WooColors.Accent,
            maxLines = 1,
        )

        Spacer(Modifier.height(10.dp))

        MetricLine(
            stringResource(R.string.stream_metric_resolution),
            if (state.width > 0 && state.height > 0) "${state.width}×${state.height}" else "—",
        )
        MetricLine(
            stringResource(R.string.stream_metric_fps),
            String.format(Locale.US, "%.1f", state.fps),
        )
        MetricLine(
            stringResource(R.string.stream_metric_bitrate),
            if (state.kbps > 0) formatMbit(state.kbps) + " Мбит/с" else "—",
        )
        if (state.audioEnabled) {
            MetricLine(
                stringResource(R.string.stream_metric_audio),
                if (state.audioKbps > 0) "${state.audioKbps} кбит/с" else "ожидание",
            )
        }
        MetricLine(
            stringResource(R.string.stream_metric_latency),
            if (state.latencyMs.isFinite() && state.latencyMs > 0) {
                String.format(Locale.US, "%.0f мс", state.latencyMs)
            } else {
                "—"
            },
        )
        MetricLine(stringResource(R.string.stream_metric_dropped), state.dropped.toString())

        Spacer(Modifier.height(8.dp))
        Text(
            text = stringResource(R.string.stream_overlay_hint),
            style = MaterialTheme.typography.labelMedium,
            color = WooColors.Muted,
        )
    }
}

private fun formatMbit(kbps: Int): String = String.format(Locale.US, "%.1f", kbps / 1000.0)

@Composable
private fun MetricLine(label: String, value: String) {
    Row(modifier = Modifier.padding(vertical = 2.dp)) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyMedium,
            color = WooColors.Muted,
            modifier = Modifier.width(170.dp),
        )
        Text(
            text = value,
            style = MaterialTheme.typography.bodyMedium,
            color = WooColors.OnBackground,
            fontFamily = FontFamily.Monospace,
        )
    }
}

/** Экран ошибки: что случилось и как вернуться к ожиданию. */
@Composable
private fun ErrorScreen(state: ReceiverUiState.Failed, onDismiss: () -> Unit) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(horizontal = 72.dp, vertical = 40.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(
            text = stringResource(R.string.error_title),
            style = MaterialTheme.typography.headlineMedium,
            color = WooColors.Danger,
        )

        Spacer(Modifier.height(16.dp))

        Text(
            text = stringResource(state.reasonRes),
            style = MaterialTheme.typography.titleLarge,
            color = WooColors.OnBackground,
            textAlign = TextAlign.Center,
        )

        if (state.detail.isNotBlank()) {
            Spacer(Modifier.height(10.dp))
            Text(
                text = state.code,
                style = MaterialTheme.typography.bodyMedium,
                color = WooColors.Muted,
                fontFamily = FontFamily.Monospace,
            )
        }

        Spacer(Modifier.height(28.dp))

        FocusableButton(
            text = stringResource(R.string.error_button_retry),
            onClick = onDismiss,
        )
    }
}

/**
 * Кнопка с явной подсветкой фокуса: на телевизоре управление идёт пультом,
 * и без заметной рамки непонятно, какой элемент выбран.
 */
@Composable
private fun FocusableButton(text: String, onClick: () -> Unit) {
    var focused by remember { mutableStateOf(false) }

    Box(
        modifier = Modifier
            .clip(RoundedCornerShape(14.dp))
            .background(if (focused) WooColors.Accent else WooColors.SurfaceVariant)
            .border(
                width = if (focused) 2.dp else 1.dp,
                color = if (focused) WooColors.Accent else WooColors.Outline,
                shape = RoundedCornerShape(14.dp),
            )
            .onFocusChanged { focused = it.isFocused }
            .focusable()
            .clickable { onClick() }
            .padding(horizontal = 24.dp, vertical = 12.dp),
    ) {
        Text(
            text = text,
            style = MaterialTheme.typography.labelLarge,
            color = if (focused) WooColors.Background else WooColors.OnBackground,
        )
    }
}
