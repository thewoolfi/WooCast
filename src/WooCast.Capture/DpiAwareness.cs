using System.Runtime.InteropServices;

namespace WooCast.Capture;

/// <summary>
/// Приведение процесса к DPI-осведомлённости «на монитор».
/// </summary>
/// <remarks>
/// Без этого Windows отдаёт координаты в «виртуальных» пикселях: при масштабе 125 %
/// экран 1920×1080 выглядит как 1600×900, и такими же приходят прямоугольники окон.
/// Захват же (<c>ddagrab</c>, Desktop Duplication) работает с физическим кадром, поэтому
/// обрезка окна по виртуальным координатам попадала бы совсем в другое место — окно
/// «съезжало», а крупные окна давали почти весь рабочий стол.
/// <para>
/// Приложение для Windows объявляет осведомлённость в манифесте; этот вызов нужен
/// консольным командам и служит запасным вариантом, если манифест не применился.
/// </para>
/// </remarks>
public static class DpiAwareness
{
    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.</summary>
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    /// <summary>PROCESS_PER_MONITOR_DPI_AWARE.</summary>
    private const int ProcessPerMonitorDpiAware = 2;

    private static bool _applied;

    /// <summary>Сообщить Windows, что координаты нужны физические.</summary>
    /// <remarks>
    /// Вызов безопасен и повторно, и после манифеста: если осведомлённость уже задана,
    /// Windows вернёт ошибку, которую здесь можно игнорировать. Важно лишь то, что
    /// процесс её получил.
    /// </remarks>
    public static void EnsurePerMonitor()
    {
        if (_applied)
        {
            return;
        }

        _applied = true;

        // Windows 10 1703 и новее — самый точный режим.
        try
        {
            if (SetProcessDpiAwarenessContext(PerMonitorAwareV2))
            {
                return;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }

        // Windows 8.1 и новее.
        try
        {
            if (SetProcessDpiAwareness(ProcessPerMonitorDpiAware) == 0)
            {
                return;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }

        // Windows Vista и новее: системная осведомлённость, но всё же физические пиксели.
        try
        {
            SetProcessDPIAware();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("shcore.dll", SetLastError = true)]
    private static extern int SetProcessDpiAwareness(int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDPIAware();
}
