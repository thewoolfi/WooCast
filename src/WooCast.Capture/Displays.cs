using System.Runtime.InteropServices;

namespace WooCast.Capture;

/// <summary>Монитор, доступный для трансляции.</summary>
/// <param name="Index">Порядковый номер, соответствует <c>ddagrab=output_idx</c>.</param>
/// <param name="Name">Понятное имя для интерфейса.</param>
/// <param name="X">Левый край в координатах рабочего стола (может быть отрицательным).</param>
/// <param name="Y">Верхний край в координатах рабочего стола.</param>
/// <param name="Width">Ширина в пикселях.</param>
/// <param name="Height">Высота в пикселях.</param>
/// <param name="IsPrimary">Основной монитор системы.</param>
public readonly record struct DisplayInfo(
    int Index,
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary)
{
    /// <summary>Подпись для выпадающего списка, например «Экран 1 · 1920×1080 (основной)».</summary>
    public string Label =>
        $"{Name} · {Width}×{Height}" + (IsPrimary ? " (основной)" : string.Empty);

    /// <summary>Содержит ли монитор точку рабочего стола.</summary>
    public bool Contains(int pointX, int pointY) =>
        pointX >= X && pointX < X + Width && pointY >= Y && pointY < Y + Height;
}

/// <summary>Перечисление мониторов Windows.</summary>
/// <remarks>
/// Порядок, который возвращает <c>EnumDisplayMonitors</c>, совпадает с порядком вывода
/// DXGI в подавляющем большинстве конфигураций — этого достаточно, чтобы сопоставить
/// выбранный пользователем экран с <c>ddagrab=output_idx</c>.
/// </remarks>
public static class Displays
{
#pragma warning disable SYSLIB1054 // LibraryImport неудобен для делегата обратного вызова
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    private const uint MonitorInfoPrimary = 1;

    /// <summary>Список мониторов. Никогда не возвращает пустой список.</summary>
    public static IReadOnlyList<DisplayInfo> Enumerate()
    {
        // Координаты нужны физические: иначе они не совпадут с кадром захвата.
        DpiAwareness.EnsurePerMonitor();

        var result = new List<DisplayInfo>();

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref Rect _, IntPtr _) =>
            {
                var info = new MonitorInfo
                {
                    Size = Marshal.SizeOf<MonitorInfo>(),
                    Device = string.Empty,
                };

                if (!GetMonitorInfo(monitor, ref info))
                {
                    return true;
                }

                var index = result.Count;
                var width = info.Monitor.Right - info.Monitor.Left;
                var height = info.Monitor.Bottom - info.Monitor.Top;

                result.Add(new DisplayInfo(
                    index,
                    $"Экран {index + 1}",
                    info.Monitor.Left,
                    info.Monitor.Top,
                    width,
                    height,
                    (info.Flags & MonitorInfoPrimary) != 0));

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Не Windows или API недоступен — отдаём один виртуальный экран.
        }

        if (result.Count == 0)
        {
            result.Add(new DisplayInfo(0, "Экран 1", 0, 0, 1920, 1080, true));
        }

        return result;
    }

    /// <summary>Основной монитор или первый доступный.</summary>
    public static DisplayInfo Primary()
    {
        var displays = Enumerate();
        return displays.FirstOrDefault(display => display.IsPrimary, displays[0]);
    }
}
