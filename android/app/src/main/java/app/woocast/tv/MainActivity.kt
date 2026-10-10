// SPDX-License-Identifier: MIT
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv

import android.os.Bundle
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.Surface
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.woocast.tv.input.GamepadState
import app.woocast.tv.service.StreamService
import app.woocast.tv.ui.WooCastRoot
import app.woocast.tv.ui.WooCastTheme

/**
 * Единственная Activity приложения.
 *
 * На телевизоре она всегда на переднем плане во время просмотра, поэтому экран
 * удерживается включённым, а поверхность вывода подключается к движку при создании
 * и отключается при уходе в фон.
 */
class MainActivity : ComponentActivity() {

    /** Виден ли оверлей с метриками на экране трансляции. */
    private var overlayVisible by mutableStateOf(true)

    /** Развёрнут ли журнал событий на экране ожидания. */
    private var logVisible by mutableStateOf(false)

    /**
     * Слежение за подключением контроллеров.
     *
     * Нужно, чтобы приложение знало о геймпаде, даже если им ещё не пользовались:
     * иначе на экране не появится отметка, а компьютер не получит название устройства.
     */
    private val inputManager by lazy {
        getSystemService(INPUT_SERVICE) as android.hardware.input.InputManager
    }

    private val inputDeviceListener = object : android.hardware.input.InputManager.InputDeviceListener {
        override fun onInputDeviceAdded(deviceId: Int) = refreshGamepads()

        override fun onInputDeviceRemoved(deviceId: Int) = refreshGamepads()

        override fun onInputDeviceChanged(deviceId: Int) = refreshGamepads()
    }

    private val engine: app.woocast.tv.engine.ReceiverEngine
        get() = (application as WooCastApp).engine

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Телевизор не должен гаснуть во время просмотра.
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        // Уведомление о работе приёмника; если прошивка не разрешит — не критично.
        StreamService.startQuietly(this)

        setContent {
            WooCastTheme {
                val state by engine.state.collectAsState()
                val log by engine.log.collectAsState()
                val gamepad by engine.gamepadDevice.collectAsState()

                WooCastRoot(
                    state = state,
                    log = log,
                    overlayVisible = overlayVisible,
                    logVisible = logVisible,
                    gamepad = gamepad,
                    onToggleLog = { logVisible = !logVisible },
                    onDismissError = { engine.dismissError() },
                    onSurfaceReady = { surface: Surface -> engine.attachSurface(surface) },
                    onSurfaceDestroyed = { engine.detachSurface() },
                )
            }
        }
    }

    override fun onResume() {
        super.onResume()
        current = this

        // Возврат из фона: переподключаем поверхность, если она была потеряна.
        overlayVisible = true

        inputManager.registerInputDeviceListener(inputDeviceListener, null)
        refreshGamepads()
    }

    override fun onPause() {
        if (current === this) {
            current = null
        }

        try {
            inputManager.unregisterInputDeviceListener(inputDeviceListener)
        } catch (e: Exception) {
            // Снятие слежения не должно мешать уходу в фон.
        }

        super.onPause()
    }

    /** Пересмотреть список контроллеров и сообщить движку о первом найденном. */
    private fun refreshGamepads() {
        val gamepad = try {
            InputDevice.getDeviceIds()
                .asSequence()
                .mapNotNull { InputDevice.getDevice(it) }
                .firstOrNull { device ->
                    val sources = device.sources
                    (sources and InputDevice.SOURCE_GAMEPAD) != 0 ||
                        (sources and InputDevice.SOURCE_JOYSTICK) != 0
                }
        } catch (e: Exception) {
            // Опрос контроллеров не должен ронять приложение: на части прошивок
            // список устройств недоступен, и это не повод закрываться.
            null
        }

        try {
            runOnUiThread {
                engine.onGamepadDeviceChanged(gamepad != null, gamepad?.name)
            }
        } catch (e: Exception) {
            // Активность могла уже завершиться.
        }
    }

    companion object {
        /**
         * Текущая активность. Нужна, чтобы узнать частоту обновления экрана: система
         * сообщает о декодере завышенные пределы (встречается 960 к/с), а панель
         * телевизора обычно 60 Гц, и поток чаще показывать невозможно физически.
         */
        @Volatile
        var current: MainActivity? = null
            private set
    }

    /**
     * Обработка пульта.
     *
     * Сюда событие попадает только если его не поглотил ни один элемент интерфейса:
     * на экране трансляции фокусируемых элементов нет, поэтому OK управляет оверлеем,
     * а на экране ожидания OK нажимает кнопку журнала.
     */
    override fun onKeyDown(keyCode: Int, event: KeyEvent?): Boolean = when (keyCode) {
        KeyEvent.KEYCODE_DPAD_CENTER,
        KeyEvent.KEYCODE_ENTER,
        KeyEvent.KEYCODE_NUMPAD_ENTER,
        KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE,
        -> {
            overlayVisible = !overlayVisible
            true
        }

        else -> super.onKeyDown(keyCode, event)
    }

    override fun onDestroy() {
        // Поверхность могла остаться подключённой, если система не вызвала surfaceDestroyed.
        if (isFinishing) {
            engine.detachSurface()
        }

        super.onDestroy()
    }

    /**
     * События кнопок геймпада.
     *
     * Перехватываются до обработки интерфейсом: пока идёт трансляция, кнопки контроллера
     * должны уходить на компьютер, а не нажимать элементы экрана. Пульт и клавиатура
     * при этом продолжают работать как раньше — их события сюда не попадают.
     */
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (GamepadState.isGamepad(event) && engine.onGamepadKey(event)) {
            return true
        }

        return super.dispatchKeyEvent(event)
    }

    /**
     * События движения: стики, курки и крестовина контроллера.
     *
     * Стики приходят непрерывно, пока отклонены, поэтому их нельзя пропускать
     * в интерфейс: это вызвало бы постоянную перерисовку экрана.
     */
    override fun dispatchGenericMotionEvent(event: MotionEvent): Boolean {
        if (GamepadState.isGamepad(event) && engine.onGamepadMotion(event)) {
            return true
        }

        return super.dispatchGenericMotionEvent(event)
    }
}
