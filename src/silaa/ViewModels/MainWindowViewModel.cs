using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace silaa.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly Func<Task> _sendOrCancel;
    private readonly Action _stop;
    private readonly Action _openSettings;
    private readonly Func<Task> _openResources;
    private readonly Func<Task> _openProjectMemory;
    private readonly Func<Task> _openFolderAccess;
    private readonly Func<Task> _openHome;
    private readonly Action _newSession;
    private readonly Func<Task> _refreshSessions;
    private readonly Func<Task> _copyConversation;
    private readonly Action _closeInspector;
    private readonly Action<ReasoningEffortOption> _changeReasoningEffort;
    private bool _suppressReasoningEffortChange;

    [ObservableProperty] private string _pageTitle = "새 세션";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _modelText = "";
    [ObservableProperty] private string _editorText = "";
    [ObservableProperty] private string _sidebarContext = "";
    [ObservableProperty] private string _sendLabel = "보내기";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompose))]
    private bool _isParallelSessionRun;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompose))]
    private bool _isViewingActiveRun = true;
    [ObservableProperty] private bool _hasActiveProject;
    [ObservableProperty] private ReasoningEffortOption _selectedReasoningEffort = ReasoningEffortOption.Default;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingPrompts))]
    [NotifyPropertyChangedFor(nameof(PendingPromptLabel))]
    private int _pendingPromptCount;

    public bool HasPendingPrompts => PendingPromptCount > 0;
    public string PendingPromptLabel => $"대기 {PendingPromptCount}개";
    public string ComposerHint => IsParallelSessionRun
        ? "이 세션은 응답 중 · 다른 세션에서는 동시에 대화 가능"
        : IsRunning && !IsViewingActiveRun
        ? "다른 세션에서 응답 중 · 여기서 보내면 병렬 실행"
        : IsRunning
        ? "Enter로 추가 지시 · 현재 응답을 정리한 뒤 이어서 실행"
        : "Enter 전송 · Shift+Enter 줄바꿈";
    public string ComposerPlaceholder => IsParallelSessionRun
        ? "응답이 끝난 뒤 이 세션에 추가 메시지를 보낼 수 있습니다"
        : IsRunning && !IsViewingActiveRun
        ? "메시지를 입력하면 이 세션에서 즉시 병렬 전송됩니다"
        : IsRunning
        ? "진행 중인 응답에 추가할 지시를 입력하세요"
        : "Agent에게 작업을 요청하세요";
    public bool CanStartSession => true;
    public bool CanCompose => !IsParallelSessionRun;
    public IReadOnlyList<ReasoningEffortOption> ReasoningEfforts => ReasoningEffortOption.All;

    public MainWindowViewModel(Func<Task> sendOrCancel, Action stop, Action openSettings,
        Func<Task> openResources, Func<Task> openProjectMemory, Func<Task> openFolderAccess, Func<Task> openHome,
        Action newSession, Func<Task> refreshSessions, Func<Task> copyConversation, Action closeInspector,
        Action<ReasoningEffortOption>? changeReasoningEffort = null)
    {
        _sendOrCancel = sendOrCancel;
        _stop = stop;
        _openSettings = openSettings;
        _openResources = openResources;
        _openProjectMemory = openProjectMemory;
        _openFolderAccess = openFolderAccess;
        _openHome = openHome;
        _newSession = newSession;
        _refreshSessions = refreshSessions;
        _copyConversation = copyConversation;
        _closeInspector = closeInspector;
        _changeReasoningEffort = changeReasoningEffort ?? (_ => { });
    }

    partial void OnSelectedReasoningEffortChanged(ReasoningEffortOption value)
    {
        if (!_suppressReasoningEffortChange) _changeReasoningEffort(value);
    }

    public void SetReasoningEffort(string? value)
    {
        _suppressReasoningEffortChange = true;
        SelectedReasoningEffort = ReasoningEffortOption.All.FirstOrDefault(option =>
                                      option.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                                  ?? ReasoningEffortOption.Default;
        _suppressReasoningEffortChange = false;
    }

    partial void OnIsRunningChanged(bool value)
    {
        SendLabel = IsParallelSessionRun ? "응답 중" : value ? "추가 보내기" : "보내기";
        OnPropertyChanged(nameof(ComposerHint));
        OnPropertyChanged(nameof(ComposerPlaceholder));
        OnPropertyChanged(nameof(CanStartSession));
        OnPropertyChanged(nameof(CanCompose));
    }

    partial void OnIsViewingActiveRunChanged(bool value)
    {
        SendLabel = IsParallelSessionRun ? "응답 중" : IsRunning ? (value ? "추가 보내기" : "병렬 보내기") : "보내기";
        OnPropertyChanged(nameof(ComposerHint));
        OnPropertyChanged(nameof(ComposerPlaceholder));
    }

    partial void OnIsParallelSessionRunChanged(bool value)
    {
        SendLabel = value ? "응답 중" : IsRunning ? (IsViewingActiveRun ? "추가 보내기" : "병렬 보내기") : "보내기";
        OnPropertyChanged(nameof(ComposerHint));
        OnPropertyChanged(nameof(ComposerPlaceholder));
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SendOrCancelAsync() => _sendOrCancel();

    [RelayCommand]
    private void Stop() => _stop();

    [RelayCommand]
    private void OpenSettings() => _openSettings();

    [RelayCommand]
    private Task OpenResourcesAsync() => _openResources();

    [RelayCommand]
    private Task OpenProjectMemoryAsync() => _openProjectMemory();

    [RelayCommand]
    private Task OpenFolderAccessAsync() => _openFolderAccess();

    [RelayCommand]
    private Task OpenHomeAsync() => _openHome();

    [RelayCommand]
    private void NewSession() => _newSession();

    [RelayCommand]
    private Task RefreshSessionsAsync() => _refreshSessions();

    [RelayCommand]
    private Task CopyConversationAsync() => _copyConversation();

    [RelayCommand]
    private void CloseInspector() => _closeInspector();
}
