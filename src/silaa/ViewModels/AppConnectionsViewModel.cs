using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using silaa.Core;
using silaa.Services;

namespace silaa.ViewModels;

public partial class AppConnectionsViewModel : ObservableObject
{
    private readonly LocalStore _store;
    private readonly Func<Task<string?>> _chooseOneDriveFolder;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<string, string, bool> _notify;

    public AppConnectionsViewModel(LocalStore store, NavigationViewModel navigation,
        Func<Task<string?>> chooseOneDriveFolder, Func<string, string, Task<bool>> confirm,
        Action<string, string, bool> notify)
    {
        _store = store;
        Navigation = navigation;
        _chooseOneDriveFolder = chooseOneDriveFolder;
        _confirm = confirm;
        _notify = notify;
        OneDrivePath = Environment.GetEnvironmentVariable("OneDrive") ?? "";
        Refresh();
    }

    public NavigationViewModel Navigation { get; }
    public ObservableCollection<AppConnectionItemViewModel> Connections { get; } = [];
    public bool HasConnections => Connections.Count > 0;
    public bool IsEmpty => !HasConnections;

    [ObservableProperty] private string _gitHubName = "GitHub";
    [ObservableProperty] private string _gitHubAccount = "";
    [ObservableProperty] private string _gitHubToken = "";
    [ObservableProperty] private string _oneDriveName = "OneDrive";
    [ObservableProperty] private string _oneDrivePath = "";
    [ObservableProperty] private string _status = "연결 자격증명은 Vault에 암호화해 저장합니다.";

    [RelayCommand]
    private void ConnectGitHub()
    {
        try
        {
            _store.AppConnections.SaveGitHub(GitHubName, GitHubAccount, GitHubToken);
            GitHubToken = "";
            Refresh();
            _notify("GitHub 연결 완료", "Agent가 승인 후 읽기 전용으로 저장소를 조회할 수 있습니다.", false);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        { _notify("GitHub 연결 실패", exception.Message, true); }
    }

    [RelayCommand]
    private async Task ChooseOneDriveFolderAsync()
    {
        var selected = await _chooseOneDriveFolder();
        if (selected is not null) OneDrivePath = selected;
    }

    [RelayCommand]
    private void ConnectOneDrive()
    {
        try
        {
            _store.AppConnections.SaveOneDrive(OneDriveName, OneDrivePath);
            Refresh();
            _notify("OneDrive 연결 완료", "동기화 폴더가 읽기 전용 Agent 연결로 등록됐습니다.", false);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        { _notify("OneDrive 연결 실패", exception.Message, true); }
    }

    private void Refresh()
    {
        Connections.Clear();
        foreach (var app in _store.AppConnections.List())
            Connections.Add(new AppConnectionItemViewModel(app, RemoveAsync));
        OnPropertyChanged(nameof(HasConnections));
        OnPropertyChanged(nameof(IsEmpty));
        Status = Connections.Count == 0 ? "연결된 외부 앱이 없습니다." : $"연결 {Connections.Count}개 · 기본 읽기 전용";
    }

    private async Task RemoveAsync(ConnectedAppInfo app)
    {
        if (!await _confirm("앱 연결을 삭제할까요?", $"{app.Name} 연결과 저장된 자격증명을 삭제합니다.")) return;
        _store.AppConnections.Remove(app.Id);
        Refresh();
        _notify("앱 연결 삭제", app.Name, false);
    }
}

public sealed class AppConnectionItemViewModel
{
    public AppConnectionItemViewModel(ConnectedAppInfo app, Func<ConnectedAppInfo, Task> remove)
    {
        App = app;
        RemoveCommand = new AsyncRelayCommand(() => remove(app));
    }
    public ConnectedAppInfo App { get; }
    public string Name => App.Name;
    public string Provider => App.Provider == "github" ? "GitHub" : "OneDrive";
    public string Detail => App.Provider == "github" ? (App.Account.Length == 0 ? "계정 정보 없음" : App.Account) : App.RootPath;
    public string Access => App.AgentEnabled ? "Agent 사용 · 읽기 전용" : "Agent 사용 안 함";
    public IAsyncRelayCommand RemoveCommand { get; }
}
