using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace silaa.Services;

internal sealed class WorkspaceTools(
    string workspace,
    VaultStore? vault = null,
    IReadOnlyList<string>? allowedDirectories = null,
    SecurityPolicyContext? security = null)
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules", "publish"
    };
    private readonly string _root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private readonly List<string> _allowedRoots = FolderAccessPolicy.Combine(workspace, allowedDirectories).ToList();

    public IList<AITool> CreateFunctions()
    {
        var tools = CreateReadOnlyFunctions().ToList();
        tools.Add(AIFunctionFactory.Create((Func<string, string, string>)WriteFileTool, "write_file", "Create or replace a text file inside the workspace or an explicitly allowed folder."));
        tools.Add(AIFunctionFactory.Create((Func<string, string, string, string>)ReplaceTextTool, "replace_text", "Replace one exact text block in a file inside the workspace or an explicitly allowed folder."));
        tools.Add(AIFunctionFactory.Create((Func<IList<WorkspaceTextEdit>, string>)ApplyEditsTool, "apply_edits", "Apply one or more exact text replacements after validating every edit. Use this for coordinated multi-file changes."));
        tools.Add(AIFunctionFactory.Create((Func<string, int, CancellationToken, Task<string>>)RunCommandAsync, "run_command", "Run a shell command in the workspace and return its exit code and output."));
        if (vault is not null)
        {
            tools.Add(AIFunctionFactory.Create((Func<string>)ListVaultEntries, "list_vault_entries",
                "List available Vault entry names. Secret values are never returned."));
            tools.Add(AIFunctionFactory.Create((Func<string, string, string, string, int, CancellationToken, Task<string>>)RunCommandWithVaultToolAsync,
                "run_command_with_vault", "Run a workspace command with one Vault secret injected as an environment variable. For network use, provide the exact approved host and port. The secret is never returned and is redacted from output."));
        }
        return tools;
    }

    public IList<AITool> CreateReadOnlyFunctions() =>
        new List<AITool>
        {
            AIFunctionFactory.Create((Func<string>)ListAllowedDirectories, "list_allowed_directories", "Show the workspace and every additional folder available to file tools."),
            AIFunctionFactory.Create((Func<string, string>)ListFilesTool, "list_files", "List files inside the workspace or an explicitly allowed folder. Generated and dependency directories are omitted."),
            AIFunctionFactory.Create((Func<string, string, int, string>)GlobFilesTool, "glob_files", "Find files by a recursive glob such as **/*.cs inside the workspace or an explicitly allowed folder."),
            AIFunctionFactory.Create((Func<string, int, int, string>)ReadFileTool, "read_file", "Read a UTF-8 text file inside the workspace or an explicitly allowed folder with line numbers."),
            AIFunctionFactory.Create((Func<string, string>)InspectFileMetadataTool, "inspect_file_metadata", "Safely inspect file type and factual metadata without decoding binary content as text. Use this first for DLL, EXE, unknown, or other binary files."),
            AIFunctionFactory.Create((Func<string, string, string>)SearchTextTool, "search_text", "Search for literal text inside the workspace or an explicitly allowed folder."),
            AIFunctionFactory.Create((Func<string>)GitStatus, "git_status", "Show the current Git branch and concise working-tree status without changing the repository."),
            AIFunctionFactory.Create((Func<string, string, string>)GitDiffTool, "git_diff", "Show a working-tree or staged Git diff, optionally limited to one workspace path.")
        };

    private string ListFilesTool(
        [Description("Workspace-relative directory, or an absolute directory that was explicitly allowed. Use an empty string for the workspace root.")] string path = "") =>
        AccessResult(() => ListFiles(path));

    private string GlobFilesTool(
        [Description("Recursive glob pattern, for example **/*.cs, src/**/*.axaml, or README?.md.")] string pattern,
        [Description("Workspace-relative directory, or an absolute directory that was explicitly allowed. Use an empty string for the workspace root.")] string path = "",
        [Description("Maximum results from 1 to 1000.")] int maxResults = 500) =>
        AccessResult(() => GlobFiles(pattern, path, maxResults));

    private string ReadFileTool(
        [Description("Workspace-relative file path, or an absolute path inside an explicitly allowed folder.")] string path,
        [Description("First line, 1-based.")] int startLine = 1,
        [Description("Maximum number of lines.")] int lineCount = 400) =>
        AccessResult(() => ReadFile(path, startLine, lineCount));

    private string InspectFileMetadataTool(
        [Description("Workspace-relative file path, or an absolute path inside an explicitly allowed folder.")] string path) =>
        AccessResult(() => InspectFileMetadata(path));

    private string SearchTextTool(
        [Description("Literal text to find.")] string query,
        [Description("Workspace-relative path, or an absolute path inside an explicitly allowed folder. Use an empty string for the workspace root.")] string path = "") =>
        AccessResult(() => SearchText(query, path));

    private string WriteFileTool(
        [Description("Workspace-relative file path, or an absolute path inside an explicitly allowed folder.")] string path,
        [Description("Complete new file content.")] string content) =>
        AccessResult(() => WriteFile(path, content));

    private string ReplaceTextTool(
        [Description("Workspace-relative file path, or an absolute path inside an explicitly allowed folder.")] string path,
        [Description("Exact existing text; it must occur once.")] string oldText,
        [Description("Replacement text.")] string newText) =>
        AccessResult(() => ReplaceText(path, oldText, newText));

    private string ApplyEditsTool(
        [Description("Exact replacements. Every oldText must occur exactly once in the staged content of its file.")] IList<WorkspaceTextEdit> edits) =>
        AccessResult(() => ApplyEdits(edits));

    private string GitDiffTool(
        [Description("Diff scope: working or staged.")] string scope = "working",
        [Description("Optional workspace-relative file or directory. Use an empty string for the whole repository.")] string path = "") =>
        AccessResult(() => GitDiff(scope, path));

    private static string AccessResult(Func<string> action)
    {
        try { return action(); }
        catch (UnauthorizedAccessException exception)
        {
            return $"ACCESS_DENIED: {exception.Message} Call request_folder_access with the required absolute folder and a concise reason.";
        }
    }

    public string ListAllowedDirectories()
    {
        var lines = new List<string> { $"workspace (primary): {_root}" };
        lines.AddRange(_allowedRoots.Select(root => $"additional: {root}"));
        lines.Add("Relative file paths resolve from the primary workspace. Shell commands also start there.");
        return string.Join('\n', lines);
    }

    internal bool HasDirectoryAccess(string path)
    {
        var full = FolderAccessPolicy.NormalizePath(path);
        security?.EnsurePathAllowed(full, false);
        return FolderAccessPolicy.IsInside(full, _root) ||
               _allowedRoots.Any(root => FolderAccessPolicy.IsInside(full, root));
    }

    internal string ResolveAccessiblePath(string path, bool mustExist, bool write = false) =>
        Resolve(path, mustExist, write);

    internal void GrantDirectoryAccess(string path)
    {
        var full = FolderAccessPolicy.NormalizePath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Directory not found.");
        if (HasDirectoryAccess(full)) return;
        if (IsReparsePoint(full))
            throw new UnauthorizedAccessException("Reparse-point folders cannot be granted as access roots.");
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (!_allowedRoots.Contains(full, comparison)) _allowedRoots.Add(full);
    }

    public string ListFiles([Description("Workspace-relative directory, or an absolute directory that was explicitly allowed. Use an empty string for the workspace root.")] string path = "")
    {
        var target = Resolve(path, false);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException("Directory not found.");
        var files = EnumerateFiles(target).Take(500).Select(DisplayPath).ToList();
        return files.Count == 0 ? "No files found." : string.Join('\n', files);
    }

    public string GlobFiles(
        [Description("Recursive glob pattern, for example **/*.cs, src/**/*.axaml, or README?.md.")] string pattern,
        [Description("Workspace-relative directory, or an absolute directory that was explicitly allowed. Use an empty string for the workspace root.")] string path = "",
        [Description("Maximum results from 1 to 1000.")] int maxResults = 500)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 300)
            throw new ArgumentException("Glob pattern must contain 1 to 300 characters.");
        var target = Resolve(path, false);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException("Directory not found.");
        maxResults = Math.Clamp(maxResults, 1, 1000);
        var matcher = CreateGlobRegex(pattern);
        var matches = new List<string>();
        foreach (var file in EnumerateFiles(target))
        {
            var relative = Path.GetRelativePath(target, file).Replace('\\', '/');
            if (!matcher.IsMatch(relative)) continue;
            matches.Add(DisplayPath(file));
            if (matches.Count >= maxResults)
                return string.Join('\n', matches) + "\n[truncated]";
        }
        return matches.Count == 0 ? "No files found." : string.Join('\n', matches);
    }

    public string ReadFile(
        [Description("Workspace-relative file path, or an absolute path inside an explicitly allowed folder.")] string path,
        [Description("First line, 1-based.")] int startLine = 1,
        [Description("Maximum number of lines.")] int lineCount = 400)
    {
        var target = Resolve(path, true);
        if (IsProbablyBinary(target))
            throw new InvalidOperationException(
                "BINARY_FILE: read_file only accepts UTF-8 text. Use inspect_file_metadata for binaries, or read_document for a supported document format.");
        startLine = Math.Max(1, startLine);
        lineCount = Math.Clamp(lineCount, 1, 1000);
        var lines = File.ReadLines(target).Skip(startLine - 1).Take(lineCount)
            .Select((line, index) => $"{startLine + index,6}: {line}");
        var result = string.Join('\n', lines);
        return result.Length > 100_000 ? result[..100_000] + "\n[truncated]" : result;
    }

    public string InspectFileMetadata(
        [Description("Workspace-relative file path, or an absolute path inside an explicitly allowed folder.")] string path)
    {
        var target = Resolve(path, true);
        var info = new FileInfo(target);
        var metadata = new Dictionary<string, object?>
        {
            ["name"] = info.Name,
            ["extension"] = info.Extension,
            ["sizeBytes"] = info.Length,
            ["lastWriteUtc"] = info.LastWriteTimeUtc.ToString("O"),
            ["kind"] = IsProbablyBinary(target) ? "binary" : "text"
        };

        using var stream = File.OpenRead(target);
        if (stream.Length >= 2 && stream.ReadByte() == 'M' && stream.ReadByte() == 'Z')
        {
            stream.Position = 0;
            try
            {
                using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                metadata["kind"] = "portable-executable";
                metadata["peMachine"] = pe.PEHeaders.CoffHeader.Machine.ToString();
                metadata["hasClrMetadata"] = pe.HasMetadata;
                if (pe.PEHeaders.CorHeader is { } corHeader)
                    metadata["corFlags"] = corHeader.Flags.ToString();
                if (pe.HasMetadata)
                {
                    var reader = pe.GetMetadataReader();
                    metadata["typeDefinitionCount"] = reader.TypeDefinitions.Count;
                    if (reader.IsAssembly)
                    {
                        var assembly = reader.GetAssemblyDefinition();
                        metadata["assemblyName"] = reader.GetString(assembly.Name);
                        metadata["assemblyVersion"] = assembly.Version.ToString();
                    }
                }
            }
            catch (BadImageFormatException)
            {
                metadata["peMetadataError"] = "Invalid or unsupported PE metadata.";
            }
        }

        return JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool IsProbablyBinary(string path)
    {
        var buffer = new byte[8192];
        using var stream = File.OpenRead(path);
        var length = stream.Read(buffer, 0, buffer.Length);
        if (length == 0) return false;
        if (Array.IndexOf(buffer, (byte)0, 0, length) >= 0) return true;
        try
        {
            _ = new UTF8Encoding(false, true).GetString(buffer, 0, length);
            return false;
        }
        catch (DecoderFallbackException)
        {
            return true;
        }
    }

    public string SearchText(
        [Description("Literal text to find.")] string query,
        [Description("Workspace-relative path, or an absolute path inside an explicitly allowed folder. Use an empty string for the workspace root.")] string path = "")
    {
        if (query.Length is 0 or > 1000) throw new ArgumentException("Query must contain 1 to 1000 characters.");
        var target = Resolve(path, false);
        var files = File.Exists(target) ? new[] { target } : EnumerateFiles(target);
        var matches = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNumber++;
                    if (!line.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    matches.Add($"{DisplayPath(file)}:{lineNumber}: {line.Trim()}");
                    if (matches.Count >= 300) return string.Join('\n', matches) + "\n[truncated]";
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return matches.Count == 0 ? "No matches found." : string.Join('\n', matches);
    }

    public string WriteFile(
        [Description("Workspace-relative file path, or an absolute path inside an explicitly allowed folder.")] string path,
        [Description("Complete new file content.")] string content)
    {
        if (content.Length > 2_000_000) throw new ArgumentException("File content is too large.");
        var target = Resolve(path, false, true);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".silaa.tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, target, true);
        return $"Wrote {DisplayPath(target)} ({content.Length} characters).";
    }

    public string ReplaceText(
        [Description("Workspace-relative file path, or an absolute path inside an explicitly allowed folder.")] string path,
        [Description("Exact existing text; it must occur once.")] string oldText,
        [Description("Replacement text.")] string newText)
    {
        if (oldText.Length == 0) throw new ArgumentException("oldText cannot be empty.");
        var target = Resolve(path, true, true);
        var content = File.ReadAllText(target);
        var first = content.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0) throw new InvalidOperationException("oldText was not found.");
        if (content.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("oldText occurs more than once; provide a larger unique block.");
        return WriteFile(path, content[..first] + newText + content[(first + oldText.Length)..]);
    }

    public string ApplyEdits(
        [Description("Exact replacements. Every oldText must occur exactly once in the staged content of its file.")] IList<WorkspaceTextEdit> edits)
    {
        if (edits.Count is 0 or > 100) throw new ArgumentException("Provide 1 to 100 edits.");
        var staged = new Dictionary<string, string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            if (string.IsNullOrWhiteSpace(edit.Path)) throw new ArgumentException("Every edit needs a path.");
            if (string.IsNullOrEmpty(edit.OldText)) throw new ArgumentException("oldText cannot be empty.");
            var target = Resolve(edit.Path, true, true);
            var content = staged.TryGetValue(target, out var current) ? current : File.ReadAllText(target);
            var first = content.IndexOf(edit.OldText, StringComparison.Ordinal);
            if (first < 0) throw new InvalidOperationException($"oldText was not found in {DisplayPath(target)}.");
            if (content.IndexOf(edit.OldText, first + edit.OldText.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException($"oldText occurs more than once in {DisplayPath(target)}; provide a larger unique block.");
            var updated = content[..first] + edit.NewText + content[(first + edit.OldText.Length)..];
            if (updated.Length > 2_000_000) throw new ArgumentException($"Edited file is too large: {DisplayPath(target)}.");
            staged[target] = updated;
        }

        var temporaryFiles = new List<(string Target, string Temporary)>();
        try
        {
            foreach (var (target, content) in staged)
            {
                var temporary = target + $".silaa.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temporary, content, new UTF8Encoding(false));
                temporaryFiles.Add((target, temporary));
            }
            foreach (var (target, temporary) in temporaryFiles) File.Move(temporary, target, true);
        }
        finally
        {
            foreach (var (_, temporary) in temporaryFiles)
                if (File.Exists(temporary)) File.Delete(temporary);
        }
        return $"Applied {edits.Count} edit(s) to {staged.Count} file(s):\n" +
               string.Join('\n', staged.Keys.Select(DisplayPath));
    }

    public string GitStatus() => RunGit("status", "--short", "--branch");

    public string GitDiff(
        [Description("Diff scope: working or staged.")] string scope = "working",
        [Description("Optional workspace-relative file or directory. Use an empty string for the whole repository.")] string path = "")
    {
        var normalizedScope = scope.Trim().ToLowerInvariant();
        if (normalizedScope is not ("working" or "staged"))
            throw new ArgumentException("scope must be working or staged.");
        var arguments = new List<string> { "diff", "--no-ext-diff", "--no-color" };
        if (normalizedScope == "staged") arguments.Add("--cached");
        arguments.Add("--");
        if (!string.IsNullOrWhiteSpace(path))
        {
            var target = Resolve(path, false);
            if (!FolderAccessPolicy.IsInside(target, _root))
                throw new UnauthorizedAccessException("Git paths must be inside the primary workspace.");
            arguments.Add(Path.GetRelativePath(_root, target));
        }
        var result = RunGit(arguments.ToArray());
        return string.IsNullOrWhiteSpace(result) ? "No differences." : result;
    }

    public async Task<string> RunCommandAsync(
        [Description("Command to run.")] string command,
        [Description("Timeout from 1 to 120 seconds.")] int timeoutSeconds = 60,
        CancellationToken cancellationToken = default)
        => await RunCommandCoreAsync(command, timeoutSeconds, null, null, cancellationToken);

    public string ListVaultEntries() => vault is null
        ? "Vault is unavailable."
        : vault.List().Count == 0 ? "Vault is empty." : string.Join('\n', vault.List().Select(item => item.Name));

    public async Task<string> RunCommandWithVaultAsync(
        [Description("Command to run without embedding any secret value.")] string command,
        [Description("Vault entry name returned by list_vault_entries.")] string vaultEntry,
        [Description("Environment variable name, for example GITHUB_TOKEN.")] string environmentVariable,
        [Description("Timeout from 1 to 120 seconds.")] int timeoutSeconds = 60,
        CancellationToken cancellationToken = default)
        => await RunCommandWithVaultCoreAsync(command, vaultEntry, environmentVariable, "", timeoutSeconds, cancellationToken);

    private async Task<string> RunCommandWithVaultToolAsync(
        [Description("Command to run without embedding any secret value.")] string command,
        [Description("Vault entry name returned by list_vault_entries.")] string vaultEntry,
        [Description("Environment variable name, for example GITHUB_TOKEN.")] string environmentVariable,
        [Description("Exact approved destination as host:port for network use, or empty for a local-only command.")] string destinationEndpoint = "",
        [Description("Timeout from 1 to 120 seconds.")] int timeoutSeconds = 60,
        CancellationToken cancellationToken = default)
        => await RunCommandWithVaultCoreAsync(command, vaultEntry, environmentVariable, destinationEndpoint, timeoutSeconds, cancellationToken);

    private async Task<string> RunCommandWithVaultCoreAsync(
        string command,
        string vaultEntry,
        string environmentVariable,
        string destinationEndpoint,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (vault is null) throw new InvalidOperationException("Vault is unavailable.");
        if (environmentVariable.Length is 0 or > 80 || !environmentVariable.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') || char.IsDigit(environmentVariable[0]))
            throw new ArgumentException("Use a valid environment variable name.");
        var secret = vault.Get(vaultEntry) ?? throw new InvalidOperationException(
            $"VAULT_REQUIRED: Vault entry '{vaultEntry}' was not found. Call request_vault_secret with this exact entry name and a plain-language reason.");
        return await RunCommandCoreAsync(command, timeoutSeconds, environmentVariable, secret, cancellationToken,
            string.IsNullOrWhiteSpace(destinationEndpoint) ? null : destinationEndpoint);
    }

    private async Task<string> RunCommandCoreAsync(string command, int timeoutSeconds, string? environmentVariable,
        string? secret, CancellationToken cancellationToken, string? credentialEndpoint = null)
    {
        if (command.Length is 0 or > 20_000) throw new ArgumentException("Command is empty or too long.");
        if (security is not null)
        {
            var decision = security.InspectCommand(command, credentialEndpoint, secret is not null);
            if (!decision.Allowed) return decision.Message;
        }
        timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 120);
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
        }
        else start.ArgumentList.Add("-lc");
        start.ArgumentList.Add(command);
        ConfigureSanitizedEnvironment(start);
        security?.ApplyNetworkEnvironment(start);
        if (environmentVariable is not null && secret is not null) start.Environment[environmentVariable] = secret;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start command.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            cancellationToken.ThrowIfCancellationRequested();
            return $"Command timed out after {timeoutSeconds} seconds.";
        }
        var output = $"Exit code: {process.ExitCode}\n{await stdout}{await stderr}";
        if (!string.IsNullOrEmpty(secret)) output = output.Replace(secret, "[VAULT_SECRET]", StringComparison.Ordinal);
        return output.Length > 100_000 ? output[..100_000] + "\n[truncated]" : output;
    }

    private string RunGit(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        ConfigureSanitizedEnvironment(start);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Git.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                return "Git command timed out after 30 seconds.";
            }
            var output = (stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()).TrimEnd();
            if (process.ExitCode != 0) return $"Git exit code: {process.ExitCode}\n{output}";
            return output;
        }
        catch (Win32Exception exception)
        {
            return $"Git is unavailable: {exception.Message}";
        }
    }

    private IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> directories;
            IEnumerable<string> files;
            try
            {
                directories = Directory.EnumerateDirectories(directory).ToArray();
                files = Directory.EnumerateFiles(directory).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in directories)
                if (!IgnoredDirectories.Contains(Path.GetFileName(child)) && !IsReparsePoint(child)) pending.Push(child);
            foreach (var file in files)
                if (security?.IsProtectedPath(file) != true) yield return file;
        }
    }

    private string Resolve(string path, bool mustExist, bool write = false)
    {
        var full = Path.IsPathRooted(path)
            ? FolderAccessPolicy.NormalizePath(path)
            : Path.GetFullPath(Path.Combine(_root, path));
        var permitted = FolderAccessPolicy.IsInside(full, _root) ||
                        _allowedRoots.Any(root => FolderAccessPolicy.IsInside(full, root));
        if (!permitted)
            throw new UnauthorizedAccessException("Path is outside the workspace and allowed folders.");
        security?.EnsurePathAllowed(full, write);
        EnsureNoReparsePoint(full);
        if (mustExist && !File.Exists(full)) throw new FileNotFoundException("File not found.", path);
        return full;
    }

    private string DisplayPath(string path) => FolderAccessPolicy.IsInside(path, _root)
        ? Path.GetRelativePath(_root, path).Replace('\\', '/')
        : Path.GetFullPath(path).Replace('\\', '/');

    private void EnsureNoReparsePoint(string path)
    {
        var root = FolderAccessPolicy.IsInside(path, _root)
            ? _root
            : _allowedRoots.First(candidate => FolderAccessPolicy.IsInside(path, candidate));
        var current = root;
        var relative = Path.GetRelativePath(root, path);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) && IsReparsePoint(current))
                throw new UnauthorizedAccessException("Reparse points are not followed across folder access boundaries.");
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
    }

    internal static void ConfigureSanitizedEnvironment(ProcessStartInfo start)
    {
        var source = Environment.GetEnvironmentVariables();
        start.Environment.Clear();
        foreach (var name in new[]
                 {
                     "PATH", "PATHEXT", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE",
                     "HOMEDRIVE", "HOMEPATH", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
                     "DOTNET_ROOT", "DOTNET_CLI_HOME", "NUGET_PACKAGES", "LANG", "LC_ALL"
                 })
        {
            if (source[name] is string value && value.Length > 0) start.Environment[name] = value;
        }
        start.Environment["PATH"] = EffectiveExecutablePath();
        start.Environment["SILAA_SECURITY_MODE"] = "enforce";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
    }

    internal static string EffectiveExecutablePath()
    {
        var values = new List<string?> { Environment.GetEnvironmentVariable("PATH") };
        if (OperatingSystem.IsWindows())
        {
            values.Add(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine));
            values.Add(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User));
        }
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var directories = new HashSet<string>(comparer);
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            foreach (var item in value!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var expanded = Environment.ExpandEnvironmentVariables(item.Trim('"'));
                if (expanded.Length > 0) directories.Add(expanded);
            }
        }
        return string.Join(Path.PathSeparator, directories);
    }

    private static Regex CreateGlobRegex(string pattern)
    {
        var normalized = pattern.Trim().Replace('\\', '/').TrimStart('/');
        var expression = new StringBuilder("^");
        if (!normalized.Contains('/')) expression.Append("(?:.*/)?");
        for (var index = 0; index < normalized.Length; index++)
        {
            var character = normalized[index];
            if (character == '*')
            {
                var recursive = index + 1 < normalized.Length && normalized[index + 1] == '*';
                if (!recursive) expression.Append("[^/]*");
                else
                {
                    index++;
                    if (index + 1 < normalized.Length && normalized[index + 1] == '/')
                    {
                        expression.Append("(?:.*/)?");
                        index++;
                    }
                    else expression.Append(".*");
                }
            }
            else if (character == '?') expression.Append("[^/]");
            else expression.Append(Regex.Escape(character.ToString()));
        }
        expression.Append('$');
        var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
        if (OperatingSystem.IsWindows()) options |= RegexOptions.IgnoreCase;
        return new Regex(expression.ToString(), options, TimeSpan.FromSeconds(1));
    }
}

internal sealed class WorkspaceTextEdit
{
    [Description("Workspace-relative file path, or an absolute path inside an explicitly allowed folder.")]
    public string Path { get; init; } = "";

    [Description("Exact existing text; it must occur once in the staged file content.")]
    public string OldText { get; init; } = "";

    [Description("Replacement text.")]
    public string NewText { get; init; } = "";
}
