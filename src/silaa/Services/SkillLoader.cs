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
