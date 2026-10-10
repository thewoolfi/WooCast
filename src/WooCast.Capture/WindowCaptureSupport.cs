using System.Runtime.Versioning;
using Windows.Graphics.Capture;

namespace WooCast.Capture;

/// <summary>
/// Проверка доступности системного захвата окна (Windows.Graphics.Capture).
/// </summary>
/// <remarks>
/// Этот способ отдаёт содержимое окна напрямую, независимо от того, что его перекрывает.
/// Прежний способ — обрезка снимка экрана — показывает то, что лежит сверху, и именно
/// поэтому «выбор окна» работал не так, как ожидается.
/// <para>
/// Доступность зависит и от версии системы, и от политики: в некоторых сборках Windows
/// захват отключён групповой политикой, и вызов создания элемента падает. Поэтому
/// проверка выполняется заранее, а не в момент запуска трансляции.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class WindowCaptureSupport
{
    /// <summary>
    /// Переменная окружения для отказа от системного захвата окна.
    /// </summary>
    /// <remarks>
    /// Нужна в двух случаях: на машине, где системный захват работает неправильно
    /// (тогда остаётся прежний способ — обрезка снимка экрана), и при проверке самого
    /// запасного пути.
    /// </remarks>
    public const string DisableVariable = "WOOCAST_NO_WINDOW_CAPTURE";

    /// <summary>Поддерживает ли система захват окна.</summary>
    public static bool IsSupported()
    {
        if (IsDisabledByConfiguration())
        {
            return false;
        }

        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch (Exception)
        {
            // Проверка не должна ронять приложение: недоступность захвата — не ошибка,
            // а повод использовать прежний способ.
            return false;
        }
    }

    /// <summary>Отключён ли системный захват настройкой.</summary>
    public static bool IsDisabledByConfiguration()
    {
        var value = Environment.GetEnvironmentVariable(DisableVariable);

        return !string.IsNullOrWhiteSpace(value) &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("да", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Короткое пояснение для интерфейса и журнала.</summary>
    public static string Describe()
    {
        if (IsDisabledByConfiguration())
        {
            return $"отключён переменной {DisableVariable} — захват пойдёт обрезкой снимка экрана";
        }

        return IsSupported()
            ? "Windows.Graphics.Capture доступен — окно захватывается вместе с перекрытым содержимым"
            : "Windows.Graphics.Capture недоступен — захват окна пойдёт обрезкой снимка экрана";
    }
}
