using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using silaa.Core;
using silaa.Services;

namespace silaa.ViewModels;

public partial class HomePageViewModel : ObservableObject
{
    private readonly LlmHarness _harness;
    private readonly Func<string, Task> _beginConversation;
    private readonly Func<Task> _createProject;
    private readonly Func<ProjectInfo, Task> _openProject;
    private readonly Func<Task> _openMemory;
    private readonly Action _openVault;
    private readonly Action _openBrowser;
    private readonly Action _openSettings;
    private readonly Action _openSkills;
    private readonly Action _openAutomation;
    private readonly Action _openAppConnections;
    private bool _greetingRequested;

    public HomePageViewModel(
        LlmHarness harness,
        NavigationViewModel navigation,
        Func<string, Task> beginConversation,
        Func<Task> createProject,
        Func<ProjectInfo, Task> openProject,
        Func<Task> openMemory,
        Action openVault,
        Action openBrowser,
        Action openSettings,
        Action openSkills,
        Action openAutomation,
        bool projectsOnly = false,
        bool appsOnly = false,
        Action? openAppConnections = null)
    {
        _harness = harness;
        Navigation = navigation;
        _beginConversation = beginConversation;
        _createProject = createProject;
        _openProject = openProject;
        _openMemory = openMemory;
        _openVault = openVault;
        _openBrowser = openBrowser;
        _openSettings = openSettings;
        _openSkills = openSkills;
        _openAutomation = openAutomation;
        _openAppConnections = openAppConnections ?? (() => { });
        ProjectsOnly = projectsOnly;
        AppsOnly = appsOnly;
    }

    public NavigationViewModel Navigation { get; }
    public bool ProjectsOnly { get; }
    public bool AppsOnly { get; }
    public bool IsDashboard => !ProjectsOnly && !AppsOnly;
    public bool IsCatalogPage => ProjectsOnly || AppsOnly;
    public string PageTitle => AppsOnly ? "앱" : ProjectsOnly ? "프로젝트" : "홈";
    public string PageSubtitle => AppsOnly
        ? "프로젝트에서 만든 앱과 할당된 실행 주소를 관리합니다"
        : ProjectsOnly
            ? "프로젝트별 대화, 파일, 기억과 실행 기록을 관리합니다"
            : "대화를 시작하거나 최근 프로젝트로 바로 이동합니다";
    public string ProjectSectionTitle => AppsOnly ? "내 앱" : ProjectsOnly ? "프로젝트 목록" : "최근 프로젝트";
    public string ProjectSectionDescription => AppsOnly
        ? "앱 산출물이 지정된 프로젝트만 표시됩니다."
        : "Agent가 사용할 파일, 대화, 기억과 실행 기록이 함께 유지됩니다.";
    public string CreateLabel => AppsOnly ? "새 앱" : "새 프로젝트";
    public string EmptyTitle => AppsOnly ? "아직 만든 앱이 없습니다" : "첫 프로젝트를 만들어 보세요";
    public string EmptyDescription => AppsOnly
        ? "새 앱을 누르면 Agent 설문으로 목적과 기능을 구체화한 뒤 앱 프로젝트를 만듭니다."
        : "막연한 아이디어부터 시작해도 Agent가 질문으로 실행 가능한 수준까지 구체화합니다.";
    public ObservableCollection<ProjectCardViewModel> Projects { get; } = [];
    public ObservableCollection<ProjectCardViewModel> Apps { get; } = [];

    [ObservableProperty] private string _quickPrompt = "";
    [ObservableProperty] private string _greeting = "안녕하세요. 오늘은 무엇을 시작할까요?";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasProjects;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _hasApps;
    public bool IsAppsEmpty => !HasApps;
    public bool ShowProjectSection => !IsDashboard || HasProjects;

    partial void OnHasAppsChanged(bool value) => OnPropertyChanged(nameof(IsAppsEmpty));
    partial void OnHasProjectsChanged(bool value) => OnPropertyChanged(nameof(ShowProjectSection));

    [RelayCommand(AllowConcurrentExecutions = false)]
    public async Task RefreshAsync()
    {
        if (!_greetingRequested)
        {
            _greetingRequested = true;
            _ = RefreshGreetingAsync();
        }
        Status = "프로젝트를 찾는 중…";
        try
        {
            var projects = await _harness.ListProjectsAsync();
            Projects.Clear();
            Apps.Clear();
            foreach (var project in projects)
            {
                var card = new ProjectCardViewModel(project, _openProject);
                if (AppsOnly && project.Delivery.IsWebApp)
                    Projects.Add(card);
                else if (ProjectsOnly || (!AppsOnly && !project.Delivery.IsWebApp && Projects.Count < 4))
                    Projects.Add(card);
                if (project.Delivery.IsWebApp && (ProjectsOnly || Apps.Count < 4)) Apps.Add(card);
            }
            HasProjects = Projects.Count > 0;
            HasApps = Apps.Count > 0;
            IsEmpty = !HasProjects;
            Status = IsEmpty
                ? AppsOnly ? "앱 프로젝트를 만들면 여기에 자동으로 나타납니다." : "프로젝트 작업을 만들면 여기에 자동으로 나타납니다."
                : AppsOnly ? $"앱 {Projects.Count}개" : $"프로젝트 {Projects.Count}개";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            Projects.Clear();
            Apps.Clear();
            HasProjects = false;
            HasApps = false;
            IsEmpty = true;
            Status = exception.Message;
        }
    }

    private async Task RefreshGreetingAsync()
    {
        var generated = await _harness.GenerateGreetingAsync();
        if (!string.IsNullOrWhiteSpace(generated)) Greeting = generated;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task BeginConversationAsync()
    {
        var prompt = QuickPrompt.Trim();
        QuickPrompt = "";
        await _beginConversation(prompt);
    }

    [RelayCommand]
    private Task CreateProjectAsync() => _createProject();

    [RelayCommand]
    private Task OpenMemoryAsync() => _openMemory();

    [RelayCommand] private void OpenVault() => _openVault();
    [RelayCommand] private void OpenBrowser() => _openBrowser();
    [RelayCommand] private void OpenSettings() => _openSettings();
    [RelayCommand] private void OpenSkills() => _openSkills();
    [RelayCommand] private void OpenAutomation() => _openAutomation();
    [RelayCommand] private void OpenAppConnections() => _openAppConnections();
}

public sealed class ProjectCardViewModel
{
    public ProjectCardViewModel(ProjectInfo project, Func<ProjectInfo, Task> openProject)
    {
        Project = project;
        OpenCommand = new AsyncRelayCommand(() => openProject(project));
    }

    public ProjectInfo Project { get; }
    public string Initial => string.IsNullOrWhiteSpace(Project.Name) ? "·" : Project.Name[..1].ToUpperInvariant();
    public string Name => Project.Name;
    public string Directory => Project.Directory;
    public string Summary => Project.Summary;
    public bool IsSummaryVisible => !string.IsNullOrWhiteSpace(Project.Summary);
    public bool HasDiscovery => Project.SpecificityScore > 0;
    public string Discovery => $"구체화 {Project.SpecificityScore}% · 질의 {Project.DiscoveryHistory.Count}개";
    public bool HasDelivery => Project.Delivery.IsWebApp;
    public string Delivery => Project.Delivery.IsWebApp
        ? $"앱 :{Project.Delivery.AppPort} · 내부 :{Project.Delivery.InternalPort} · {Project.Delivery.SkillName}"
        : "";
    public string UpdatedAt => Project.UpdatedAt.LocalDateTime.ToString("MM.dd  HH:mm");
    public IAsyncRelayCommand OpenCommand { get; }
}
