using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using silaa.Core;
using silaa.Services;

namespace silaa.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly LocalStore _store;
    private readonly LlmHarness _harness;
    private readonly Func<Task<string?>> _pickWorkspace;
    private bool _suppressSelection;
    private bool _suppressAutoUpdateSave;

    public SettingsViewModel(
        LocalStore store,
        LlmHarness harness,
        Func<Task<string?>> pickWorkspace,
        NavigationViewModel? navigation = null)
    {
        _store = store;
        _harness = harness;
        _pickWorkspace = pickWorkspace;
        Navigation = navigation;
        Refresh();
    }

    public NavigationViewModel? Navigation { get; }
    public bool HasNavigation => Navigation is not null;

    public IReadOnlyList<string> MaxTokenFields { get; } = ["max_tokens", "max_completion_tokens"];
    public IReadOnlyList<string> Themes { get; } = ["다크", "라이트"];
    public IReadOnlyList<ReasoningEffortOption> ReasoningEfforts { get; } = ReasoningEffortOption.All;
    public ObservableCollection<LlmConnectionItemViewModel> Connections { get; } = [];
    public ObservableCollection<AllowedDirectoryItemViewModel> GlobalAllowedDirectories { get; } = [];
    public ObservableCollection<NetworkEndpointItemViewModel> GlobalAllowedNetworkEndpoints { get; } = [];

    [ObservableProperty] private LlmConnectionItemViewModel? _selectedConnection;
    [ObservableProperty] private string _connectionName = "";
    [ObservableProperty] private string _endpoint = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _apiKeyState = "";
    [ObservableProperty] private string _apiKeyPlaceholder = "";
    [ObservableProperty] private string _workspace = "";
    [ObservableProperty] private bool _removeApiKey;
    [ObservableProperty] private string _maxOutputTokens = "";
    [ObservableProperty] private string _contextWindowTokens = "";
    [ObservableProperty] private string _selectedMaxTokensField = "max_tokens";
    [ObservableProperty] private string _selectedTheme = "다크";
    [ObservableProperty] private string _temperature = "";
    [ObservableProperty] private string _topP = "";
    [ObservableProperty] private string _frequencyPenalty = "";
    [ObservableProperty] private string _presencePenalty = "";
    [ObservableProperty] private string _seed = "";
    [ObservableProperty] private string _requestTimeout = "";
    [ObservableProperty] private ReasoningEffortOption _selectedReasoningEffort = ReasoningEffortOption.Default;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private bool _autoUpdateEnabled;
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private string _updateStatus = "";

    public string CurrentAppVersion => ReleaseUpdateService.CurrentVersion;

    public string ConnectionCountText => $"저장된 연결 {Connections.Count}개";
    public bool HasSelectedConnection => SelectedConnection is not null;
    public bool HasConnections => Connections.Count > 0;
    public bool IsConnectionList => !IsEditorOpen;
    public string PageTitle => IsEditorOpen ? "LLM 연결 설정" : "LLM 연결";
    public string PageSubtitle => IsEditorOpen
        ? "엔드포인트, 인증 정보와 모델 옵션을 편집합니다"
        : "저장된 연결을 선택하거나 새 연결을 추가합니다";
    public string BackLabel => IsEditorOpen ? "목록으로" : "돌아가기";
    public string ReasoningEffortHelp => SelectedReasoningEffort.Help;
    public string SecuritySummary =>
        $"Enforce · 네트워크 기본 거부 · 전체 허용 {GlobalAllowedNetworkEndpoints.Count}개 · 감사 로그 security-audit.jsonl";

    public event Action? BackRequested;
    public event Action? Saved;

    public void Refresh() => RefreshFromStore(_store.Settings.ActiveConnectionId, clearStatus: true);

    partial void OnSelectedConnectionChanged(LlmConnectionItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedConnection));
        if (_suppressSelection || value is null) return;
        if (_store.GetConnection(value.Id) is { } profile) LoadProfile(profile);
    }

    [RelayCommand]
    private void Back()
    {
        if (IsEditorOpen)
        {
            IsEditorOpen = false;
            Status = "";
            return;
        }
        BackRequested?.Invoke();
    }

    partial void OnIsEditorOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsConnectionList));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(BackLabel));
    }

    partial void OnSelectedReasoningEffortChanged(ReasoningEffortOption value) =>
        OnPropertyChanged(nameof(ReasoningEffortHelp));

    partial void OnAutoUpdateEnabledChanged(bool value)
    {
        if (_suppressAutoUpdateSave) return;
        _store.SaveAutoUpdateEnabled(value);
        UpdateStatus = value
            ? "다음 실행부터 GitHub Release를 확인하고 검증된 업데이트를 자동 설치합니다."
            : "자동 업데이트를 사용하지 않습니다.";
    }

    [RelayCommand]
    private void NewConnection()
    {
        _suppressSelection = true;
        SelectedConnection = null;
        _suppressSelection = false;
        ClearProfileFields();
        ConnectionName = Connections.Count == 0 ? "기본 연결" : $"새 연결 {Connections.Count + 1}";
        IsEditorOpen = true;
        Status = "새 연결 정보를 입력한 뒤 ‘저장 및 사용’을 누르세요.";
    }

    [RelayCommand]
    private void EditConnection()
    {
        if (SelectedConnection is null)
        {
            Status = "설정을 편집할 연결을 선택하세요.";
            return;
        }
        if (_store.GetConnection(SelectedConnection.Id) is { } profile) LoadProfile(profile);
        IsEditorOpen = true;
        Status = "";
    }

    [RelayCommand]
    private void ActivateConnection()
    {
        if (SelectedConnection is null)
        {
            Status = "사용할 저장된 연결을 선택하세요.";
            return;
        }
        try
        {
            var selectedId = SelectedConnection.Id;
            _store.ActivateConnection(selectedId);
            RefreshFromStore(selectedId, clearStatus: false);
            Status = $"{_store.ActiveConnection?.Name} 연결을 사용합니다.";
            Saved?.Invoke();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Status = exception.Message;
        }
    }

    [RelayCommand]
    private void DeleteConnection()
    {
        if (SelectedConnection is null)
        {
            Status = "삭제할 저장된 연결을 선택하세요.";
            return;
        }
        try
        {
            var name = SelectedConnection.Name;
            if (!_store.DeleteConnection(SelectedConnection.Id))
            {
                Status = "삭제할 연결을 찾지 못했습니다.";
                return;
            }
            RefreshFromStore(_store.Settings.ActiveConnectionId, clearStatus: false);
            Status = $"{name} 연결과 해당 API 키를 삭제했습니다.";
            Saved?.Invoke();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Status = exception.Message;
        }
    }

    [RelayCommand]
    private async Task BrowseWorkspaceAsync()
    {
        var path = await _pickWorkspace();
        if (!string.IsNullOrWhiteSpace(path)) Workspace = path;
    }

    [RelayCommand]
    private async Task AddGlobalDirectoryAsync()
    {
        var path = await _pickWorkspace();
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = FolderAccessPolicy.NormalizePath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (GlobalAllowedDirectories.Any(item => item.Path.Equals(normalized, comparison)))
        {
            Status = "이미 전체 접근 권한에 추가된 폴더입니다.";
            return;
        }
        GlobalAllowedDirectories.Add(new AllowedDirectoryItemViewModel(normalized, RemoveGlobalDirectory));
        Status = "전체 접근 폴더를 추가했습니다. 저장 및 사용을 눌러 적용하세요.";
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            var keyStored = _store.SaveLlmConnection(
                SelectedConnection?.Id,
                ConnectionName,
                Endpoint,
                Model,
                ApiKey,
                RemoveApiKey,
                ParseOptionalInt(MaxOutputTokens, "최대 출력 토큰"),
                ParseOptionalInt(ContextWindowTokens, "컨텍스트 길이"),
                SelectedMaxTokensField,
                ParseOptionalDouble(Temperature, "Temperature"),
                ParseOptionalDouble(TopP, "Top P"),
                ParseOptionalInt(RequestTimeout, "요청 타임아웃"),
                ParseOptionalDouble(FrequencyPenalty, "Frequency penalty"),
                ParseOptionalDouble(PresencePenalty, "Presence penalty"),
                ParseOptionalNullableInt(Seed, "Seed"),
                SelectedReasoningEffort.Value);

            var activeId = _store.Settings.ActiveConnectionId;
            RefreshFromStore(activeId, clearStatus: false);
            Status = keyStored
                ? "연결과 API 키를 저장하고 활성화했습니다."
                : "연결을 저장하고 활성화했습니다.";
            IsEditorOpen = false;
            Saved?.Invoke();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           DirectoryNotFoundException or IOException or UnauthorizedAccessException or OverflowException)
        {
            Status = exception.Message;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task CheckHarnessAsync()
    {
        Status = "C# Agent 하네스 연결을 확인하는 중…";
        try
        {
            var version = await _harness.GetVersionAsync();
            var active = _store.ActiveConnection;
            Status = active is null
                ? $"{version} 준비됨 · 활성 LLM 연결 없음"
                : $"{version} 준비됨 · {active.Name} / {active.Model} · API 키 {(_store.HasApiKey ? "저장됨" : "없음")}";
        }
        catch (Exception exception) when (exception is LlmUnavailableException or InvalidOperationException)
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
                : $"새 버전 {result.Release.Tag}을 사용할 수 있습니다. 자동 업데이트를 켜면 다음 실행 때 설치합니다.";
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or InvalidOperationException or TaskCanceledException)
        {
            UpdateStatus = $"업데이트 확인 실패: {exception.Message}";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private void RefreshFromStore(string? preferredConnectionId, bool clearStatus)
    {
        var settings = _store.Settings;
        _suppressAutoUpdateSave = true;
        AutoUpdateEnabled = settings.AutoUpdateEnabled;
        _suppressAutoUpdateSave = false;
        UpdateStatus = AutoUpdateEnabled
            ? "실행할 때 GitHub Release를 자동 확인합니다."
            : "자동 업데이트가 꺼져 있습니다.";
        Workspace = settings.WorkspacePath;
        GlobalAllowedDirectories.Clear();
        foreach (var directory in settings.GlobalAllowedDirectories)
            GlobalAllowedDirectories.Add(new AllowedDirectoryItemViewModel(directory, RemoveGlobalDirectory));
        GlobalAllowedNetworkEndpoints.Clear();
        foreach (var endpoint in settings.GlobalAllowedNetworkEndpoints)
            GlobalAllowedNetworkEndpoints.Add(new NetworkEndpointItemViewModel(endpoint, RemoveGlobalNetworkEndpoint));
        OnPropertyChanged(nameof(SecuritySummary));
        SelectedTheme = settings.Theme == "light" ? "라이트" : "다크";

        _suppressSelection = true;
        Connections.Clear();
        foreach (var connection in _store.LlmConnections)
        {
            Connections.Add(new LlmConnectionItemViewModel(
                connection.Id,
                connection.Name,
                connection.Model,
                connection.LlmEndpoint,
                connection.Id == settings.ActiveConnectionId));
        }
        SelectedConnection = Connections.FirstOrDefault(item => item.Id == preferredConnectionId)
                             ?? Connections.FirstOrDefault(item => item.IsActive)
                             ?? Connections.FirstOrDefault();
        _suppressSelection = false;
        OnPropertyChanged(nameof(ConnectionCountText));
        OnPropertyChanged(nameof(HasSelectedConnection));
        OnPropertyChanged(nameof(HasConnections));

        if (SelectedConnection is not null && _store.GetConnection(SelectedConnection.Id) is { } profile)
            LoadProfile(profile);
        else
        {
            ClearProfileFields();
            ConnectionName = "기본 연결";
        }
        if (clearStatus) Status = "";
    }

    private void LoadProfile(LlmConnectionProfile profile)
    {
        ConnectionName = profile.Name;
        Endpoint = profile.LlmEndpoint;
        Model = profile.Model;
        MaxOutputTokens = Optional(profile.MaxOutputTokens);
        ContextWindowTokens = Optional(profile.ContextWindowTokens);
        SelectedMaxTokensField = profile.MaxTokensField;
        Temperature = Optional(profile.Temperature);
        TopP = Optional(profile.TopP);
        FrequencyPenalty = Optional(profile.FrequencyPenalty);
        PresencePenalty = Optional(profile.PresencePenalty);
        Seed = profile.Seed?.ToString(CultureInfo.InvariantCulture) ?? "";
        RequestTimeout = Optional(profile.RequestTimeoutSeconds);
        SelectedReasoningEffort = ReasoningEfforts.FirstOrDefault(option =>
                                      option.Value.Equals(profile.ReasoningEffort, StringComparison.OrdinalIgnoreCase))
                                  ?? ReasoningEffortOption.Default;
        ApiKey = "";
        RemoveApiKey = false;
        UpdateApiKeyState(_store.HasApiKeyFor(profile.Id));
    }

    private void ClearProfileFields()
    {
        Endpoint = "";
        Model = "";
        ApiKey = "";
        RemoveApiKey = false;
        MaxOutputTokens = "";
        ContextWindowTokens = "";
        SelectedMaxTokensField = "max_tokens";
        Temperature = "";
        TopP = "";
        FrequencyPenalty = "";
        PresencePenalty = "";
        Seed = "";
        RequestTimeout = "";
        SelectedReasoningEffort = ReasoningEffortOption.Default;
        UpdateApiKeyState(false);
    }

    private void UpdateApiKeyState(bool stored)
    {
        ApiKeyState = stored
            ? "이 연결의 API 키가 저장됨 · 비워두면 유지하고 새 값을 입력하면 교체합니다."
            : "이 연결에 저장된 API 키가 없습니다. 인증이 없는 로컬 서버라면 비워두세요.";
        ApiKeyPlaceholder = stored ? "새 키를 입력할 때만 작성" : "필요한 경우 입력";
    }

    private void RemoveGlobalDirectory(AllowedDirectoryItemViewModel item)
    {
        GlobalAllowedDirectories.Remove(item);
        Status = "전체 접근 폴더 권한을 제거했습니다. 저장 및 사용을 눌러 적용하세요.";
    }

    private void RemoveGlobalNetworkEndpoint(NetworkEndpointItemViewModel item)
    {
        GlobalAllowedNetworkEndpoints.Remove(item);
        OnPropertyChanged(nameof(SecuritySummary));
        Status = "전체 네트워크 권한을 제거했습니다. 저장 및 사용을 눌러 적용하세요.";
    }

    private static int ParseOptionalInt(string? raw, string name)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var value) ||
            int.TryParse(raw.Trim(), NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value)) return value;
        throw new ArgumentException($"{name}에 정수를 입력하세요.");
    }

    private static double? ParseOptionalDouble(string? raw, string name)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
            double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return value;
        throw new ArgumentException($"{name}에 숫자를 입력하세요.");
    }

    private static int? ParseOptionalNullableInt(string? raw, string name)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ||
            int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return value;
        throw new ArgumentException($"{name}에 정수를 입력하세요.");
    }

    private static string Optional(int value) => value == 0 ? "" : value.ToString(CultureInfo.InvariantCulture);
    private static string Optional(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
}

