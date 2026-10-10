using WooCast.Capture;

// Псевдонимы нужны из-за проекций Windows SDK: там есть пространство имён
// CaptureWindows.Graphics.Capture, и короткое имя Windows стало неоднозначным.
using CaptureWindows = WooCast.Capture.Windows;
using CaptureDisplays = WooCast.Capture.Displays;

namespace WooCast.Cli;

/// <summary>Команда <c>windows</c>: список окон, доступных для трансляции.</summary>
internal static class WindowsCommand
{
    public static int Run(string[] args)
    {
        ConsoleUi.Header("Окна, доступные для трансляции");

        var windows = CaptureWindows.Enumerate(Environment.ProcessId);

        if (windows.Count == 0)
        {
            ConsoleUi.Warn("Окна не найдены");
            return 2;
        }

        var displays = Displays.Enumerate();

        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            var mapped = CaptureWindows.MapToDisplay(window, displays);

            ConsoleUi.Info($"[{i,2}]  {window.ProcessName} — {Truncate(window.Title, 58)}");
            ConsoleUi.Info(
                $"      {window.Width}×{window.Height} на экране {mapped.DisplayIndex + 1}" +
                $", область {mapped.Width}×{mapped.Height}+{mapped.X}+{mapped.Y}");
        }

        ConsoleUi.Info();
        ConsoleUi.Info("Трансляция окна: woocast send --to <ip> --window-id <номер>");
        ConsoleUi.Info();
        ConsoleUi.Warn("Захват идёт по снимку экрана, поэтому перекрытые окна покажут то, что сверху.");
        ConsoleUi.Info("  Захват перекрытых окон появится на следующем этапе (CaptureWindows.Graphics.Capture).");
        return 0;
    }

    /// <summary>Собрать цель по номеру окна из списка.</summary>
    public static (WindowTarget? Target, string? Error) ResolveByIndex(int index)
    {
        var windows = CaptureWindows.Enumerate(Environment.ProcessId);
        if (index < 0 || index >= windows.Count)
        {
            return (null, $"Окно с номером {index} не найдено (всего окон: {windows.Count})");
        }

        return (ToTarget(windows[index]), null);
    }

    /// <summary>Найти окно по фрагменту заголовка или имени процесса.</summary>
    public static (WindowTarget? Target, string? Error) ResolveByTitle(string fragment)
    {
        var windows = CaptureWindows.Enumerate(Environment.ProcessId);

        var matches = windows
            .Where(w => w.Title.Contains(fragment, StringComparison.CurrentCultureIgnoreCase) ||
                        w.ProcessName.Contains(fragment, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            return (null, $"Окно по запросу «{fragment}» не найдено");
        }

        if (matches.Count > 1)
        {
            var list = string.Join(", ", matches.Take(5).Select(m => m.ProcessName));
            return (null, $"Под запрос «{fragment}» подходит несколько окон: {list}. Уточните запрос.");
        }

        return (ToTarget(matches[0]), null);
    }

    private static WindowTarget ToTarget(WindowInfo window)
    {
        var displays = Displays.Enumerate();
        var (displayIndex, _, _, _, _) = CaptureWindows.MapToDisplay(window, displays);
        var display = displays.FirstOrDefault(d => d.Index == displayIndex, displays[0]);

        return new WindowTarget
        {
            Handle = window.Handle,
            Title = window.Title,
            ProcessName = window.ProcessName,
            X = window.X,
            Y = window.Y,
            Width = window.Width,
            Height = window.Height,
            DisplayIndex = display.Index,
            DisplayOriginX = display.X,
            DisplayOriginY = display.Y,
        };
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
