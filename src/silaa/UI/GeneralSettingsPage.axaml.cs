using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using silaa.Services;
using silaa.ViewModels;

namespace silaa.UI;

public sealed partial class GeneralSettingsPage : UserControl
{
    private readonly GeneralSettingsViewModel _viewModel;

    public GeneralSettingsPage() : this(new LocalStore(), new NavigationViewModel(
        () => Task.CompletedTask, () => Task.CompletedTask, () => { }, () => { }, () => { }, () => { }, () => { }))
    {
    }

    public GeneralSettingsPage(LocalStore store, NavigationViewModel navigation)
    {
        AvaloniaXamlLoader.Load(this);
        _viewModel = new GeneralSettingsViewModel(store, navigation);
        DataContext = _viewModel;
        _viewModel.BackRequested += () => BackRequested?.Invoke();
        _viewModel.Saved += () => Saved?.Invoke();
    }

    public event Action? BackRequested;
    public event Action? Saved;
    public void RefreshFromStore() => _viewModel.Refresh();
}
