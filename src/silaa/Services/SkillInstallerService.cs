using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
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
    string Source,
    bool Verified);

internal sealed partial class SkillInstallerService
{
    private const int MaximumFiles = 500;
    private const long MaximumBytes = 50L * 1024 * 1024;
    private const long MaximumDownloadBytes = MaximumBytes + 5L * 1024 * 1024;
    private const int MaximumRedirects = 5;
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

    public static bool IsGitLabSource(string source) =>
        Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" &&
        uri.Host.Contains("gitlab", StringComparison.OrdinalIgnoreCase) &&
        uri.UserInfo.Length == 0 &&
        uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 2;

    public static bool IsWebSource(string source) =>
        Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" &&
        !string.IsNullOrWhiteSpace(uri.Host) && uri.UserInfo.Length == 0;

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
            else if (IsGitLabSource(source))
            {
                var gitlab = ParseGitLabSource(source, subpath);
                var networkDecision = _security?.InspectNetworkUri(gitlab.ArchiveUri, "skill.install");
                if (networkDecision is { Allowed: false })
                    throw new InvalidOperationException(networkDecision.Message);
                temporaryRoot = Path.Combine(Path.GetTempPath(), $"silaa-skill-{Guid.NewGuid():N}");
                Directory.CreateDirectory(temporaryRoot);
                var downloaded = await DownloadWebSkillAsync(
                    gitlab.ArchiveUri, temporaryRoot, gitlab.Subpath, cancellationToken);
                skillDirectory = ResolveSubpath(downloaded.Directory, gitlab.Subpath);
                sourceLabel = gitlab.DisplaySource;
            }
            else if (IsWebSource(source))
            {
                var sourceUri = new Uri(source, UriKind.Absolute);
                temporaryRoot = Path.Combine(Path.GetTempPath(), $"silaa-skill-{Guid.NewGuid():N}");
                Directory.CreateDirectory(temporaryRoot);
                var downloaded = await DownloadWebSkillAsync(sourceUri, temporaryRoot, subpath, cancellationToken);
                skillDirectory = ResolveSubpath(downloaded.Directory, subpath);
                sourceLabel = downloaded.Source.AbsoluteUri;
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

    private async Task<WebSkillSource> DownloadWebSkillAsync(
        Uri sourceUri,
        string temporaryRoot,
        string explicitSubpath,
        CancellationToken cancellationToken)
    {
        var first = await DownloadAsync(sourceUri, cancellationToken);
        if (LooksLikeZip(first.Bytes))
        {
            var extracted = Path.Combine(temporaryRoot, "package");
            ExtractZip(first.Bytes, extracted);
            return new WebSkillSource(
                explicitSubpath.Length == 0 ? FindSingleSkillDirectory(extracted) : extracted,
                first.FinalUri);
        }
        if (LooksLikeTarGzip(first.Bytes))
        {
            var extracted = Path.Combine(temporaryRoot, "package");
            ExtractTarGzip(first.Bytes, extracted);
            return new WebSkillSource(
                explicitSubpath.Length == 0 ? FindSingleSkillDirectory(extracted) : extracted,
                first.FinalUri);
        }

        if (LooksLikeSkillMarkdown(first.MediaType, first.FinalUri, first.Bytes))
        {
            var directory = Path.Combine(temporaryRoot, "package");
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "SKILL.md"), first.Bytes, cancellationToken);
            return new WebSkillSource(directory, first.FinalUri);
        }

        if (!first.MediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"SKILL_FETCH_FAILED: The URL returned unsupported content ({first.MediaType}). Provide a Skill ZIP, TAR.GZ, SKILL.md, or an HTML page linking to one.");

