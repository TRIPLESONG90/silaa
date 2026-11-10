using System.Text;
using System.Text.Json;
using silaa.Core;

namespace silaa.Services;

public sealed class LocalStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _settingsGate = new();

    public LocalStore(string? root = null)
    {
        Root = root ?? AppPaths.DataRoot;
        Directory.CreateDirectory(Root);
        Settings = LoadSettings();
        ApiCredentials = new ApiCredentials(Root);
        InitializeConnectionProfiles();
        Vault = new VaultStore(Root);
        AppConnections = new ConnectedAppStore(Root, Vault);
        SecurityAudit = new SecurityAuditStore(Root);
        Resources = new ProjectResourceStore(Root);
        Memory = new MemoryStore(Root);
        Calendar = new CalendarEventStore(Root);
    }

    public string Root { get; }
    public AppSettings Settings { get; private set; }
    internal ApiCredentials ApiCredentials { get; }
    internal VaultStore Vault { get; }
    internal ConnectedAppStore AppConnections { get; }
    internal SecurityAuditStore SecurityAudit { get; }
    internal ProjectResourceStore Resources { get; }
    internal MemoryStore Memory { get; }
    internal CalendarEventStore Calendar { get; }

    public IReadOnlyList<LlmConnectionProfile> LlmConnections
    {
        get
        {
            lock (_settingsGate) return Settings.LlmConnections.Select(CloneProfile).ToList();
        }
    }

    public LlmConnectionProfile? ActiveConnection
    {
        get
        {
            lock (_settingsGate)
            {
                var profile = FindActiveConnection();
                return profile is null ? null : CloneProfile(profile);
            }
        }
    }

    public bool IsConfigured => ActiveConnection is { LlmEndpoint.Length: > 0, Model.Length: > 0 };
    public bool HasApiKey => ActiveConnection is { } profile && ApiCredentials.HasKey(profile.Id, profile.LlmEndpoint);

    public bool HasApiKeyFor(string connectionId)
    {
        var profile = GetConnection(connectionId);
        return profile is not null && ApiCredentials.HasKey(profile.Id, profile.LlmEndpoint);
    }

    public void SaveAutoUpdateEnabled(bool enabled)
    {
        lock (_settingsGate)
        {
            Settings.AutoUpdateEnabled = enabled;
            Save("settings.json", Settings);
        }
    }

    public string? GetApiKey(string? connectionId = null)
    {
        var profile = connectionId is null ? ActiveConnection : GetConnection(connectionId);
        return profile is null ? null : ApiCredentials.Get(profile.Id, profile.LlmEndpoint);
    }

    public LlmConnectionProfile? GetConnection(string connectionId)
    {
        lock (_settingsGate)
        {
            var profile = Settings.LlmConnections.FirstOrDefault(item => item.Id == connectionId);
            return profile is null ? null : CloneProfile(profile);
        }
    }

    public bool SaveSettings(
        string endpoint, string model, string workspace, string? apiKey = null, bool removeApiKey = false,
        int maxOutputTokens = 0, int contextWindowTokens = 0, string maxTokensField = "max_tokens",
        double? temperature = null, double? topP = null, int requestTimeoutSeconds = 0,
        double? frequencyPenalty = null, double? presencePenalty = null, int? seed = null,
        string? theme = null, string? reasoningEffort = null,
        IEnumerable<string>? allowedDirectories = null)
    {
        var active = ActiveConnection;
        return SaveConnection(
            active?.Id,
            active?.Name ?? "기본 연결",
            endpoint,
            model,
            workspace,
            apiKey,
            removeApiKey,
            maxOutputTokens,
            contextWindowTokens,
            maxTokensField,
            temperature,
            topP,
            requestTimeoutSeconds,
            frequencyPenalty,
            presencePenalty,
            seed,
            theme,
            reasoningEffort,
            allowedDirectories);
    }

    public bool SaveConnection(
        string? connectionId, string name, string endpoint, string model, string workspace,
        string? apiKey = null, bool removeApiKey = false,
        int maxOutputTokens = 0, int contextWindowTokens = 0, string maxTokensField = "max_tokens",
        double? temperature = null, double? topP = null, int requestTimeoutSeconds = 0,
        double? frequencyPenalty = null, double? presencePenalty = null, int? seed = null,
        string? theme = null, string? reasoningEffort = null,
        IEnumerable<string>? allowedDirectories = null)
    {
        var normalizedEndpoint = EndpointGuard.Parse(endpoint).AbsoluteUri;
        var normalizedModel = EndpointGuard.ValidateModel(model);
        var normalizedName = NormalizeConnectionName(name, normalizedModel);
        var normalizedWorkspace = EndpointGuard.ValidateWorkspace(workspace);
        var normalizedAllowedDirectories = FolderAccessPolicy.Normalize(
            allowedDirectories ?? Settings.GlobalAllowedDirectories,
            normalizedWorkspace,
            requireExisting: true);
        var normalizedMaxOutput = ValidateOptionalInt(maxOutputTokens, 1, 1_000_000, "최대 출력 토큰");
        var normalizedContext = ValidateOptionalInt(contextWindowTokens, 1, 4_000_000, "컨텍스트 길이");
        if (normalizedMaxOutput > 0 && normalizedContext > 0 && normalizedMaxOutput > normalizedContext)
            throw new ArgumentException("최대 출력 토큰은 컨텍스트 길이보다 클 수 없습니다.");
        var normalizedField = maxTokensField is "max_tokens" or "max_completion_tokens"
            ? maxTokensField : throw new ArgumentException("올바른 최대 토큰 필드를 선택하세요.");
        var normalizedTemperature = ValidateOptionalDouble(temperature, 0, 2, "Temperature");
        var normalizedTopP = ValidateOptionalDouble(topP, 0, 1, "Top P");
        var normalizedFrequencyPenalty = ValidateOptionalDouble(frequencyPenalty, -2, 2, "Frequency penalty");
        var normalizedPresencePenalty = ValidateOptionalDouble(presencePenalty, -2, 2, "Presence penalty");
        var normalizedTimeout = ValidateOptionalInt(requestTimeoutSeconds, 1, 3_600, "요청 타임아웃");

        lock (_settingsGate)
        {
            var normalizedTheme = NormalizeTheme(theme ?? Settings.Theme);
            var id = string.IsNullOrWhiteSpace(connectionId)
                ? $"llm_{Guid.NewGuid():N}"
                : ValidateConnectionId(connectionId);
            var existing = Settings.LlmConnections.FirstOrDefault(item => item.Id == id);
            if (existing is null && !string.IsNullOrWhiteSpace(connectionId))
                throw new InvalidOperationException("저장할 LLM 연결을 찾지 못했습니다.");
            var normalizedReasoning = NormalizeReasoningEffort(reasoningEffort ?? existing?.ReasoningEffort);
            var previousEndpoint = existing?.LlmEndpoint;
            var profile = new LlmConnectionProfile
            {
                Id = id,
                Name = normalizedName,
                LlmEndpoint = normalizedEndpoint,
                Model = normalizedModel,
                MaxOutputTokens = normalizedMaxOutput,
                ContextWindowTokens = normalizedContext,
                MaxTokensField = normalizedField,
                Temperature = normalizedTemperature,
                TopP = normalizedTopP,
                FrequencyPenalty = normalizedFrequencyPenalty,
                PresencePenalty = normalizedPresencePenalty,
                Seed = seed,
                RequestTimeoutSeconds = normalizedTimeout,
                ReasoningEffort = normalizedReasoning
            };

            if (existing is null) Settings.LlmConnections.Add(profile);
            else Settings.LlmConnections[Settings.LlmConnections.IndexOf(existing)] = profile;
            Settings.ActiveConnectionId = id;
            Settings.WorkspacePath = normalizedWorkspace;
            Settings.GlobalAllowedDirectories = normalizedAllowedDirectories;
            Settings.Theme = normalizedTheme;
            SyncLegacyFields(profile);
            Save("settings.json", Settings);

            if (!string.IsNullOrWhiteSpace(previousEndpoint) &&
                !string.Equals(previousEndpoint, normalizedEndpoint, StringComparison.OrdinalIgnoreCase))
                ApiCredentials.Delete(id, previousEndpoint);
            if (removeApiKey) ApiCredentials.Delete(id, normalizedEndpoint);
            else if (!string.IsNullOrWhiteSpace(apiKey)) ApiCredentials.Save(id, normalizedEndpoint, apiKey);
            return ApiCredentials.HasKey(id, normalizedEndpoint);
        }
    }

    /// <summary>
    /// Saves only an LLM connection profile. Workspace, folder permissions,
    /// network permissions, and appearance settings are intentionally untouched.
    /// </summary>
    public bool SaveLlmConnection(
        string? connectionId, string name, string endpoint, string model,
        string? apiKey = null, bool removeApiKey = false,
        int maxOutputTokens = 0, int contextWindowTokens = 0, string maxTokensField = "max_tokens",
        double? temperature = null, double? topP = null, int requestTimeoutSeconds = 0,
        double? frequencyPenalty = null, double? presencePenalty = null, int? seed = null,
        string? reasoningEffort = null)
    {
        var normalizedEndpoint = EndpointGuard.Parse(endpoint).AbsoluteUri;
        var normalizedModel = EndpointGuard.ValidateModel(model);
        var normalizedName = NormalizeConnectionName(name, normalizedModel);
        var normalizedMaxOutput = ValidateOptionalInt(maxOutputTokens, 1, 1_000_000, "최대 출력 토큰");
        var normalizedContext = ValidateOptionalInt(contextWindowTokens, 1, 4_000_000, "컨텍스트 길이");
        if (normalizedMaxOutput > 0 && normalizedContext > 0 && normalizedMaxOutput > normalizedContext)
            throw new ArgumentException("최대 출력 토큰은 컨텍스트 길이보다 클 수 없습니다.");
        var normalizedField = maxTokensField is "max_tokens" or "max_completion_tokens"
            ? maxTokensField : throw new ArgumentException("올바른 최대 토큰 필드를 선택하세요.");
        var normalizedTemperature = ValidateOptionalDouble(temperature, 0, 2, "Temperature");
        var normalizedTopP = ValidateOptionalDouble(topP, 0, 1, "Top P");
        var normalizedFrequencyPenalty = ValidateOptionalDouble(frequencyPenalty, -2, 2, "Frequency penalty");
        var normalizedPresencePenalty = ValidateOptionalDouble(presencePenalty, -2, 2, "Presence penalty");
        var normalizedTimeout = ValidateOptionalInt(requestTimeoutSeconds, 1, 3_600, "요청 타임아웃");

        lock (_settingsGate)
        {
            var id = string.IsNullOrWhiteSpace(connectionId)
                ? $"llm_{Guid.NewGuid():N}"
                : ValidateConnectionId(connectionId);
            var existing = Settings.LlmConnections.FirstOrDefault(item => item.Id == id);
            if (existing is null && !string.IsNullOrWhiteSpace(connectionId))
                throw new InvalidOperationException("저장할 LLM 연결을 찾지 못했습니다.");
            var normalizedReasoning = NormalizeReasoningEffort(reasoningEffort ?? existing?.ReasoningEffort);
            var previousEndpoint = existing?.LlmEndpoint;
            var profile = new LlmConnectionProfile
            {
                Id = id,
                Name = normalizedName,
                LlmEndpoint = normalizedEndpoint,
                Model = normalizedModel,
                MaxOutputTokens = normalizedMaxOutput,
                ContextWindowTokens = normalizedContext,
                MaxTokensField = normalizedField,
                Temperature = normalizedTemperature,
                TopP = normalizedTopP,
                FrequencyPenalty = normalizedFrequencyPenalty,
                PresencePenalty = normalizedPresencePenalty,
                Seed = seed,
                RequestTimeoutSeconds = normalizedTimeout,
                ReasoningEffort = normalizedReasoning
            };

            if (existing is null) Settings.LlmConnections.Add(profile);
            else Settings.LlmConnections[Settings.LlmConnections.IndexOf(existing)] = profile;
            Settings.ActiveConnectionId = id;
            SyncLegacyFields(profile);
            Save("settings.json", Settings);

            if (!string.IsNullOrWhiteSpace(previousEndpoint) &&
                !string.Equals(previousEndpoint, normalizedEndpoint, StringComparison.OrdinalIgnoreCase))
                ApiCredentials.Delete(id, previousEndpoint);
            if (removeApiKey) ApiCredentials.Delete(id, normalizedEndpoint);
            else if (!string.IsNullOrWhiteSpace(apiKey)) ApiCredentials.Save(id, normalizedEndpoint, apiKey);
            return ApiCredentials.HasKey(id, normalizedEndpoint);
        }
    }

    public void SaveWorkspaceSettings(
        string workspace,
        string? theme,
        IEnumerable<string> allowedDirectories,
        IEnumerable<string> allowedNetworkEndpoints)
    {
        var normalizedWorkspace = EndpointGuard.ValidateWorkspace(workspace);
        var normalizedDirectories = FolderAccessPolicy.Normalize(
            allowedDirectories, normalizedWorkspace, requireExisting: true);
        var normalizedEndpoints = NetworkAccessPolicy.Normalize(allowedNetworkEndpoints).ToList();
        var normalizedTheme = NormalizeTheme(theme ?? Settings.Theme);
        lock (_settingsGate)
        {
            Settings.WorkspacePath = normalizedWorkspace;
            Settings.GlobalAllowedDirectories = normalizedDirectories;
            Settings.GlobalAllowedNetworkEndpoints = normalizedEndpoints;
            Settings.Theme = normalizedTheme;
            Save("settings.json", Settings);
        }
    }

    public void ActivateConnection(string connectionId)
    {
        lock (_settingsGate)
        {
            var id = ValidateConnectionId(connectionId);
            var profile = Settings.LlmConnections.FirstOrDefault(item => item.Id == id)
                ?? throw new InvalidOperationException("LLM 연결을 찾지 못했습니다.");
            Settings.ActiveConnectionId = id;
            SyncLegacyFields(profile);
            Save("settings.json", Settings);
        }
    }

    public LlmConnectionProfile SetActiveReasoningEffort(string reasoningEffort)
    {
        lock (_settingsGate)
        {
            var profile = FindActiveConnection()
                ?? throw new InvalidOperationException("추론 수준을 변경할 활성 LLM 연결이 없습니다.");
            profile.ReasoningEffort = NormalizeReasoningEffort(reasoningEffort);
            SyncLegacyFields(profile);
            Save("settings.json", Settings);
            return CloneProfile(profile);
        }
    }

    public IReadOnlyList<string> SetGlobalAllowedDirectories(IEnumerable<string> directories)
    {
        lock (_settingsGate)
        {
            Settings.GlobalAllowedDirectories = FolderAccessPolicy.Normalize(
                directories,
                Settings.WorkspacePath,
                requireExisting: true);
            Save("settings.json", Settings);
            return Settings.GlobalAllowedDirectories.ToList();
        }
    }

    public IReadOnlyList<string> SetGlobalAllowedNetworkEndpoints(IEnumerable<string> endpoints)
    {
        lock (_settingsGate)
        {
            Settings.GlobalAllowedNetworkEndpoints = NetworkAccessPolicy.Normalize(endpoints).ToList();
            Save("settings.json", Settings);
            return Settings.GlobalAllowedNetworkEndpoints.ToList();
        }
    }

    public bool DeleteConnection(string connectionId)
    {
        lock (_settingsGate)
        {
            var id = ValidateConnectionId(connectionId);
            var profile = Settings.LlmConnections.FirstOrDefault(item => item.Id == id);
            if (profile is null) return false;
            ApiCredentials.Delete(profile.Id, profile.LlmEndpoint);
            Settings.LlmConnections.Remove(profile);
            if (Settings.ActiveConnectionId == id)
                Settings.ActiveConnectionId = Settings.LlmConnections.FirstOrDefault()?.Id ?? "";
            SyncLegacyFields(FindActiveConnection());
            Save("settings.json", Settings);
            return true;
        }
    }

    internal LlmRuntimeSettings GetActiveRuntimeSettings()
    {
        lock (_settingsGate)
        {
            var profile = FindActiveConnection()
                ?? throw new InvalidOperationException("먼저 LLM 연결을 설정하세요.");
            var snapshot = CloneSettings(Settings);
            return new LlmRuntimeSettings(snapshot, ApiCredentials.Get(profile.Id, profile.LlmEndpoint));
        }
    }

    private void InitializeConnectionProfiles()
    {
        lock (_settingsGate)
        {
            Settings.LlmConnections ??= [];
            var changed = false;
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in Settings.LlmConnections)
            {
                if (!IsValidConnectionId(profile.Id) || !usedIds.Add(profile.Id))
                {
                    profile.Id = $"llm_{Guid.NewGuid():N}";
                    usedIds.Add(profile.Id);
                    changed = true;
                }
                if (string.IsNullOrWhiteSpace(profile.Name))
                {
                    profile.Name = string.IsNullOrWhiteSpace(profile.Model) ? "LLM 연결" : profile.Model;
                    changed = true;
                }
                if (profile.MaxTokensField is not ("max_tokens" or "max_completion_tokens"))
                {
                    profile.MaxTokensField = "max_tokens";
                    changed = true;
                }
                try { profile.ReasoningEffort = NormalizeReasoningEffort(profile.ReasoningEffort); }
                catch (ArgumentException) { profile.ReasoningEffort = ""; changed = true; }
            }

            if (Settings.LlmConnections.Count == 0 &&
                !string.IsNullOrWhiteSpace(Settings.LlmEndpoint) && !string.IsNullOrWhiteSpace(Settings.Model))
            {
                var migrated = ProfileFromLegacy(Settings);
                Settings.LlmConnections.Add(migrated);
                Settings.ActiveConnectionId = migrated.Id;
                changed = true;
            }

            if (Settings.LlmConnections.Count > 0 &&
                Settings.LlmConnections.All(item => item.Id != Settings.ActiveConnectionId))
            {
                Settings.ActiveConnectionId = Settings.LlmConnections[0].Id;
                changed = true;
            }

            var active = FindActiveConnection();
            SyncLegacyFields(active);
            if (active is not null)
            {
                try { ApiCredentials.MigrateLegacy(active.Id, active.LlmEndpoint); }
                catch (ArgumentException) { }
            }
            if (changed) Save("settings.json", Settings);
        }
    }

    private AppSettings LoadSettings()
    {
        try
        {
            var path = Path.Combine(Root, "settings.json");
            var settings = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings()
                : new AppSettings();
            if (!Directory.Exists(settings.WorkspacePath)) settings.WorkspacePath = Environment.CurrentDirectory;
            settings.GlobalAllowedDirectories = FolderAccessPolicy.Normalize(
                settings.GlobalAllowedDirectories,
                settings.WorkspacePath,
                requireExisting: false);
            settings.GlobalAllowedNetworkEndpoints = NetworkAccessPolicy.Normalize(
                settings.GlobalAllowedNetworkEndpoints).ToList();
            if (settings.MaxTokensField is not ("max_tokens" or "max_completion_tokens"))
                settings.MaxTokensField = "max_tokens";
            settings.Theme = settings.Theme is "dark" or "light" ? settings.Theme : "dark";
            try { settings.ReasoningEffort = NormalizeReasoningEffort(settings.ReasoningEffort); }
            catch (ArgumentException) { settings.ReasoningEffort = ""; }
            settings.LlmConnections ??= [];
            return settings;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    private LlmConnectionProfile? FindActiveConnection() =>
        Settings.LlmConnections.FirstOrDefault(item => item.Id == Settings.ActiveConnectionId);

    private void SyncLegacyFields(LlmConnectionProfile? profile)
    {
        Settings.LlmEndpoint = profile?.LlmEndpoint ?? "";
        Settings.Model = profile?.Model ?? "";
        Settings.MaxOutputTokens = profile?.MaxOutputTokens ?? 0;
        Settings.ContextWindowTokens = profile?.ContextWindowTokens ?? 0;
        Settings.MaxTokensField = profile?.MaxTokensField ?? "max_tokens";
        Settings.Temperature = profile?.Temperature;
        Settings.TopP = profile?.TopP;
        Settings.FrequencyPenalty = profile?.FrequencyPenalty;
        Settings.PresencePenalty = profile?.PresencePenalty;
        Settings.Seed = profile?.Seed;
        Settings.RequestTimeoutSeconds = profile?.RequestTimeoutSeconds ?? 0;
        Settings.ReasoningEffort = profile?.ReasoningEffort ?? "";
    }

    private static LlmConnectionProfile ProfileFromLegacy(AppSettings settings) => new()
    {
        Id = $"llm_{Guid.NewGuid():N}",
        Name = "기본 연결",
        LlmEndpoint = settings.LlmEndpoint,
        Model = settings.Model,
        MaxOutputTokens = settings.MaxOutputTokens,
        ContextWindowTokens = settings.ContextWindowTokens,
        MaxTokensField = settings.MaxTokensField,
        Temperature = settings.Temperature,
        TopP = settings.TopP,
        FrequencyPenalty = settings.FrequencyPenalty,
        PresencePenalty = settings.PresencePenalty,
        Seed = settings.Seed,
        RequestTimeoutSeconds = settings.RequestTimeoutSeconds,
        ReasoningEffort = settings.ReasoningEffort
    };

    private static LlmConnectionProfile CloneProfile(LlmConnectionProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        LlmEndpoint = profile.LlmEndpoint,
        Model = profile.Model,
        MaxOutputTokens = profile.MaxOutputTokens,
        ContextWindowTokens = profile.ContextWindowTokens,
        MaxTokensField = profile.MaxTokensField,
        Temperature = profile.Temperature,
        TopP = profile.TopP,
        FrequencyPenalty = profile.FrequencyPenalty,
        PresencePenalty = profile.PresencePenalty,
        Seed = profile.Seed,
        RequestTimeoutSeconds = profile.RequestTimeoutSeconds,
        ReasoningEffort = profile.ReasoningEffort
    };

    private static AppSettings CloneSettings(AppSettings settings) => new()
    {
        ActiveConnectionId = settings.ActiveConnectionId,
        LlmConnections = settings.LlmConnections.Select(CloneProfile).ToList(),
        LlmEndpoint = settings.LlmEndpoint,
        Model = settings.Model,
        WorkspacePath = settings.WorkspacePath,
        GlobalAllowedDirectories = settings.GlobalAllowedDirectories.ToList(),
        GlobalAllowedNetworkEndpoints = settings.GlobalAllowedNetworkEndpoints.ToList(),
        Theme = settings.Theme,
        MaxOutputTokens = settings.MaxOutputTokens,
        ContextWindowTokens = settings.ContextWindowTokens,
        MaxTokensField = settings.MaxTokensField,
        Temperature = settings.Temperature,
        TopP = settings.TopP,
        FrequencyPenalty = settings.FrequencyPenalty,
        PresencePenalty = settings.PresencePenalty,
        Seed = settings.Seed,
        RequestTimeoutSeconds = settings.RequestTimeoutSeconds,
        ReasoningEffort = settings.ReasoningEffort
    };

    private static string NormalizeConnectionName(string name, string model)
    {
        var normalized = name.Trim();
        if (normalized.Length == 0) normalized = model;
        if (normalized.Length > 80 || normalized.Any(char.IsControl))
            throw new ArgumentException("연결 이름은 80자 이내로 입력하세요.");
        return normalized;
    }

    private static string ValidateConnectionId(string connectionId)
    {
        var normalized = connectionId.Trim();
        if (!IsValidConnectionId(normalized)) throw new ArgumentException("올바르지 않은 LLM 연결 ID입니다.");
        return normalized;
    }

    private static bool IsValidConnectionId(string value) =>
        value.Length is > 0 and <= 128 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static int ValidateOptionalInt(int value, int minimum, int maximum, string name)
    {
        if (value == 0) return 0;
        if (value < minimum || value > maximum)
            throw new ArgumentException($"{name}은(는) {minimum:N0}~{maximum:N0} 범위로 입력하세요.");
        return value;
    }

    private static double? ValidateOptionalDouble(double? value, double minimum, double maximum, string name)
    {
        if (value is null) return null;
        if (!double.IsFinite(value.Value) || value.Value < minimum || value.Value > maximum)
            throw new ArgumentException($"{name}은(는) {minimum:0.##}~{maximum:0.##} 범위로 입력하세요.");
        return value;
    }

    private static string NormalizeReasoningEffort(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        if (normalized is "" or "default") return "";
        if (normalized.Length <= 32 && normalized.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) return normalized;
        throw new ArgumentException("추론 수준은 low, medium, high 같은 영문 이름으로 입력하세요.");
    }

    private static string NormalizeTheme(string value) => value.Trim().ToLowerInvariant() switch
    {
        "dark" => "dark",
        "light" => "light",
        _ => throw new ArgumentException("테마는 dark 또는 light 중 하나여야 합니다.")
    };

    private void Save<T>(string name, T value)
    {
        var path = Path.Combine(Root, name);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}

internal sealed record LlmRuntimeSettings(AppSettings Settings, string? ApiKey);
