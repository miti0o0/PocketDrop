package app.pocketdrop.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

val Accent = Color(0xFF0A84FF)
val Success = Color(0xFF30D158)
val Danger = Color(0xFFFF453A)

private val Light = lightColorScheme(
    primary = Accent,
    onPrimary = Color.White,
    primaryContainer = Color(0xFFDCEBFF),
    onPrimaryContainer = Color(0xFF00315F),
    background = Color(0xFFF2F2F7),
    onBackground = Color(0xFF1C1C1E),
    surface = Color(0xFFF2F2F7),
    onSurface = Color(0xFF1C1C1E),
    surfaceContainer = Color.White,
    surfaceContainerHigh = Color.White,
    surfaceContainerHighest = Color(0xFFEBEBF0),
    surfaceContainerLow = Color.White,
    onSurfaceVariant = Color(0xFF6E6E73),
    outline = Color(0xFFD1D1D6),
    outlineVariant = Color(0xFFE3E3E8),
    error = Danger,
)

private val Dark = darkColorScheme(
    primary = Accent,
    onPrimary = Color.White,
    primaryContainer = Color(0xFF0A3A6B),
    onPrimaryContainer = Color(0xFFDCEBFF),
    background = Color.Black,
    onBackground = Color(0xFFF5F5F7),
    surface = Color.Black,
    onSurface = Color(0xFFF5F5F7),
    surfaceContainer = Color(0xFF1C1C1E),
    surfaceContainerHigh = Color(0xFF1C1C1E),
    surfaceContainerHighest = Color(0xFF2C2C2E),
    surfaceContainerLow = Color(0xFF1C1C1E),
    onSurfaceVariant = Color(0xFFA1A1A6),
    outline = Color(0xFF48484A),
    outlineVariant = Color(0xFF2C2C2E),
    error = Danger,
)

@Composable
fun PocketDropTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = if (isSystemInDarkTheme()) Dark else Light, content = content)
}
