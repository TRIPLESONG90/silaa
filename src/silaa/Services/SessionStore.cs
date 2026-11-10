using System.Text;
using System.Text.Json;
using silaa.Core;

namespace silaa.Services;

internal sealed class SessionStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;

    public SessionStore(string root) => _path = Path.Combine(root, "harness-sessions.json");

    public IReadOnlyList<ChatSession> List(string workspace, string? projectId = null, bool generalOnly = false)
    {
        lock (_gate)
            return Read().Where(item => PathsEqual(item.Directory, workspace))
                .Where(item => !item.IsDraft)
                .Where(item => projectId is null ? !generalOnly || item.ProjectId is null : item.ProjectId == projectId)
                .OrderByDescending(item => item.UpdatedAt)
                .Select(item => item.ToSummary()).ToList();
    }

    public IReadOnlyList<ChatMessage> Messages(string sessionId)
    {
        ValidateId(sessionId);
        lock (_gate)
            return Read().FirstOrDefault(item => item.Id == sessionId)?.Messages
                .OrderBy(item => item.CreatedAt).ToList() ?? [];
    }

    public ChatSession? Get(string sessionId)
    {
        ValidateId(sessionId);
        lock (_gate) return Read().FirstOrDefault(item => item.Id == sessionId)?.ToSummary();
    }

    public string Create(string title, string workspace, string? projectId = null, bool isDraft = false)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new StoredSession
        {
            Id = $"ses_{Guid.NewGuid():N}",
            Title = string.IsNullOrWhiteSpace(title) ? "새 세션" : title.Trim(),
            Directory = Path.GetFullPath(workspace),
            CreatedAt = now,
            UpdatedAt = now,
            ProjectId = projectId,
            IsDraft = isDraft
        };
        lock (_gate)
        {
            var sessions = Read();
            sessions.Add(session);
            Write(sessions);
        }
        return session.Id;
    }

    public string CreateProjectSurveySession(ProjectInfo project, string workspace)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(project.Id))
            throw new ArgumentException("프로젝트 ID가 필요합니다.", nameof(project));

        var now = DateTimeOffset.UtcNow;
        var cursor = project.DiscoveryHistory.Count > 0
            ? project.DiscoveryHistory.Min(entry => entry.CreatedAt)
            : now;
        var messages = new List<ChatMessage>();
        foreach (var entry in project.DiscoveryHistory.OrderBy(entry => entry.CreatedAt))
        {
            messages.Add(new ChatMessage
            {
                Role = "assistant", Text = entry.Question.Trim(), CreatedAt = cursor
            });
            cursor = cursor.AddTicks(1);
            messages.Add(new ChatMessage
            {
                Role = "user", Text = entry.Answer.Trim(), CreatedAt = cursor
            });
            cursor = cursor.AddTicks(1);
        }
        messages.Add(new ChatMessage
        {
            Role = "assistant",
            Text = $"프로젝트가 생성되었습니다.\n\n## {project.Name}\n\n{project.Summary}\n\n구체화 {project.SpecificityScore}%",
            CreatedAt = cursor > now ? cursor : now
        });

        var session = new StoredSession
        {
            Id = $"ses_{Guid.NewGuid():N}",
            Title = $"{project.Name} 기획 설문",
            Directory = Path.GetFullPath(workspace),
            CreatedAt = messages.Count > 0 ? messages[0].CreatedAt : now,
            UpdatedAt = messages[^1].CreatedAt,
            ProjectId = project.Id,
            Messages = messages
        };
        lock (_gate)
        {
            var sessions = Read();
            sessions.Add(session);
            Write(sessions);
        }
        return session.Id;
    }

    public void Append(string sessionId, ChatMessage message)
    {
        ValidateId(sessionId);
        lock (_gate)
        {
            var sessions = Read();
            var session = sessions.FirstOrDefault(item => item.Id == sessionId)
                ?? throw new InvalidOperationException("세션을 찾지 못했습니다.");
            session.Messages.Add(message);
            session.UpdatedAt = message.CreatedAt;
            Write(sessions);
        }
    }

    public void MoveToProject(string sessionId, string projectId)
    {
        ValidateId(sessionId);
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("올바르지 않은 프로젝트 ID입니다.", nameof(projectId));
        lock (_gate)
        {
            var sessions = Read();
            var session = sessions.FirstOrDefault(item => item.Id == sessionId)
                ?? throw new InvalidOperationException("세션을 찾지 못했습니다.");
            session.ProjectId = projectId;
            session.IsDraft = false;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            Write(sessions);
        }
    }

    public bool IsDraft(string sessionId)
    {
        ValidateId(sessionId);
        lock (_gate) return Read().FirstOrDefault(item => item.Id == sessionId)?.IsDraft == true;
    }

    public bool DiscardDraft(string sessionId)
    {
        ValidateId(sessionId);
        lock (_gate)
        {
            var sessions = Read();
            var session = sessions.FirstOrDefault(item => item.Id == sessionId);
            if (session is null || !session.IsDraft) return false;
            sessions.Remove(session);
            Write(sessions);
            return true;
        }
    }

    public bool Delete(string sessionId)
    {
        ValidateId(sessionId);
        lock (_gate)
        {
            var sessions = Read();
            var session = sessions.FirstOrDefault(item => item.Id == sessionId);
            if (session is null) return false;
            sessions.Remove(session);
            Write(sessions);
            return true;
        }
    }

    public IReadOnlyList<string> SetAllowedDirectories(string sessionId, IEnumerable<string> directories)
    {
        ValidateId(sessionId);
        lock (_gate)
        {
            var sessions = Read();
            var session = sessions.FirstOrDefault(item => item.Id == sessionId)
                ?? throw new InvalidOperationException("세션을 찾지 못했습니다.");
            session.AllowedDirectories = FolderAccessPolicy.Normalize(
                directories,
                session.Directory,
                requireExisting: true);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            Write(sessions);
            return session.AllowedDirectories.ToList();
        }
    }

    public IReadOnlyList<string> SetAllowedNetworkEndpoints(string sessionId, IEnumerable<string> endpoints)
    {
        ValidateId(sessionId);
        lock (_gate)
        {
            var sessions = Read();
            var session = sessions.FirstOrDefault(item => item.Id == sessionId)
                ?? throw new InvalidOperationException("세션을 찾지 못했습니다.");
            session.AllowedNetworkEndpoints = NetworkAccessPolicy.Normalize(endpoints).ToList();
            session.UpdatedAt = DateTimeOffset.UtcNow;
            Write(sessions);
            return session.AllowedNetworkEndpoints.ToList();
        }
    }

    private List<StoredSession> Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<StoredSession>>(File.ReadAllText(_path), Json) ?? []
                : [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new IOException("로컬 세션 파일을 읽지 못했습니다.", exception);
        }
    }

    private void Write(List<StoredSession> sessions)
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(sessions, Json), new UTF8Encoding(false));
        File.Move(temporary, _path, true);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 ||
            id.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-')))
            throw new ArgumentException("올바르지 않은 세션 ID입니다.", nameof(id));
    }

    private sealed class StoredSession
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "새 세션";
        public string Directory { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? ProjectId { get; set; }
        public bool IsDraft { get; set; }
        public List<string> AllowedDirectories { get; set; } = [];
        public List<string> AllowedNetworkEndpoints { get; set; } = [];
        public List<ChatMessage> Messages { get; set; } = [];

        public ChatSession ToSummary() => new()
        {
            Id = Id, Title = Title, Directory = Directory,
            CreatedAt = CreatedAt, UpdatedAt = UpdatedAt, ProjectId = ProjectId,
            AllowedDirectories = AllowedDirectories ?? [],
            AllowedNetworkEndpoints = AllowedNetworkEndpoints ?? []
        };
    }
}
