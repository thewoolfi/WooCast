using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;

namespace WooCast.Capture;

/// <summary>Итог установки обновления.</summary>
/// <param name="Path">Где лежит скачанная сборка.</param>
/// <param name="Bytes">Размер файла.</param>
/// <param name="DigestVerified">Совпала ли контрольная сумма с указанной в выпуске.</param>
public readonly record struct DownloadedUpdate(string Path, long Bytes, bool DigestVerified);

/// <summary>
/// Скачивание обновления и подмена запущенного файла.
/// </summary>
/// <remarks>
/// Windows не позволяет заменить работающий исполняемый файл, поэтому обновление
/// делается в два шага: текущая сборка **переименовывается** (это разрешено),
/// на её место кладётся новая, и приложение запускается заново. Прежний файл
/// остаётся рядом — если новая версия не запустится, есть к чему вернуться.
/// <para>
/// Замена выполняется отдельным помощником: приложение к этому моменту ещё работает
/// и не может переписать само себя. Помощник дожидается завершения процесса,
/// подменяет файл и запускает новую версию.
/// </para>
/// </remarks>
public static class UpdateInstaller
{
    private static readonly HttpClient Client = CreateClient();

    /// <summary>Имя прежней сборки, остающейся для отката.</summary>
    public const string BackupSuffix = ".prev";

    /// <summary>
    /// Скачать сборку обновления.
    /// </summary>
    /// <param name="downloadUrl">Прямая ссылка на файл из вложения выпуска.</param>
    /// <param name="expectedDigest">Ожидаемая сумма в виде <c>sha256:...</c> или <see langword="null"/>.</param>
    /// <param name="progress">Ход загрузки: доля от 0 до 1, если размер известен.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public static async Task<DownloadedUpdate?> DownloadAsync(
        string downloadUrl,
        string? expectedDigest,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var target = Path.Combine(Path.GetTempPath(), $"WooCast-{Guid.NewGuid():N}.exe");

        try
        {
            using var response = await Client
                .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var total = response.Content.Headers.ContentLength;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = File.Create(target))
            {
                var buffer = new byte[128 * 1024];
                long written = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;

                    if (total is > 0)
                    {
                        progress?.Report((double)written / total.Value);
                    }
                }
            }

            var info = new FileInfo(target);

            // Пустой или подозрительно маленький файл — почти наверняка страница ошибки.
            if (info.Length < 1024 * 512)
            {
                File.Delete(target);
                return null;
            }

            var verified = VerifyDigest(target, expectedDigest);

            if (expectedDigest is { Length: > 0 } && !verified)
            {
                // Сумма не совпала: файл повреждён или подменён. Такую сборку не ставим.
                File.Delete(target);
                return null;
            }

            progress?.Report(1.0);
            return new DownloadedUpdate(target, info.Length, verified);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            TryDelete(target);
            return null;
        }
    }

    /// <summary>Проверить контрольную сумму файла.</summary>
    /// <remarks>
    /// GitHub отдаёт сумму в виде «sha256:шестнадцатеричные цифры». Если суммы нет,
    /// проверка считается пройденной: это не повод отказываться от обновления.
    /// </remarks>
    public static bool VerifyDigest(string path, string? expectedDigest)
    {
        if (string.IsNullOrWhiteSpace(expectedDigest))
        {
            return false;
        }

        var expected = expectedDigest.Contains(':')
            ? expectedDigest[(expectedDigest.IndexOf(':') + 1)..]
            : expectedDigest;

        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));

            return actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Применить обновление и перезапустить приложение.
    /// </summary>
    /// <param name="downloadedPath">Скачанная сборка.</param>
    /// <param name="error">Причина отказа, если применить не удалось.</param>
    /// <returns>Запущенный помощник или <see langword="null"/>.</returns>
    public static Process? TryApplyAndRestart(string downloadedPath, out string error)
    {
        error = string.Empty;

        try
        {
            var current = Environment.ProcessPath;

            if (string.IsNullOrEmpty(current) || !File.Exists(current))
            {
                error = "не удалось определить путь к запущенной программе";
                return null;
            }

            if (!File.Exists(downloadedPath))
            {
                error = "скачанная сборка не найдена";
                return null;
            }

            // Скачанное переносим рядом с программой: помощник работает в той же папке,
            // и путь не зависит от временного каталога, который может быть очищен.
            var staged = current + ".new";
            File.Copy(downloadedPath, staged, overwrite: true);

            var backup = current + BackupSuffix;
            var helper = Path.Combine(Path.GetTempPath(), $"woocast-update-{Guid.NewGuid():N}.cmd");

            // Помощник ждёт освобождения файла: Windows не даёт заменить работающий
            // исполняемый файл, поэтому переименование делается после выхода процесса.
            var script = $"""
                @echo off
                chcp 65001 >nul

                rem Ждём завершения приложения: файл занят, пока процесс жив.
                :wait
                tasklist /fi "PID eq {Environment.ProcessId}" | find "{Environment.ProcessId}" >nul
                if not errorlevel 1 (
                    timeout /t 1 /nobreak >nul
                    goto wait
                )

                if exist "{current}" move /y "{current}" "{backup}" >nul
                move /y "{staged}" "{current}" >nul
                start "" "{current}"
                del "%~f0"
                """;

            File.WriteAllText(helper, script, new System.Text.UTF8Encoding(false));

            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c \"{helper}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            return Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Временный файл может остаться — это не беда.
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WooCast");

        return client;
    }
}
