using Avalonia.Controls;
using WooCast.Desktop.ViewModels;

namespace WooCast.Desktop.Views;

/// <summary>Главное окно приложения. Вся логика — в <see cref="MainViewModel"/>.</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        // Настройки сохраняются и при закрытии: окно могли закрыть, ничего не транслируя,
        // но выбранные источник, окно и звук всё равно стоит запомнить.
        Closing += (_, _) => _viewModel?.SaveSettings();
    }
}
