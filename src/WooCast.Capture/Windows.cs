using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WooCast.Capture;

/// <summary>Окно верхнего уровня, доступное для трансляции.</summary>
/// <param name="Handle">Дескриптор окна.</param>
/// <param name="Title">Заголовок окна.</param>
/// <param name="ProcessName">Имя процесса, которому принадлежит окно.</param>
/// <param name="X">Левый край в координатах рабочего стола (виртуального экрана).</param>
/// <param name="Y">Верхний край в координатах рабочего стола.</param>
/// <param name="Width">Ширина области окна.</param>
/// <param name="Height">Высота области окна.</param>
public readonly record struct WindowInfo(
    nint Handle,
    string Title,
    string ProcessName,
    int X,
    int Y,
    int Width,
    int Height)
{
    /// <summary>Подпись для выпадающего списка: «Процесс — Заголовок (1920×1080)».</summary>
    public string Label
    {
        get
        {
            var title = Title.Length > 60 ? Title[..57] + "…" : Title;
            return $"{ProcessName} — {title}  ({Width}×{Height})";
        }
    }

    /// <summary>Окно ещё существует и не свёрнуто.</summary>
    public bool IsAlive => Handle != 0 && Windows.IsWindowUsable(Handle);
}

/// <summary>
/// Перечисление и отслеживание окон Windows.
/// </summary>
/// <remarks>
/// Захват отдельного окна в этапе 1 делается «отрезанием» его области от снимка рабочего
/// стола (<c>ddagrab</c> + <c>crop</c>): это даёт GPU-захват без внешнего процесса на кадр.
/// Отсюда важное следствие — окно должно быть видимым: перекрытые части покажут то, что
/// находится сверху. Захват перекрытых окон без этого ограничения появится на следующем
/// этапе вместе с Windows.Graphics.Capture.
/// </remarks>
public static class Windows
{
#pragma warning disable SYSLIB1054 // LibraryImport неудобен для делегатов обратного вызова
    private delegate bool EnumWindowsProc(nint handle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(nint handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(nint handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(nint handle, StringBuilder name, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint handle, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out Rect rect);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint handle, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out Rect value, int size);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    private const uint GwOwner = 4;
    private const uint DwmwaCloaked = 14;
    private const uint DwmwaExtendedFrameBounds = 9;

    /// <summary>Индекс расширенных стилей окна для GetWindowLongPtr.</summary>
    private const int GwlExStyle = -20;

    /// <summary>WS_EX_TOOLWINDOW: плавающие панели, подсказки и прозрачные оверлеи.</summary>
    private const long WsExToolWindow = 0x00000080;

    /// <summary>Классы окон оболочки Windows — это рабочий стол, а не приложение.</summary>
    private static readonly string[] ShellWindowClasses =
    [
        "Progman",              // рабочий стол
        "WorkerW",              // обои и слой под иконками
        "Shell_TrayWnd",        // панель задач
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",  // системные окна UWP (меню «Пуск», поиск)
        "Windows.UI.Input.InputSite.WindowClass",
    ];

    /// <summary>Окно принадлежит оболочке Windows, а не приложению.</summary>
    private static bool IsShellWindow(string className)
    {
        foreach (var shellClass in ShellWindowClasses)
        {
            if (string.Equals(className, shellClass, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Имя класса окна. Пустая строка, если получить не удалось.</summary>
    private static string GetClassNameText(nint handle)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(handle, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    /// <summary>Расширенные стили окна. Разрядность учитывается: 32-битная функция — запасной путь.</summary>
    private static nint GetWindowLongPtr(nint handle, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(handle, index) : new nint(GetWindowLong32(handle, index));

    /// <summary>
    /// Список окон, которые имеет смысл транслировать: видимые, не свёрнутые,
    /// с заголовком и ненулевой площадью.
    /// </summary>
    /// <param name="excludeProcessId">
    /// Процесс, окна которого нужно исключить — обычно сам WooCast, чтобы не транслировать себя.
    /// </param>
    public static IReadOnlyList<WindowInfo> Enumerate(int excludeProcessId = 0)
    {
        // Прямоугольники окон должны быть в тех же пикселях, что и кадр захвата.
        DpiAwareness.EnsurePerMonitor();

        var result = new List<WindowInfo>();

        try
        {
            EnumWindows((handle, _) =>
            {
                var info = Describe(handle, excludeProcessId);
                if (info is not null)
                {
                    result.Add(info.Value);
                }

                return true;
            }, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Не Windows — список окон недоступен.
            return [];
        }

        return result
            .OrderBy(window => window.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Описание одного окна или <see langword="null"/>, если оно не подходит.</summary>
    public static WindowInfo? Describe(nint handle, int excludeProcessId = 0)
    {
        if (handle == 0 || !IsWindow(handle) || !IsWindowVisible(handle) || IsIconic(handle))
        {
            return null;
        }

        // Окна-владельцы (всплывающие подсказки, меню) пропускаем.
        if (GetWindow(handle, GwOwner) != 0)
        {
            return null;
        }

        // Скрытые окна UWP-приложений остаются «видимыми» для Win32.
        if (DwmGetWindowAttribute(handle, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return null;
        }

        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return null;
        }

        // Рабочий стол и панель задач — это не приложения. «Program Manager» занимает
        // весь экран, и его выбор в списке окон выглядит как «транслируется весь рабочий
        // стол»: пользователь думает, что режим окна не работает.
        var className = GetClassNameText(handle);
        if (IsShellWindow(className))
        {
            return null;
        }

        // Прозрачные оверлеи (например, NVIDIA GeForce Overlay) тоже растянуты на весь
        // экран и не содержат собственного изображения — в списке окон им не место.
        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        if ((extendedStyle & WsExToolWindow) != 0)
        {
            return null;
        }

        var buffer = new StringBuilder(length + 1);
        GetWindowText(handle, buffer, buffer.Capacity);
        var title = buffer.ToString().Trim();
        if (title.Length == 0)
        {
            return null;
        }

        // Границы с учётом тени: DwmGetWindowAttribute точнее, чем GetWindowRect.
        var rect = default(Rect);
        if (DwmGetWindowAttribute(handle, DwmwaExtendedFrameBounds, out Rect extended, Marshal.SizeOf<Rect>()) == 0)
        {
            rect = extended;
        }
        else if (!GetWindowRect(handle, out rect))
        {
            return null;
        }

        if (rect.Width < 160 || rect.Height < 120)
        {
            return null;
        }

        var processName = "?";
        GetWindowThreadProcessId(handle, out uint processId);

        if (excludeProcessId != 0 && processId == (uint)excludeProcessId)
        {
            return null;
        }

        try
        {
            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Процесс мог завершиться между перечислением и запросом имени.
        }

        return new WindowInfo(handle, title, processName, rect.Left, rect.Top, rect.Width, rect.Height);
    }

    /// <summary>Окно существует, видимо и не свёрнуто.</summary>
    public static bool IsWindowUsable(nint handle) =>
        handle != 0 && IsWindow(handle) && IsWindowVisible(handle) && !IsIconic(handle);

    /// <summary>
    /// Окно свёрнуто.
    /// </summary>
    /// <remarks>
    /// Важно для системного захвата: Windows не отрисовывает свёрнутые окна, поэтому
    /// кадры перестают поступать. Это не сбой, а свойство системы — о нём нужно сказать
    /// пользователю, иначе замершая картинка выглядит как поломка.
    /// </remarks>
    public static bool IsMinimized(nint handle) => handle != 0 && IsIconic(handle);

    /// <summary>Текущие границы окна в координатах рабочего стола.</summary>
    public static (int X, int Y, int Width, int Height)? GetBounds(nint handle)
    {
        if (handle == 0 || !IsWindow(handle))
        {
            return null;
        }

        var rect = default(Rect);
        if (DwmGetWindowAttribute(handle, DwmwaExtendedFrameBounds, out Rect extended, Marshal.SizeOf<Rect>()) == 0)
        {
            rect = extended;
        }
        else if (!GetWindowRect(handle, out rect))
        {
            return null;
        }

        return (rect.Left, rect.Top, rect.Width, rect.Height);
    }

    /// <summary>
    /// Индекс монитора, на котором находится окно, и координаты окна относительно этого монитора.
    /// </summary>
    /// <remarks>
    /// <c>ddagrab</c> захватывает конкретный вывод, поэтому координаты обрезки нужно
    /// пересчитать в систему координат выбранного монитора.
    /// </remarks>
    public static (int DisplayIndex, int X, int Y, int Width, int Height) MapToDisplay(
        WindowInfo window,
        IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0)
        {
            return (0, window.X, window.Y, window.Width, window.Height);
        }

        // Центр окна определяет, на каком мониторе оно находится.
        var centerX = window.X + (window.Width / 2);
        var centerY = window.Y + (window.Height / 2);

        var best = displays[0];
        var bestDistance = int.MaxValue;

        foreach (var display in displays)
        {
            var inside = display.Contains(centerX, centerY);
            var distance = inside
                ? 0
                : Math.Abs(centerX - (display.X + (display.Width / 2))) +
                  Math.Abs(centerY - (display.Y + (display.Height / 2)));

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = display;
            }
        }

        // Координаты обрезки — в системе координат выбранного монитора.
        var x = Math.Max(0, window.X - best.X);
        var y = Math.Max(0, window.Y - best.Y);

        // Кадр кодировщика должен иметь чётные размеры (требование yuv420p).
        var width = Math.Min(window.Width, best.Width - x);
        var height = Math.Min(window.Height, best.Height - y);

        return (best.Index, MakeEven(x), MakeEven(y), MakeEven(width), MakeEven(height));
    }

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;
}
