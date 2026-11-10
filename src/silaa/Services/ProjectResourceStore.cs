using System.Text;
using System.Text.Json;
using silaa.Core;

namespace silaa.Services;

internal sealed class ProjectResourceStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase) { "file", "folder", "web", "code", "note" };
    private readonly object _gate = new();
    private readonly string _path;

    public ProjectResourceStore(string root) => _path = Path.Combine(root, "harness-resources.json");

    public IReadOnlyList<ProjectResourceInfo> List(string projectId)
    {
        lock (_gate) return Read().Where(item => item.ProjectId == projectId).OrderByDescending(item => item.UpdatedAt).ToList();
    }

    public ProjectResourceInfo? Get(string projectId, string resourceId)
    {
        lock (_gate) return Read().FirstOrDefault(item => item.ProjectId == projectId && item.Id == resourceId);
    }

    public ProjectResourceInfo Add(string projectId, string type, string title, string source, string content = "", string language = "")
    {
        type = type.Trim().ToLowerInvariant();
        if (!Types.Contains(type)) throw new ArgumentException("지원하지 않는 리소스 유형입니다.");
        title = title.Trim();
        if (title.Length is 0 or > 200) throw new ArgumentException("리소스 제목은 1~200자로 입력하세요.");
        source = source.Trim();
        if (type is "file" or "folder")
        {
            if (source.Length == 0) throw new ArgumentException("파일 또는 폴더 경로를 입력하세요.");
            source = Path.GetFullPath(source);
            if (type == "file" && !File.Exists(source)) throw new FileNotFoundException("파일을 찾지 못했습니다.", source);
            if (type == "folder" && !Directory.Exists(source)) throw new DirectoryNotFoundException("폴더를 찾지 못했습니다.");
        }
        if (type == "web" && (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException("올바른 HTTP 또는 HTTPS 주소를 입력하세요.");
        if (type is "note" or "code" && string.IsNullOrWhiteSpace(content)) throw new ArgumentException("내용을 입력하세요.");
        var now = DateTimeOffset.UtcNow;
        var resource = new ProjectResourceInfo
        {
            Id = $"res_{Guid.NewGuid():N}", ProjectId = projectId, Type = type, Title = title,
            Source = source, Content = content, Language = language.Trim(), CreatedAt = now, UpdatedAt = now
        };
        lock (_gate)
        {
            var resources = Read();
            resources.Add(resource);
            Write(resources);
        }
        return resource;
    }

    public void Remove(string projectId, string resourceId)
    {
        lock (_gate)
        {
            var resources = Read();
            if (resources.RemoveAll(item => item.ProjectId == projectId && item.Id == resourceId) > 0) Write(resources);
        }
    }

    private List<ProjectResourceInfo> Read()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<List<ProjectResourceInfo>>(File.ReadAllText(_path), Json) ?? [] : []; }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { throw new IOException("프로젝트 리소스를 읽지 못했습니다.", exception); }
    }

    private void Write(List<ProjectResourceInfo> resources)
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(resources, Json), new UTF8Encoding(false));
        File.Move(temporary, _path, true);
    }
}
