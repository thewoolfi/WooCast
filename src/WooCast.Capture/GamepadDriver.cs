using System.Diagnostics;

namespace WooCast.Capture;

/// <summary>
/// Драйвер виртуального геймпада: проверка, поиск установщика и запуск установки.
/// </summary>
/// <remarks>
/// Драйвер ViGEmBus нужен только для управления с контроллера, подключённого
/// к приёмнику. Он устанавливается в систему, поэтому рядом с программой лежать
/// не может — но установщик лежит, и приложение запускает его по кнопке,
/// чтобы человеку не пришлось ничего искать.
/// <para>
/// Установка драйвера требует прав администратора: запуск идёт через системный
/// запрос повышения, и отказ пользователя — не ошибка приложения.
/// </para>
/// </remarks>
public static class GamepadDriver
{
    /// <summary>Страница загрузки — если установщика рядом не оказалось.</summary>
    public const string DownloadPage = "https://github.com/nefarius/ViGEmBus/releases";

    private const string DriverFile = @"C:\Windows\System32\drivers\ViGEmBus.sys";

    /// <summary>Установлен ли драйвер в системе.</summary>
    public static bool IsInstalled()
    {
        try
        {
            // Наличие файла драйвера — достаточный признак: он появляется только
            // после установки и не удаляется вместе с приложениями, которые им пользуются.
            return File.Exists(DriverFile);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Найти установщик: рядом с программой, затем в кэше загрузки.
    /// </summary>
    /// <returns>Путь к установщику или <see langword="null"/>.</returns>
    public static string? FindInstaller()
    {
        var baseDirectory = AppContext.BaseDirectory;

        var candidates = new[]
        {
            // Основное место: папка поставки.
            Path.Combine(baseDirectory, "драйвер геймпада", "ViGEmBus_setup.exe"),

            // Запасные варианты: рядом с программой и в кэше загрузки.
            Path.Combine(baseDirectory, "ViGEmBus_setup.exe"),
            Path.Combine(baseDirectory, "driver", "ViGEmBus_setup.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WooCast",
                "vendor",
                "vigem",
                "ViGEmBus_setup.exe"),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // Недоступный путь — просто пробуем следующий.
            }
        }

        return null;
    }

    /// <summary>
    /// Запустить установку драйвера с правами администратора.
    /// </summary>
    /// <param name="error">Причина отказа, если запустить не удалось.</param>
    /// <param name="cancelled">Пользователь отказался от повышения прав.</param>
    /// <returns>Запущенный процесс установки или <see langword="null"/>.</returns>
    public static Process? TryInstall(out string error, out bool cancelled)
    {
        error = string.Empty;
        cancelled = false;

        var installer = FindInstaller();

        if (installer is null)
        {
            error = "установщик не найден рядом с программой";
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo(installer)
            {
                // Установка драйвера невозможна без прав администратора: системный
                // запрос повышения показывает привычное окно Windows.
                UseShellExecute = true,
                Verb = "runas",
            };

            return Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 — пользователь отказался от запроса прав. Это его решение, а не сбой.
            cancelled = true;
            error = "установка отменена";
            return null;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>Открыть страницу загрузки драйвера в браузере.</summary>
    public static void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DownloadPage) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Если браузер не открылся — не беда: адрес показан в интерфейсе.
        }
    }

    /// <summary>Короткое описание состояния драйвера для интерфейса.</summary>
    public static string Describe()
    {
        if (IsInstalled())
        {
            return "Драйвер геймпада установлен";
        }

        return FindInstaller() is null
            ? "Драйвер геймпада не установлен, установщика рядом нет — скачать: " + DownloadPage
            : "Драйвер геймпада не установлен: нажмите «Установить драйвер»";
    }
}
