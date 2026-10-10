using CommunityToolkit.Mvvm.ComponentModel;
using WooCast.Protocol.Net;

namespace WooCast.Desktop.ViewModels;

/// <summary>Приставка в списке устройств.</summary>
public sealed partial class DeviceViewModel : ObservableObject
{
    public DeviceViewModel(DiscoveredDevice device)
    {
        Update(device);
    }

    public string Id { get; private set; } = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _address = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _model;

    [ObservableProperty]
    private int _maxWidth;

    [ObservableProperty]
    private int _maxHeight;

    [ObservableProperty]
    private int _maxFps;

    [ObservableProperty]
    private bool _lowLatency;

    /// <summary>Устройство отвечает на широковещательные оповещения.</summary>
    [ObservableProperty]
    private bool _isOnline = true;

    /// <summary>Снимок протокола, из которого построена карточка.</summary>
    public DiscoveredDevice Device { get; private set; } = null!;

    /// <summary>Строка возможностей декодера для карточки.</summary>
    public string CapsText
    {
        get
        {
            if (MaxWidth <= 0 || MaxHeight <= 0)
            {
                return "декодер не сообщён";
            }

            var text = $"до {MaxWidth}×{MaxHeight}";
            if (MaxFps > 0)
            {
                text += $" @ {MaxFps} к/с";
            }

            if (LowLatency)
            {
                text += " · low-latency";
            }

            return text;
        }
    }

    /// <summary>Обновить данные из очередного <c>announce</c>.</summary>
    public void Update(DiscoveredDevice device)
    {
        Device = device;
        Id = device.Id;
        Name = device.Name;

        // Версия приложения приставки показывается рядом с адресом: по ней видно,
        // надо ли обновлять вторую часть — приёмник обновляется отдельно от ПК.
        Subtitle = string.IsNullOrWhiteSpace(device.Version)
            ? device.Subtitle
            : $"{device.Subtitle} · версия {device.Version}";

        Address = device.EndPointText;
        IsBusy = device.IsBusy;
        IsOnline = true;

        if (!ReferenceEquals(device, Device))
        {
            Model = device.Model;
        }

        var caps = device.Caps;
        MaxWidth = caps?.MaxWidth ?? 0;
        MaxHeight = caps?.MaxHeight ?? 0;
        MaxFps = caps?.MaxFps ?? 0;
        LowLatency = caps?.LowLatencyDecode == true;

        OnPropertyChanged(nameof(CapsText));
    }

    /// <summary>Отметить устройство как пропавшее из сети.</summary>
    public void MarkOffline()
    {
        IsOnline = false;
        IsBusy = false;
    }
}
