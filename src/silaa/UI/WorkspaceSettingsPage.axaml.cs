using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using silaa.Services;
using silaa.ViewModels;

namespace silaa.UI;

public sealed partial class WorkspaceSettingsPage : UserControl
{
    private readonly WorkspaceSettingsViewModel _viewModel;

    public WorkspaceSettingsPage() : this(new LocalStore())
    {
    }

    public WorkspaceSettingsPage(
        LocalStore store,
        NavigationViewModel? navigation = null,
        WorkspaceSettingsSection section = WorkspaceSettingsSection.Folders)
    {
        AvaloniaXamlLoader.Load(this);
        _viewModel = new WorkspaceSettingsViewModel(store, ChooseFolderAsync, navigation, section);
        DataContext = _viewModel;
        _viewModel.BackRequested += () => BackRequested?.Invoke();
        _viewModel.Saved += () => Saved?.Invoke();
    }

    public event Action? BackRequested;
    public event Action? Saved;

    public void RefreshFromStore() => _viewModel.Refresh();

    private async Task<string?> ChooseFolderAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return null;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "폴더 선택",
            AllowMultiple = false
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
