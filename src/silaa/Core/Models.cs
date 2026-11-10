namespace silaa.Core;

public sealed class AppSettings
{
    public string ActiveConnectionId { get; set; } = "";
    public List<LlmConnectionProfile> LlmConnections { get; set; } = [];
    public string LlmEndpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public string WorkspacePath { get; set; } = Environment.CurrentDirectory;
    public List<string> GlobalAllowedDirectories { get; set; } = [];
    public List<string> GlobalAllowedNetworkEndpoints { get; set; } = [];
    public string Theme { get; set; } = "dark";
    public int MaxOutputTokens { get; set; }
    public int ContextWindowTokens { get; set; }
    public string MaxTokensField { get; set; } = "max_tokens";
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public double? FrequencyPenalty { get; set; }
    public double? PresencePenalty { get; set; }
    public int? Seed { get; set; }
    public int RequestTimeoutSeconds { get; set; }
    public string ReasoningEffort { get; set; } = "";
    public bool AutoUpdateEnabled { get; set; }
}

public sealed class LlmConnectionProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string LlmEndpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public int MaxOutputTokens { get; set; }
    public int ContextWindowTokens { get; set; }
    public string MaxTokensField { get; set; } = "max_tokens";
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public double? FrequencyPenalty { get; set; }
    public double? PresencePenalty { get; set; }
    public int? Seed { get; set; }
    public int RequestTimeoutSeconds { get; set; }
    public string ReasoningEffort { get; set; } = "";
}

public sealed class ConnectedAppInfo
{
    public string Id { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Name { get; init; } = "";
    public string Account { get; init; } = "";
    public string RootPath { get; init; } = "";
    public bool AgentEnabled { get; init; } = true;
    public bool ReadOnly { get; init; } = true;
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class ChatSession
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "새 세션";
    public string Directory { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? ProjectId { get; init; }
    public bool IsResponding { get; init; }
    public IReadOnlyList<string> AllowedDirectories { get; init; } = [];
    public IReadOnlyList<string> AllowedNetworkEndpoints { get; init; } = [];
}

public sealed class ProjectInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Directory { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    public string Summary { get; init; } = "";
    public int SpecificityScore { get; init; }
    public IReadOnlyList<ProjectDiscoveryEntry> DiscoveryHistory { get; init; } = [];
    public IReadOnlyList<string> AllowedDirectories { get; init; } = [];
    public IReadOnlyList<string> AllowedNetworkEndpoints { get; init; } = [];
    public ProjectDeliveryProfile Delivery { get; init; } = new();
}

public sealed class ProjectDeliveryProfile
{
    public string Kind { get; init; } = "other";
    public string SkillName { get; init; } = "";
    public int AppPort { get; init; }
    public int InternalPort { get; init; }
    public bool IsWebApp => Kind.Equals("webapp", StringComparison.OrdinalIgnoreCase);
}

public sealed class ProjectDiscoveryEntry
{
    public string Question { get; init; } = "";
    public string Answer { get; init; } = "";
    public int SpecificityScore { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class ProjectInputRequest
{
    public string RequestId { get; init; } = "";
    public string Title { get; init; } = "프로젝트 구체화";
    public string Question { get; init; } = "";
    public IReadOnlyList<string> Options { get; init; } = [];
    public string Placeholder { get; init; } = "직접 입력할 수도 있습니다.";
    public int SpecificityScore { get; init; }
    public bool ShowProgress { get; init; } = true;
}

public sealed class FolderAccessRequest
{
    public string RequestId { get; init; } = "";
    public string Path { get; init; } = "";
    public string Reason { get; init; } = "";
    public bool CanGrantProject { get; init; }
}

public sealed class NetworkAccessRequest
{
    public string RequestId { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Reason { get; init; } = "";
    public bool CanGrantProject { get; init; }
    public bool PersistentOnly { get; init; }
}

public sealed class VaultSecretRequest
{
    public string RequestId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Reason { get; init; } = "";
}

public sealed class SecurityAuditRecord
{
    public DateTimeOffset Timestamp { get; init; }
    public string SessionId { get; init; } = "";
    public string Action { get; init; } = "";
    public string Decision { get; init; } = "";
    public string Resource { get; init; } = "";
    public string Reason { get; init; } = "";
}

public sealed class VaultEntryInfo
{
    public string Name { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class ProjectResourceInfo
{
    public string Id { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string Type { get; init; } = "note";
    public string Title { get; init; } = "";
    public string Source { get; init; } = "";
    public string Content { get; init; } = "";
    public string Language { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class MemoryEntry
{
    public string Id { get; init; } = "";
    public string? ProjectId { get; init; }
    public string Category { get; init; } = "fact";
    public string Content { get; init; } = "";
    public string Source { get; init; } = "user";
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class ChatMessage
{
    public string Role { get; init; } = "assistant";
    public string Text { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record ChatRunEvent(
    string Kind,
    string Text,
    string? SessionId = null,
    string? ActivityId = null);

public sealed record ChatRunResult(string SessionId, string Answer);

public sealed class BackgroundTaskInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Instruction { get; set; } = "";
    public string Kind { get; set; } = "agent";
    public string Status { get; set; } = "queued";
    public string? ProjectId { get; set; }
    public string? ScheduleId { get; set; }
    public string? SessionId { get; set; }
    public string ResultPreview { get; set; } = "";
    public string Error { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class ScheduledJobInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string CronExpression { get; set; } = "";
    public string Instruction { get; set; } = "";
    public string? ProjectId { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public string? LastTaskId { get; set; }
    public string LastStatus { get; set; } = "대기 중";
}

/// <summary>
/// A real-world calendar entry. Calendar events never imply Agent execution or recurrence.
/// Optional reminders and preparation tasks are created separately, only after user consent.
/// </summary>
public sealed class CalendarEventInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public bool AllDay { get; set; }
    public string Location { get; set; } = "";
    public string Notes { get; set; } = "";
    public string? ReminderTaskId { get; set; }
    public string? PreparationTaskId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
