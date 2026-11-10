using System.Text;

namespace silaa.Services;

internal static class SkillLoader
{
    private const int MaximumCharacters = 120_000;
    private static readonly HashSet<string> ExcludedCodexSkills = new(StringComparer.OrdinalIgnoreCase)
    {
        "vibe-memory", "imagegen", "openai-docs", "skill-installer"
    };

    public static string BuildSystemPrompt(
        string workspace,
        IReadOnlyList<string>? allowedDirectories = null,
        string? dataRoot = null)
    {
        var builder = new StringBuilder();
        var system = FindBundledFile("prompts", "system.md");
        if (system is not null) builder.AppendLine(File.ReadAllText(system));
        else builder.AppendLine("You are a coding agent. Inspect the workspace, use tools, make requested changes, and verify your work.");

        builder.AppendLine().AppendLine($"Workspace: {Path.GetFullPath(workspace)}");
        builder.AppendLine("All relative tool paths are resolved inside this workspace.");
        var allowed = FolderAccessPolicy.Combine(workspace, allowedDirectories);
        if (allowed.Count > 0)
        {
            builder.AppendLine("The user explicitly allowed these additional folder roots for this run:");
            foreach (var directory in allowed) builder.AppendLine($"- {directory}");
            builder.AppendLine("Absolute file-tool paths are permitted only inside the workspace or these roots. Do not access any other folder.");
        }
        builder.AppendLine().AppendLine("# Available skills");

        foreach (var file in SkillFiles(workspace, dataRoot))
        {
            if (builder.Length >= MaximumCharacters) break;
            try
            {
                var text = File.ReadAllText(file);
                var remaining = MaximumCharacters - builder.Length;
                if (text.Length > remaining) text = text[..remaining];
                builder.AppendLine().AppendLine($"<skill source=\"{Path.GetFileName(Path.GetDirectoryName(file))}\">");
                builder.AppendLine(text).AppendLine("</skill>");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return builder.ToString();
    }

    public static string ReadSkill(string name, string workspace, string? dataRoot = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var roots = new[]
        {
            Path.Combine(Path.GetFullPath(workspace), ".silaa", "skills"),
            Path.Combine(Path.GetFullPath(dataRoot ?? AppPaths.DataRoot), "skills"),
            FindBundledDirectory("skills")
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var file = Path.Combine(root, name, "SKILL.md");
            try
            {
                if (File.Exists(file)) return File.ReadAllText(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return "";
    }

    public static string ReadSkillReference(string name, string relativePath, string workspace, string? dataRoot = null)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Skill name and reference path are required.");
        if (Path.IsPathRooted(relativePath)) throw new UnauthorizedAccessException("Skill reference paths must be relative.");
        if (relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
            throw new UnauthorizedAccessException("Skill reference paths cannot leave the Skill folder.");
        var normalizedName = Path.GetFileName(name.Trim());
        if (!normalizedName.Equals(name.Trim(), StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Invalid Skill name.");
        foreach (var root in SkillRoots(workspace, dataRoot))
        {
            var directory = Path.GetFullPath(Path.Combine(root, normalizedName));
            var file = Path.GetFullPath(Path.Combine(directory, relativePath));
            if (!FolderAccessPolicy.IsInside(file, directory) || !File.Exists(file)) continue;
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".md" or ".txt" or ".json" or ".yaml" or ".yml" or ".toml"))
                throw new NotSupportedException("Only text Skill references can be read.");
            var text = File.ReadAllText(file);
            if (text.Length > 100_000) throw new InvalidOperationException("Skill reference exceeds 100,000 characters.");
            return text;
        }
        throw new FileNotFoundException($"Reference '{relativePath}' was not found in Skill '{name}'.");
    }

    private static IEnumerable<string> SkillFiles(string workspace, string? dataRoot)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var bundled = FindBundledDirectory("skills");
        if (bundled is not null)
            foreach (var file in DirectSkills(bundled))
                if (seen.Add(Path.GetFullPath(file))) yield return file;

        foreach (var file in DirectSkills(Path.Combine(Path.GetFullPath(dataRoot ?? AppPaths.DataRoot), "skills")))
            if (seen.Add(Path.GetFullPath(file))) yield return file;

        foreach (var file in DirectSkills(Path.Combine(Path.GetFullPath(workspace), ".silaa", "skills")))
            if (seen.Add(Path.GetFullPath(file))) yield return file;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var file in DirectSkills(Path.Combine(home, ".claude", "skills")))
            if (seen.Add(Path.GetFullPath(file))) yield return file;

        var commands = Path.Combine(home, ".claude", "commands");
        if (Directory.Exists(commands))
            foreach (var file in Directory.EnumerateFiles(commands, "*.md").OrderBy(path => path))
                if (seen.Add(Path.GetFullPath(file))) yield return file;

        var codex = Path.Combine(home, ".codex", "skills");
        foreach (var file in DirectSkills(codex).Where(file =>
                     !ExcludedCodexSkills.Contains(Path.GetFileName(Path.GetDirectoryName(file)) ?? "")))
            if (seen.Add(Path.GetFullPath(file))) yield return file;

        var review = Path.Combine(codex, ".system", "review-agent", "SKILL.md");
        if (File.Exists(review) && seen.Add(Path.GetFullPath(review))) yield return review;
    }

    private static IEnumerable<string> SkillRoots(string workspace, string? dataRoot)
    {
        yield return Path.Combine(Path.GetFullPath(workspace), ".silaa", "skills");
        yield return Path.Combine(Path.GetFullPath(dataRoot ?? AppPaths.DataRoot), "skills");
        var bundled = FindBundledDirectory("skills");
        if (bundled is not null) yield return bundled;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".claude", "skills");
        yield return Path.Combine(home, ".codex", "skills");
    }

    private static IEnumerable<string> DirectSkills(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(path => path))
        {
            if (Path.GetFileName(directory).StartsWith('.')) continue;
            var file = Path.Combine(directory, "SKILL.md");
            if (File.Exists(file)) yield return file;
        }
    }

    private static string? FindBundledFile(string directory, string name)
    {
        var root = FindBundledDirectory(directory);
        var path = root is null ? null : Path.Combine(root, name);
        return path is not null && File.Exists(path) ? path : null;
    }

    private static string? FindBundledDirectory(string name)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, name);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