        var html = DecodeText(first.Bytes, first.Charset);
        var candidate = FindPackageLink(first.FinalUri, html);
        if (candidate is null)
            return await CreateSkillFromWebPageAsync(first.FinalUri, html, temporaryRoot, cancellationToken);
        var package = await DownloadAsync(candidate, cancellationToken);
        if (LooksLikeZip(package.Bytes))
        {
            var extracted = Path.Combine(temporaryRoot, "package");
            ExtractZip(package.Bytes, extracted);
            return new WebSkillSource(
                explicitSubpath.Length == 0 ? FindSingleSkillDirectory(extracted) : extracted,
                package.FinalUri);
        }
        if (LooksLikeTarGzip(package.Bytes))
        {
            var extracted = Path.Combine(temporaryRoot, "package");
            ExtractTarGzip(package.Bytes, extracted);
            return new WebSkillSource(
                explicitSubpath.Length == 0 ? FindSingleSkillDirectory(extracted) : extracted,
                package.FinalUri);
        }
        if (LooksLikeSkillMarkdown(package.MediaType, package.FinalUri, package.Bytes))
        {
            var directory = Path.Combine(temporaryRoot, "package");
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "SKILL.md"), package.Bytes, cancellationToken);
            return new WebSkillSource(directory, package.FinalUri);
        }
        if (package.MediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            return await CreateSkillFromWebPageAsync(
                package.FinalUri,
                DecodeText(package.Bytes, package.Charset),
                temporaryRoot,
                cancellationToken);
        throw new InvalidOperationException(
            $"SKILL_FETCH_FAILED: The package link returned unsupported content ({package.MediaType}); nothing was installed.");
    }

    private static async Task<WebSkillSource> CreateSkillFromWebPageAsync(
        Uri source,
        string html,
        string temporaryRoot,
        CancellationToken cancellationToken)
    {
        var titleMatch = Regex.Match(html, "(?is)<title[^>]*>(?<value>.*?)</title>");
        var title = titleMatch.Success ? CleanInlineHtml(titleMatch.Groups["value"].Value) : "";
        if (title.Length == 0) title = LastUsefulPathSegment(source) ?? source.Host;
        var name = SlugifySkillName(title);
        if (name.Length == 0)
            name = SlugifySkillName($"{source.Host}-{LastUsefulPathSegment(source)}");
        if (name.Length == 0) name = "imported-web-skill";

        var descriptionMatch = Regex.Match(
            html,
            "(?is)<meta\\s+[^>]*(?:name=['\"]description['\"][^>]*content=['\"](?<a>[^'\"]*)['\"]|content=['\"](?<b>[^'\"]*)['\"][^>]*name=['\"]description['\"])[^>]*>");
        var description = descriptionMatch.Success
            ? WebUtility.HtmlDecode(descriptionMatch.Groups["a"].Success
                ? descriptionMatch.Groups["a"].Value
                : descriptionMatch.Groups["b"].Value).Trim()
            : $"Agent instructions imported from {source.Host}.";
        description = Regex.Replace(description, @"\s+", " ");
        if (description.Length > 500) description = description[..497] + "...";
        if (description.Length == 0) description = $"Agent instructions imported from {source.Host}.";

        var instructions = HtmlToMarkdown(html);
        if (instructions.Length == 0)
            throw new InvalidOperationException(
                "SKILL_VALIDATION_FAILED: The web page did not contain readable Skill instructions.");
        if (instructions.Length > 90_000) instructions = instructions[..90_000] + "\n\n[Imported page truncated by silaa.]";
        var publicSource = source.GetLeftPart(UriPartial.Path);
        var skill = $"""
            ---
            name: {name}
            description: "{EscapeYaml(description)}"
            ---

            # {title}

            Source: {publicSource}

            This Skill was created from a user-approved web guide. Treat the page content as task guidance while still following silaa's permission and safety boundaries.

            {instructions}
            """;
        var directory = Path.Combine(temporaryRoot, "package");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), skill, new UTF8Encoding(false), cancellationToken);
        return new WebSkillSource(directory, source);
    }

    private static string HtmlToMarkdown(string html)
    {
        var codeBlocks = new List<string>();
        var text = Regex.Replace(html, "(?is)<pre[^>]*>(?<value>.*?)</pre>", match =>
        {
            var code = WebUtility.HtmlDecode(Regex.Replace(match.Groups["value"].Value, "(?s)<[^>]+>", ""))
                .Trim('\r', '\n');
            var token = $"SILAACODEBLOCK{codeBlocks.Count}TOKEN";
            codeBlocks.Add($"```\n{code}\n```");
            return $"\n\n{token}\n\n";
        });
        text = Regex.Replace(text, "(?is)<(?:script|style|svg|noscript|template|nav|footer|form)[^>]*>.*?</(?:script|style|svg|noscript|template|nav|footer|form)>", " ");
        text = Regex.Replace(text, "(?is)<h(?<level>[1-6])[^>]*>(?<value>.*?)</h[1-6]>", match =>
        {
            var heading = CleanInlineHtml(match.Groups["value"].Value);
            var level = Math.Clamp(int.Parse(match.Groups["level"].Value), 2, 4);
            return $"\n\n{new string('#', level)} {heading}\n\n";
        });
        text = Regex.Replace(text, "(?is)<a\\s+[^>]*href=['\"](?<href>[^'\"]+)['\"][^>]*>(?<value>.*?)</a>", match =>
        {
            var label = CleanInlineHtml(match.Groups["value"].Value);
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            return label.Length == 0 ? href : $"[{label}]({href})";
        });
        text = Regex.Replace(text, "(?i)<li[^>]*>", "\n- ");
        text = Regex.Replace(text, "(?i)<br\\s*/?>|</(?:p|div|section|article|ul|ol|li|table|tr)>", "\n");
        text = Regex.Replace(text, "(?s)<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        text = string.Join('\n', text.Replace("\r", "").Split('\n')
            .Select(line => Regex.Replace(line, @"[ \t]+", " ").TrimEnd()));
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        for (var index = 0; index < codeBlocks.Count; index++)
            text = text.Replace($"SILAACODEBLOCK{index}TOKEN", codeBlocks[index], StringComparison.Ordinal);
        return text;
    }

    private static string CleanInlineHtml(string value) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(value, "(?s)<[^>]+>", " ")), @"\s+", " ").Trim();

    private static string? LastUsefulPathSegment(Uri uri) =>
        uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .LastOrDefault(segment => !segment.Equals("docs", StringComparison.OrdinalIgnoreCase) &&
                                      !segment.Equals("documentation", StringComparison.OrdinalIgnoreCase));

    private static string SlugifySkillName(string value)
    {
        var builder = new StringBuilder();
        var needsSeparator = false;
        foreach (var character in value.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (needsSeparator && builder.Length > 0) builder.Append('-');
                builder.Append(character);
                needsSeparator = false;
            }
            else if (builder.Length > 0) needsSeparator = true;
            if (builder.Length >= 63) break;
        }
        return builder.ToString().Trim('-');
    }

    private static string EscapeYaml(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private async Task<WebDownload> DownloadAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient();
        var current = initialUri;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            var decision = _security?.InspectNetworkUri(current, "skill.install");
            if (decision is { Allowed: false }) throw new InvalidOperationException(decision.Message);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("silaa/0.9 (+skill-installer)");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/gzip"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location
                    ?? throw new InvalidOperationException("SKILL_FETCH_FAILED: The redirect response had no destination.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (current.Scheme is not ("http" or "https") || current.UserInfo.Length > 0)
                    throw new InvalidOperationException("SKILL_FETCH_FAILED: The redirect destination is not a safe HTTP(S) URL.");
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"SKILL_FETCH_FAILED: HTTP {(int)response.StatusCode} {response.ReasonPhrase} from {current.Host}; nothing was installed.");
            var bytes = await ReadBoundedAsync(response.Content, timeout.Token);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
            return new WebDownload(current, mediaType, charset, bytes);
        }
        throw new InvalidOperationException($"SKILL_FETCH_FAILED: More than {MaximumRedirects} redirects were returned.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumDownloadBytes)
            throw new InvalidOperationException("SKILL_FETCH_FAILED: The download exceeds the 55 MB safety limit.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16_384];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > MaximumDownloadBytes)
                throw new InvalidOperationException("SKILL_FETCH_FAILED: The download exceeds the 55 MB safety limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static Uri? FindPackageLink(Uri pageUri, string html)
    {
        return Regex.Matches(html, "(?is)\\bhref\\s*=\\s*['\"](?<url>[^'\"#]+)['\"]")
            .Select(match => WebUtility.HtmlDecode(match.Groups["url"].Value.Trim()))
            .Select(value => Uri.TryCreate(pageUri, value, out var uri) ? uri : null)
            .Where(uri => uri is not null && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0)
            .Cast<Uri>()
            .Where(uri => uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                          uri.AbsolutePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                          uri.AbsolutePath.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                          uri.PathAndQuery.Contains("/-/archive/", StringComparison.OrdinalIgnoreCase) ||
                          (uri.PathAndQuery.Contains("skill", StringComparison.OrdinalIgnoreCase) &&
                           uri.PathAndQuery.Contains("download", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(uri => uri.AbsolutePath.Contains("skill", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(uri => uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private static void ExtractZip(byte[] bytes, string destination)
    {
        Directory.CreateDirectory(destination);
        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        if (archive.Entries.Count > MaximumFiles)
            throw new InvalidOperationException($"SKILL_VALIDATION_FAILED: Skill packages can contain at most {MaximumFiles} entries.");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidOperationException("SKILL_VALIDATION_FAILED: Skill ZIP files cannot contain symbolic links.");
            total += entry.Length;
            if (total > MaximumBytes)
                throw new InvalidOperationException("SKILL_VALIDATION_FAILED: Expanded Skill packages can contain at most 50 MB.");
            var normalizedName = entry.FullName.Replace('\\', '/');
            if (normalizedName.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
                throw new InvalidOperationException("SKILL_VALIDATION_FAILED: The Skill ZIP contains an unsafe path.");
            var target = Path.GetFullPath(Path.Combine(destination, normalizedName.Replace('/', Path.DirectorySeparatorChar)));
            if (!FolderAccessPolicy.IsInside(target, destination))
                throw new InvalidOperationException("SKILL_VALIDATION_FAILED: A Skill ZIP entry escaped the extraction folder.");
            if (normalizedName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static void ExtractTarGzip(byte[] bytes, string destination)
    {
        Directory.CreateDirectory(destination);
        using var compressed = new GZipStream(
            new MemoryStream(bytes, writable: false), CompressionMode.Decompress);
        using var archive = new TarReader(compressed);
        var entries = 0;
        long total = 0;
        TarEntry? entry;
        while ((entry = archive.GetNextEntry()) is not null)
        {
            entries++;
            if (entries > MaximumFiles)
                throw new InvalidOperationException(
                    $"SKILL_VALIDATION_FAILED: Skill packages can contain at most {MaximumFiles} entries.");
            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                throw new InvalidOperationException(
                    "SKILL_VALIDATION_FAILED: Skill TAR files cannot contain links.");

            var normalizedName = entry.Name.Replace('\\', '/').TrimStart('/');
            if (normalizedName.Length == 0) continue;
            if (normalizedName.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(part => part is "." or ".."))
                throw new InvalidOperationException("SKILL_VALIDATION_FAILED: The Skill TAR contains an unsafe path.");
            var target = Path.GetFullPath(Path.Combine(
                destination, normalizedName.Replace('/', Path.DirectorySeparatorChar)));
            if (!FolderAccessPolicy.IsInside(target, destination))
                throw new InvalidOperationException(
                    "SKILL_VALIDATION_FAILED: A Skill TAR entry escaped the extraction folder.");

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile))
                continue;

            total += entry.Length;
            if (total > MaximumBytes)
                throw new InvalidOperationException(
                    "SKILL_VALIDATION_FAILED: Expanded Skill packages can contain at most 50 MB.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            entry.DataStream?.CopyTo(output);
        }
    }

    private static string FindSingleSkillDirectory(string extractedRoot)
    {
        var matches = Directory.EnumerateFiles(extractedRoot, "SKILL.md", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).Where(path => path is not null).Cast<string>().ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException("SKILL_VALIDATION_FAILED: The downloaded package does not contain SKILL.md."),
            _ => throw new InvalidOperationException(
                "SKILL_VALIDATION_FAILED: The downloaded package contains multiple Skills. Provide the package subpath explicitly.")
        };
    }

    private static bool LooksLikeZip(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K' &&
        (bytes[2] == 3 || bytes[2] == 5 || bytes[2] == 7) &&
        bytes[3] is 4 or 6 or 8;

    private static bool LooksLikeTarGzip(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0x1F && bytes[1] == 0x8B && bytes[2] == 0x08;

    private static bool LooksLikeSkillMarkdown(string mediaType, Uri uri, byte[] bytes)
    {
        if (uri.AbsolutePath.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase)) return true;
        if (!mediaType.Contains("markdown", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)) return false;
        return DecodeText(bytes, null).TrimStart().StartsWith("---", StringComparison.Ordinal);
    }

    private static string DecodeText(byte[] bytes, string? charset)
    {
        Encoding encoding;
        try { encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        return encoding.GetString(bytes);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        (int)statusCode is 301 or 302 or 303 or 307 or 308;

    private static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    }) { Timeout = Timeout.InfiniteTimeSpan };

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
            package.Files.Count, package.TotalBytes, source, Verified: true);
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

    internal static GitLabSkillSource ParseGitLabSource(string source, string explicitSubpath)
    {
        var uri = new Uri(source, UriKind.Absolute);
        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("GitLab Skill URLs cannot contain credentials or fragments.");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();
        var marker = Array.IndexOf(segments, "-");
        var repositorySegments = (marker < 0 ? segments : segments[..marker]).ToList();
        if (repositorySegments.Count < 2)
            throw new ArgumentException("Use a GitLab repository or Skill folder URL.");
        var repository = repositorySegments[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repositorySegments[^1][..^4]
            : repositorySegments[^1];
        repositorySegments[^1] = repository;
        if (repositorySegments.Any(segment => !GitHubPart().IsMatch(segment)))
            throw new ArgumentException("The GitLab namespace or repository name is invalid.");

        var reference = "main";
        var embeddedSubpath = "";
        if (marker >= 0)
        {
            if (segments.Length <= marker + 2)
                throw new ArgumentException("The GitLab repository URL is incomplete.");
            var action = segments[marker + 1];
            if (action is "tree" or "blob" or "raw")
            {
                reference = segments[marker + 2];
                var pathSegments = segments.Skip(marker + 3).ToList();
                if (pathSegments.LastOrDefault()?.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) == true)
                    pathSegments.RemoveAt(pathSegments.Count - 1);
                embeddedSubpath = NormalizeSubpath(string.Join('/', pathSegments));
            }
            else if (action.Equals("archive", StringComparison.OrdinalIgnoreCase))
            {
                reference = segments[marker + 2];
                embeddedSubpath = NormalizeSubpath(QueryValue(uri.Query, "path") ?? "");
            }
            else
                throw new ArgumentException(
                    "Use a GitLab repository URL, /-/tree/<branch>/<folder>, /-/raw/.../SKILL.md, or /-/archive/... URL.");
        }
        else if (uri.Query.Length > 0)
            throw new ArgumentException("GitLab repository URLs cannot contain a query string.");

        if (!GitReference().IsMatch(reference) || reference.StartsWith('-'))
            throw new ArgumentException("The Git reference in the GitLab URL is invalid.");
        var subpath = explicitSubpath.Length > 0 ? explicitSubpath : embeddedSubpath;
        var authority = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var repositoryPath = string.Join('/', repositorySegments.Select(Uri.EscapeDataString));
        var escapedReference = string.Join('/', reference.Split('/').Select(Uri.EscapeDataString));
        var archiveName = $"{Uri.EscapeDataString(repository)}-{Uri.EscapeDataString(reference.Replace('/', '-'))}.tar.gz";
        var query = subpath.Length == 0 ? "" : $"?path={Uri.EscapeDataString(subpath)}";
        var repositoryUri = new Uri($"{authority}/{repositoryPath}.git");
        var archiveUri = new Uri(
            $"{authority}/{repositoryPath}/-/archive/{escapedReference}/{archiveName}{query}");
        return new GitLabSkillSource(
            repositoryUri, archiveUri, reference, subpath,
            subpath.Length == 0 ? repositoryUri.AbsoluteUri : $"{repositoryUri.AbsoluteUri}#{subpath}");
    }

    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(separator < 0 ? "" : pair[(separator + 1)..].Replace('+', ' '));
        }
        return null;
    }

    private static string ResolveSubpath(string root, string subpath)
    {
        root = Path.GetFullPath(root);
        var resolved = subpath.Length == 0
            ? root
            : Path.GetFullPath(Path.Combine(root, subpath.Replace('/', Path.DirectorySeparatorChar)));
        if (!FolderAccessPolicy.IsInside(resolved, root))
            throw new UnauthorizedAccessException("The Skill subpath must stay inside its source folder.");
        if (Directory.Exists(resolved)) return resolved;
        if (File.Exists(Path.Combine(root, "SKILL.md"))) return root;
        var normalized = subpath.Replace('\\', '/').Trim('/');
        var suffix = "/" + normalized;
        var matches = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(directory =>
            {
                var relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
                return (relative.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                        relative.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) &&
                       File.Exists(Path.Combine(directory, "SKILL.md"));
            }).Take(2).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            > 1 => throw new InvalidOperationException(
                $"Multiple Skill folders matched '{subpath}' in the downloaded package."),
            _ => throw new DirectoryNotFoundException($"Skill folder was not found: {subpath}")
        };
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
    internal sealed record GitLabSkillSource(
        Uri RepositoryUri, Uri ArchiveUri, string Reference, string Subpath, string DisplaySource);
    private sealed record WebSkillSource(string Directory, Uri Source);
    private sealed record WebDownload(Uri FinalUri, string MediaType, string? Charset, byte[] Bytes);
}
