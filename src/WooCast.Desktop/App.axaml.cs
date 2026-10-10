using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WooCast.Desktop.Services;
using WooCast.Desktop.ViewModels;
using WooCast.Desktop.Views;

namespace WooCast.Desktop;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var log = new AppLog();
            _viewModel = new MainViewModel(log);

            desktop.MainWindow = new MainWindow(_viewModel);
            desktop.Exit += OnExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.DisposeAsync();
        }
    }
}
