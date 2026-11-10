using System.Text;
using System.Text.Json;
using silaa.Core;

namespace silaa.Services;

internal sealed class MemoryStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;

    public MemoryStore(string root) => _path = Path.Combine(root, "memory.json");

    public IReadOnlyList<MemoryEntry> List(string? projectId = null)
    {
        lock (_gate) return Read().Where(item => projectId is null ? item.ProjectId is null : item.ProjectId is null || item.ProjectId == projectId)
            .OrderByDescending(item => item.UpdatedAt).ToList();
    }

    public MemoryEntry Add(string? projectId, string category, string content, string source)
    {
        content = content.Trim();
        if (content.Length is 0 or > 20_000) throw new ArgumentException("Memory 내용은 1~20,000자로 입력하세요.");
        var entry = new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}", ProjectId = projectId, Category = string.IsNullOrWhiteSpace(category) ? "fact" : category.Trim(),
            Content = content, Source = string.IsNullOrWhiteSpace(source) ? "agent" : source.Trim(), UpdatedAt = DateTimeOffset.UtcNow
        };
        lock (_gate)
        {
            var entries = Read();
            entries.Add(entry);
            Write(entries);
        }
        return entry;
    }

    public IReadOnlyList<MemoryEntry> Search(string? projectId, string query)
    {
        query = query.Trim();
        return List(projectId).Where(item => item.Content.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.Category.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(20).ToList();
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var entries = Read();
            if (entries.RemoveAll(item => item.Id == id) > 0) Write(entries);
        }
    }

    private List<MemoryEntry> Read()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<List<MemoryEntry>>(File.ReadAllText(_path), Json) ?? [] : []; }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { throw new IOException("Memory를 읽지 못했습니다.", exception); }
    }

    private void Write(List<MemoryEntry> entries)
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries, Json), new UTF8Encoding(false));
        File.Move(temporary, _path, true);
    }
}
