using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using silaa.Services;

namespace silaa.ViewModels;

public enum WorkspaceSettingsSection
{
    Folders,
    Security
}

public partial class WorkspaceSettingsViewModel : ObservableObject
{
    private readonly LocalStore _store;
    private readonly Func<Task<string?>> _pickFolder;

    public WorkspaceSettingsViewModel(
        LocalStore store,
        Func<Task<string?>> pickFolder,
        NavigationViewModel? navigation = null,
        WorkspaceSettingsSection section = WorkspaceSettingsSection.Folders)
    {
        _store = store;
        _pickFolder = pickFolder;
        Navigation = navigation;
        Section = section;
        Refresh();
    }

    public NavigationViewModel? Navigation { get; }
    public WorkspaceSettingsSection Section { get; }
    public bool HasNavigation => Navigation is not null;
    public bool IsFolderSettings => Section == WorkspaceSettingsSection.Folders;
    public bool IsSecuritySettings => Section == WorkspaceSettingsSection.Security;
    public string PageTitle => IsFolderSettings ? "접근 허용 폴더" : "Agent 보안";
    public string PageSubtitle => IsFolderSettings
        ? "LLM 연결과 독립적으로 작업 폴더 권한을 관리합니다"
        : "Agent의 외부 주소 접근과 보안 경계를 관리합니다";
    public IReadOnlyList<string> Themes { get; } = ["다크", "라이트"];
    public ObservableCollection<AllowedDirectoryItemViewModel> AllowedDirectories { get; } = [];
    public ObservableCollection<NetworkEndpointItemViewModel> AllowedNetworkEndpoints { get; } = [];

    [ObservableProperty] private string _workspace = "";
    [ObservableProperty] private string _newNetworkEndpoint = "";
    [ObservableProperty] private string _selectedTheme = "다크";
    [ObservableProperty] private string _status = "";

    public string FolderSummary => $"기본 작업 폴더 + 추가 허용 {AllowedDirectories.Count}개";
    public string SecuritySummary =>
        $"Enforce · 네트워크 기본 거부 · 전체 허용 {AllowedNetworkEndpoints.Count}개";

    public event Action? BackRequested;
    public event Action? Saved;

    public void Refresh()
    {
        Workspace = _store.Settings.WorkspacePath;
        SelectedTheme = _store.Settings.Theme == "light" ? "라이트" : "다크";
        AllowedDirectories.Clear();
        foreach (var directory in _store.Settings.GlobalAllowedDirectories)
            AllowedDirectories.Add(new AllowedDirectoryItemViewModel(directory, RemoveDirectory));
        AllowedNetworkEndpoints.Clear();
        foreach (var endpoint in _store.Settings.GlobalAllowedNetworkEndpoints)
            AllowedNetworkEndpoints.Add(new NetworkEndpointItemViewModel(endpoint, RemoveNetworkEndpoint));
        NewNetworkEndpoint = "";
        Status = "";
        RefreshSummaries();
    }

    [RelayCommand]
    private void Back() => BackRequested?.Invoke();

    [RelayCommand]
    private async Task BrowseWorkspaceAsync()
    {
        var path = await _pickFolder();
        if (!string.IsNullOrWhiteSpace(path)) Workspace = path;
    }

    [RelayCommand]
    private async Task AddDirectoryAsync()
    {
        try
        {
            var path = await _pickFolder();
            if (string.IsNullOrWhiteSpace(path)) return;
            var normalized = FolderAccessPolicy.NormalizePath(path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (AllowedDirectories.Any(item => item.Path.Equals(normalized, comparison)))
            {
                Status = "이미 전체 허용 목록에 있는 폴더입니다.";
                return;
            }
            AllowedDirectories.Add(new AllowedDirectoryItemViewModel(normalized, RemoveDirectory));
            Status = "폴더를 추가했습니다. 저장을 눌러 적용하세요.";
            RefreshSummaries();
        }
        catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException)
        {
            Status = exception.Message;
        }
    }

    [RelayCommand]
    private void AddNetworkEndpoint()
    {
        try
        {
            var normalized = NetworkAccessPolicy.NormalizeEndpoint(NewNetworkEndpoint);
            if (AllowedNetworkEndpoints.Any(item =>
                    item.Endpoint.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            {
                Status = "이미 전체 허용 목록에 있는 주소입니다.";
                return;
            }
            AllowedNetworkEndpoints.Add(new NetworkEndpointItemViewModel(normalized, RemoveNetworkEndpoint));
            NewNetworkEndpoint = "";
            Status = "허용 주소를 추가했습니다. 저장을 눌러 적용하세요.";
            RefreshSummaries();
        }
        catch (ArgumentException exception)
        {
            Status = exception.Message;
        }
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            _store.SaveWorkspaceSettings(
                Workspace,
                SelectedTheme == "라이트" ? "light" : "dark",
                AllowedDirectories.Select(item => item.Path),
                AllowedNetworkEndpoints.Select(item => item.Endpoint));
            Refresh();
            Status = "작업 환경과 권한 설정을 저장했습니다. 테마는 다음 실행부터 적용됩니다.";
            Saved?.Invoke();
        }
        catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException or
                                           IOException or UnauthorizedAccessException)
        {
            Status = exception.Message;
        }
    }

    private void RemoveDirectory(AllowedDirectoryItemViewModel item)
    {
        AllowedDirectories.Remove(item);
        Status = "폴더 권한을 제거했습니다. 저장을 눌러 적용하세요.";
        RefreshSummaries();
    }

    private void RemoveNetworkEndpoint(NetworkEndpointItemViewModel item)
    {
        AllowedNetworkEndpoints.Remove(item);
        Status = "허용 주소를 제거했습니다. 저장을 눌러 적용하세요.";
        RefreshSummaries();
    }

    private void RefreshSummaries()
    {
        OnPropertyChanged(nameof(FolderSummary));
        OnPropertyChanged(nameof(SecuritySummary));
    }
}
