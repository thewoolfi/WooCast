using System.Globalization;
using System.Runtime.InteropServices;

namespace WooCast.Cli;

/// <summary>
/// Команда <c>gamepad-watch</c>: наблюдение за состоянием геймпада в системе.
/// </summary>
/// <remarks>
/// Нужна для сквозной проверки: одно окно отправляет управления, другое наблюдает,
/// дошли ли они до виртуального геймпада. Без этого проверка сводилась бы к тому,
/// что «сообщения отправлены», а не «игра увидит нажатие».
/// </remarks>
internal static class GamepadWatchCommand
{
    public static int Run(string[] args)
    {
        var seconds = 10.0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--seconds" &&
                double.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var parsed))
            {
                seconds = parsed;
            }
        }

        ConsoleUi.Header($"Наблюдение за геймпадом, {seconds:F0} с");

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var sawAny = false;
        var sawButton = false;
        var sawStick = false;
        var sawTrigger = false;
        var index = -1;

        while (DateTime.UtcNow < deadline)
        {
            if (index < 0)
            {
                index = FindPad();
                if (index < 0)
                {
                    Thread.Sleep(200);
                    continue;
                }

                ConsoleUi.Info($"Геймпад найден: устройство №{index}");
            }

            var state = ReadPad(index);
            if (state is null)
            {
                // Устройство исчезло — ждём следующего появления.
                index = -1;
                Thread.Sleep(200);
                continue;
            }

            var buttons = state.Value.Buttons;
            var stick = state.Value.LeftThumbX;
            var trigger = state.Value.LeftTrigger;

            if (buttons != 0 || stick != 0 || trigger != 0)
            {
                sawAny = true;

                if ((buttons & 0x1000) != 0 && !sawButton)
                {
                    sawButton = true;
                    ConsoleUi.Success($"Кнопка A нажата (стик {stick}, курок {trigger})");
                }

                if (Math.Abs(stick) > 20000 && !sawStick)
                {
                    sawStick = true;
                    ConsoleUi.Success($"Левый стик отклонён: {stick}");
                }

                if (trigger > 150 && !sawTrigger)
                {
                    sawTrigger = true;
                    ConsoleUi.Success($"Левый курок нажат: {trigger} из 255");
                }
            }

            Thread.Sleep(100);
        }

        ConsoleUi.Info();

        if (sawAny)
        {
            ConsoleUi.Success("Состояния дошли до геймпада");
            return 0;
        }

        ConsoleUi.Error("Геймпад найден, но состояния не менялись");
        return 1;
    }

    private static int FindPad()
    {
        for (var i = 0; i < 4; i++)
        {
            if (ReadPad(i) is not null)
            {
                return i;
            }
        }

        return -1;
    }

    private static XinputState? ReadPad(int index)
    {
        try
        {
            return XInputGetState(index, out var state) == 0 ? state : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XinputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftThumbX;
        public short LeftThumbY;
        public short RightThumbX;
        public short RightThumbY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XinputState
    {
        public uint PacketNumber;
        public XinputGamepad Gamepad;

        public readonly ushort Buttons => Gamepad.Buttons;

        public readonly byte LeftTrigger => Gamepad.LeftTrigger;

        public readonly short LeftThumbX => Gamepad.LeftThumbX;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int index, out XinputState state);
}
