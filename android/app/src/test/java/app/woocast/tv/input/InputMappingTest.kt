// SPDX-License-Identifier: MIT
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.input

import android.view.KeyEvent
import app.woocast.tv.net.InputAxes
import app.woocast.tv.net.InputButtons
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * Проверка преобразования управления с геймпада.
 *
 * Тесты выполняются на JVM: вся логика здесь чистая и не требует ни устройства,
 * ни контроллера. Проверяются два места, где ошибка обходится дорого:
 * разворот оси «вверх» и перенос курков из дробного значения в целое.
 */
class InputMappingTest {

    @Test
    fun `стик в крайних положениях дает предельные значения`() {
        assertEquals(InputAxes.STICK_MAX, InputAxes.fromAxis(1f))
        assertEquals(InputAxes.STICK_MIN, InputAxes.fromAxis(-1f))
        assertEquals(0, InputAxes.fromAxis(0f))
    }

    @Test
    fun `значения за пределами диапазона ограничиваются`() {
        assertEquals(InputAxes.STICK_MAX, InputAxes.fromAxis(5f))
        assertEquals(InputAxes.STICK_MIN, InputAxes.fromAxis(-5f))
    }

    /**
     * У Android «вверх» — отрицательное значение оси, а у геймпада Xbox и в нашей
     * нумерации «вверх» положительное. Без разворота стик работал бы наоборот,
     * и персонаж уходил бы в землю вместо неба.
     */
    @Test
    fun `вертикальная ось разворачивается`() {
        assertEquals(InputAxes.STICK_MIN, InputAxes.fromAxis(1f, invert = true))
        assertEquals(InputAxes.STICK_MAX, InputAxes.fromAxis(-1f, invert = true))
        assertEquals(0, InputAxes.fromAxis(0f, invert = true))
    }

    @Test
    fun `курок переводится в целое от нуля до предела`() {
        assertEquals(0, InputAxes.fromTrigger(0f))
        assertEquals(InputAxes.TRIGGER_MAX, InputAxes.fromTrigger(1f))
        assertEquals(InputAxes.TRIGGER_MAX / 2, InputAxes.fromTrigger(0.5f))
    }

    /**
     * Все оси курков в Android лежат в пределах от нуля до единицы, поэтому минус
     * на входе — это ошибка устройства, и он должен читаться как отпущенный курок,
     * а не как наполовину нажатый.
     */
    @Test
    fun `отрицательное значение курка читается как отпущенный`() {
        assertEquals(0, InputAxes.fromTrigger(-1f))
        assertEquals(0, InputAxes.fromTrigger(-0.5f))
    }

    /**
     * Android приводит любой контроллер к общему виду: крест DualShock приходит
     * как кнопка A, круг — как B. Проверяем, что соответствие не потерялось.
     */
    @Test
    fun `кнопки контроллера переводятся в общую маску`() {
        assertEquals(InputButtons.A, GamepadState.buttonMask(KeyEvent.KEYCODE_BUTTON_A))
        assertEquals(InputButtons.B, GamepadState.buttonMask(KeyEvent.KEYCODE_BUTTON_B))
        assertEquals(InputButtons.X, GamepadState.buttonMask(KeyEvent.KEYCODE_BUTTON_X))
        assertEquals(InputButtons.Y, GamepadState.buttonMask(KeyEvent.KEYCODE_BUTTON_Y))
        assertEquals(InputButtons.LEFT_BUMPER, GamepadState.buttonMask(KeyEvent.KEYCODE_BUTTON_L1))
        assertEquals(InputButtons.RIGHT_BUMPER, GamepadState.buttonMask(KeyEvent.KEYCODE_BUTTON_R1))
        assertEquals(InputButtons.START, GamepadState.buttonMask(KeyEvent.KEYCODE_BUTTON_START))
        assertEquals(InputButtons.BACK, GamepadState.buttonMask(KeyEvent.KEYCODE_BUTTON_SELECT))
        assertEquals(InputButtons.DPAD_UP, GamepadState.buttonMask(KeyEvent.KEYCODE_DPAD_UP))
        assertEquals(InputButtons.DPAD_LEFT, GamepadState.buttonMask(KeyEvent.KEYCODE_DPAD_LEFT))
    }

    /** Курки приходят осями: кнопками их считать нельзя, иначе теряется сила нажатия. */
    @Test
    fun `курки не считаются кнопками`() {
        assertNull(GamepadState.buttonMask(KeyEvent.KEYCODE_BUTTON_L2))
        assertNull(GamepadState.buttonMask(KeyEvent.KEYCODE_BUTTON_R2))
    }

    /** Кнопки пульта и клавиатуры управлением не являются. */
    @Test
    fun `чужие кнопки не переводятся`() {
        assertNull(GamepadState.buttonMask(KeyEvent.KEYCODE_VOLUME_UP))
        assertNull(GamepadState.buttonMask(KeyEvent.KEYCODE_HOME))
    }

    @Test
    fun `маска кнопок не пересекается`() {
        val masks = listOf(
            InputButtons.A, InputButtons.B, InputButtons.X, InputButtons.Y,
            InputButtons.LEFT_BUMPER, InputButtons.RIGHT_BUMPER,
            InputButtons.BACK, InputButtons.START,
            InputButtons.LEFT_STICK, InputButtons.RIGHT_STICK, InputButtons.GUIDE,
            InputButtons.DPAD_UP, InputButtons.DPAD_DOWN,
            InputButtons.DPAD_LEFT, InputButtons.DPAD_RIGHT,
        )

        // Каждая кнопка — отдельный бит: совпадение масок означало бы, что нажатие
        // одной кнопки приходит в игру как другая.
        assertEquals(masks.size, masks.toSet().size)
    }

    /** Состояние контроллера начинает с нейтрального положения и меняется по кнопкам. */
    @Test
    fun `состояние начинается с покоя`() {
        val state = GamepadState()
        state.onDeviceAdded("проверка")

        val first = state.take()
        assertEquals("нажатий быть не должно", 0, first?.buttons)
        assertEquals(0, first?.lx)
        assertEquals("проверка", first?.device)
    }

    /** Без изменений состояние не отправляется: иначе канал забивался бы впустую. */
    @Test
    fun `без изменений состояние не отправляется`() {
        val state = GamepadState()
        state.onDeviceAdded("проверка")
        state.take()

        assertNull("повторная отправка без изменений не нужна", state.take())
    }
}