public sealed record ReasoningEffortOption(string Value, string Label, string Help)
{
    public static ReasoningEffortOption Default { get; } = new(
        "default", "서버 기본값", "reasoning_effort를 보내지 않고 연결된 API와 모델의 기본 동작을 사용합니다.");

    public static IReadOnlyList<ReasoningEffortOption> All { get; } =
    [
        Default,
        new("none", "끔", "추론 제어를 끕니다. 지원 모델에서는 가장 빠르지만 복잡한 작업 품질이 낮아질 수 있습니다."),
        new("low", "낮음", "짧게 판단합니다. 간단한 대화와 빠른 응답에 적합합니다."),
        new("medium", "보통", "속도와 정확도의 균형을 맞춥니다."),
        new("high", "높음", "복잡한 코딩·분석에 더 많은 추론을 사용해 응답이 느려질 수 있습니다."),
        new("max", "최대", "지원 모델의 가장 높은 추론 수준을 요청합니다. 지연과 토큰 사용량이 가장 큽니다.")
    ];

    public override string ToString() => Label;
}

public sealed record LlmConnectionItemViewModel(
    string Id,
    string Name,
    string Model,
    string Endpoint,
    bool IsActive)
{
    public string DisplayName => IsActive ? $"{Name}  ·  사용 중" : Name;
    public string Summary => $"{Model}  ·  {Endpoint}";
}

public sealed class AllowedDirectoryItemViewModel
{
    public AllowedDirectoryItemViewModel(string path, Action<AllowedDirectoryItemViewModel> remove)
    {
        Path = path;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Path { get; }
    public IRelayCommand RemoveCommand { get; }
}

public sealed class NetworkEndpointItemViewModel
{
    public NetworkEndpointItemViewModel(string endpoint, Action<NetworkEndpointItemViewModel> remove)
    {
        Endpoint = endpoint;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Endpoint { get; }
    public IRelayCommand RemoveCommand { get; }
}
