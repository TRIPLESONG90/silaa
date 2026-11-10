using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using silaa.Services;
using silaa.ViewModels;

namespace silaa.UI;

public sealed partial class SettingsPage : UserControl
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage() : this(new LocalStore())
    {
    }

    private SettingsPage(LocalStore store) : this(store, new LlmHarness(store))
    {
    }

    public SettingsPage(LocalStore store, LlmHarness harness, NavigationViewModel? navigation = null)
    {
        AvaloniaXamlLoader.Load(this);
        _viewModel = new SettingsViewModel(store, harness, ChooseWorkspaceAsync, navigation);
        DataContext = _viewModel;
        _viewModel.BackRequested += () => BackRequested?.Invoke();
        _viewModel.Saved += () => Saved?.Invoke();
    }

    public event Action? BackRequested;
    public event Action? Saved;

    public void RefreshFromStore() => _viewModel.Refresh();

    private async Task<string?> ChooseWorkspaceAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return null;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "작업 폴더 선택",
            AllowMultiple = false
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
