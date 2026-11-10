using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace silaa.Services;

internal sealed record ReleaseUpdateInfo(
    string Tag,
    string Version,
    Uri PackageUri,
    Uri ChecksumUri,
    string PackageName,
    string ReleasePage);

internal sealed record ReleaseUpdateCheck(
    bool UpdateAvailable,
    string CurrentVersion,
    ReleaseUpdateInfo? Release);

internal sealed partial class ReleaseUpdateService
{
    private const string ReleasesApi = "https://api.github.com/repos/TRIPLESONG90/silaa/releases?per_page=20";
    private const long MaximumPackageBytes = 500L * 1024 * 1024;
    private const string FailureMarkerFileName = "update-failed.txt";
    private readonly string _dataRoot;

    public ReleaseUpdateService(string dataRoot) => _dataRoot = Path.GetFullPath(dataRoot);

    public string? ReadFailureMarker()
    {
        var path = Path.Combine(_dataRoot, FailureMarkerFileName);
        try
        {
            if (!File.Exists(path)) return null;
            var message = File.ReadAllText(path).Trim();
            return message.Length == 0 ? "이전 자동 업데이트가 실패했습니다." : message[..Math.Min(message.Length, 500)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "이전 자동 업데이트 실패 기록을 읽을 수 없습니다.";
        }
    }

    public void ClearFailureMarker()
    {
        try { File.Delete(Path.Combine(_dataRoot, FailureMarkerFileName)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public static string CurrentVersion
    {
        get
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var value = string.IsNullOrWhiteSpace(informational)
                ? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0"
                : informational;
            return value.Split('+', 2)[0];
        }
    }

    public async Task<ReleaseUpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var bytes = await DownloadAsync(client, new Uri(ReleasesApi), 2 * 1024 * 1024, cancellationToken);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("GitHub Release 응답 형식이 올바르지 않습니다.");
        var current = CurrentVersion;
        ReleaseUpdateInfo? newest = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            var version = tag.TrimStart('v', 'V');
            if (!IsNewerVersion(current, version)) continue;
            var expectedPackage = $"silaa-{version}-win-x64.zip";
            var expectedChecksum = expectedPackage + ".sha256";
            Uri? packageUri = null;
            Uri? checksumUri = null;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var url = asset.GetProperty("browser_download_url").GetString();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                if (name == expectedPackage) packageUri = uri;
                else if (name == expectedChecksum) checksumUri = uri;
            }
            if (packageUri is null || checksumUri is null) continue;
            newest = new ReleaseUpdateInfo(
                tag,
                version,
                packageUri,
                checksumUri,
                expectedPackage,
                release.GetProperty("html_url").GetString() ?? $"https://github.com/TRIPLESONG90/silaa/releases/tag/{tag}");
            break;
        }
        return new ReleaseUpdateCheck(newest is not null, current, newest);
    }

    public async Task<string> DownloadAndVerifyAsync(
        ReleaseUpdateInfo release,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var checksumBytes = await DownloadAsync(client, release.ChecksumUri, 64 * 1024, cancellationToken);
        var checksumText = Encoding.UTF8.GetString(checksumBytes).Trim();
        var expected = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!Sha256Pattern().IsMatch(expected))
            throw new InvalidOperationException("Release 체크섬 파일이 올바르지 않습니다.");

        var package = await DownloadAsync(client, release.PackageUri, MaximumPackageBytes, cancellationToken);
        var actual = Convert.ToHexString(SHA256.HashData(package));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("다운로드한 업데이트의 SHA-256 체크섬이 일치하지 않습니다.");
        ValidatePackage(package);

