using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using WooCast.Protocol;
using WooCast.Protocol.Messages;

namespace WooCast.Capture;

/// <summary>Каким геймпадом притворяется компьютер.</summary>
public enum GamepadKind
{
    /// <summary>Геймпад Xbox 360: его понимают практически все игры с поддержкой геймпада.</summary>
    Xbox360,

    /// <summary>DualShock 4: для игр с родными подсказками PlayStation.</summary>
    DualShock4,
}

/// <summary>
/// Вывод состояния геймпада с приёмника в виртуальный геймпад Windows.
/// </summary>
/// <remarks>
/// Контроллер подключён к приставке или телефону, а игре нужен геймпад в системе.
/// Поэтому состояние, пришедшее по сети, подаётся в драйвер ViGEmBus, который
/// показывает игре обычный геймпад с аналоговыми осями и курками.
/// <para>
/// Драйвер может быть не установлен — это не ошибка приложения, а отсутствие
/// возможности: в таком случае управление просто не включается, о чём сообщается
/// понятным текстом.
/// </para>
/// </remarks>
public sealed class GamepadOutput : IDisposable
{
    private readonly ViGEmClient _client;
    private readonly IVirtualGamepad _pad;
    private readonly GamepadKind _kind;
    private readonly object _sync = new();

    private bool _disposed;
    private int _lastButtons;
    private bool _hasState;

    private GamepadOutput(ViGEmClient client, IVirtualGamepad pad, GamepadKind kind)
    {
        _client = client;
        _pad = pad;
        _kind = kind;
    }

    /// <summary>Человекочитаемое описание для интерфейса и журнала.</summary>
    public string Describe => _kind == GamepadKind.Xbox360
        ? "виртуальный геймпад Xbox 360"
        : "виртуальный геймпад DualShock 4";

    /// <summary>Сколько состояний подано в геймпад.</summary>
    public long Reports { get; private set; }

    /// <summary>
    /// Создать виртуальный геймпад.
    /// </summary>
    /// <param name="kind">Каким геймпадом притворяться.</param>
    /// <param name="error">Причина отказа, если создать не удалось.</param>
    /// <returns>Геймпад или <see langword="null"/>.</returns>
    public static GamepadOutput? TryStart(GamepadKind kind, out string error)
    {
        error = string.Empty;
        ViGEmClient? client = null;

        try
        {
            // Подключение к драйверу: если его нет, исключение приходит именно здесь.
            client = new ViGEmClient();

            IVirtualGamepad pad = kind == GamepadKind.Xbox360
                ? client.CreateXbox360Controller()
                : client.CreateDualShock4Controller();

            pad.AutoSubmitReport = false;
            pad.Connect();

            return new GamepadOutput(client, pad, kind);        }
        catch (Exception ex)
        {
            client?.Dispose();
            error = DescribeFailure(ex);
            return null;
        }
    }

    /// <summary>Понятное объяснение отказа вместо технического текста драйвера.</summary>
    private static string DescribeFailure(Exception ex)
    {
        var text = ex.Message;

        if (text.Contains("VIGEM_ERROR_BUS_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("bus not found", StringComparison.OrdinalIgnoreCase) ||
            ex is DllNotFoundException or BadImageFormatException)
        {
            return "драйвер ViGEmBus не установлен — установите его с https://github.com/nefarius/ViGEmBus/releases";
        }

        return $"{ex.GetType().Name}: {text}";
    }

    /// <summary>
    /// Подать состояние с приёмника в геймпад.
    /// </summary>
    /// <remarks>
    /// Вызывается из потока приёма сообщений, поэтому защищено от одновременного
    /// вызова: два состояния, поданные вразнобой, дали бы дрожание осей.
    /// </remarks>
    public void Submit(InputSpec state)
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_kind == GamepadKind.Xbox360 && _pad is IXbox360Controller xbox)
                {
                    ApplyXbox360(xbox, state);
                }
                else if (_kind == GamepadKind.DualShock4 && _pad is IDualShock4Controller ds4)
                {
                    ApplyDualShock4(ds4, state);
                }

                _pad.SubmitReport();

