// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

/**
 * Палитра WooCast. Тот же акцент используется в приложении для ПК,
 * поэтому приставка и компьютер выглядят как одно целое.
 */
object WooColors {
    val Background = Color(0xFF07090D)
    val Surface = Color(0xFF11151C)
    val SurfaceVariant = Color(0xFF1A2029)
    val Accent = Color(0xFF35D6C4)
    val AccentDim = Color(0xFF1E7A70)
    val OnBackground = Color(0xFFE6EDF3)
    val Muted = Color(0xFF8B949E)
    val Danger = Color(0xFFFF5C5C)
    val Warning = Color(0xFFFFC24B)
    val Outline = Color(0xFF232A36)
}

private val WooColorScheme = darkColorScheme(
    primary = WooColors.Accent,
    onPrimary = WooColors.Background,
    secondary = WooColors.AccentDim,
    background = WooColors.Background,
    onBackground = WooColors.OnBackground,
    surface = WooColors.Surface,
    onSurface = WooColors.OnBackground,
    surfaceVariant = WooColors.SurfaceVariant,
    onSurfaceVariant = WooColors.Muted,
    error = WooColors.Danger,
    outline = WooColors.Outline,
)

/**
 * Типографика рассчитана на экран 960×540 dp (логический размер 1080p на ТВ)
 * и просмотр с дивана: базовый размер крупнее обычного, но без риска
 * вытеснить содержимое за границы кадра.
 */
private val WooTypography = Typography(
    displayLarge = TextStyle(fontSize = 42.sp, fontWeight = FontWeight.SemiBold, letterSpacing = (-0.5).sp),
    headlineMedium = TextStyle(fontSize = 30.sp, fontWeight = FontWeight.SemiBold),
    titleLarge = TextStyle(fontSize = 20.sp, fontWeight = FontWeight.Medium),
    titleMedium = TextStyle(fontSize = 17.sp, fontWeight = FontWeight.Medium),
    bodyLarge = TextStyle(fontSize = 16.sp),
    bodyMedium = TextStyle(fontSize = 14.sp),
    labelLarge = TextStyle(fontSize = 14.sp, fontWeight = FontWeight.Medium, letterSpacing = 0.5.sp),
    labelMedium = TextStyle(fontSize = 12.sp, letterSpacing = 0.8.sp),
)

@Composable
fun WooCastTheme(content: @Composable () -> Unit) {
    // Тема всегда тёмная: приёмник работает на телевизоре, светлый вариант только мешает.
    isSystemInDarkTheme()
    MaterialTheme(
        colorScheme = WooColorScheme,
        typography = WooTypography,
        content = content,
    )
}
