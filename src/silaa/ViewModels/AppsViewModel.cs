using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using silaa.Core;
using silaa.Services;

namespace silaa.ViewModels;

public partial class AppsViewModel : ObservableObject
{
    private readonly LlmHarness _harness;
    private readonly Func<Task> _createApp;
    private readonly Action _openConnections;
    private readonly Func<ProjectInfo, Task> _openProject;
    private readonly Action<Uri> _openBrowser;
    private readonly Func<string, Task> _copy;

    public AppsViewModel(LlmHarness harness, NavigationViewModel navigation, Func<Task> createApp,
        Action openConnections, Func<ProjectInfo, Task> openProject, Action<Uri> openBrowser,
        Func<string, Task> copy)
    {
        _harness = harness;
        Navigation = navigation;
        _createApp = createApp;
        _openConnections = openConnections;
        _openProject = openProject;
        _openBrowser = openBrowser;
        _copy = copy;
    }

    public NavigationViewModel Navigation { get; }
    public ObservableCollection<AppCardViewModel> Apps { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasNoApps = true;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _onlineCount;
    public int AppCount => Apps.Count;
    public int OfflineCount => Math.Max(0, AppCount - OnlineCount);
    public string AppCountLabel => $"{AppCount}개";
    public string OnlineCountLabel => $"{OnlineCount}개";
    public string OfflineCountLabel => $"{OfflineCount}개";

    [RelayCommand(AllowConcurrentExecutions = false)]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        Status = "앱과 실행 주소를 확인하는 중…";
        try
        {
            var projects = await _harness.ListProjectsAsync();
            Apps.Clear();
            foreach (var project in projects.Where(project => project.Delivery.IsWebApp))
                Apps.Add(new AppCardViewModel(project, _openProject, _openBrowser, _copy));
            HasNoApps = Apps.Count == 0;
            await Task.WhenAll(Apps.Select(app => app.CheckHealthAsync()));
            OnlineCount = Apps.Count(app => app.IsOnline);
            OnPropertyChanged(nameof(AppCount));
            OnPropertyChanged(nameof(OfflineCount));
            OnPropertyChanged(nameof(AppCountLabel));
            OnPropertyChanged(nameof(OnlineCountLabel));
            OnPropertyChanged(nameof(OfflineCountLabel));
            Status = HasNoApps ? "앱 프로젝트를 만들면 이곳에서 실행 상태를 관리할 수 있습니다." : $"앱 {Apps.Count}개 확인 완료";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            Apps.Clear();
            HasNoApps = true;
            OnlineCount = 0;
            Status = exception.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand] private Task CreateAppAsync() => _createApp();
    [RelayCommand] private void OpenConnections() => _openConnections();
}

public partial class AppCardViewModel : ObservableObject
{
    private static readonly HttpClient HealthClient = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private readonly ProjectInfo _project;
    private readonly Action<Uri> _openBrowser;
    private readonly Func<string, Task> _copy;

    public AppCardViewModel(ProjectInfo project, Func<ProjectInfo, Task> openProject,
        Action<Uri> openBrowser, Func<string, Task> copy)
    {
        _project = project;
        _openBrowser = openBrowser;
        _copy = copy;
        OpenProjectCommand = new AsyncRelayCommand(() => openProject(project));
        OpenBrowserCommand = new RelayCommand(OpenBrowser);
        CopyUrlCommand = new AsyncRelayCommand(CopyUrlAsync);
        CheckHealthCommand = new AsyncRelayCommand(CheckHealthAsync);
    }

    public string Name => _project.Name;
    public string Summary => _project.Summary;
    public string Directory => _project.Directory;
    public string Skill => string.IsNullOrWhiteSpace(_project.Delivery.SkillName) ? "기본 전달 스킬" : _project.Delivery.SkillName;
    public string LocalUrl => $"http://localhost:{_project.Delivery.AppPort}";
    public string InternalUrl => $"http://localhost:{_project.Delivery.InternalPort}";
    public string Ports => $"외부 {_project.Delivery.AppPort} · 내부 {_project.Delivery.InternalPort}";
    public string UpdatedAt => _project.UpdatedAt.LocalDateTime.ToString("yyyy.MM.dd HH:mm");

    [ObservableProperty] private string _statusText = "확인 전";
    [ObservableProperty] private string _statusDetail = "실행 상태를 아직 확인하지 않았습니다.";
    [ObservableProperty] private string _statusBackground = "#1AFFFFFF";
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private bool _isChecking;

    public IAsyncRelayCommand OpenProjectCommand { get; }
    public IRelayCommand OpenBrowserCommand { get; }
    public IAsyncRelayCommand CopyUrlCommand { get; }
    public IAsyncRelayCommand CheckHealthCommand { get; }

    public async Task CheckHealthAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        StatusText = "확인 중";
        StatusDetail = "로컬 주소에 연결하는 중…";
        StatusBackground = "#1AFFFFFF";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_project.Delivery.AppPort}/");
            using var response = await HealthClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            IsOnline = response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            StatusText = IsOnline ? "실행 중" : $"HTTP {(int)response.StatusCode}";
            StatusDetail = IsOnline ? "내장 브라우저에서 바로 열 수 있습니다." : "서버는 응답했지만 상태를 확인해야 합니다.";
            StatusBackground = IsOnline ? "#263DD68C" : "#26FFB020";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            IsOnline = false;
            StatusText = "중지됨";
            StatusDetail = "현재 로컬 포트에서 응답이 없습니다.";
            StatusBackground = "#20FFFFFF";
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void OpenBrowser() => _openBrowser(new Uri(LocalUrl));
    private Task CopyUrlAsync() => _copy(LocalUrl);
}
