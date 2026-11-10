using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Win32;

namespace silaa.Services;

internal sealed class PythonEnvironmentTools
{
    public IList<AITool> CreateFunctions() =>
    [
        AIFunctionFactory.Create((Func<CancellationToken, Task<string>>)InspectAsync,
            "inspect_python_environment",
            "Detect Python on this PC without guessing. Checks the live process/user/system PATH, python/python3/py launchers, " +
            "standard per-user locations, Windows registry installations, the resolved interpreter path, version, and `-m pip`. " +
            "The returned interpreter is ranked to prefer a user Python over application-bundled runtimes such as LibreOffice. " +
            "Call this before saying Python or pip is missing, and use the returned absolute interpreter instead of a bare `python` command.")
    ];

    public async Task<string> InspectAsync(CancellationToken cancellationToken = default)
    {
        var candidates = (await WhereCandidatesAsync(cancellationToken))
            .Concat(CandidateExecutables())
            .DistinctBy(candidate => candidate.Path, OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();
        var results = new List<PythonProbe>();
        foreach (var candidate in candidates)
        {
            if (candidate.Path.Contains("\\Microsoft\\WindowsApps\\", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(CreateProbe(candidate, false, "", "", false,
                    "Windows App Execution Alias; not treated as a verified interpreter."));
                continue;
            }
            results.Add(await ProbeAsync(candidate, cancellationToken));
        }
        var working = results.Where(result => result.Working)
            .OrderByDescending(result => result.Priority)
            .ThenByDescending(result => result.PipAvailable)
            .FirstOrDefault();
        return JsonSerializer.Serialize(new
        {
            detected = working is not null,
            interpreter = working?.ResolvedInterpreter ?? "",
            command = working?.Path ?? "",
            version = working?.Version ?? "",
            pipAvailable = working?.PipAvailable ?? false,
            pip = working?.Pip ?? "",
            selection = working is null ? "none" : working.BundledApplication
                ? "Only an application-bundled Python runtime was verified. Prefer installing or selecting a normal user Python before changing packages."
                : "Recommended user/system interpreter. Use this absolute path for Python and `-m pip` commands.",
            effectivePath = WorkspaceTools.EffectiveExecutablePath(),
            candidates = results,
            diagnosis = working is not null
                ? working.PipAvailable
                    ? "A working Python interpreter and its pip module were verified."
                    : "A working Python interpreter was verified, but `python -m pip` was not available."
                : "No working interpreter was verified from PATH, standard locations, or PythonCore registry entries. This result does not justify claiming that Python is uninstalled without showing these probe results."
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task<IReadOnlyList<PythonCandidate>> WhereCandidatesAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var candidates = new List<PythonCandidate>();
        foreach (var name in new[] { "python", "python3", "py" })
        {
            var start = new ProcessStartInfo
            {
                FileName = "where.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(name);
            WorkspaceTools.ConfigureSanitizedEnvironment(start);
            try
            {
                using var process = Process.Start(start);
                if (process is null) continue;
                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0) continue;
                foreach (var path in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (File.Exists(path))
                        candidates.Add(new PythonCandidate(Path.GetFullPath(path), $"where.exe {name}",
                            Path.GetFileName(path).Equals("py.exe", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // PATH enumeration and registry probing below remain available as deterministic fallbacks.
            }
        }
        return candidates;
    }

    private static IEnumerable<PythonCandidate> CandidateExecutables()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        foreach (var directory in WorkspaceTools.EffectiveExecutablePath()
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in OperatingSystem.IsWindows()
                         ? new[] { "python.exe", "python3.exe", "py.exe" }
                         : new[] { "python3", "python" })
            {
                var path = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(path) && seen.Add(Path.GetFullPath(path)))
                    yield return new PythonCandidate(Path.GetFullPath(path), "PATH", name.Equals("py.exe", StringComparison.OrdinalIgnoreCase));
            }
        }
        if (!OperatingSystem.IsWindows()) yield break;

        foreach (var path in RegistryPythonExecutables())
            if (File.Exists(path) && seen.Add(Path.GetFullPath(path)))
                yield return new PythonCandidate(Path.GetFullPath(path), "PythonCore registry", false);

        var localPrograms = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (localPrograms.Length > 0)
        {
            var pythonRoot = Path.Combine(localPrograms, "Programs", "Python");
            if (Directory.Exists(pythonRoot))
            {
                foreach (var path in Directory.EnumerateFiles(pythonRoot, "python.exe", SearchOption.AllDirectories)
                             .Where(path => !path.Contains("\\Scripts\\", StringComparison.OrdinalIgnoreCase)))
                    if (seen.Add(Path.GetFullPath(path)))
                        yield return new PythonCandidate(Path.GetFullPath(path), "per-user Python install", false);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegistryPythonExecutables()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            RegistryKey? root = null;
            try
            {
                root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"Software\Python\PythonCore");
                if (root is null) continue;
                foreach (var version in root.GetSubKeyNames())
                {
                    using var install = root.OpenSubKey($@"{version}\InstallPath");
                    if (install?.GetValue("") is not string directory || directory.Length == 0) continue;
                    yield return Path.Combine(Environment.ExpandEnvironmentVariables(directory), "python.exe");
                }
            }
            finally { root?.Dispose(); }
        }
    }

    private static async Task<PythonProbe> ProbeAsync(PythonCandidate candidate, CancellationToken cancellationToken)
    {
        var prefix = candidate.IsLauncher ? new[] { "-3" } : Array.Empty<string>();
        var version = await RunAsync(candidate.Path, [.. prefix, "--version"], cancellationToken);
        if (!version.Success)
            return CreateProbe(candidate, false, "", "", false, version.Output);
        var resolved = await RunAsync(candidate.Path,
            [.. prefix, "-c", "import sys; print(sys.executable)"], cancellationToken);
        var pip = await RunAsync(candidate.Path, [.. prefix, "-m", "pip", "--version"], cancellationToken);
        return CreateProbe(
            candidate,
            true,
            version.Output,
            resolved.Success ? resolved.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? candidate.Path : candidate.Path,
            pip.Success,
            pip.Output);
    }

    private static PythonProbe CreateProbe(PythonCandidate candidate, bool working, string version,
        string resolvedInterpreter, bool pipAvailable, string pip)
    {
        var bundled = IsBundledApplicationPython(candidate.Path) || IsBundledApplicationPython(resolvedInterpreter);
        var priority = working ? CandidatePriority(candidate.Path, candidate.Source, pipAvailable, bundled, candidate.IsLauncher) : int.MinValue;
        return new PythonProbe(
            candidate.Path,
            candidate.Source,
            working,
            version,
            resolvedInterpreter,
            pipAvailable,
            pip,
            bundled,
            priority);
    }

    internal static int CandidatePriority(string path, string source, bool pipAvailable, bool bundled, bool isLauncher)
    {
        var score = pipAvailable ? 100 : 0;
        if (source.Equals("per-user Python install", StringComparison.OrdinalIgnoreCase)) score += 70;
        if (source.Equals("PythonCore registry", StringComparison.OrdinalIgnoreCase)) score += 50;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (userProfile.Length > 0 && path.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase)) score += 40;
        if (isLauncher) score -= 5;
        if (bundled) score -= 1_000;
        return score;
    }

    private static bool IsBundledApplicationPython(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('/', '\\');
        return new[]
        {
            "\\LibreOffice\\program\\", "\\Blender Foundation\\", "\\Autodesk\\",
            "\\ArcGIS\\", "\\GIMP ", "\\Inkscape\\"
        }.Any(marker => normalized.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<ProbeResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        WorkspaceTools.ConfigureSanitizedEnvironment(start);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return new ProbeResult(false, "Process could not be started.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                return new ProbeResult(false, "Probe timed out after five seconds.");
            }
            var text = ((await output) + "\n" + (await error)).Trim();
            if (text.Length > 1_000) text = text[..1_000];
            return new ProbeResult(process.ExitCode == 0, text);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new ProbeResult(false, exception.Message);
        }
    }

    private sealed record PythonCandidate(string Path, string Source, bool IsLauncher);
    private sealed record ProbeResult(bool Success, string Output);
    private sealed record PythonProbe(
        [property: Description("Executable or launcher path that was probed.")] string Path,
        string Source,
        bool Working,
        string Version,
        string ResolvedInterpreter,
        bool PipAvailable,
        string Pip,
        bool BundledApplication,
        int Priority);
}
