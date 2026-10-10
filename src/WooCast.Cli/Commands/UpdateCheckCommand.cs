using System.Reflection;
using WooCast.Capture;

namespace WooCast.Cli;

/// <summary>
/// Команда <c>update-check</c>: проверка наличия новой версии.
/// </summary>
/// <remarks>
/// Позволяет указать репозиторий: тогда проверку можно прогнать на любом живом
/// репозитории GitHub, не пересобирая приложение. Этим проверяется и разбор ответа,
/// и сравнение версий — то есть весь путь целиком, а не только своя логика.
/// </remarks>
internal static class UpdateCheckCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            return RunSelfTest();
        }

        var repository = ReadOption(args, "--repo");
        var current = ReadOption(args, "--current") ?? CurrentVersion();

        if (repository is not null)
        {
            Environment.SetEnvironmentVariable(UpdateChecker.RepositoryVariable, repository);
        }

        ConsoleUi.Header("Проверка обновлений");
        ConsoleUi.Detail("Репозиторий", UpdateChecker.Repository);
        ConsoleUi.Detail("Установленная версия", current);

        var update = await UpdateChecker.CheckAsync(current, cancellationToken);

        if (update is null)
        {
            ConsoleUi.Warn("Узнать о выпусках не удалось");
            ConsoleUi.Info();
            ConsoleUi.Info("Это не ошибка приложения: сеть может быть недоступна, репозиторий");
            ConsoleUi.Info("закрыт или выпусков в нём ещё нет.");
            return 1;
        }

        var info = update.Value;

        ConsoleUi.Detail("Последний выпуск", info.Version);
        ConsoleUi.Detail("Сборка для Windows", info.DownloadUrl ?? "не приложена");
        ConsoleUi.Detail("Контрольная сумма", info.Digest ?? "в выпуске не указана");
        ConsoleUi.Detail("Страница выпуска", info.PageUrl ?? "нет");

        // Скачивание проверяется на настоящем вложении: так проверяется и загрузка,
        // и проверка суммы — то, от чего зависит безопасность обновления.
        if (args.Any(a => a.Equals("--download", StringComparison.OrdinalIgnoreCase)))
        {
            if (info.DownloadUrl is null)
            {
                ConsoleUi.Error("В выпуске нет сборки для Windows — скачивать нечего");
                return 1;
            }

            ConsoleUi.Info();
            ConsoleUi.Info("Скачиваю сборку…");

            var progress = new Progress<double>(value =>
                Console.Write($"\r  загружено {value * 100:F0} %   "));

            var downloaded = await UpdateInstaller
                .DownloadAsync(info.DownloadUrl, ReadOption(args, "--digest") ?? info.Digest, progress, cancellationToken);

            Console.WriteLine();

            if (downloaded is null)
            {
                ConsoleUi.Error("Скачать не удалось или контрольная сумма не совпала");
                return 1;
            }

            var file = downloaded.Value;
            ConsoleUi.Success($"Скачано: {file.Bytes / 1024.0 / 1024.0:F1} МБ");
            ConsoleUi.Detail("Контрольная сумма", file.DigestVerified ? "совпала" : "в выпуске не указана");
            ConsoleUi.Detail("Файл", file.Path);

            // Файл остаётся на диске: это проверка, а не установка.
            ConsoleUi.Info();
            ConsoleUi.Info("Это проверка загрузки. Для установки нажмите кнопку в приложении.");
            return 0;
        }

        ConsoleUi.Info();

        if (info.IsNewer)
        {
            ConsoleUi.Success($"Доступна новая версия: {info.Version} (у вас {current})");
        }
        else
        {
            ConsoleUi.Success($"Установлена последняя версия: {current}");
        }

        if (info.Notes is { Length: > 0 } notes)
        {
            ConsoleUi.Info();
            ConsoleUi.Info("Что нового:");

            foreach (var line in notes.Split('\n').Take(6))
            {
                ConsoleUi.Info("  " + line.TrimEnd());
            }
        }

        return 0;
    }

    /// <summary>
    /// Проверка сравнения версий без обращения к сети.
    /// </summary>
    /// <remarks>
    /// Сравнение версий — единственное место проверки обновлений, где ошибка не видна
    /// сразу: неверный результат выглядит как «обновлений нет», и человек об этом
    /// даже не узнает. Поэтому случаи, ломающие наивную реализацию, проверяются здесь.
    /// </remarks>
    private static int RunSelfTest()
    {
        ConsoleUi.Header("Проверка сравнения версий");

        // newVersion, currentVersion, ожидается ли обновление.
        var cases = new (string Newer, string Current, bool Expected)[]
        {
            ("0.2.0", "0.1.0", true),
            ("0.1.0", "0.1.0", false),
            ("0.1.1", "0.1.0", true),

            // Сравнение по строкам здесь ошибается: «0.9» больше «0.10».
            ("0.10.0", "0.9.0", true),
            ("1.9.0", "1.22.0", false),

            // Разная длина: отсутствующие части считаются нулями.
            ("1.0", "1.0.0", false),
            ("1.0.1", "1.0", true),

            // Метка «v» не влияет на сравнение.
            ("v1.2.0", "1.1.0", true),

            // Предварительный выпуск старше окончательного.
            ("1.22.0", "v1.22.0-beta", true),
        };

        var failed = 0;

        foreach (var (newer, current, expected) in cases)
        {
            var actual = UpdateChecker.CompareVersions(newer, current) > 0;

            if (actual == expected)
            {
                ConsoleUi.Success($"{newer} против {current}: {(actual ? "обновление есть" : "обновления нет")}");
            }
            else
            {
                failed++;
                ConsoleUi.Error(
                    $"{newer} против {current}: ожидалось " +
                    $"{(expected ? "обновление есть" : "обновления нет")}, получено обратное");
            }
        }

        ConsoleUi.Info();

        if (failed == 0)
        {
            ConsoleUi.Success($"Все случаи пройдены: {cases.Length}");
            return 0;
        }

        ConsoleUi.Error($"Не пройдено случаев: {failed}");
        return 1;
    }

    /// <summary>Версия самого приложения.</summary>
    private static string CurrentVersion() =>        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "0.0.0";

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
