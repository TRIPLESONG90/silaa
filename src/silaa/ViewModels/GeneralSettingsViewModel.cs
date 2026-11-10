using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using silaa.Services;

namespace silaa.ViewModels;

public partial class GeneralSettingsViewModel : ObservableObject
{
    private readonly LocalStore _store;
    private bool _suppressAutoUpdateSave;

    public GeneralSettingsViewModel(LocalStore store, NavigationViewModel navigation)
    {
        _store = store;
        Navigation = navigation;
        Refresh();
    }

    public NavigationViewModel Navigation { get; }
    public IReadOnlyList<string> Themes { get; } = ["다크", "라이트"];
    public string CurrentAppVersion => ReleaseUpdateService.CurrentVersion;

    [ObservableProperty] private string _selectedTheme = "다크";
    [ObservableProperty] private bool _autoUpdateEnabled;
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private string _status = "";

    public event Action? BackRequested;
    public event Action? Saved;

    public void Refresh()
    {
        var settings = _store.Settings;
        SelectedTheme = settings.Theme == "light" ? "라이트" : "다크";
        _suppressAutoUpdateSave = true;
        AutoUpdateEnabled = settings.AutoUpdateEnabled;
        _suppressAutoUpdateSave = false;
        UpdateStatus = AutoUpdateEnabled
            ? "실행할 때 GitHub Release를 자동 확인합니다."
            : "자동 업데이트가 꺼져 있습니다.";
        Status = "";
    }

    partial void OnAutoUpdateEnabledChanged(bool value)
    {
        if (_suppressAutoUpdateSave) return;
        _store.SaveAutoUpdateEnabled(value);
        if (value) new ReleaseUpdateService(_store.Root).ClearFailureMarker();
        UpdateStatus = value
            ? "다음 실행부터 검증된 새 버전을 자동 설치합니다."
            : "자동 업데이트를 사용하지 않습니다.";
    }

    [RelayCommand]
    private void Back() => BackRequested?.Invoke();

    [RelayCommand]
    private void Save()
    {
        try
        {
            var settings = _store.Settings;
            _store.SaveWorkspaceSettings(
                settings.WorkspacePath,
                SelectedTheme == "라이트" ? "light" : "dark",
                settings.GlobalAllowedDirectories,
                settings.GlobalAllowedNetworkEndpoints);
            Status = "일반 설정을 저장했습니다. 테마는 다음 실행부터 적용됩니다.";
            Saved?.Invoke();
        }
        catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException or
                                           IOException or UnauthorizedAccessException)
        {
            Status = exception.Message;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        UpdateStatus = "GitHub Release를 확인하는 중…";
        try
        {
            var result = await new ReleaseUpdateService(_store.Root).CheckAsync();
            UpdateStatus = result.Release is null
                ? $"현재 {result.CurrentVersion}이 최신 버전입니다."
                : $"새 버전 {result.Release.Tag}을 사용할 수 있습니다.";
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or
                                           InvalidOperationException or TaskCanceledException)
        {
            UpdateStatus = $"업데이트 확인 실패: {exception.Message}";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }
}
