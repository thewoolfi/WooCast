using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WooCast.Capture;
using WooCast.Desktop.Services;
using WooCast.Protocol;
using WooCast.Protocol.Net;

// Псевдонимы нужны из-за проекций Windows SDK: там есть пространство имён
// Windows.Graphics.Capture, и путь Capture.Windows стал неоднозначным.
using CaptureWindows = WooCast.Capture.Windows;
using CaptureDisplays = WooCast.Capture.Displays;

namespace WooCast.Desktop.ViewModels;

/// <summary>
/// Состояние главного окна: список приставок, настройки трансляции, телеметрия и журнал.
/// </summary>
/// <remarks>
/// Вся работа с сетью идёт в фоновых задачах; обновления интерфейса выполняются через
/// <see cref="Dispatcher.UIThread"/>. Логика протокола целиком лежит в WooCast.Protocol
/// и WooCast.Capture — здесь только представление.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DiscoveryListener _discovery;
    private readonly AppLog _log;

    /// <summary>Настройки с прошлого запуска. Обновляются при сохранении.</summary>
    private AppSettings _settings;

    /// <summary>Заголовок окна, который нужно выбрать, когда список окон загрузится.</summary>
    private string? _pendingWindowTitle;

    private SenderSession? _session;
    private CaptureCapabilities? _capabilities;
    private CancellationTokenSource? _sessionCts;
    private bool _disposed;

    public MainViewModel(AppLog log)
    {
        _log = log;

        // Слушатель обнаружения — единственный на приложение.
        _discovery = new DiscoveryListener(LocalDevice.Id, LocalDevice.Name);
        _discovery.DeviceDiscovered += (_, device) => OnUi(() => AddOrUpdateDevice(device, isNew: true));
        _discovery.DeviceUpdated += (_, device) => OnUi(() => AddOrUpdateDevice(device, isNew: false));
        _discovery.DeviceLost += (_, device) => OnUi(() => RemoveDevice(device.Id));

        _discovery.Start();
        _discovery.ProbeAsync().ContinueWith(
            task => OnUi(() => _log.Info($"Поиск устройств: разослан запрос на {Wcp.DiscoveryPort}/udp")),
            TaskScheduler.Default);

        // Проверка обновлений идёт в фоне при запуске: она не должна задерживать
        // появление окна и ничего не показывает, если сети нет.
        _ = CheckUpdateAsync();

        // Свойство Displays перекрывает имя статического класса, поэтому обращаемся явно.
        foreach (var display in CaptureDisplays.Enumerate())
        {
            Displays.Add(display);
        }

        SelectedDisplayIndex = Math.Max(0, Displays.ToList().FindIndex(d => d.IsPrimary));

        // Список разрешений зависит от источника, поэтому собирается после мониторов.
        UpdateResolutionOptions();

        // Настройки прошлого запуска применяются после того, как заполнены списки мониторов:
        // иначе сохранённый номер монитора некуда было бы приложить.
        _settings = SettingsStore.Load(out var settingsProblem);
        if (settingsProblem is not null)
        {
            _log.Warn(settingsProblem);
        }

        ApplySettings(_settings);

        _ = InitializeAsync();
    }

    // ------------------------------------------------------------------
    // Настройки между запусками
    // ------------------------------------------------------------------

    /// <summary>Применить настройки прошлого запуска к текущему состоянию интерфейса.</summary>
    private void ApplySettings(AppSettings settings)
    {
        SourceKindIndex = settings.SourceKindIndex is 0 or 1 ? settings.SourceKindIndex : 0;
        SelectedResolutionIndex = ClampIndex(settings.ResolutionIndex, _resolutionCandidates.Length, 0);
        SelectedFpsIndex = ClampIndex(settings.FpsIndex, FpsCandidates.Length, 1);
        SelectedQualityIndex = ClampIndex(settings.QualityIndex, QualityOptions.Count, 1);
        SelectedEncoderIndex = Math.Max(0, settings.EncoderIndex);

        AudioEnabled = settings.AudioEnabled;
        AudioBitrateKbps = Math.Clamp(settings.AudioBitrateKbps, 32, 320);
        AudioDelayMs = Math.Clamp(settings.AudioDelayMs, -500, 500);

        GamepadEnabled = settings.GamepadEnabled;
        GamepadKindIndex = Math.Clamp(settings.GamepadKindIndex, 0, GamepadKindOptions.Count - 1);

        // Проверяем драйвер сразу при запуске: иначе кнопка установки не появилась бы,
        // пока пользователь не тронет переключатель управления.
        RefreshGamepadDriver();

        if (settings.DisplayIndex >= 0 && settings.DisplayIndex < Displays.Count)
        {
            SelectedDisplayIndex = settings.DisplayIndex;
        }

        // Курсор: для окна он выключается сам, поэтому настройку применяем только к экрану.
        if (!IsWindowSource)
        {
            DrawCursor = settings.DrawCursor;
        }

        if (IsWindowSource && !string.IsNullOrEmpty(settings.WindowTitle))
        {
            // Список окон придёт асинхронно — выбор произойдёт в RefreshWindows.
            _pendingWindowTitle = settings.WindowTitle;
        }
    }

    /// <summary>Собрать текущее состояние интерфейса для сохранения.</summary>
    private AppSettings BuildSettings()
    {
        var windowTitle = SelectedWindowIndex >= 0 && SelectedWindowIndex < AvailableWindows.Count
            ? AvailableWindows[SelectedWindowIndex].Title
            : _settings.WindowTitle;

        return new AppSettings
        {
            LastDeviceId = SelectedDevice?.Id ?? _settings.LastDeviceId,
            SourceKindIndex = SourceKindIndex,
            DisplayIndex = SelectedDisplayIndex,
            WindowTitle = windowTitle,
            ResolutionIndex = SelectedResolutionIndex,
            FpsIndex = SelectedFpsIndex,
            QualityIndex = SelectedQualityIndex,
            EncoderIndex = SelectedEncoderIndex,
            DrawCursor = DrawCursor,
            AudioEnabled = AudioEnabled,
            AudioBitrateKbps = AudioBitrateKbps,
            AudioDelayMs = AudioDelayMs,
            GamepadEnabled = GamepadEnabled,
            GamepadKindIndex = GamepadKindIndex,
        };
    }

    /// <summary>Сохранить настройки. Вызывается при старте трансляции и при закрытии окна.</summary>
    public void SaveSettings()
    {
        var settings = BuildSettings();
        _settings = settings;

        if (!SettingsStore.Save(settings, out var problem) && problem is not null)
        {
            _log.Warn(problem);
        }
    }

    private static int ClampIndex(int value, int count, int fallback) =>
        count <= 0 ? fallback : Math.Clamp(value, 0, count - 1);

    // ------------------------------------------------------------------
    // Устройства
    // ------------------------------------------------------------------

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    [ObservableProperty]
    private DeviceViewModel? _selectedDevice;

    [ObservableProperty]
    private string _manualAddress = string.Empty;

    // ------------------------------------------------------------------
    // Настройки трансляции
    // ------------------------------------------------------------------

    /// <summary>
    /// Разрешения потока. Первым пунктом — родной размер источника: он не требует
    /// масштабирования, а значит даёт самую чёткую картинку и меньше нагрузки.
    /// </summary>
    public ObservableCollection<string> ResolutionOptions { get; } = [];

    /// <summary>Разрешения в том же порядке, что и <see cref="ResolutionOptions"/>.</summary>
    private (int Width, int Height)[] _resolutionCandidates = [(1920, 1080)];

    /// <summary>
    /// Пересобрать список разрешений под текущий источник.
    /// </summary>
    /// <remarks>
    /// Родной размер источника идёт первым и выбирается по умолчанию: растр «как есть»
    /// не масштабируется, поэтому картинка остаётся чёткой, а битрейт не тратится
    /// на пиксели, которых в источнике нет. Остальные варианты оставлены на случай,
    /// когда нужен конкретный размер, и помечены, если они крупнее источника.
    /// </remarks>
    private void UpdateResolutionOptions()
    {
        var (sourceWidth, sourceHeight) = SourceNativeSize;

        // Запоминаем выбранный размер, а не номер строки: список пересобирается,
        // и номер указывает уже на другой пункт — из-за этого выбор пользователя
        // «сбрасывался» на «как в источнике» после нажатия «Транслировать».
        var previous = SelectedResolutionIndex >= 0 && SelectedResolutionIndex < _resolutionCandidates.Length
            ? _resolutionCandidates[SelectedResolutionIndex]
            : ((int Width, int Height)?)null;

        var wasNative = SelectedResolutionIndex <= 0;

        var options = new List<string>();
        var candidates = new List<(int Width, int Height)>();

        var nativeLabel = sourceWidth > 0 && sourceHeight > 0
            ? $"Как в источнике ({sourceWidth} × {sourceHeight})"
            : "Как в источнике";

        options.Add(nativeLabel);
        candidates.Add((MakeEven(sourceWidth > 0 ? sourceWidth : 1920), MakeEven(sourceHeight > 0 ? sourceHeight : 1080)));

        foreach (var (width, height) in StandardResolutions)
        {
            // Совпадающий с источником вариант не дублируем.
            if (width == candidates[0].Width && height == candidates[0].Height)
            {
                continue;
            }

            var enlarged = sourceWidth > 0 && (width > sourceWidth || height > sourceHeight);
            var note = enlarged ? " — источник мельче, деталей не прибавится" : string.Empty;

            options.Add($"{width} × {height} ({DescribeResolution(width, height)}){note}");
            candidates.Add((width, height));
        }

        _resolutionCandidates = [.. candidates];

        ResolutionOptions.Clear();
        foreach (var option in options)
        {
            ResolutionOptions.Add(option);
        }

        // Восстанавливаем выбор по размеру, а не по номеру строки: список пересобран,
        // и тот же номер указывает уже на другой пункт.
        if (wasNative)
        {
            SelectedResolutionIndex = 0;
        }
        else if (previous is { } wanted)
        {
            var restored = -1;
            for (var i = 0; i < _resolutionCandidates.Length; i++)
            {
                if (_resolutionCandidates[i] == wanted)
                {
                    restored = i;
                    break;
                }
            }

            SelectedResolutionIndex = restored >= 0
                ? restored
                : Math.Clamp(SelectedResolutionIndex, 0, Math.Max(0, ResolutionOptions.Count - 1));
        }
        else
        {
            SelectedResolutionIndex = Math.Clamp(SelectedResolutionIndex, 0, Math.Max(0, ResolutionOptions.Count - 1));
        }

        OnPropertyChanged(nameof(BitrateHint));
    }

    /// <summary>Стандартные разрешения помимо родного размера источника.</summary>
    private static readonly (int Width, int Height)[] StandardResolutions =
    [
        (3840, 2160),
        (2560, 1440),
        (1920, 1080),
        (1600, 900),
        (1280, 720),
    ];

    private static string DescribeResolution(int width, int height) => (width, height) switch
    {
        (3840, 2160) => "4K",
        (2560, 1440) => "2K",
        (1920, 1080) => "Full HD",
        (1600, 900) => "HD+",
        (1280, 720) => "HD",
        _ => $"{width}×{height}",
    };

    private static int MakeEven(int value) => value % 2 == 0 ? value : value - 1;

    /// <summary>Размер источника как есть: выбранного окна или монитора.</summary>
    private (int Width, int Height) SourceNativeSize
    {
        get
        {
            if (IsWindowSource && SelectedWindowIndex >= 0 && SelectedWindowIndex < AvailableWindows.Count)
            {
                var window = AvailableWindows[SelectedWindowIndex];
                return (window.Width, window.Height);
            }

            if (SelectedDisplayIndex >= 0 && SelectedDisplayIndex < Displays.Count)
            {
                var display = Displays[SelectedDisplayIndex];
                return (display.Width, display.Height);
            }

            return (0, 0);
        }
    }

    private static readonly int[] FpsCandidates = [30, 60, 120];

    public IReadOnlyList<string> FpsOptions { get; } = ["30 к/с", "60 к/с", "120 к/с"];

    /// <summary>Что транслируем: весь экран или отдельное окно.</summary>
    public IReadOnlyList<string> SourceOptions { get; } = ["Весь экран", "Окно приложения"];

    public IReadOnlyList<string> QualityOptions { get; } =
    [
        "Максимум (проводная сеть)",
        "Высокое",
        "Сбалансированное",
        "Экономное",
    ];

    [ObservableProperty]
    private int _selectedResolutionIndex;

    /// <summary>
    /// Частота кадров по умолчанию. 30, а не 60: телевизоры и приставки справляются
    /// с ней заметно надёжнее, а для кино и интерфейса разницы не видно. Поднять
    /// до 60 можно в любой момент, и подстройка сама вернётся к выбранному значению,
    /// если приставка держится.
    /// </summary>
    [ObservableProperty]
    private int _selectedFpsIndex;

    /// <summary>
    /// Качество по умолчанию — «Сбалансированное». Измерения на реальной приставке
    /// (Xiaomi MiTV, Wi-Fi) показали: 1080p60 при 22 Мбит/с она не тянет — задержка
    /// скачет до 300 мс и кадры теряются, а те же 1080p30 при 6 Мбит/с идут идеально.
    /// Кто хочет больше — поднимет в списке, и подстройка вернётся к выбранному,
    /// если приставка выдержит.
    /// </summary>
    [ObservableProperty]
    private int _selectedQualityIndex = 2;

    [ObservableProperty]
    private int _selectedEncoderIndex;

    [ObservableProperty]
    private int _selectedDisplayIndex;

    /// <summary>Смена монитора меняет и родной размер источника.</summary>
    partial void OnSelectedDisplayIndexChanged(int value) => UpdateResolutionOptions();

    [ObservableProperty]
    private bool _drawCursor = true;

    /// <summary>Передавать ли системный звук вместе с изображением.</summary>
    [ObservableProperty]
    private bool _audioEnabled = true;

    /// <summary>Принимать ли управление с контроллера, подключённого к приёмнику.</summary>
    [ObservableProperty]
    private bool _gamepadEnabled;

    /// <summary>Вид виртуального геймпада: 0 — Xbox 360, 1 — DualShock 4.</summary>
    [ObservableProperty]
    private int _gamepadKindIndex;

    /// <summary>Варианты вида геймпада для выпадающего списка.</summary>
    public IReadOnlyList<string> GamepadKindOptions { get; } =
    [
        "Xbox 360 (понимают почти все игры)",
        "DualShock 4 (родные подсказки PlayStation)",
    ];

    /// <summary>Пояснение под настройкой управления.</summary>
    public string GamepadHint => GamepadEnabled
        ? GamepadKindIndex == 1
            ? "Игра увидит геймпад DualShock 4. Вибрация и гироскоп не передаются."
            : "Игра увидит геймпад Xbox 360: стики и курки работают полностью."
        : "Контроллер подключается к приставке или телефону, а управление уходит на компьютер.";

    /// <summary>Установлен ли драйвер виртуального геймпада.</summary>
    [ObservableProperty]
    private bool _gamepadDriverInstalled;

    /// <summary>Состояние драйвера: текст для интерфейса.</summary>
    public string GamepadDriverText => GamepadDriver.Describe();

    /// <summary>
    /// Доступна ли кнопка установки драйвера.
    /// </summary>
    /// <remarks>
    /// Кнопка показывается всегда, но при установленном драйвере неактивна: так её
    /// видно и понятно, что она есть, а нажимать нечего.
    /// </remarks>
    public bool CanInstallGamepadDriver => !GamepadDriverInstalled;

    partial void OnGamepadEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(GamepadHint));
        RefreshGamepadDriver();
    }

    partial void OnGamepadKindIndexChanged(int value) => OnPropertyChanged(nameof(GamepadHint));

    partial void OnGamepadDriverInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(GamepadDriverText));
        OnPropertyChanged(nameof(CanInstallGamepadDriver));
    }

    /// <summary>Перечитать состояние драйвера геймпада.</summary>
    private void RefreshGamepadDriver() => GamepadDriverInstalled = GamepadDriver.IsInstalled();

    // ------------------------------------------------------------------
    // Обновления
    // ------------------------------------------------------------------

    /// <summary>Доступна ли новая версия приложения.</summary>
    [ObservableProperty]
    private bool _updateAvailable;

    /// <summary>Версия приложения без лишних чисел.</summary>
    public string VersionText =>
        typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Строка под названием приложения: версия и, если есть, новость об обновлении.
    /// </summary>
    /// <remarks>
    /// Версия показывается в шапке, а не только в настройках: в настройках её видно
    /// лишь после прокрутки, а знать свою версию нужно сразу — по ней понятно,
    /// стоит ли обновляться.
    /// </remarks>
    public string HeaderLine => UpdateAvailable
        ? $"трансляция экрана на Android TV · версия {VersionText} · доступна новая"
        : $"трансляция экрана на Android TV · версия {VersionText}";

    /// <summary>Показать доступное обновление в шапке другим цветом.</summary>
    public bool HeaderHasUpdate => UpdateAvailable;

    partial void OnUpdateAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(HeaderLine));
        OnPropertyChanged(nameof(HeaderHasUpdate));
    }

    /// <summary>Строка о состоянии обновления.</summary>
    [ObservableProperty]
    private string _updateText = string.Empty;

    /// <summary>Ссылка на страницу выпуска с новой версией.</summary>
    private string? _updatePageUrl;

    /// <summary>Прямая ссылка на сборку обновления.</summary>
    private string? _updateDownloadUrl;

    /// <summary>Контрольная сумма сборки обновления из выпуска.</summary>
    private string? _updateDigest;

    /// <summary>Идёт скачивание обновления.</summary>
    [ObservableProperty]
    private bool _isUpdating;

    /// <summary>
    /// Проверить обновления.
    /// </summary>
    /// <remarks>
    /// Проверка идёт молча: если сети нет или выпусков ещё нет, ничего не показывается.
    /// Тревожить пользователя из-за недоступного GitHub нельзя — это не его проблема.
    /// </remarks>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        var current = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var update = await UpdateChecker.CheckAsync(current).ConfigureAwait(true);

        if (update is null)
        {
            UpdateText = "Обновления: проверить не удалось";
            UpdateAvailable = false;
            return;
        }

        var info = update.Value;
        _updatePageUrl = info.PageUrl;
        _updateDownloadUrl = info.DownloadUrl;
        _updateDigest = info.Digest;

        if (info.IsNewer)
        {
            UpdateAvailable = true;
            UpdateText = $"Доступна версия {info.Version} (у вас {current})";
            _log.Info($"Доступна новая версия WooCast: {info.Version}");
        }
        else
        {
            UpdateAvailable = false;
            UpdateText = $"Установлена последняя версия ({current})";
        }
    }

    /// <summary>
    /// Ссылка для поддержки проекта.
    /// </summary>
    /// <remarks>
    /// Та же ссылка, что и в приложении для приставки: поддержка не привязана
    /// к тому, с какой стороны смотрят трансляцию.
    /// </remarks>
    public const string SupportUrl = "https://boosty.to/andrewwoolfi";

    /// <summary>Открыть страницу поддержки проекта в браузере.</summary>
    [RelayCommand]
    private void OpenSupportPage()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(SupportUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"Не удалось открыть страницу поддержки: {ex.Message}");
        }
    }

    /// <summary>Открыть страницу выпуска с новой версией.</summary>
    [RelayCommand]
    private void OpenUpdatePage()
    {
        var url = _updatePageUrl;

        if (string.IsNullOrWhiteSpace(url))
        {
            // Страницы нет — открываем страницу выпусков репозитория.
            url = $"https://github.com/{UpdateChecker.Repository}/releases";
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"Не удалось открыть страницу загрузки: {ex.Message}");
        }
    }

    /// <summary>
    /// Скачать обновление и установить его.
    /// </summary>
    /// <remarks>
    /// Порядок такой: скачать во временную папку, проверить контрольную сумму,
    /// передать замену помощнику и завершить работу. Заменить работающий файл Windows
    /// не даёт, поэтому помощник дожидается выхода приложения, подменяет файл
    /// и запускает новую версию. Прежняя остаётся рядом для отката.
    /// </remarks>
    [RelayCommand]
    private async Task DownloadAndUpdateAsync()
    {
        if (_updateDownloadUrl is null)
        {
            UpdateText = "В выпуске нет сборки для Windows";
            return;
        }

        if (IsStreaming)
        {
            UpdateText = "Сначала остановите трансляцию";
            return;
        }

        try
        {
            IsUpdating = true;
            UpdateText = "Скачивание…";

            var progress = new Progress<double>(value =>
                UpdateText = $"Скачивание: {value * 100:F0} %");

            var downloaded = await UpdateInstaller
                .DownloadAsync(_updateDownloadUrl, _updateDigest, progress)
                .ConfigureAwait(true);

            if (downloaded is null)
            {
                UpdateText = "Скачать не удалось или файл повреждён";
                _log.Warn("Обновление: скачивание не удалось или контрольная сумма не совпала");
                return;
            }

            var file = downloaded.Value;
            _log.Info(
                $"Обновление скачано: {file.Bytes / 1024.0 / 1024.0:F1} МБ, " +
                $"сумма {(file.DigestVerified ? "совпала" : "не указана в выпуске")}");

            var helper = UpdateInstaller.TryApplyAndRestart(file.Path, out var error);

            if (helper is null)
            {
                UpdateText = $"Не удалось установить: {error}";
                _log.Warn($"Обновление: {error}");
                return;
            }

            UpdateText = "Установка: приложение перезапустится";
            _log.Info("Обновление применяется, приложение будет перезапущено");

            SaveSettings();

            // Завершаем работу — иначе помощник не сможет заменить файл.
            Environment.Exit(0);
        }
        finally
        {
            IsUpdating = false;
        }
    }

    /// <summary>
    /// Установить драйвер виртуального геймпада.
    /// </summary>
    /// <remarks>
    /// Установка идёт отдельным окном с правами администратора, поэтому приложение
    /// не ждёт её завершения: после закрытия установщика состояние перечитывается.
    /// </remarks>
    [RelayCommand]
    private async Task InstallGamepadDriverAsync()
    {
        if (GamepadDriverInstalled)
        {
            StatusText = "Драйвер геймпада уже установлен";
            return;
        }

        var process = GamepadDriver.TryInstall(out var error, out var cancelled);

        if (process is null)
        {
            _log.Warn($"Установка драйвера геймпада: {error}");

            if (cancelled)
            {
                StatusText = "Установка драйвера отменена";
                return;
            }

            // Установщика рядом нет — предлагаем скачать его со страницы выпуска.
            StatusText = "Установщик драйвера не найден";
            AdaptationNote = "Открываю страницу загрузки драйвера ViGEmBus";
            GamepadDriver.OpenDownloadPage();
            return;
        }

        _log.Info("Запущена установка драйвера геймпада");
        StatusText = "Установка драйвера запущена";
        AdaptationNote = "Завершите установку драйвера и вернитесь к трансляции";

        await process.WaitForExitAsync().ConfigureAwait(true);

        RefreshGamepadDriver();

        AdaptationNote = GamepadDriverInstalled
            ? "Драйвер геймпада установлен"
            : "Драйвер устанавливается: если система попросит перезагрузку, перезагрузите её";

        StatusText = GamepadDriverInstalled ? "Драйвер готов" : "Установка завершена";
    }

    /// <summary>Битрейт звука в килобитах в секунду.</summary>
    [ObservableProperty]
    private int _audioBitrateKbps = 160;

    /// <summary>
    /// Ручная поправка синхронизации звука: положительная — звук позже картинки.
    /// </summary>
    [ObservableProperty]
    private int _audioDelayMs;

    /// <summary>
    /// Запас сглаживания на приставке, миллисекунды: выбор между плавностью и задержкой.
    /// По умолчанию ноль — картинка должна быть ровной сама по себе, а не за счёт
    /// добавленной задержки. Запас нужен лишь как страховка от неровностей сети.
    /// </summary>
    [ObservableProperty]
    private int _bufferMs;

    /// <summary>Подпись к ползунку запаса сглаживания.</summary>
    public string BufferText => BufferMs switch
    {
        0 => "без запаса — минимальная задержка",
        <= 150 => $"{BufferMs} мс — небольшая страховка от неровностей сети",
        <= 300 => $"{BufferMs} мс — сглаживает всплески ключевых кадров",
        _ => $"{BufferMs} мс — максимальная плавность, задержка ощутима",
    };

    partial void OnBufferMsChanged(int value) => OnPropertyChanged(nameof(BufferText));

    /// <summary>Подпись к ползунку задержки звука.</summary>
    public string AudioDelayText => AudioDelayMs switch
    {
        0 => "без поправки",
        > 0 => $"+{AudioDelayMs} мс — звук позже",
        _ => $"{AudioDelayMs} мс — звук раньше",
    };

    partial void OnAudioDelayMsChanged(int value) => OnPropertyChanged(nameof(AudioDelayText));

    public ObservableCollection<string> EncoderOptions { get; } = ["Авто (рекомендуется)"];

    public ObservableCollection<DisplayInfo> Displays { get; } = [];

    /// <summary>Окна, доступные для трансляции. Обновляется по кнопке.</summary>
    public ObservableCollection<WindowInfo> AvailableWindows { get; } = [];

    /// <summary>Источник: 0 — весь экран, 1 — отдельное окно.</summary>
    [ObservableProperty]
    private int _sourceKindIndex;

    [ObservableProperty]
    private int _selectedWindowIndex = -1;

    partial void OnSourceKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsWindowSource));
        OnPropertyChanged(nameof(SourceHint));

        if (IsWindowSource)
        {
            // Курсор принадлежит рабочему столу, а не окну: в кадре окна он выглядит лишним.
            DrawCursor = false;

            if (AvailableWindows.Count == 0)
            {
                RefreshWindows();
            }
        }
        else
        {
            DrawCursor = true;
        }

        // У окна и у экрана разные родные размеры — список разрешений пересобирается.
        UpdateResolutionOptions();
    }

    partial void OnSelectedWindowIndexChanged(int value)
    {
        if (value >= 0)
        {
            // Размер окна — это и есть родное разрешение для него.
            UpdateResolutionOptions();
            SuggestForSelectedWindow();
        }

        OnPropertyChanged(nameof(SourceHint));
    }

    /// <summary>Транслируется отдельное окно, а не весь экран.</summary>
    public bool IsWindowSource => SourceKindIndex == 1;

    /// <summary>Пояснение под выбором источника.</summary>
    public string SourceHint
    {
        get
        {
            if (!IsWindowSource)
            {
                return "Захват всего рабочего стола выбранного монитора.";
            }

            if (SelectedWindowIndex < 0 || SelectedWindowIndex >= AvailableWindows.Count)
            {
                return "Выберите окно. Список можно обновить, если окно открыли позже.";
            }

            var window = AvailableWindows[SelectedWindowIndex];

            // Окно во весь экран даёт кадр, неотличимый от трансляции всего экрана:
            // пользователь думает, что выбор окна не сработал. Предупреждаем заранее.
            var (displayIndex, _, _, _, _) = CaptureWindows.MapToDisplay(window, Displays);
            var display = Displays.FirstOrDefault(d => d.Index == displayIndex, Displays[0]);

            if (display.Width > 0 && display.Height > 0)
            {
                var coversWidth = (double)window.Width / display.Width;
                var coversHeight = (double)window.Height / display.Height;

                if (coversWidth >= 0.9 && coversHeight >= 0.9)
                {
                    return $"Окно занимает почти весь экран ({window.Width}×{window.Height} " +
                           $"из {display.Width}×{display.Height}). Захват идёт по содержимому окна, " +
                           "поэтому картинка верна даже у перекрытого окна. Для полноэкранных " +
                           "приложений проще выбрать «Весь экран» — результат будет тот же.";
                }
            }

            return $"Область {window.Width}×{window.Height}. " +
                   "Захватывается содержимое окна целиком: перекрытие и уход в фон картинку не портят.";
        }
    }

    /// <summary>Итоговый битрейт выбранных параметров — показывается под настройками.</summary>
    public string BitrateHint
    {
        get
        {
            var (width, height) = SelectedResolution;
            var options = new CaptureOptions
            {
                Width = width,
                Height = height,
                Fps = SelectedFps,
                Quality = SelectedQuality,
            };

            return $"{options.DescribeQuality()} · {options.EffectiveBitrateKbps() / 1000.0:F1} Мбит/с";
        }
    }

    private (int Width, int Height) SelectedResolution
    {
        get
        {
            if (_resolutionCandidates.Length == 0)
            {
                return (1920, 1080);
            }

            return _resolutionCandidates[Math.Clamp(SelectedResolutionIndex, 0, _resolutionCandidates.Length - 1)];
        }
    }

    private int SelectedFps => SelectedFpsIndex switch
    {
        0 => 30,
        2 => 120,
        _ => 60,
    };

    private QualityPreset SelectedQuality => SelectedQualityIndex switch
    {
        0 => QualityPreset.Maximum,
        2 => QualityPreset.Balanced,
        3 => QualityPreset.Economy,
        _ => QualityPreset.High,
    };

    partial void OnSelectedResolutionIndexChanged(int value) => OnPropertyChanged(nameof(BitrateHint));

    partial void OnSelectedFpsIndexChanged(int value) => OnPropertyChanged(nameof(BitrateHint));

    partial void OnSelectedQualityIndexChanged(int value) => OnPropertyChanged(nameof(BitrateHint));

    // ------------------------------------------------------------------
    // Состояние
    // ------------------------------------------------------------------

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Поиск приставок в сети…";

    [ObservableProperty]
    private string _metricsText = string.Empty;

    [ObservableProperty]
    private string _encoderText = "определяется…";

    [ObservableProperty]
    private bool _hasDevices;

    [ObservableProperty]
    private string? _lastError;

    /// <summary>Пояснение от адаптивной подстройки: что и почему изменено.</summary>
    [ObservableProperty]
    private string? _adaptationNote;

    /// <summary>Пределы выбранной приставки — показываются под настройками.</summary>
    [ObservableProperty]
    private string? _capabilityNote;

    /// <summary>Параметры пришлось подрезать под возможности приставки.</summary>
    [ObservableProperty]
    private bool _capabilityClamped;

    public AppLog Log => _log;

    /// <summary>Строка с найденными приставками — используется в подсказке пустого состояния.</summary>
    public string DiscoveryHint =>
        $"Порт обнаружения {Wcp.DiscoveryPort}/udp. Если приставка не находится, введите её IP вручную.";

    // ------------------------------------------------------------------
    // Команды
    // ------------------------------------------------------------------

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            await _discovery.ProbeAsync();
            _log.Info("Разослан запрос обнаружения");
        }
        catch (Exception ex)
        {
            _log.Warn($"Не удалось разослать запрос: {ex.Message}");
        }
    }

    /// <summary>
    /// Занимает ли окно почти весь свой монитор (90 % и более по обеим сторонам).
    /// </summary>
    /// <remarks>
    /// Раньше такой выбор считался неотличимым от трансляции экрана, и при возврате
    /// настроек окно заменялось на «Весь экран». Теперь это не так: содержимое окна
    /// захватывается системой целиком, и полноэкранное окно транслируется корректно.
    /// Проверка осталась только для подсказки в интерфейсе.
    /// </remarks>
    private bool CoversWholeDisplay(WindowInfo window)
    {
        if (Displays.Count == 0 || window.Width <= 0 || window.Height <= 0)
        {
            return false;
        }

        var (displayIndex, _, _, _, _) = CaptureWindows.MapToDisplay(window, Displays);
        var display = Displays.FirstOrDefault(d => d.Index == displayIndex, Displays[0]);

        if (display.Width <= 0 || display.Height <= 0)
        {
            return false;
        }

        return (double)window.Width / display.Width >= 0.9
               && (double)window.Height / display.Height >= 0.9;
    }

    /// <summary>Перечитать список окон. Окна, открытые после запуска, появятся здесь.</summary>
    [RelayCommand]
    private void RefreshWindows()
    {
        var previousHandle = SelectedWindowIndex >= 0 && SelectedWindowIndex < AvailableWindows.Count
            ? AvailableWindows[SelectedWindowIndex].Handle
            : 0;

        AvailableWindows.Clear();

        // Свои собственные окна в список не попадают: транслировать себя бессмысленно.
        foreach (var window in CaptureWindows.Enumerate(Environment.ProcessId))
        {
            AvailableWindows.Add(window);
        }

        // Окно само не выбирается. Раньше подставлялось первое из списка, а им обычно
        // оказывается развёрнутый браузер или игра — то есть почти весь экран. Человек
        // нажимал «Транслировать» и получал рабочий стол, решая, что выбор окна сломан.
        var restored = -1;
        if (previousHandle != 0)
        {
            for (var i = 0; i < AvailableWindows.Count; i++)
            {
                if (AvailableWindows[i].Handle == previousHandle)
                {
                    restored = i;
                    break;
                }
            }
        }
        else if (!string.IsNullOrEmpty(_pendingWindowTitle))
        {
            // Восстанавливаем окно из прошлого запуска по заголовку: дескриптор
            // между запусками не сохраняется, а заголовок узнаваем.
            for (var i = 0; i < AvailableWindows.Count; i++)
            {
                if (string.Equals(AvailableWindows[i].Title, _pendingWindowTitle, StringComparison.OrdinalIgnoreCase))
                {
                    // Окно восстанавливается как есть, даже если занимает весь экран:
                    // содержимое окна захватывается системой целиком, и такой выбор
                    // больше не даёт «сюрпризов» при перекрытии.
                    restored = i;
                    _log.Info($"Восстановлено окно из прошлого запуска: {_pendingWindowTitle}");

                    if (CoversWholeDisplay(AvailableWindows[i]))
                    {
                        _log.Info("Окно занимает весь экран — картинка будет как у всего экрана");
                    }

                    break;
                }
            }

            _pendingWindowTitle = null;
        }

        SelectedWindowIndex = restored;

        // Список окон изменился — родной размер источника тоже мог измениться.
        UpdateResolutionOptions();

        _log.Info($"Доступно окон: {AvailableWindows.Count}");

        if (AvailableWindows.Count == 0)
        {
            _log.Warn("Окна не найдены — возможно, все свёрнуты");
        }
    }

    /// <summary>
    /// Разрешение под выбранное окно больше не подставляется само.
    /// </summary>
    /// <remarks>
    /// Раньше здесь принудительно выбирался родной размер окна, и это выглядело как сброс
    /// настроек: человек ставил 1280×720, затем выбирал окно — и разрешение возвращалось
    /// к родному размеру. Родной размер и так стоит по умолчанию, а навязывать его поверх
    /// явного выбора нельзя: выбор пользователя важнее догадки приложения.
    /// </remarks>
    private void SuggestForSelectedWindow()
    {
        // Оставлено намеренно пустым.
    }

    /// <summary>
    /// Привести параметры потока в соответствие с возможностями приставки.
    /// </summary>
    /// <remarks>
    /// Приёмник сообщает реальные ограничения своего декодера в <c>announce.caps</c>.
    /// Отправить ему поток, который он не тянет, — верный способ получить чёрный экран,
    /// поэтому параметры подрезаются заранее, а пользователь получает пояснение.
    /// </remarks>
    private void ApplyDeviceCapabilities(DeviceViewModel device)
    {
        var clamped = false;

        if (device.MaxWidth > 0 && device.MaxHeight > 0)
        {
            var current = _resolutionCandidates[Math.Clamp(SelectedResolutionIndex, 0, _resolutionCandidates.Length - 1)];

            if (current.Width > device.MaxWidth || current.Height > device.MaxHeight)
            {
                var bestIndex = -1;
                long bestArea = long.MaxValue;

                for (var i = 0; i < _resolutionCandidates.Length; i++)
                {
                    var (width, height) = _resolutionCandidates[i];
                    if (width > device.MaxWidth || height > device.MaxHeight)
                    {
                        continue;
                    }

                    var area = (long)width * height;
                    if (area < bestArea)
                    {
                        bestArea = area;
                        bestIndex = i;
                    }
                }

                if (bestIndex >= 0)
                {
                    _log.Warn(
                        $"Приставка поддерживает до {device.MaxWidth}×{device.MaxHeight}: " +
                        $"разрешение изменено на {ResolutionOptions[bestIndex]}");
                    SelectedResolutionIndex = bestIndex;
                    clamped = true;
                }
                else
                {
                    _log.Error(
                        $"Приставка не поддерживает ни одного из доступных разрешений " +
                        $"(её предел {device.MaxWidth}×{device.MaxHeight})");
                }
            }
        }

        if (device.MaxFps > 0)
        {
            var current = FpsCandidates[Math.Clamp(SelectedFpsIndex, 0, FpsCandidates.Length - 1)];

            if (current > device.MaxFps)
            {
                var best = 0;
                for (var i = 0; i < FpsCandidates.Length; i++)
                {
                    if (FpsCandidates[i] <= device.MaxFps)
                    {
                        best = i;
                    }
                }

                _log.Warn(
                    $"Приставка декодирует до {device.MaxFps} к/с: " +
                    $"частота изменена на {FpsOptions[best]}");
                SelectedFpsIndex = best;
                clamped = true;
            }
        }

        // Предупреждение выносится в интерфейс: молча изменённые настройки выглядят как сбой.
        var limits = device.CapsText;
        CapabilityClamped = clamped;

        CapabilityNote = clamped
            ? $"Параметры подрезаны под приставку ({limits})"
            : $"Приставка: {limits}";

        if (device.MaxWidth <= 0 && device.MaxHeight <= 0 && device.MaxFps <= 0)
        {
            CapabilityClamped = false;
            CapabilityNote = null;
        }
    }

    partial void OnSelectedDeviceChanged(DeviceViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        ApplyDeviceCapabilities(value);
    }

    [RelayCommand]
    private async Task ConnectManualAsync()
    {
        if (IsStreaming)
        {
            return;
        }

        if (!NetworkUtil.TryParseEndpoint(ManualAddress, Wcp.ControlPort, out var address, out var port))
        {
            LastError = "Введите адрес вида 192.168.1.50 или 192.168.1.50:47811";
            _log.Warn(LastError);
            return;
        }

        var known = Devices.FirstOrDefault(d => Equals(d.Device.Address, address));
        if (known is not null)
        {
            SelectedDevice = known;
            _log.Info($"Адрес {address} уже найден в списке устройств");
            return;
        }

        _log.Info($"Ручное подключение к {address}:{port}");
        await StartStreamingAsync(address, port);
    }

    [RelayCommand]
    private async Task ToggleStreamingAsync()
    {
        if (IsStreaming)
        {
            await StopStreamingAsync();
            return;
        }

        var device = SelectedDevice;
        if (device is null)
        {
            LastError = "Выберите приставку в списке или введите её адрес";
            return;
        }

        if (!device.IsOnline)
        {
            LastError = "Приставка не отвечает";
            return;
        }

        await StartStreamingAsync(device.Device.Address, device.Device.ControlPort);
    }

    // ------------------------------------------------------------------
    // Трансляция
    // ------------------------------------------------------------------

    private async Task StartStreamingAsync(IPAddress address, int controlPort)
    {
        if (IsStreaming || IsBusy)
        {
            return;
        }

        IsBusy = true;
        LastError = null;
        AdaptationNote = null;
        StatusText = "Подготовка…";

        try
        {
            _capabilities ??= await DetectCapabilitiesAsync();
            var capabilities = _capabilities;

            if (capabilities.WorkingEncoders.Count == 0)
            {
                LastError = "Не найдено ни одного рабочего кодировщика H.264. Проверьте ffmpeg.";
                _log.Error(LastError);
                StatusText = "Ошибка";
                return;
            }

            var (width, height) = SelectedResolution;

            // Источник: весь экран или отдельное окно.
            var sourceKind = CaptureSourceKind.Display;
            WindowTarget? windowTarget = null;

            if (IsWindowSource)
            {
                if (SelectedWindowIndex < 0 || SelectedWindowIndex >= AvailableWindows.Count)
                {
                    LastError = "Выберите окно для трансляции";
                    StatusText = "Ошибка";
                    _log.Warn(LastError);
                    return;
                }

                var info = AvailableWindows[SelectedWindowIndex];
                if (!info.IsAlive)
                {
                    LastError = "Выбранное окно закрыто или свёрнуто — список обновлён";
                    StatusText = "Ошибка";
                    _log.Warn(LastError);
                    RefreshWindows();
                    return;
                }

                // Окно во весь экран транслировать можно: захват идёт по снимку экрана,
                // и пока окно видно, картинка верная. Приложение лишь предупреждает
                // об этом в пояснении под списком. Молча отменять выбор пользователя
                // нельзя — он выглядит как сброс настроек.
                var (displayIndex, _, _, _, _) = CaptureWindows.MapToDisplay(info, Displays);
                var display = Displays.FirstOrDefault(d => d.Index == displayIndex, Displays[0]);

                windowTarget = new WindowTarget
                {
                    Handle = info.Handle,
                    Title = info.Title,
                    ProcessName = info.ProcessName,
                    X = info.X,
                    Y = info.Y,
                    Width = info.Width,
                    Height = info.Height,
                    DisplayIndex = display.Index,
                    DisplayOriginX = display.X,
                    DisplayOriginY = display.Y,
                };

                sourceKind = CaptureSourceKind.Window;
            }

            var options = new CaptureOptions
            {
                SourceKind = sourceKind,
                Window = windowTarget,
                WindowTitle = windowTarget?.Title,
                DisplayIndex = windowTarget?.DisplayIndex ?? SelectedDisplayIndex,
                Width = width,
                Height = height,
                Fps = SelectedFps,
                Quality = SelectedQuality,
                Encoder = SelectedEncoderIndex > 0 && SelectedEncoderIndex - 1 < capabilities.WorkingEncoders.Count
                    ? capabilities.WorkingEncoders[SelectedEncoderIndex - 1]
                    : EncoderKind.Auto,
                DrawCursor = DrawCursor,
                BufferMs = BufferMs,
                GamepadEnabled = GamepadEnabled,
                Gamepad = GamepadKindIndex == 1 ? GamepadKind.DualShock4 : GamepadKind.Xbox360,
                Audio = new AudioOptions
                {
                    Enabled = AudioEnabled,
                    BitrateKbps = AudioBitrateKbps,
                    DelayMs = AudioDelayMs,
                },
                FfmpegPath = capabilities.FfmpegPath,
            };

            // Настройки запоминаем в момент запуска: именно тогда известно всё —
            // и приставка, и источник, и параметры потока.
            SaveSettings();

            _sessionCts = new CancellationTokenSource();
            StatusText = $"Подключение к {address}…";

            var session = await SenderSession.StartAsync(
                address,
                controlPort,
                options,
                capabilities,
                _sessionCts.Token);

            session.StatusChanged += (_, status) => OnUi(() => ApplyStatus(status));
            session.Log += (_, line) => OnUi(() => _log.Add("info", line));

            _session = session;
            IsStreaming = true;
            StatusText = "В эфире";

            _log.Success(
                $"Трансляция на {address}: {options.DescribeSource()}, " +
                $"{options.Width}×{options.Height}@{options.Fps}, " +
                $"{CaptureCapabilities.Describe(FfmpegArguments.ResolveEncoder(options.Encoder, capabilities))}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusText = "Ошибка";
            _log.Error($"Не удалось начать трансляцию: {ex.Message}");

            if (_sessionCts is not null)
            {
                await _sessionCts.CancelAsync();
                _sessionCts.Dispose();
                _sessionCts = null;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopStreamingAsync()
    {
        IsBusy = true;
        StatusText = "Остановка…";

        try
        {
            if (_session is not null)
            {
                await _session.StopAsync();
                _log.Info("Трансляция остановлена");
            }

            if (_sessionCts is not null)
            {
                await _sessionCts.CancelAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Ошибка при остановке: {ex.Message}");
        }
        finally
        {
            if (_session is not null)
            {
                await _session.DisposeAsync();
                _session = null;
            }

            _sessionCts?.Dispose();
            _sessionCts = null;

            IsStreaming = false;
            IsBusy = false;
            MetricsText = string.Empty;
            UpdateIdleStatus();
        }
    }

    private async Task<CaptureCapabilities> DetectCapabilitiesAsync()
    {
        _log.Info("Проверяю доступные кодировщики…");

        var capabilities = await EncoderProbe.DetectAsync();

        EncoderOptions.Clear();
        EncoderOptions.Add("Авто (рекомендуется)");
        foreach (var encoder in capabilities.WorkingEncoders)
        {
            EncoderOptions.Add(CaptureCapabilities.Describe(encoder));
        }

        SelectedEncoderIndex = 0;
        EncoderText = CaptureCapabilities.Describe(capabilities.RecommendedEncoder) +
                      (capabilities.HasDdagrab ? " · ddagrab" : " · gdigrab");
        _log.Success($"Кодировщик: {EncoderText}");

        return capabilities;
    }

    private void ApplyStatus(SenderStatus status)
    {
        if (status.Phase == SenderPhase.Failed)
        {
            LastError = status.Error ?? status.Message;
            StatusText = "Ошибка";
            _log.Error(status.Message);
            _ = StopStreamingAsync();
            return;
        }

        StatusText = status.Phase switch
        {
            SenderPhase.Streaming => "В эфире",
            SenderPhase.Stopping => "Остановка…",
            SenderPhase.Idle => "Готово",
            _ => status.Message,
        };

        if (!status.IsActive)
        {
            return;
        }

        AdaptationNote = status.AdaptationNote;

        // Ошибка приставки показывается в отдельной строке и не затирается подсказками
        // подстройки: именно её текст объясняет причину.
        if (status.ReceiverError is { Length: > 0 } receiverError)
        {
            LastError = $"Приставка: {receiverError}";
        }

        var latency = status.ReceiverLatencyMs > 0
            ? $"{status.ReceiverLatencyMs:F0} мс"
            : "—";

        var audio = status.AudioEnabled
            ? $"   звук {status.AudioKbps} кбит/с {LevelBar(status.AudioLevel)}"
            : string.Empty;

        // Рывок и отставание звука — то, что видно и слышно. Потери кадров и задержка
        // о них молчат, поэтому они здесь на первом месте.
        var stutter = status.ReceiverMaxGapMs > 0
            ? $"   рывок {status.ReceiverMaxGapMs:F0} мс ×{status.ReceiverStutters}"
            : string.Empty;

        var backlog = status.AudioEnabled
            ? $"   звук+{status.ReceiverAudioBacklogMs} мс"
            : string.Empty;

        MetricsText =
            $"{status.Fps:F1} к/с   {status.Kbps / 1000.0:F1} Мбит/с   " +
            $"кадров {status.FramesSent}   потери {status.ReceiverDropped}{stutter}{backlog}{audio}";
    }

    /// <summary>Полоска уровня звука — сразу видно, идёт ли звук на самом деле.</summary>
    private static string LevelBar(float level)
    {
        var filled = (int)Math.Round(Math.Clamp(level, 0, 1) * 6);
        return "[" + new string('|', filled) + new string('.', 6 - filled) + "]";
    }

    // ------------------------------------------------------------------
    // Обслуживание списка устройств
    // ------------------------------------------------------------------

    private void AddOrUpdateDevice(DiscoveredDevice device, bool isNew)
    {
        var existing = Devices.FirstOrDefault(d => d.Id == device.Id);

        if (existing is null)
        {
            var viewModel = new DeviceViewModel(device);
            Devices.Add(viewModel);
            HasDevices = true;

            if (isNew)
            {
                _log.Success($"Найдена приставка: {device.Name} ({device.EndPointText})");
            }

            // Приоритет у приставки из прошлого запуска: если человек всё время смотрит
            // на одну и ту же, выбирать её заново при каждом старте незачем.
            if (SelectedDevice is null && device.Id == _settings.LastDeviceId)
            {
                SelectedDevice = viewModel;
                _log.Info($"Выбрана приставка из прошлого запуска: {device.Name}");
                UpdateIdleStatus();
                return;
            }

            SelectedDevice ??= viewModel;
            UpdateIdleStatus();
            return;
        }

        existing.Update(device);

        if (isNew)
        {
            _log.Info($"Приставка снова в сети: {device.Name}");
            UpdateIdleStatus();
        }
    }

    /// <summary>Обновить строку состояния, когда трансляция не идёт.</summary>
    private void UpdateIdleStatus()
    {
        if (IsStreaming || IsBusy)
        {
            return;
        }

        StatusText = Devices.Count > 0
            ? $"Найдено устройств: {Devices.Count}"
            : "Поиск приставок в сети…";
    }

    private void RemoveDevice(string id)
    {
        var existing = Devices.FirstOrDefault(d => d.Id == id);
        if (existing is null)
        {
            return;
        }

        existing.MarkOffline();
        Devices.Remove(existing);
        HasDevices = Devices.Count > 0;

        if (ReferenceEquals(SelectedDevice, existing))
        {
            SelectedDevice = Devices.FirstOrDefault();
        }

        _log.Warn($"Приставка пропала из сети: {existing.Name}");
        UpdateIdleStatus();
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    /// <summary>Запуск при открытии окна: сразу ищем устройства и проверяем окружение.</summary>
    private async Task InitializeAsync()
    {
        try
        {
            if (FfmpegLocator.Find() is null)
            {
                LastError = "ffmpeg не найден. Запустите tools/fetch-ffmpeg.ps1 или укажите WOOCAST_FFMPEG.";
                _log.Error(LastError);
                StatusText = "Нет ffmpeg";
                return;
            }

            _capabilities = await DetectCapabilitiesAsync();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Error($"Ошибка инициализации: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        _sessionCts?.Dispose();
        await _discovery.DisposeAsync();
    }
}
