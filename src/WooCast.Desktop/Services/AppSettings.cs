using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WooCast.Desktop.Services;

/// <summary>
/// Настройки, которые переживают перезапуск приложения.
/// </summary>
/// <remarks>
/// Хранятся в <c>%APPDATA%\WooCast\settings.json</c>: это пользовательские данные,
/// а не машинные, и они не должны попадать в каталог с программой.
/// </remarks>
public sealed class AppSettings
{
    /// <summary>Идентификатор приставки, выбранной в прошлый раз.</summary>
    public string? LastDeviceId { get; set; }

    /// <summary>Источник: 0 — весь экран, 1 — отдельное окно.</summary>
    public int SourceKindIndex { get; set; }

    /// <summary>Номер монитора.</summary>
    public int DisplayIndex { get; set; }

    /// <summary>Заголовок окна, выбранного в прошлый раз.</summary>
    public string? WindowTitle { get; set; }

    public int ResolutionIndex { get; set; }

    public int FpsIndex { get; set; } = 1;

    public int QualityIndex { get; set; } = 1;

    public int EncoderIndex { get; set; }

    public bool DrawCursor { get; set; } = true;

    public bool AudioEnabled { get; set; } = true;

    public int AudioBitrateKbps { get; set; } = 160;

    /// <summary>Ручная поправка синхронизации звука, миллисекунды.</summary>
    public int AudioDelayMs { get; set; }

    /// <summary>Принимать ли управление с контроллера, подключённого к приёмнику.</summary>
    public bool GamepadEnabled { get; set; }

    /// <summary>Вид виртуального геймпада: 0 — Xbox 360, 1 — DualShock 4.</summary>
    public int GamepadKindIndex { get; set; }
}

/// <summary>Чтение и запись файла настроек.</summary>
/// <remarks>
/// Ошибки чтения и записи не считаются сбоем приложения: испорченный или недоступный
/// файл — повод начать с настроек по умолчанию, а не отказываться запускаться.
/// </remarks>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>Путь к файлу настроек.</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WooCast",
        "settings.json");

    /// <summary>Прочитать настройки. При любой проблеме возвращает значения по умолчанию.</summary>
    /// <param name="problem">Описание проблемы для журнала или <see langword="null"/>.</param>
    public static AppSettings Load(out string? problem)
    {
        problem = null;

        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);

            if (settings is null)
            {
                problem = "Файл настроек пуст — взяты значения по умолчанию";
                return new AppSettings();
            }

            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            problem = $"Настройки не прочитаны ({ex.Message}) — взяты значения по умолчанию";
            return new AppSettings();
        }
    }

    /// <summary>Записать настройки.</summary>
    /// <param name="problem">Описание проблемы для журнала или <see langword="null"/>.</param>
    /// <returns>Удалось ли сохранить.</returns>
    public static bool Save(AppSettings settings, out string? problem)
    {
        problem = null;

        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            problem = $"Настройки не сохранены: {ex.Message}";
            Debug.WriteLine($"[WooCast] {problem}");
            return false;
        }
    }
}
