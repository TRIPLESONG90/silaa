using System.Text;
using System.Text.Json;
using silaa.Core;

namespace silaa.Services;

internal sealed class ProjectDraftStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;

    public ProjectDraftStore(string root) => _path = Path.Combine(root, "harness-project-drafts.json");

    public void Record(string sessionId, string question, string answer, int score)
    {
        lock (_gate)
        {
            var drafts = Read();
            if (!drafts.TryGetValue(sessionId, out var entries)) drafts[sessionId] = entries = [];
            entries.Add(new ProjectDiscoveryEntry
            {
                Question = question.Trim(), Answer = answer.Trim(), SpecificityScore = Math.Clamp(score, 0, 100),
                CreatedAt = DateTimeOffset.UtcNow
            });
            Write(drafts);
        }
    }

    public IReadOnlyList<ProjectDiscoveryEntry> Get(string sessionId)
    {
        lock (_gate) return Read().TryGetValue(sessionId, out var entries) ? entries.ToList() : [];
    }

    public void Remove(string sessionId)
    {
        lock (_gate)
        {
            var drafts = Read();
            if (drafts.Remove(sessionId)) Write(drafts);
        }
    }

    private Dictionary<string, List<ProjectDiscoveryEntry>> Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, List<ProjectDiscoveryEntry>>>(File.ReadAllText(_path), Json) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new IOException("프로젝트 구체화 기록을 읽지 못했습니다.", exception);
        }
    }

    private void Write(Dictionary<string, List<ProjectDiscoveryEntry>> drafts)
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(drafts, Json), new UTF8Encoding(false));
        File.Move(temporary, _path, true);
    }
}
