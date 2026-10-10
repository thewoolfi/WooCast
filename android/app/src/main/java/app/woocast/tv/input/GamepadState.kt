// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.input

import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import app.woocast.tv.net.InputAxes
import app.woocast.tv.net.InputButtons
import app.woocast.tv.net.InputSpec

/**
 * Состояние геймпада, подключённого к приёмнику.
 *
 * Собирается из событий Android: кнопки приходят событиями клавиш, стики и курки —
 * событиями движения. Android сам приводит любой контроллер к общему виду, поэтому
 * DualShock 4, геймпад Xbox и прочие устройства различий здесь не требуют:
 * крест DualShock приходит как [KeyEvent.KEYCODE_BUTTON_A], круг — как B и так далее.
 *
 * Класс не привязан к потоку интерфейса: события приходят с него, а читает состояние
 * поток отправки, поэтому все изменения защищены блокировкой.
 */
class GamepadState {
    private val lock = Any()

    private var buttons = 0
    private var leftX = 0
    private var leftY = 0
    private var rightX = 0
    private var rightY = 0
    private var leftTrigger = 0
    private var rightTrigger = 0
    private var deviceName: String? = null
    private var sequence = 0L

    /** Есть ли подключённый контроллер. */
    var connected: Boolean = false
        private set

    /** Название подключённого контроллера — для показа на экране и на компьютере. */
    val device: String?
        get() = synchronized(lock) { deviceName }

    /** Были ли изменения с прошлой отправки. */
    private var dirty = false

    /** Отметить подключение контроллера. */
    fun onDeviceAdded(name: String?) {
        synchronized(lock) {
            connected = true
            deviceName = name
            dirty = true
            // Новый контроллер начинает с нейтрального положения: иначе игра унаследует
            // нажатия от предыдущего.
            buttons = 0
            leftX = 0
            leftY = 0
            rightX = 0
            rightY = 0
            leftTrigger = 0
            rightTrigger = 0
        }
    }

    /** Отметить отключение контроллера. */
    fun onDeviceRemoved() {
        synchronized(lock) {
            connected = false
            deviceName = null
            buttons = 0
            leftX = 0
            leftY = 0
            rightX = 0
            rightY = 0
            leftTrigger = 0
            rightTrigger = 0
            dirty = true
        }
    }

    /** Обработать нажатие или отпускание кнопки. Возвращает true, если событие наше. */
    fun onKey(event: KeyEvent): Boolean {
        val mask = maskOf(event.keyCode) ?: return false
        if (event.repeatCount > 0) return true

        val pressed = event.action == KeyEvent.ACTION_DOWN

        synchronized(lock) {
            val was = (buttons and mask) != 0
            if (was == pressed) return true

            buttons = if (pressed) buttons or mask else buttons and mask.inv()
            dirty = true
        }

        return true
    }

    /** Обработать движение: стики, курки, крестовина как оси. */
    fun onMotion(event: MotionEvent): Boolean {
        var handled = false

        synchronized(lock) {
            fun axis(code: Int, invert: Boolean = false): Float? {
                // Ось читается только если контроллер её заявляет: иначе getAxisValue
                // вернул бы ноль, и мы бы затирали реальное положение стика.
                if (event.device.getMotionRange(code) == null) return null

                val value = event.getAxisValue(code)
                return if (value.isNaN()) null else value
            }

            axis(MotionEvent.AXIS_X)?.let { leftX = InputAxes.fromAxis(it); handled = true }
            axis(MotionEvent.AXIS_Y, invert = true)?.let { leftY = InputAxes.fromAxis(it); handled = true }

            // Правый стик: на разных контроллерах это Z/RZ или RX/RY.
            (axis(MotionEvent.AXIS_RX) ?: axis(MotionEvent.AXIS_Z))?.let {
                rightX = InputAxes.fromAxis(it)
                handled = true
            }
            (axis(MotionEvent.AXIS_RY) ?: axis(MotionEvent.AXIS_RZ))?.let {
                rightY = InputAxes.fromAxis(it, invert = true)
                handled = true
            }

            // Курки: у части контроллеров это LTRIGGER/RTRIGGER, у части — BRAKE/GAS.
            (axis(MotionEvent.AXIS_LTRIGGER) ?: axis(MotionEvent.AXIS_BRAKE))?.let {
                leftTrigger = InputAxes.fromTrigger(it)
                handled = true
            }
            (axis(MotionEvent.AXIS_RTRIGGER) ?: axis(MotionEvent.AXIS_GAS))?.let {
                rightTrigger = InputAxes.fromTrigger(it)
                handled = true
            }

            // Крестовина у части контроллеров приходит осями, а не кнопками.
            axis(MotionEvent.AXIS_HAT_X)?.let { value ->
                buttons = if (value <= -0.5f) buttons or InputButtons.DPAD_LEFT else buttons and InputButtons.DPAD_LEFT.inv()
                buttons = if (value >= 0.5f) buttons or InputButtons.DPAD_RIGHT else buttons and InputButtons.DPAD_RIGHT.inv()
                handled = true
            }
            axis(MotionEvent.AXIS_HAT_Y)?.let { value ->
                buttons = if (value <= -0.5f) buttons or InputButtons.DPAD_UP else buttons and InputButtons.DPAD_UP.inv()
                buttons = if (value >= 0.5f) buttons or InputButtons.DPAD_DOWN else buttons and InputButtons.DPAD_DOWN.inv()
                handled = true
            }

            if (handled) dirty = true
        }

        return handled
    }