                Reports++;
                _hasState = true;
                _lastButtons = state.Buttons;
            }
            catch (Exception)
            {
                // Сбой подачи одного состояния не должен ронять трансляцию:
                // следующее придёт через шестнадцать миллисекунд.
            }
        }
    }

    /// <summary>
    /// Отпустить все кнопки и вернуть оси в покой.
    /// </summary>
    /// <remarks>
    /// Нужно при разрыве связи: иначе игра продолжит считать, что стик отклонён,
    /// и персонаж будет бежать в стену до самого отключения геймпада.
    /// </remarks>
    public void Release()
    {
        if (_disposed || !_hasState)
        {
            return;
        }

        Submit(new InputSpec());
    }

    private static void ApplyXbox360(IXbox360Controller pad, InputSpec state)
    {
        var buttons = state.Buttons;

        pad.SetButtonState(Xbox360Button.A, IsSet(buttons, Wcp.InputButtons.A));
        pad.SetButtonState(Xbox360Button.B, IsSet(buttons, Wcp.InputButtons.B));
        pad.SetButtonState(Xbox360Button.X, IsSet(buttons, Wcp.InputButtons.X));
        pad.SetButtonState(Xbox360Button.Y, IsSet(buttons, Wcp.InputButtons.Y));
        pad.SetButtonState(Xbox360Button.LeftShoulder, IsSet(buttons, Wcp.InputButtons.LeftBumper));
        pad.SetButtonState(Xbox360Button.RightShoulder, IsSet(buttons, Wcp.InputButtons.RightBumper));
        pad.SetButtonState(Xbox360Button.Back, IsSet(buttons, Wcp.InputButtons.Back));
        pad.SetButtonState(Xbox360Button.Start, IsSet(buttons, Wcp.InputButtons.Start));
        pad.SetButtonState(Xbox360Button.LeftThumb, IsSet(buttons, Wcp.InputButtons.LeftStick));
        pad.SetButtonState(Xbox360Button.RightThumb, IsSet(buttons, Wcp.InputButtons.RightStick));
        pad.SetButtonState(Xbox360Button.Guide, IsSet(buttons, Wcp.InputButtons.Guide));
        pad.SetButtonState(Xbox360Button.Up, IsSet(buttons, Wcp.InputButtons.DpadUp));
        pad.SetButtonState(Xbox360Button.Down, IsSet(buttons, Wcp.InputButtons.DpadDown));
        pad.SetButtonState(Xbox360Button.Left, IsSet(buttons, Wcp.InputButtons.DpadLeft));
        pad.SetButtonState(Xbox360Button.Right, IsSet(buttons, Wcp.InputButtons.DpadRight));

        pad.SetAxisValue(Xbox360Axis.LeftThumbX, (short)Wcp.InputAxes.ClampStick(state.Lx));
        pad.SetAxisValue(Xbox360Axis.LeftThumbY, (short)Wcp.InputAxes.ClampStick(state.Ly));
        pad.SetAxisValue(Xbox360Axis.RightThumbX, (short)Wcp.InputAxes.ClampStick(state.Rx));
        pad.SetAxisValue(Xbox360Axis.RightThumbY, (short)Wcp.InputAxes.ClampStick(state.Ry));

        pad.SetSliderValue(Xbox360Slider.LeftTrigger, (byte)Wcp.InputAxes.ClampTrigger(state.Lt));
        pad.SetSliderValue(Xbox360Slider.RightTrigger, (byte)Wcp.InputAxes.ClampTrigger(state.Rt));
    }

    private static void ApplyDualShock4(IDualShock4Controller pad, InputSpec state)
    {
        var buttons = state.Buttons;

        pad.SetButtonState(DualShock4Button.Cross, IsSet(buttons, Wcp.InputButtons.A));
        pad.SetButtonState(DualShock4Button.Circle, IsSet(buttons, Wcp.InputButtons.B));
        pad.SetButtonState(DualShock4Button.Square, IsSet(buttons, Wcp.InputButtons.X));
        pad.SetButtonState(DualShock4Button.Triangle, IsSet(buttons, Wcp.InputButtons.Y));
        pad.SetButtonState(DualShock4Button.ShoulderLeft, IsSet(buttons, Wcp.InputButtons.LeftBumper));
        pad.SetButtonState(DualShock4Button.ShoulderRight, IsSet(buttons, Wcp.InputButtons.RightBumper));
        pad.SetButtonState(DualShock4Button.Share, IsSet(buttons, Wcp.InputButtons.Back));
        pad.SetButtonState(DualShock4Button.Options, IsSet(buttons, Wcp.InputButtons.Start));
        pad.SetButtonState(DualShock4Button.ThumbLeft, IsSet(buttons, Wcp.InputButtons.LeftStick));
        pad.SetButtonState(DualShock4Button.ThumbRight, IsSet(buttons, Wcp.InputButtons.RightStick));

        // У DualShock крестовина — не четыре кнопки, а одно направление, поэтому
        // её положение вычисляется из нажатых сторон.
        pad.SetDPadDirection(ResolveDPad(buttons));

        // Кнопка PS в перечислении кнопок отсутствует: она «особая», отдельным байтом.
        pad.SetSpecialButtonsFull((byte)(IsSet(buttons, Wcp.InputButtons.Guide) ? 0x01 : 0x00));

        // У DualShock оси восьмибитные с серединой в 128, а у нас шестнадцатибитные
        // с нулём в покое — отсюда перенос диапазона.
        pad.SetAxisValue(DualShock4Axis.LeftThumbX, ToByteAxis(state.Lx));
        pad.SetAxisValue(DualShock4Axis.LeftThumbY, ToByteAxis(state.Ly));
        pad.SetAxisValue(DualShock4Axis.RightThumbX, ToByteAxis(state.Rx));
        pad.SetAxisValue(DualShock4Axis.RightThumbY, ToByteAxis(state.Ry));

        pad.SetSliderValue(DualShock4Slider.LeftTrigger, (byte)Wcp.InputAxes.ClampTrigger(state.Lt));
        pad.SetSliderValue(DualShock4Slider.RightTrigger, (byte)Wcp.InputAxes.ClampTrigger(state.Rt));
    }

    /// <summary>Определить направление крестовины по нажатым сторонам.</summary>
    private static DualShock4DPadDirection ResolveDPad(int buttons)
    {
        var up = IsSet(buttons, Wcp.InputButtons.DpadUp);
        var down = IsSet(buttons, Wcp.InputButtons.DpadDown);
        var left = IsSet(buttons, Wcp.InputButtons.DpadLeft);
        var right = IsSet(buttons, Wcp.InputButtons.DpadRight);

        // Противоположные стороны одновременно — обычное дело у крестовин: побеждает
        // та, что нажата последней, но различить это нельзя, поэтому берём «вверх» и «влево».
        if (up && down)
        {
            up = false;
        }

        if (left && right)
        {
            left = false;
        }

        return (up, down, left, right) switch
        {
            (true, _, true, _) => DualShock4DPadDirection.Northwest,
            (true, _, _, true) => DualShock4DPadDirection.Northeast,
            (_, true, true, _) => DualShock4DPadDirection.Southwest,
            (_, true, _, true) => DualShock4DPadDirection.Southeast,
            (true, _, _, _) => DualShock4DPadDirection.North,
            (_, true, _, _) => DualShock4DPadDirection.South,
            (_, _, true, _) => DualShock4DPadDirection.West,
            (_, _, _, true) => DualShock4DPadDirection.East,
            _ => DualShock4DPadDirection.None,
        };
    }

    /// <summary>Перенести значение стика из шестнадцатибитного диапазона в восьмибитный.</summary>
    public static byte ToByteAxis(int value)
    {
        var clamped = Wcp.InputAxes.ClampStick(value);
        return (byte)Math.Clamp((clamped + 32768) / 257, 0, 255);
    }

    private static bool IsSet(int buttons, int mask) => (buttons & mask) != 0;

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _pad.Disconnect();
            }
            catch (Exception)
            {
                // Отключение уже отключённого геймпада не должно мешать освобождению.
            }

            try
            {
                _client.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}
