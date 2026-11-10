using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace silaa.Services;

internal sealed record SkillInstallResult(
    string Name,
    string Description,
    string Scope,
    string Destination,
    int FileCount,
    long TotalBytes,
    string Source);

internal sealed partial class SkillInstallerService
{
    private const int MaximumFiles = 500;
    private const long MaximumBytes = 50L * 1024 * 1024;
    private readonly string _workspace;
    private readonly string _globalSkillsRoot;
    private readonly Func<string, bool> _hasSourceAccess;
    private readonly SecurityPolicyContext? _security;

    public SkillInstallerService(
        string workspace,
        string? dataRoot = null,
        Func<string, bool>? hasSourceAccess = null,
        SecurityPolicyContext? security = null)
    {
        _workspace = Path.GetFullPath(workspace);
        _globalSkillsRoot = Path.Combine(Path.GetFullPath(dataRoot ?? AppPaths.DataRoot), "skills");
        _hasSourceAccess = hasSourceAccess ?? (path => FolderAccessPolicy.IsInside(path, _workspace));
        _security = security;
    }

    public static bool IsGitHubSource(string source) =>
        Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    public static string SourceDirectory(string source)
    {
        var full = Path.GetFullPath(source.Trim().Trim('"'));
        return File.Exists(full) ? Path.GetDirectoryName(full)! : full;
    }

    public async Task<SkillInstallResult> InstallAsync(
        string source,
        string subpath,
        string scope,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        source = source.Trim();
        if (source.Length == 0) throw new ArgumentException("Skill source is required.");
        subpath = NormalizeSubpath(subpath);
        var normalizedScope = NormalizeScope(scope);
        string? temporaryRoot = null;
        string skillDirectory;
        string sourceLabel;

        try
        {
            if (IsGitHubSource(source))
            {
                var github = ParseGitHubSource(source, subpath);
                var networkDecision = _security?.InspectNetworkUri(github.RepositoryUri, "skill.install");
                if (networkDecision is { Allowed: false })
                    throw new InvalidOperationException(networkDecision.Message);
                temporaryRoot = Path.Combine(Path.GetTempPath(), $"silaa-skill-{Guid.NewGuid():N}");
                Directory.CreateDirectory(temporaryRoot);
                var checkout = Path.Combine(temporaryRoot, "repository");
                await CloneAsync(github, checkout, cancellationToken);
                skillDirectory = ResolveSubpath(checkout, github.Subpath);
                sourceLabel = github.DisplaySource;
            }
            else
            {
                var sourceDirectory = SourceDirectory(source);
                if (!Directory.Exists(sourceDirectory))
                    throw new DirectoryNotFoundException($"Skill source folder was not found: {sourceDirectory}");
                if (!_hasSourceAccess(sourceDirectory))
                    throw new UnauthorizedAccessException($"Folder access is required before installing this Skill: {sourceDirectory}");
                skillDirectory = ResolveSubpath(sourceDirectory, subpath);
                sourceLabel = skillDirectory;
            }

            var package = InspectPackage(skillDirectory);
            var destinationRoot = normalizedScope == "global"
                ? _globalSkillsRoot
                : Path.Combine(_workspace, ".silaa", "skills");
            return InstallPackage(package, destinationRoot, normalizedScope, sourceLabel, overwrite);
        }
        finally
        {
            if (temporaryRoot is not null) DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    private async Task CloneAsync(GitHubSkillSource source, string checkout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("clone");
        start.ArgumentList.Add("--quiet");
        start.ArgumentList.Add("--depth");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add("--no-tags");
        if (source.Reference.Length > 0)
        {
            start.ArgumentList.Add("--branch");
            start.ArgumentList.Add(source.Reference);
        }
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(source.RepositoryUri.AbsoluteUri);
        start.ArgumentList.Add(checkout);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        WorkspaceTools.ConfigureSanitizedEnvironment(start);
        _security?.ApplyNetworkEnvironment(start);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Git could not be started. Install Git for Windows and try again.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("Skill download exceeded two minutes.");
        }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode == 0) return;
        var detail = string.Join(' ', new[] { error, output })
            .Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (detail.Length > 600) detail = detail[..600];
        throw new InvalidOperationException(
            $"GitHub Skill download failed (exit {process.ExitCode}). {detail}".Trim());
    }

