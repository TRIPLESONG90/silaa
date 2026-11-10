using System.Text;
using System.Text.RegularExpressions;

namespace silaa.Services;

internal static class SkillLoader
{
    private const int MaximumCatalogCharacters = 24_000;
    private const int MaximumSelectedSkillCharacters = 60_000;
    private const int MaximumSelectedSkills = 3;
    private static readonly HashSet<string> ExcludedCodexSkills = new(StringComparer.OrdinalIgnoreCase)
    {
        "vibe-memory", "imagegen", "openai-docs", "skill-installer"
    };

    public static string BuildSystemPrompt(
        string workspace,
        IReadOnlyList<string>? allowedDirectories = null,
        string? dataRoot = null,
        string? requestContext = null)
        => BuildSystemPrompt(workspace, allowedDirectories, dataRoot,
            SelectRelevantSkills(workspace, dataRoot, requestContext));

    internal static string BuildSystemPrompt(
        string workspace,
        IReadOnlyList<string>? allowedDirectories,
        string? dataRoot,
        SkillSelection selection)
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
        builder.AppendLine().AppendLine("# Available skills (catalog)");
        builder.AppendLine("Only the compact catalog is listed by default. Full guidance is included below only for skills relevant to the current request.");
        foreach (var skill in selection.Catalog)
        {
            if (builder.Length >= MaximumCatalogCharacters) break;
            builder.Append("- ").Append(skill.Name);
            if (!string.IsNullOrWhiteSpace(skill.Description))
                builder.Append(": ").Append(skill.Description);
            builder.AppendLine();
        }

        if (selection.Selected.Count > 0)
        {
            builder.AppendLine().AppendLine("# Skills selected for this request");
            var selectedCharacters = 0;
            foreach (var skill in selection.Selected)
            {
                if (selectedCharacters >= MaximumSelectedSkillCharacters) break;
                var remaining = MaximumSelectedSkillCharacters - selectedCharacters;
                var text = skill.Content.Length > remaining ? skill.Content[..remaining] : skill.Content;
                builder.AppendLine().AppendLine($"<skill source=\"{skill.Name}\">");
                builder.AppendLine(text).AppendLine("</skill>");
                selectedCharacters += text.Length;
            }
        }
        return builder.ToString();
    }

    internal static SkillSelection SelectRelevantSkills(
        string workspace, string? dataRoot, string? requestContext)
    {
        var catalog = SkillFiles(workspace, dataRoot)
            .Select(ReadDescriptor)
            .Where(skill => skill is not null)
            .Cast<SkillDescriptor>()
            .GroupBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (string.IsNullOrWhiteSpace(requestContext)) return new SkillSelection(catalog, []);

        var context = requestContext.Trim();
        var contextTokens = Tokens(context).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = catalog
            .Select(skill => (Skill: skill, Score: RelevanceScore(skill, context, contextTokens)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Skill.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaximumSelectedSkills)
            .Select(candidate => candidate.Skill)
            .ToList();
        return new SkillSelection(catalog, selected);
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
        foreach (var file in SkillFiles(workspace, dataRoot))
        {
            var skill = ReadDescriptor(file);
            if (skill?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true) return skill.Content;
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

    private static SkillDescriptor? ReadDescriptor(string file)
    {
        try
        {
            var content = File.ReadAllText(file);
            var fallbackName = Path.GetFileName(Path.GetDirectoryName(file)) ?? Path.GetFileNameWithoutExtension(file);
            var name = FrontmatterValue(content, "name") ?? fallbackName;
            var description = FrontmatterValue(content, "description") ?? FirstDescription(content);
            return new SkillDescriptor(name.Trim(), description.Trim(), file, content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int RelevanceScore(SkillDescriptor skill, string context, HashSet<string> contextTokens)
    {
        var score = 0;
        if (context.Contains(skill.Name, StringComparison.OrdinalIgnoreCase) ||
            context.Contains(skill.Name.Replace('-', ' '), StringComparison.OrdinalIgnoreCase))
            score += 100;
        foreach (var token in Tokens(skill.Name))
            if (contextTokens.Contains(token)) score += 12;
        foreach (var token in Tokens(skill.Description).Distinct(StringComparer.OrdinalIgnoreCase).Take(40))
            if (contextTokens.Contains(token)) score += 3;
        return score;
    }

    private static IEnumerable<string> Tokens(string value) =>
        Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{N}][\p{L}\p{N}_-]{1,}")
            .Select(match => match.Value)
            .Where(token => token is not ("the" or "and" or "for" or "with" or "from" or "skill" or "agent" or "use" or "using" or "this" or "that"));

    private static string? FrontmatterValue(string content, string key)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal)) return null;
        var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return null;
        var match = Regex.Match(content[..end], $@"(?im)^\s*{Regex.Escape(key)}\s*:\s*[""']?(?<value>.*?)[""']?\s*$");
        return match.Success ? match.Groups["value"].Value.Trim().Trim('"', '\'') : null;
    }

    private static string FirstDescription(string content)
    {
        foreach (var line in content.Replace("\r", "").Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0 || text is "---" || text.StartsWith('#') || text.Contains(':')) continue;
            return text.Length > 240 ? text[..240] : text;
        }
        return "Task-specific guidance.";
    }

    private static IEnumerable<string> SkillFiles(string workspace, string? dataRoot)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var file in DirectSkills(Path.Combine(Path.GetFullPath(workspace), ".silaa", "skills")))
            if (seen.Add(Path.GetFullPath(file))) yield return file;

        foreach (var file in DirectSkills(Path.Combine(Path.GetFullPath(dataRoot ?? AppPaths.DataRoot), "skills")))
            if (seen.Add(Path.GetFullPath(file))) yield return file;

        var bundled = FindBundledDirectory("skills");
        if (bundled is not null)
            foreach (var file in DirectSkills(bundled))
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

internal sealed record SkillDescriptor(string Name, string Description, string File, string Content);
internal sealed record SkillSelection(IReadOnlyList<SkillDescriptor> Catalog, IReadOnlyList<SkillDescriptor> Selected)
{
    public static SkillSelection Empty { get; } = new([], []);
    public string SelectedContent => string.Join("\n", Selected.Select(skill => skill.Content));
}
