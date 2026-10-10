namespace WooCast.Capture;

/// <summary>Поиск исполняемого файла ffmpeg.</summary>
/// <remarks>
/// Порядок поиска подчинён одному правилу: сначала то, что указал пользователь, затем то,
/// что лежит рядом с приложением (портативная сборка), затем кэш установщика, затем PATH.
/// </remarks>
public static class FfmpegLocator
{
    /// <summary>Переменная окружения с путём к ffmpeg (файл или каталог).</summary>
    public const string EnvironmentVariable = "WOOCAST_FFMPEG";

    private static string? _cached;

    /// <summary>Найти ffmpeg. Возвращает <see langword="null"/>, если найти не удалось.</summary>
    public static string? Find()
    {
        if (_cached is not null && File.Exists(_cached))
        {
            return _cached;
        }

        foreach (var candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate))
            {
                _cached = Path.GetFullPath(candidate);
                return _cached;
            }
        }

        return null;
    }

    /// <summary>Найти ffmpeg или выбросить исключение с подсказкой, что делать.</summary>
    /// <exception cref="FileNotFoundException">ffmpeg не найден.</exception>
    public static string Require() =>
        Find() ?? throw new FileNotFoundException(
            "Не найден ffmpeg.exe. Запустите `pwsh -File tools/fetch-ffmpeg.ps1` " +
            $"или укажите путь в переменной окружения {EnvironmentVariable}.");

    private static IEnumerable<string> EnumerateCandidates()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return Directory.Exists(configured)
                ? Path.Combine(configured, "ffmpeg.exe")
                : configured;

            // Пользователь мог указать корень распакованной сборки.
            yield return Path.Combine(configured, "bin", "ffmpeg.exe");
        }

        // Рядом с приложением: портативная раскладка.
        var baseDirectory = AppContext.BaseDirectory;
        yield return Path.Combine(baseDirectory, "ffmpeg.exe");
        yield return Path.Combine(baseDirectory, "ffmpeg", "ffmpeg.exe");
        yield return Path.Combine(baseDirectory, "ffmpeg", "bin", "ffmpeg.exe");

        // Кэш установщика tools/fetch-ffmpeg.ps1.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return Path.Combine(localAppData, "WooCast", "vendor", "ffmpeg", "bin", "ffmpeg.exe");

            // Раскладка BtbN: ffmpeg-master-latest-win64-gpl/bin/ffmpeg.exe
            var vendorDirectory = Path.Combine(localAppData, "WooCast", "vendor");
            if (Directory.Exists(vendorDirectory))
            {
                foreach (var directory in SafeEnumerateDirectories(vendorDirectory, "ffmpeg*"))
                {
                    yield return Path.Combine(directory, "bin", "ffmpeg.exe");
                }
            }
        }

        // Локальная папка vendor/ в репозитории — для запуска из дерева исходников.
        foreach (var root in EnumerateSourceRoots())
        {
            yield return Path.Combine(root, "vendor", "ffmpeg", "bin", "ffmpeg.exe");
        }

        // PATH.
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = directory.Trim('"');
                if (trimmed.Length > 0)
                {
                    yield return Path.Combine(trimmed, "ffmpeg.exe");
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateSourceRoots()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        // Поднимаемся от bin/Debug/net10.0-windows до корня репозитория.
        for (var i = 0; i < 6 && directory?.Parent is not null; i++)
        {
            directory = directory.Parent;

            // .NET 10 создаёт решение в формате .slnx, старые SDK — в .sln.
            if (File.Exists(Path.Combine(directory.FullName, "WooCast.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, "WooCast.sln")) ||
                File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                yield return directory.FullName;
                yield break;
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path, string pattern)
    {
        try
        {
            return Directory.EnumerateDirectories(path, pattern);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
