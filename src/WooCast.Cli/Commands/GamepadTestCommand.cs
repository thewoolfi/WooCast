using System.Globalization;
using System.Runtime.InteropServices;
using WooCast.Capture;
using WooCast.Protocol;
using WooCast.Protocol.Messages;

namespace WooCast.Cli;

/// <summary>
/// Команда <c>gamepad-test</c>: проверка виртуального геймпада.
/// </summary>
/// <remarks>
/// Создаёт виртуальный геймпад, подаёт в него несколько состояний и **читает их обратно
/// через системный интерфейс XInput**. Это и есть проверка: если Windows видит нажатия
/// и оси, значит игра увидит их тоже. Без обратного чтения проверка сводилась бы
/// к «код не упал», что ничего не доказывает.
/// </remarks>
internal static class GamepadTestCommand
{
    public static int Run(string[] args)
    {
        var kind = args.Any(a => a.Equals("--ds4", StringComparison.OrdinalIgnoreCase))
            ? GamepadKind.DualShock4
            : GamepadKind.Xbox360;

        ConsoleUi.Header("Проверка виртуального геймпада");

        var before = FindConnectedPad();

        using var pad = GamepadOutput.TryStart(kind, out var error);
        if (pad is null)
        {
            ConsoleUi.Error($"Не удалось создать геймпад: {error}");
            return 1;
        }

        ConsoleUi.Success($"Создан: {pad.Describe}");

        // Даём системе время увидеть новое устройство.
        Thread.Sleep(600);

        // DualShock в системе — не устройство XInput, а HID-контроллер, поэтому обратное
        // чтение через XInput к нему неприменимо. Для него проверяется, что состояния
        // принимаются драйвером, и об этом говорится прямо, без выдачи за проверку системы.
        if (kind == GamepadKind.DualShock4)
        {
            pad.Submit(new InputSpec { Buttons = Wcp.InputButtons.A, Lx = 30000, Lt = 200 });
            Thread.Sleep(250);

            ConsoleUi.Success($"Состояния приняты драйвером: подано {pad.Reports}");
            ConsoleUi.Warn("DualShock виден системе как HID-контроллер, а не как XInput-устройство");
            ConsoleUi.Info("  проверка «глазами игры» для него — запустить игру с поддержкой DualShock 4");
            return 0;
        }

        var index = FindConnectedPad();
        if (index < 0)
        {
            ConsoleUi.Error("Система не увидела геймпад после подключения");
            return 1;
        }

        ConsoleUi.Detail("Система видит геймпад", $"устройство №{index}");

        var ok = true;

        // Кнопка A, левый курок до упора, левый стик вправо.
        var press = new InputSpec
        {
            Buttons = Wcp.InputButtons.A,
            Lx = 30000,
            Ly = 0,
            Rx = 0,
            Ry = 0,
            Lt = 200,
            Rt = 0,
            Device = "проверка",
        };

        pad.Submit(press);
        Thread.Sleep(250);

        var state = ReadPad(index);
        if (state is null)
        {
            ConsoleUi.Error("Не удалось прочитать состояние геймпада");
            return 1;
        }

        ok &= Check("кнопка A", (state.Value.Buttons & XinputA) != 0, "нажата");
        ok &= Check("левый курок", Math.Abs(state.Value.LeftTrigger - 200) <= 4, $"{state.Value.LeftTrigger} из 255");
        ok &= Check("левый стик вправо", state.Value.LeftThumbX > 28000, $"{state.Value.LeftThumbX} из 32767");

        // Покой: всё должно вернуться в ноль.
        pad.Release();
        Thread.Sleep(250);

        var released = ReadPad(index);
        if (released is null)
        {
            ConsoleUi.Error("Не удалось прочитать состояние после отпускания");
            return 1;
        }

        ok &= Check("кнопка A отпущена", (released.Value.Buttons & XinputA) == 0, "отпущена");
        ok &= Check("левый стик в покое", Math.Abs(released.Value.LeftThumbX) < 2000, $"{released.Value.LeftThumbX}");

        ConsoleUi.Info();

        if (ok)
        {
            ConsoleUi.Success($"Геймпад работает: подано состояний {pad.Reports}");
            return 0;
        }

        ConsoleUi.Error("Геймпад создан, но состояния не доходят до системы");
        return 1;
    }

    private static bool Check(string name, bool passed, string detail)
    {
        if (passed)
        {
            ConsoleUi.Success($"{name}: {detail}");
        }
        else
        {
            ConsoleUi.Error($"{name}: не подтвердилось ({detail})");
        }

        return passed;
    }

    private static int FindConnectedPad()
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

    private const ushort XinputA = 0x1000;

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
