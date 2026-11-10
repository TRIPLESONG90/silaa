using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using silaa.Core;

namespace silaa.Services;

internal sealed class ProjectStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly HashSet<string> MarkerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "pyproject.toml", "Cargo.toml", "go.mod", "pom.xml",
        "build.gradle", "build.gradle.kts", "composer.json", "Gemfile"
    };
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "publish", "dist", "build"
    };
    private readonly object _gate = new();
    private readonly string _path;

    public ProjectStore(string root) => _path = Path.Combine(root, "harness-projects.json");

    public IReadOnlyList<ProjectInfo> List(string workspace)
    {
        var workspaceRoot = Path.GetFullPath(workspace);
        lock (_gate)
        {
            var projects = Read();
            var discovered = Discover(workspaceRoot).ToList();
            var changed = false;
            foreach (var directory in discovered)
            {
                var id = IdFor(directory);
                var existing = projects.FirstOrDefault(item => item.Id == id);
                var updated = Directory.GetLastWriteTimeUtc(directory);
                if (existing is null)
                {
                    projects.Add(new StoredProject
                    {
                        Id = id,
                        Name = ProjectName(directory),
                        Directory = directory,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = updated
                    });
                    changed = true;
                }
                else if (existing.UpdatedAt != updated || existing.Name != ProjectName(directory))
                {
                    existing.Name = ProjectName(directory);
                    existing.UpdatedAt = updated;
                    changed = true;
                }
            }
            projects.RemoveAll(item => IsInside(item.Directory, workspaceRoot) && !Directory.Exists(item.Directory));
            if (changed) Write(projects);
            return projects.Where(item => IsInside(item.Directory, workspaceRoot) && Directory.Exists(item.Directory))
                .OrderByDescending(item => item.UpdatedAt)
                .Select(item => item.ToInfo()).ToList();
        }
    }

    public ProjectInfo? Get(string workspace, string? projectId) =>
        string.IsNullOrWhiteSpace(projectId) ? null : List(workspace).FirstOrDefault(item => item.Id == projectId);

    public ProjectInfo? Match(string workspace, string text) =>
        List(workspace).FirstOrDefault(project =>
            project.Name.Length >= 2 && text.Contains(project.Name, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> SetAllowedDirectories(
        string workspace,
        string projectId,
        IEnumerable<string> directories)
    {
        var workspaceRoot = Path.GetFullPath(workspace);
        lock (_gate)
        {
            var projects = Read();
            var project = projects.FirstOrDefault(item =>
                item.Id == projectId && IsInside(item.Directory, workspaceRoot))
                ?? throw new InvalidOperationException("프로젝트를 찾지 못했습니다.");
            project.AllowedDirectories = FolderAccessPolicy.Normalize(
                directories,
                project.Directory,
                requireExisting: true);
            project.UpdatedAt = DateTimeOffset.UtcNow;
            Write(projects);
            return project.AllowedDirectories.ToList();
        }
    }

    public IReadOnlyList<string> SetAllowedNetworkEndpoints(
        string workspace,
        string projectId,
        IEnumerable<string> endpoints)
    {
        var workspaceRoot = Path.GetFullPath(workspace);
        lock (_gate)
        {
            var projects = Read();
            var project = projects.FirstOrDefault(item =>
                item.Id == projectId && IsInside(item.Directory, workspaceRoot))
                ?? throw new InvalidOperationException("프로젝트를 찾지 못했습니다.");
            project.AllowedNetworkEndpoints = NetworkAccessPolicy.Normalize(endpoints).ToList();
            project.UpdatedAt = DateTimeOffset.UtcNow;
            Write(projects);
            return project.AllowedNetworkEndpoints.ToList();
        }
    }

    public ProjectInfo Create(string workspace, string name, string summary, int specificityScore,
        IReadOnlyList<ProjectDiscoveryEntry> history, string outputType = "other")
    {
        if (specificityScore < 70) throw new InvalidOperationException("구체화 지수가 70 이상이어야 프로젝트를 시작할 수 있습니다.");
        var root = Path.GetFullPath(workspace);
        var folderName = SafeFolderName(name);
        var directory = Path.Combine(root, folderName);
        for (var suffix = 2; Directory.Exists(directory); suffix++) directory = Path.Combine(root, $"{folderName}-{suffix}");
        Directory.CreateDirectory(directory);
        lock (_gate)
        {
            var projects = Read();
            var delivery = CreateDeliveryProfile(outputType, projects);
            var project = new StoredProject
            {
                Id = IdFor(directory),
                Name = string.IsNullOrWhiteSpace(name) ? folderName : name.Trim(),
                Directory = directory,
                Summary = summary.Trim(),
                SpecificityScore = Math.Clamp(specificityScore, 0, 100),
                DiscoveryHistory = history.ToList(),
                Delivery = delivery,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            projects.RemoveAll(item => item.Id == project.Id);
            projects.Add(project);
            Write(projects);
            return project.ToInfo();
        }
    }

    private static ProjectDeliveryProfile CreateDeliveryProfile(string outputType, IReadOnlyList<StoredProject> projects)
    {
        var normalized = (outputType ?? "").Trim().ToLowerInvariant();
        if (normalized is not ("app" or "webapp" or "web-app" or "웹앱" or "앱"))
            return new ProjectDeliveryProfile { Kind = string.IsNullOrWhiteSpace(normalized) ? "other" : normalized };

        var usedAppPorts = projects.Select(item => item.Delivery?.AppPort ?? 0).ToHashSet();
        var usedInternalPorts = projects.Select(item => item.Delivery?.InternalPort ?? 0).ToHashSet();
        return new ProjectDeliveryProfile
        {
            Kind = "webapp",
            SkillName = "web-app-delivery",
            AppPort = NextAvailablePort(5100, usedAppPorts),
            InternalPort = NextAvailablePort(3000, usedInternalPorts)
        };
    }

    private static int NextAvailablePort(int start, HashSet<int> used)
    {
        for (var port = start; port <= 65535; port++)
            if (!used.Contains(port)) return port;
        throw new InvalidOperationException("프로젝트에 할당할 로컬 포트가 없습니다.");
    }

    private static IEnumerable<string> Discover(string workspace)
    {
        if (Directory.Exists(Path.Combine(workspace, ".git")) || HasProjectMarker(workspace, 0))
            yield return workspace;

        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(workspace).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var child in children)
        {
            if (IgnoredDirectories.Contains(Path.GetFileName(child))) continue;
            if (HasProjectMarker(child, 2)) yield return Path.GetFullPath(child);
        }
    }

    private static bool HasProjectMarker(string root, int maximumDepth)
    {
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    var name = Path.GetFileName(file);
                    if (MarkerNames.Contains(name) || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) return true;
                }
                if (depth >= maximumDepth) continue;
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    if (name.EndsWith(".xcodeproj", StringComparison.OrdinalIgnoreCase)) return true;
                    if (!IgnoredDirectories.Contains(name)) pending.Push((child, depth + 1));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return false;
    }

    private List<StoredProject> Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<StoredProject>>(File.ReadAllText(_path), Json) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new IOException("프로젝트 목록을 읽지 못했습니다.", exception);
        }
    }

    private void Write(List<StoredProject> projects)
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(projects, Json), new UTF8Encoding(false));
        File.Move(temporary, _path, true);
    }

    private static string ProjectName(string directory)
    {
        var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }

    private static string SafeFolderName(string name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "new-project" : name.Trim();
        foreach (var character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '-');
        value = string.Join('-', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        value = value.Trim('.', ' ', '-');
        return value.Length == 0 ? "new-project" : value[..Math.Min(value.Length, 80)];
    }

    private static string IdFor(string directory)
    {
        var normalized = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        return "prj_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
    }

    private static bool IsInside(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.Equals(fullRoot, comparison) || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private sealed class StoredProject
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Directory { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string Summary { get; set; } = "";
        public int SpecificityScore { get; set; }
        public List<ProjectDiscoveryEntry> DiscoveryHistory { get; set; } = [];
        public List<string> AllowedDirectories { get; set; } = [];
        public List<string> AllowedNetworkEndpoints { get; set; } = [];
        public ProjectDeliveryProfile? Delivery { get; set; } = new();
        public ProjectInfo ToInfo() => new()
        {
            Id = Id, Name = Name, Directory = Directory, UpdatedAt = UpdatedAt,
            Summary = Summary, SpecificityScore = SpecificityScore, DiscoveryHistory = DiscoveryHistory,
            AllowedDirectories = AllowedDirectories ?? [],
            AllowedNetworkEndpoints = AllowedNetworkEndpoints ?? [],
            Delivery = Delivery ?? new ProjectDeliveryProfile()
        };
    }
}
