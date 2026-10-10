using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace WooCast.Capture;

/// <summary>Сведения о доступном обновлении.</summary>
/// <param name="Version">Версия последнего выпуска без ведущей «v».</param>
/// <param name="Notes">Описание выпуска: что изменилось.</param>
/// <param name="PageUrl">Страница выпуска — открывается в браузере.</param>
/// <param name="DownloadUrl">Прямая ссылка на сборку для Windows, если она приложена.</param>
/// <param name="IsNewer">Новее ли эта версия, чем установленная.</param>
public readonly record struct UpdateInfo(
    string Version,
    string? Notes,
    string? PageUrl,
    string? DownloadUrl,
    bool IsNewer,
    string? Digest);

/// <summary>
/// Проверка обновлений через выпуски GitHub.
/// </summary>
/// <remarks>
/// Обновления берутся из выпусков репозитория: сборка прикладывается к выпуску,
/// а приложение спрашивает у GitHub, какой выпуск последний. Своего сервера
/// для этого не нужно, вебхук тоже не нужен — вебхук уведомлял бы сервер,
/// а здесь приложение само задаёт вопрос.
/// <para>
/// Недоступная сеть, отсутствующий репозиторий или пустой список выпусков —
/// не ошибка приложения: проверка просто ничего не сообщает. Тревожить
/// пользователя из-за недоступного GitHub нельзя.
/// </para>
/// </remarks>
public static class UpdateChecker
{
    /// <summary>
    /// Переменная окружения с репозиторием обновлений в виде «владелец/имя».
    /// </summary>
    /// <remarks>
    /// Позволяет проверить обновление на любом репозитории, не пересобирая приложение:
    /// этим пользуется и команда <c>woocast update-check</c>.
    /// </remarks>
    public const string RepositoryVariable = "WOOCAST_UPDATE_REPO";

    /// <summary>Репозиторий обновлений по умолчанию.</summary>
    public const string DefaultRepository = "woocast/woocast";

    private static readonly HttpClient Client = CreateClient();

    /// <summary>Репозиторий, у которого спрашивают о выпусках.</summary>
    public static string Repository
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(RepositoryVariable);

            return string.IsNullOrWhiteSpace(configured) ? DefaultRepository : configured.Trim();
        }
    }

    /// <summary>
    /// Узнать, есть ли выпуск новее установленной версии.
    /// </summary>
    /// <param name="currentVersion">Текущая версия, например «0.1.0».</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Сведения о выпуске или <see langword="null"/>, если узнать не удалось.</returns>
    public static async Task<UpdateInfo?> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var repository = Repository;

        if (string.IsNullOrWhiteSpace(repository) || !repository.Contains('/'))
        {
            return null;
        }

        try
        {
            var url = $"https://api.github.com/repos/{repository}/releases/latest";

            using var response = await Client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Нет выпусков, репозиторий закрыт или его нет — молчим.
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;

            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            var version = Normalize(tag);
            var pageUrl = root.TryGetProperty("html_url", out var pageElement) ? pageElement.GetString() : null;
            var notes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null;
            var (downloadUrl, digest) = FindWindowsAsset(root);

            return new UpdateInfo(
                version,
                Shorten(notes),
                pageUrl,
                downloadUrl,
                CompareVersions(version, currentVersion) > 0,
                digest);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
        {
            // Сеть недоступна или ответ не разобрался: обновление — не то, ради чего
            // стоит показывать ошибку.
            return null;
        }
    }

    /// <summary>Найти в выпуске сборку для Windows.</summary>
    /// <remarks>
    /// Кроме ссылки берётся и контрольная сумма: GitHub отдаёт её вместе с вложением,
    /// и по ней проверяется, что скачанный файл не повреждён и не подменён.
    /// </remarks>
    private static (string? Url, string? Digest) FindWindowsAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;

            if (name is null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                ? urlElement.GetString()
                : null;

            var digest = asset.TryGetProperty("digest", out var digestElement)
                ? digestElement.GetString()
                : null;

            return (url, digest);
        }

        return (null, null);
    }

    /// <summary>Убрать ведущую «v» из метки выпуска.</summary>
    public static string Normalize(string tag) =>
        tag.Trim().TrimStart('v', 'V');

    /// <summary>
    /// Сравнить две версии.
    /// </summary>
    /// <remarks>
    /// Версии сравниваются по числам, а не по строкам: иначе «0.9.0» оказалась бы
    /// новее «0.10.0». Части, которые не являются числами (например, «beta»),
    /// сравниваются как есть и считаются старше числа — так предварительные
    /// выпуски не выдаются за окончательные.
    /// </remarks>
    /// <returns>Больше нуля, если <paramref name="left"/> новее; меньше — если старше.</returns>
    public static int CompareVersions(string left, string right)
    {
        var leftParts = Split(left);
        var rightParts = Split(right);
        var length = Math.Max(leftParts.Count, rightParts.Count);

        for (var i = 0; i < length; i++)
        {
            var a = i < leftParts.Count ? leftParts[i] : "0";
            var b = i < rightParts.Count ? rightParts[i] : "0";

            var aIsNumber = int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var aNumber);
            var bIsNumber = int.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bNumber);

            var comparison = aIsNumber && bIsNumber
                ? aNumber.CompareTo(bNumber)
                : !aIsNumber && !bIsNumber
                    ? string.CompareOrdinal(a, b)
                    : aIsNumber ? 1 : -1;

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static List<string> Split(string version)
    {
        var normalized = Normalize(version);

        // Разделители версии: точка, дефис и знак «плюс» (сборка).
        var parts = normalized
            .Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();

        return parts.Count == 0 ? ["0"] : parts;
    }

    private static string? Shorten(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var trimmed = notes.Trim();

        // Описание выпуска может быть длинным: в интерфейсе показывается только начало.
        return trimmed.Length <= 400 ? trimmed : trimmed[..397] + "…";
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // GitHub не отвечает без имени клиента.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WooCast");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        return client;
    }
}