    private static SkillPackage InspectPackage(string directory)
    {
        directory = Path.GetFullPath(directory);
        EnsureOrdinaryDirectory(directory, "Skill source folders cannot be links or reparse points.");
        var skillFile = Path.Combine(directory, "SKILL.md");
        if (!File.Exists(skillFile))
            throw new FileNotFoundException("The selected folder does not contain SKILL.md.", skillFile);
        EnsureOrdinaryFile(skillFile);

        var text = File.ReadAllText(skillFile, Encoding.UTF8);
        if (text.Length > 100_000) throw new InvalidOperationException("SKILL.md exceeds 100,000 characters.");
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            throw new InvalidOperationException("SKILL.md must start with YAML frontmatter.");
        var frontmatterEnd = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (frontmatterEnd < 0) throw new InvalidOperationException("SKILL.md frontmatter is not closed.");
        var metadata = ReadMetadata(normalized[4..frontmatterEnd]);
        var name = NormalizeName(metadata.Name ?? Path.GetFileName(directory));
        var description = (metadata.Description ?? "").Trim();
        if (description.Length is < 1 or > 500)
            throw new InvalidOperationException("SKILL.md requires a description containing 1 to 500 characters.");
        if (string.IsNullOrWhiteSpace(normalized[(frontmatterEnd + 5)..]))
            throw new InvalidOperationException("SKILL.md instructions are empty.");

        var files = new List<SkillPackageFile>();
        long totalBytes = 0;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var childDirectory in Directory.EnumerateDirectories(current))
            {
                if (Path.GetFileName(childDirectory).Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                EnsureOrdinaryDirectory(childDirectory, "Skill packages cannot contain linked directories.");
                pending.Push(childDirectory);
            }
            foreach (var file in Directory.EnumerateFiles(current))
            {
                EnsureOrdinaryFile(file);
                var info = new FileInfo(file);
                totalBytes += info.Length;
                files.Add(new SkillPackageFile(file, Path.GetRelativePath(directory, file), info.Length));
                if (files.Count > MaximumFiles)
                    throw new InvalidOperationException($"Skill packages can contain at most {MaximumFiles} files.");
                if (totalBytes > MaximumBytes)
                    throw new InvalidOperationException("Skill packages can contain at most 50 MB.");
            }
        }
        return new SkillPackage(name, description, directory, files, totalBytes);
    }

    private static SkillInstallResult InstallPackage(
        SkillPackage package,
        string destinationRoot,
        string scope,
        string source,
        bool overwrite)
    {
        Directory.CreateDirectory(destinationRoot);
        EnsureOrdinaryDirectory(destinationRoot, "Skill installation folders cannot be links or reparse points.");
        var destination = Path.Combine(destinationRoot, package.Name);
        if (Directory.Exists(destination) && !overwrite)
            throw new InvalidOperationException(
                $"Skill '{package.Name}' is already installed in the {scope} scope. Enable overwrite only when the user requested an update.");
        if (File.Exists(destination))
            throw new InvalidOperationException("The Skill destination is occupied by a file.");

        var staging = Path.Combine(destinationRoot, $".install-{package.Name}-{Guid.NewGuid():N}");
        var backup = Path.Combine(destinationRoot, $".backup-{package.Name}-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in package.Files)
            {
                var target = Path.GetFullPath(Path.Combine(staging, file.RelativePath));
                if (!FolderAccessPolicy.IsInside(target, staging))
                    throw new InvalidOperationException("A Skill file escaped the installation folder.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file.SourcePath, target, overwrite: false);
            }
            InspectPackage(staging);

            if (Directory.Exists(destination)) Directory.Move(destination, backup);
            try
            {
                Directory.Move(staging, destination);
                if (Directory.Exists(backup)) DeleteTemporaryDirectory(backup);
            }
            catch
            {
                if (!Directory.Exists(destination) && Directory.Exists(backup)) Directory.Move(backup, destination);
                throw;
            }
        }
        finally
        {
            if (Directory.Exists(staging)) DeleteTemporaryDirectory(staging);
            if (Directory.Exists(backup) && Directory.Exists(destination)) DeleteTemporaryDirectory(backup);
        }

        return new SkillInstallResult(
            package.Name, package.Description, scope, destination,
            package.Files.Count, package.TotalBytes, source);
    }

    private static GitHubSkillSource ParseGitHubSource(string source, string explicitSubpath)
    {
        var uri = new Uri(source, UriKind.Absolute);
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
            throw new ArgumentException("GitHub Skill URLs cannot contain credentials, query strings, or fragments.");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length < 2) throw new ArgumentException("Use a GitHub repository or Skill folder URL.");
        var owner = segments[0];
        var repository = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4] : segments[1];
        if (!GitHubPart().IsMatch(owner) || !GitHubPart().IsMatch(repository))
            throw new ArgumentException("The GitHub owner or repository name is invalid.");

        var reference = "";
        var embeddedSubpath = "";
        if (segments.Length > 2)
        {
            if (segments[2] is not ("tree" or "blob") || segments.Length < 4)
                throw new ArgumentException("Use a GitHub repository URL or a /tree/<branch>/<folder> URL.");
            reference = segments[3];
            if (!GitReference().IsMatch(reference) || reference.StartsWith('-'))
                throw new ArgumentException("The Git reference in the URL is invalid.");
            var pathSegments = segments.Skip(4).ToList();
            if (segments[2] == "blob" && pathSegments.LastOrDefault()?.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) == true)
                pathSegments.RemoveAt(pathSegments.Count - 1);
            embeddedSubpath = NormalizeSubpath(string.Join('/', pathSegments));
        }
        var subpath = explicitSubpath.Length > 0 ? explicitSubpath : embeddedSubpath;
        var repositoryUri = new Uri($"https://github.com/{owner}/{repository}.git");
        return new GitHubSkillSource(repositoryUri, reference, subpath,
            subpath.Length == 0 ? repositoryUri.AbsoluteUri : $"{repositoryUri.AbsoluteUri}#{subpath}");
    }

    private static string ResolveSubpath(string root, string subpath)
    {
        root = Path.GetFullPath(root);
        var resolved = subpath.Length == 0
            ? root
            : Path.GetFullPath(Path.Combine(root, subpath.Replace('/', Path.DirectorySeparatorChar)));
        if (!FolderAccessPolicy.IsInside(resolved, root))
            throw new UnauthorizedAccessException("The Skill subpath must stay inside its source folder.");
        if (!Directory.Exists(resolved))
            throw new DirectoryNotFoundException($"Skill folder was not found: {subpath}");
        return resolved;
    }

    private static string NormalizeScope(string scope) => scope.Trim().ToLowerInvariant() switch
    {
        "project" or "프로젝트" => "project",
        "global" or "전체" => "global",
        _ => throw new ArgumentException("Skill scope must be project or global.")
    };

    private static string NormalizeSubpath(string value)
    {
        value = value.Trim().Replace('\\', '/').Trim('/');
        if (value.Length == 0) return "";
        if (value.Length > 500 || Path.IsPathRooted(value) ||
            value.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Skill subpath must be a safe repository-relative folder.");
        return value;
    }

    private static string NormalizeName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or >= 64 || !SkillName().IsMatch(name))
            throw new InvalidOperationException("Skill name must be lowercase kebab-case and shorter than 64 characters.");
        return name;
    }

    private static (string? Name, string? Description) ReadMetadata(string frontmatter)
    {
        string? name = null;
        string? description = null;
        foreach (var raw in frontmatter.Split('\n').Take(40))
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

    private static void EnsureOrdinaryDirectory(string path, string message)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException(message);
    }

    private static void EnsureOrdinaryFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Skill packages cannot contain linked files.");
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillName();

    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubPart();

    [GeneratedRegex("^[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex GitReference();

    private sealed record SkillPackage(
        string Name, string Description, string Root, IReadOnlyList<SkillPackageFile> Files, long TotalBytes);
    private sealed record SkillPackageFile(string SourcePath, string RelativePath, long Length);
    private sealed record GitHubSkillSource(
        Uri RepositoryUri, string Reference, string Subpath, string DisplaySource);
}
