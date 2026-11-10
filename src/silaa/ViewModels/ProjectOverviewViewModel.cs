using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using silaa.Core;
using silaa.Services;

namespace silaa.ViewModels;

public partial class ProjectOverviewViewModel : ObservableObject
{
    private readonly LlmHarness _harness;
    private readonly ProjectInfo _project;
    private readonly Func<ProjectInfo, Task> _openConversation;
    private readonly Func<ProjectInfo, Task> _openResources;
    private readonly Func<ProjectInfo, Task> _openMemory;
    private readonly Func<ChatSession, Task> _openSession;
    private readonly Action<Uri> _openApp;

    public ProjectOverviewViewModel(LlmHarness harness, NavigationViewModel navigation, ProjectInfo project,
        Func<ProjectInfo, Task> openConversation, Func<ProjectInfo, Task> openResources,
        Func<ProjectInfo, Task> openMemory, Func<ChatSession, Task> openSession, Action<Uri> openApp)
    {
        _harness = harness;
        Navigation = navigation;
        _project = project;
        _openConversation = openConversation;
        _openResources = openResources;
        _openMemory = openMemory;
        _openSession = openSession;
        _openApp = openApp;
        foreach (var entry in project.DiscoveryHistory.TakeLast(5).Reverse())
            Discovery.Add(new ProjectDiscoveryItemViewModel(entry));
    }

    public NavigationViewModel Navigation { get; }
    public string Name => _project.Name;
    public string Summary => _project.Summary;
    public string Directory => _project.Directory;
    public string UpdatedAt => $"최근 업데이트 {_project.UpdatedAt.LocalDateTime:yyyy.MM.dd HH:mm}";
    public string Specificity => $"{_project.SpecificityScore}%";
    public string DiscoveryLabel => $"질의 {_project.DiscoveryHistory.Count}개";
    public bool HasApp => _project.Delivery.IsWebApp;
    public string AppUrl => HasApp ? $"http://localhost:{_project.Delivery.AppPort}" : "앱 산출물 없음";
    public string DeliveryLabel => HasApp
        ? $"{_project.Delivery.SkillName} · 외부 {_project.Delivery.AppPort} · 내부 {_project.Delivery.InternalPort}"
        : "프로젝트 유형에 맞춰 Agent가 산출물을 구성합니다.";
    public bool HasDiscovery => Discovery.Count > 0;

    public ObservableCollection<ProjectSessionItemViewModel> RecentSessions { get; } = [];
    public ObservableCollection<ProjectDiscoveryItemViewModel> Discovery { get; } = [];

    [ObservableProperty] private string _sessionCount = "0";
    [ObservableProperty] private string _resourceCount = "0";
    [ObservableProperty] private string _memoryCount = "0";
    [ObservableProperty] private bool _hasNoSessions = true;
    [ObservableProperty] private string _status = "";

    [RelayCommand(AllowConcurrentExecutions = false)]
    public async Task RefreshAsync()
    {
        Status = "프로젝트 정보를 불러오는 중…";
        try
        {
            var sessionsTask = _harness.ListProjectSessionsAsync(_project.Id);
            var resourcesTask = _harness.ListProjectResourcesAsync(_project.Id);
            var memoryTask = _harness.ListMemoryAsync(_project.Id);
            await Task.WhenAll(sessionsTask, resourcesTask, memoryTask);
            var sessions = await sessionsTask;
            var resources = await resourcesTask;
            var memory = await memoryTask;
            SessionCount = sessions.Count.ToString();
            ResourceCount = resources.Count.ToString();
            MemoryCount = memory.Count.ToString();
            RecentSessions.Clear();
            foreach (var session in sessions.OrderByDescending(item => item.UpdatedAt).Take(6))
                RecentSessions.Add(new ProjectSessionItemViewModel(session, _openSession));
            HasNoSessions = RecentSessions.Count == 0;
            Status = "프로젝트 맥락이 준비되었습니다.";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            Status = exception.Message;
        }
    }

    [RelayCommand] private Task OpenConversationAsync() => _openConversation(_project);
    [RelayCommand] private Task OpenResourcesAsync() => _openResources(_project);
    [RelayCommand] private Task OpenMemoryAsync() => _openMemory(_project);
    [RelayCommand] private void OpenApp() { if (HasApp) _openApp(new Uri(AppUrl)); }
}

public sealed class ProjectSessionItemViewModel
{
    public ProjectSessionItemViewModel(ChatSession session, Func<ChatSession, Task> open)
    {
        Title = session.Title;
        UpdatedAt = session.UpdatedAt == DateTimeOffset.MinValue ? "새 세션" : session.UpdatedAt.LocalDateTime.ToString("MM.dd HH:mm");
        State = session.IsResponding ? "응답 중" : "대화";
        OpenCommand = new AsyncRelayCommand(() => open(session));
    }
    public string Title { get; }
    public string UpdatedAt { get; }
    public string State { get; }
    public IAsyncRelayCommand OpenCommand { get; }
}

public sealed class ProjectDiscoveryItemViewModel
{
    public ProjectDiscoveryItemViewModel(ProjectDiscoveryEntry entry)
    {
        Question = entry.Question;
        Answer = entry.Answer;
        Score = $"구체화 {entry.SpecificityScore}%";
    }
    public string Question { get; }
    public string Answer { get; }
    public string Score { get; }
}