    /**
     * Взять состояние для отправки.
     *
     * @param force отправить даже без изменений — для редких подтверждений,
     *   чтобы компьютер знал, что связь жива.
     * @return состояние или null, если отправлять нечего.
     */
    fun take(force: Boolean = false): InputSpec? {
        synchronized(lock) {
            if (!connected) {
                if (!dirty && !force) return null
            } else if (!dirty && !force) {
                return null
            }

            dirty = false
            sequence++

            return InputSpec(
                seq = sequence,
                buttons = buttons,
                lx = leftX,
                ly = leftY,
                rx = rightX,
                ry = rightY,
                lt = leftTrigger,
                rt = rightTrigger,
                device = deviceName,
            )
        }
    }

    private fun maskOf(keyCode: Int): Int? = buttonMask(keyCode)

    /** Пришло ли событие от геймпада, а не от пульта или клавиатуры. */
    companion object {
        /**
         * Преобразовать код кнопки Android в бит маски.
         *
         * Вынесено из класса, чтобы проверялось тестом без создания события:
         * соответствие кнопок — то место, где ошибка обходится дорого
         * (игрок нажимает одно, игра получает другое).
         */
        fun buttonMask(keyCode: Int): Int? = when (keyCode) {
            KeyEvent.KEYCODE_BUTTON_A -> InputButtons.A
            KeyEvent.KEYCODE_BUTTON_B -> InputButtons.B
            KeyEvent.KEYCODE_BUTTON_X -> InputButtons.X
            KeyEvent.KEYCODE_BUTTON_Y -> InputButtons.Y
            KeyEvent.KEYCODE_BUTTON_L1 -> InputButtons.LEFT_BUMPER
            KeyEvent.KEYCODE_BUTTON_R1 -> InputButtons.RIGHT_BUMPER
            KeyEvent.KEYCODE_BUTTON_THUMBL -> InputButtons.LEFT_STICK
            KeyEvent.KEYCODE_BUTTON_THUMBR -> InputButtons.RIGHT_STICK
            KeyEvent.KEYCODE_BUTTON_START -> InputButtons.START
            KeyEvent.KEYCODE_BUTTON_SELECT -> InputButtons.BACK
            KeyEvent.KEYCODE_BUTTON_MODE -> InputButtons.GUIDE
            KeyEvent.KEYCODE_DPAD_UP -> InputButtons.DPAD_UP
            KeyEvent.KEYCODE_DPAD_DOWN -> InputButtons.DPAD_DOWN
            KeyEvent.KEYCODE_DPAD_LEFT -> InputButtons.DPAD_LEFT
            KeyEvent.KEYCODE_DPAD_RIGHT -> InputButtons.DPAD_RIGHT

            // Курки L2 и R2 приходят осями: у DualShock они аналоговые, и нажатие
            // как кнопка не передаёт силу. Кнопками их не считаем.
            KeyEvent.KEYCODE_BUTTON_L2, KeyEvent.KEYCODE_BUTTON_R2 -> null
            else -> null
        }

        fun isGamepad(event: KeyEvent): Boolean =
            (event.source and InputDevice.SOURCE_GAMEPAD) == InputDevice.SOURCE_GAMEPAD ||
                (event.source and InputDevice.SOURCE_JOYSTICK) == InputDevice.SOURCE_JOYSTICK

        fun isGamepad(event: MotionEvent): Boolean =
            (event.source and InputDevice.SOURCE_JOYSTICK) == InputDevice.SOURCE_JOYSTICK ||
                (event.source and InputDevice.SOURCE_GAMEPAD) == InputDevice.SOURCE_GAMEPAD
    }
}