        var updateRoot = Path.Combine(_dataRoot, "updates", release.Tag);
        Directory.CreateDirectory(updateRoot);
        var destination = Path.Combine(updateRoot, release.PackageName);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporary, package, cancellationToken);
        File.Move(temporary, destination, overwrite: true);
        return destination;
    }

    public bool LaunchInstaller(string packagePath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath) ||
            !Path.GetFileName(processPath).Equals("silaa.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var target = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var executable = Path.GetFullPath(processPath);
        var package = Path.GetFullPath(packagePath);
        if (!FolderAccessPolicy.IsInside(executable, target) || !File.Exists(package)) return false;

        const string script = """
            $ErrorActionPreference = 'Stop'
            $target = [IO.Path]::GetFullPath($env:SILAA_UPDATE_TARGET)
            $package = [IO.Path]::GetFullPath($env:SILAA_UPDATE_PACKAGE)
            $executable = [IO.Path]::GetFullPath($env:SILAA_UPDATE_EXECUTABLE)
            $log = [IO.Path]::GetFullPath($env:SILAA_UPDATE_LOG)
            $failureMarker = [IO.Path]::GetFullPath($env:SILAA_UPDATE_FAILURE_MARKER)
            try {
              Wait-Process -Id ([int]$env:SILAA_UPDATE_PARENT) -ErrorAction SilentlyContinue
              Get-CimInstance Win32_Process -Filter "Name = 'silaa.exe'" | Where-Object {
                $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath).Equals($executable, [StringComparison]::OrdinalIgnoreCase)
              } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
              Start-Sleep -Milliseconds 400
              $stage = Join-Path $target ('.silaa-update-' + [Guid]::NewGuid().ToString('N'))
              New-Item -ItemType Directory -Path $stage | Out-Null
              Expand-Archive -LiteralPath $package -DestinationPath $stage -Force
              $root = $stage
              $children = @(Get-ChildItem -LiteralPath $stage -Directory)
              if ($children.Count -eq 1 -and (Test-Path -LiteralPath (Join-Path $children[0].FullName 'silaa.exe'))) { $root = $children[0].FullName }
              if (-not (Test-Path -LiteralPath (Join-Path $root 'silaa.exe'))) { throw 'The update package does not contain silaa.exe.' }
              Get-ChildItem -LiteralPath $root -File -Recurse | ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($root, $_.FullName)
                $destination = [IO.Path]::GetFullPath((Join-Path $target $relative))
                if (-not $destination.StartsWith($target + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe update path.' }
                $parent = Split-Path -Parent $destination
                if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
                Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
              }
              Remove-Item -LiteralPath $stage -Recurse -Force
              if (Test-Path -LiteralPath $failureMarker) { Remove-Item -LiteralPath $failureMarker -Force }
              Start-Process -FilePath $executable -WorkingDirectory $target
            } catch {
              $failure = '[' + [DateTimeOffset]::Now.ToString('O') + '] ' + $_.Exception.Message
              Add-Content -LiteralPath $log -Value $failure
              Set-Content -LiteralPath $failureMarker -Value $failure -Encoding UTF8
              if (Test-Path -LiteralPath $executable) { Start-Process -FilePath $executable -WorkingDirectory $target }
            }
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-WindowStyle");
        start.ArgumentList.Add("Hidden");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(encoded);
        start.Environment["SILAA_UPDATE_TARGET"] = target;
        start.Environment["SILAA_UPDATE_PACKAGE"] = package;
        start.Environment["SILAA_UPDATE_EXECUTABLE"] = executable;
        start.Environment["SILAA_UPDATE_PARENT"] = Environment.ProcessId.ToString();
        start.Environment["SILAA_UPDATE_LOG"] = Path.Combine(_dataRoot, "update.log");
        start.Environment["SILAA_UPDATE_FAILURE_MARKER"] = Path.Combine(_dataRoot, FailureMarkerFileName);
        return Process.Start(start) is not null;
    }

    internal static bool IsNewerVersion(string current, string candidate)
    {
        if (!TryParseVersion(current, out var left) || !TryParseVersion(candidate, out var right)) return false;
        var main = left.Main.CompareTo(right.Main);
        if (main != 0) return main < 0;
        if (left.Prerelease is null) return false;
        if (right.Prerelease is null) return true;
        var label = string.Compare(left.Prerelease, right.Prerelease, StringComparison.OrdinalIgnoreCase);
        return label == 0 && right.PrereleaseNumber > left.PrereleaseNumber;
    }

    private static bool TryParseVersion(string value, out ParsedVersion parsed)
    {
        var match = VersionPattern().Match(value.Trim().TrimStart('v', 'V'));
        if (!match.Success || !Version.TryParse(match.Groups["main"].Value, out var main))
        {
            parsed = default!;
            return false;
        }
        parsed = new ParsedVersion(
            main,
            match.Groups["label"].Success ? match.Groups["label"].Value : null,
            match.Groups["number"].Success && int.TryParse(match.Groups["number"].Value, out var number) ? number : 0);
        return true;
    }

    private static void ValidatePackage(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        long expanded = 0;
        var hasExecutable = false;
        foreach (var entry in archive.Entries)
        {
            expanded += entry.Length;
            if (expanded > MaximumPackageBytes * 2)
                throw new InvalidOperationException("압축 해제된 업데이트가 안전 용량 제한을 초과합니다.");
            var path = entry.FullName.Replace('\\', '/');
            if (path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
                throw new InvalidOperationException("업데이트 ZIP에 안전하지 않은 경로가 있습니다.");
            if (Path.GetFileName(path).Equals("silaa.exe", StringComparison.OrdinalIgnoreCase)) hasExecutable = true;
        }
        if (!hasExecutable) throw new InvalidOperationException("업데이트 ZIP에 silaa.exe가 없습니다.");
    }

    private static async Task<byte[]> DownloadAsync(
        HttpClient client,
        Uri uri,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidOperationException("업데이트 다운로드 주소를 확인할 수 없습니다.");
        if (!IsTrustedGitHubHost(finalUri.Host))
            throw new InvalidOperationException("업데이트가 신뢰할 수 없는 호스트로 이동했습니다.");
        if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidOperationException("업데이트 다운로드가 안전 용량 제한을 초과합니다.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new InvalidOperationException("업데이트 다운로드가 안전 용량 제한을 초과합니다.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static bool IsTrustedGitHubHost(string host) =>
        host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        }) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("silaa-update/0.9");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private sealed record ParsedVersion(Version Main, string? Prerelease, int PrereleaseNumber);

    [GeneratedRegex("^(?<main>\\d+\\.\\d+\\.\\d+)(?:-(?<label>[a-z][a-z0-9-]*)\\.(?<number>\\d+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
