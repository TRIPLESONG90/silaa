using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace silaa.Services;

internal sealed class SkillManagementTools(
    string workspace,
    string? dataRoot = null,
    Func<string, bool>? hasSourceAccess = null,
    SecurityPolicyContext? security = null,
    FolderAccessRequestTools? folderAccess = null,
    NetworkAccessRequestTools? networkAccess = null)
{
    private static readonly Regex ValidName = new(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));
    private readonly string _root = Path.Combine(Path.GetFullPath(workspace), ".silaa", "skills");
    private readonly Func<string, bool> _hasSourceAccess = hasSourceAccess ??
        (path => FolderAccessPolicy.IsInside(path, Path.GetFullPath(workspace)));
    private readonly SkillInstallerService _installer = new(
        workspace, dataRoot, hasSourceAccess, security);

    public IList<AITool> CreateFunctions() =>
    [
        AIFunctionFactory.Create((Func<string>)ListLocalSkills, "list_local_skills",
            "List project-local silaa skills stored under .silaa/skills."),
        AIFunctionFactory.Create((Func<string, string, string, bool, string>)CreateOrUpdateSkill,
            "create_or_update_skill",
            "Create a project-local skill, or update it only when overwrite is true. The skill becomes available on the next Agent run."),
        AIFunctionFactory.Create((Func<string, string>)ValidateSkill, "validate_skill",
            "Validate the structure, frontmatter, naming, and unfinished placeholders of one project-local skill."),
        AIFunctionFactory.Create((Func<string, string, string, bool, CancellationToken, Task<string>>)InstallSkillAsync,
            "install_skill",
            "Register and verify Agent guidance from an allowed local folder, GitHub URL, direct ZIP/SKILL.md URL, " +
            "or an HTTP(S) Skill guide page. A guide page without a package link is converted into a validated SKILL.md. " +
            "Use project scope by default. Use global or overwrite only when the user explicitly requests it. " +
            "This only registers guidance: it never installs dependencies, executes scripts, logs in, or configures an SDK. " +
            "Never describe those actions as completed. Never claim registration success unless verified is true.")
    ];

    public async Task<string> InstallSkillAsync(
        [Description("Allowed local folder, local SKILL.md path, GitHub URL, direct Skill ZIP/SKILL.md URL, or a Skill guide web page URL.")] string source,
        [Description("Optional folder containing SKILL.md relative to the source repository or folder.")] string subpath = "",
        [Description("Install scope: project or global. Prefer project.")] string scope = "project",
        [Description("Replace an installed Skill with the same name. Use only after an explicit update or overwrite request.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (SkillInstallerService.IsWebSource(source) && networkAccess is not null)
        {
            var uri = new Uri(source.Trim(), UriKind.Absolute);
            var port = uri.IsDefaultPort ? (uri.Scheme == "http" ? 80 : 443) : uri.Port;
            var network = await networkAccess.RequestNetworkAccessAsync(
                uri.Host, port, "Read and download the Skill package requested by the user.", cancellationToken);
            if (network.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                network.Contains("unavailable", StringComparison.OrdinalIgnoreCase)) return network;
        }
        else if (!SkillInstallerService.IsWebSource(source))
        {
            var sourceDirectory = SkillInstallerService.SourceDirectory(source);
            if (!_hasSourceAccess(sourceDirectory) && folderAccess is not null)
            {
                var access = await folderAccess.RequestFolderAccessAsync(
                    sourceDirectory, "Read the Skill package requested by the user.", cancellationToken);
                if (access.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                    access.Contains("unavailable", StringComparison.OrdinalIgnoreCase)) return access;
            }
        }

        var result = await _installer.InstallAsync(source, subpath, scope, overwrite, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            registered = true,
            verified = result.Verified,
            kind = "agent_guidance",
            result.Name,
            result.Description,
            scope = result.Scope,
            destination = result.Destination.Replace('\\', '/'),
            files = result.FileCount,
            bytes = result.TotalBytes,
            source = result.Source,
            dependenciesInstalled = false,
            scriptsExecuted = false,
            available = "next Agent run"
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    public string ListLocalSkills()
    {
        if (!Directory.Exists(_root)) return "No local skills found.";
        EnsureOrdinaryDirectory(_root);
        var skills = new List<object>();
        foreach (var directory in Directory.EnumerateDirectories(_root).OrderBy(Path.GetFileName))
        {
            if (IsReparsePoint(directory)) continue;
            var file = Path.Combine(directory, "SKILL.md");
            if (!File.Exists(file) || IsReparsePoint(file)) continue;
            var metadata = ReadMetadata(file);
            skills.Add(new
            {
                name = metadata.Name ?? Path.GetFileName(directory),
                description = metadata.Description ?? "",
                folder = Path.GetRelativePath(Path.GetFullPath(workspace), directory).Replace('\\', '/')
            });
        }
        return skills.Count == 0
            ? "No local skills found."
            : JsonSerializer.Serialize(skills, new JsonSerializerOptions { WriteIndented = true });
    }

    public string CreateOrUpdateSkill(
        [Description("Lowercase kebab-case skill name, under 64 characters.")] string name,
        [Description("Concise description of what the skill does and when it should apply.")] string description,
        [Description("Markdown instructions containing only useful, non-obvious workflow guidance.")] string instructions,
        [Description("Must be true to replace an existing local SKILL.md.")] bool overwrite = false)
    {
        name = NormalizeName(name);
        description = description.Trim();
        instructions = instructions.Trim();
        if (description.Length is < 1 or > 500 || description.Contains('\n') || description.Contains('\r'))
            throw new ArgumentException("Description must be a single line containing 1 to 500 characters.");
        if (instructions.Length is < 1 or > 50_000)
            throw new ArgumentException("Instructions must contain 1 to 50,000 characters.");

        Directory.CreateDirectory(_root);
        EnsureOrdinaryDirectory(_root);
        var directory = Path.Combine(_root, name);
        if (Directory.Exists(directory)) EnsureOrdinaryDirectory(directory);
        else Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "SKILL.md");
        if (File.Exists(file) && !overwrite)
            throw new InvalidOperationException($"Local skill '{name}' already exists. Set overwrite to true only when updating it intentionally.");
        if (File.Exists(file) && IsReparsePoint(file))
            throw new UnauthorizedAccessException("Skill files cannot be reparse points.");

        var content = $"---\nname: {name}\ndescription: {JsonSerializer.Serialize(description)}\n---\n\n{instructions}\n";
        var temporary = file + $".silaa.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, file, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        var validation = ValidateSkill(name);
        return $"Saved .silaa/skills/{name}/SKILL.md. {validation} It will load on the next Agent run.";
    }

    public string ValidateSkill([Description("Project-local skill name.")] string name)
    {
        name = NormalizeName(name);
        EnsureOrdinaryDirectory(_root);
        var directory = Path.Combine(_root, name);
        EnsureOrdinaryDirectory(directory);
        var file = Path.Combine(directory, "SKILL.md");
        if (!File.Exists(file)) throw new FileNotFoundException("SKILL.md was not found.", file);
        if (IsReparsePoint(file)) throw new UnauthorizedAccessException("Skill files cannot be reparse points.");
        var text = File.ReadAllText(file);
        if (text.Length > 100_000) throw new InvalidOperationException("SKILL.md exceeds 100,000 characters.");
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            throw new InvalidOperationException("SKILL.md must start with YAML frontmatter.");
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException("SKILL.md frontmatter is not closed.");
        var metadata = ReadMetadata(file);
        if (!string.Equals(metadata.Name, name, StringComparison.Ordinal))
            throw new InvalidOperationException("Frontmatter name must match the skill folder name.");
        if (string.IsNullOrWhiteSpace(metadata.Description))
            throw new InvalidOperationException("Frontmatter description is required.");
        if (string.IsNullOrWhiteSpace(normalized[(end + 5)..]))
            throw new InvalidOperationException("SKILL.md instructions are empty.");
        var unfinished = new[] { "TODO: replace", "[TODO:", "PLACEHOLDER INSTRUCTIONS" };
        if (unfinished.Any(marker => normalized.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("SKILL.md contains an unfinished scaffold placeholder.");
        return $"Skill '{name}' is valid.";
    }

    private static string NormalizeName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or >= 64 || !ValidName.IsMatch(name))
            throw new ArgumentException("Skill name must be lowercase kebab-case and shorter than 64 characters.");
        return name;
    }

    private static (string? Name, string? Description) ReadMetadata(string file)
    {
        string? name = null;
        string? description = null;
        foreach (var raw in File.ReadLines(file).Skip(1).TakeWhile(line => line.Trim() != "---").Take(30))
        {
            var separator = raw.IndexOf(':');
            if (separator <= 0) continue;
            var key = raw[..separator].Trim();
            var value = raw[(separator + 1)..].Trim().Trim('"', '\'');
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = value;
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = value;
        }
        return (name, description);
    }

    private static void EnsureOrdinaryDirectory(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Directory not found: {path}");
        if (IsReparsePoint(path)) throw new UnauthorizedAccessException("Skill directories cannot be reparse points.");
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
    }
}
