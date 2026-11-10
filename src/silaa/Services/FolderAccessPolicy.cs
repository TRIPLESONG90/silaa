namespace silaa.Services;

internal static class FolderAccessPolicy
{
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static List<string> Normalize(
        IEnumerable<string>? directories,
        string primaryRoot,
        bool requireExisting,
        int maximum = 32)
    {
        var root = NormalizePath(primaryRoot);
        var normalized = new List<string>();
        foreach (var raw in directories ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = NormalizePath(raw);
            if (IsInside(path, root)) continue;
            if (!Directory.Exists(path))
            {
                if (requireExisting) throw new DirectoryNotFoundException($"접근 허용 폴더가 없습니다: {path}");
                continue;
            }
            if (!normalized.Contains(path, Comparer)) normalized.Add(path);
            if (normalized.Count > maximum)
                throw new ArgumentException($"접근 허용 폴더는 범위별로 최대 {maximum}개까지 추가할 수 있습니다.");
        }
        return normalized;
    }

    public static IReadOnlyList<string> Combine(string primaryRoot, params IEnumerable<string>?[] scopes)
    {
        var root = NormalizePath(primaryRoot);
        var combined = new List<string>();
        foreach (var scope in scopes)
        foreach (var raw in scope ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = NormalizePath(raw);
            if (IsInside(path, root) || combined.Contains(path, Comparer) || !Directory.Exists(path)) continue;
            combined.Add(path);
        }
        return combined;
    }

    public static bool IsInside(string path, string root)
    {
        var fullPath = NormalizePath(path);
        var fullRoot = NormalizePath(root);
        return fullPath.Equals(fullRoot, Comparison) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, Comparison);
    }

    public static string NormalizePath(string path) => Path.GetFullPath(path.Trim())
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
