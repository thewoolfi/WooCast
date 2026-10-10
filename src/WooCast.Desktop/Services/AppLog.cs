using System.Collections.ObjectModel;
using System.Diagnostics;

namespace WooCast.Desktop.Services;

/// <summary>
/// Журнал приложения: ограниченный список последних строк, пригодный для показа в интерфейсе.
/// </summary>
/// <remarks>
/// Строки приходят из разных потоков (сеть, ffmpeg, UI), поэтому добавление
/// синхронизировано, а уведомление об изменении отдаётся одним событием.
/// </remarks>
public sealed class AppLog
{
    private const int Limit = 400;

    private readonly Lock _gate = new();

    /// <summary>Строки журнала в порядке добавления.</summary>
    public ObservableCollection<string> Lines { get; } = [];

    /// <summary>Добавлена новая строка.</summary>
    public event EventHandler<string>? LineAdded;

    public void Info(string message) => Add("info", message);

    public void Warn(string message) => Add("warn", message);

    public void Error(string message) => Add("error", message);

    public void Success(string message) => Add("ok", message);

    public void Add(string level, string message)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var prefix = level switch
        {
            "warn" => "!",
            "error" => "✗",
            "ok" => "✓",
            _ => "·",
        };

        var line = $"{stamp}  {prefix}  {message}";

        lock (_gate)
        {
            Lines.Add(line);

            while (Lines.Count > Limit)
            {
                Lines.RemoveAt(0);
            }
        }

        Debug.WriteLine($"[WooCast] {line}");
        LineAdded?.Invoke(this, line);
    }

    /// <summary>Текст журнала целиком — для копирования в буфер обмена.</summary>
    public string Dump()
    {
        lock (_gate)
        {
            return string.Join(Environment.NewLine, Lines);
        }
    }
}
