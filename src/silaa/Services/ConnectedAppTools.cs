using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.AI;

namespace silaa.Services;

internal sealed class ConnectedAppTools(
    ConnectedAppStore apps,
    SecurityPolicyContext security,
    NetworkAccessRequestTools networkAccess)
{
    private static readonly HttpClient Client = CreateClient();

    public IList<AITool> CreateFunctions() =>
    [
        AIFunctionFactory.Create((Func<string>)ListConnectedApps, "list_connected_apps",
            "List GitHub and OneDrive connections explicitly enabled for Agent use. Secret values are never returned."),
        AIFunctionFactory.Create((Func<string, string, string>)ListOneDriveFiles, "list_onedrive_files",
            "List files under a connected OneDrive sync folder by connection ID and relative folder."),
        AIFunctionFactory.Create((Func<string, string, string>)ReadOneDriveFile, "read_onedrive_file",
            "Read a text or office document from a connected OneDrive sync folder."),
        AIFunctionFactory.Create((Func<string, string, CancellationToken, Task<string>>)GitHubGetAsync, "github_get",
            "Make an approved read-only GitHub API GET using a connected GitHub account. Use /user/repos or /repos/{owner}/{repo}/contents/{path}.")
    ];

    private string ListConnectedApps()
    {
        var enabled = apps.List().Where(item => item.AgentEnabled).ToList();
        return enabled.Count == 0 ? "No connected apps are enabled for Agent use." : string.Join('\n', enabled.Select(item =>
            $"{item.Id} [{item.Provider}] {item.Name} · read-only" +
            (item.Provider == "onedrive" ? $" · root={item.RootPath}" : $" · account={item.Account}")));
    }

    private string ListOneDriveFiles(
        [Description("OneDrive connection ID from list_connected_apps.")] string connectionId,
        [Description("Relative folder inside the OneDrive root. Use empty for root.")] string path = "")
    {
        var app = Require(connectionId, "onedrive");
        var target = ResolveInside(app.RootPath, path, directory: true);
        var entries = Directory.EnumerateFileSystemEntries(target).Take(500)
            .Select(item => Path.GetRelativePath(app.RootPath, item).Replace('\\', '/'));
        var result = string.Join('\n', entries);
        return result.Length == 0 ? "No files found." : result;
    }

    private string ReadOneDriveFile(
        [Description("OneDrive connection ID from list_connected_apps.")] string connectionId,
        [Description("Relative file path inside the OneDrive root.")] string path)
    {
        var app = Require(connectionId, "onedrive");
        var target = ResolveInside(app.RootPath, path, directory: false);
        var text = DocumentArtifactService.Supports(target) ? DocumentArtifactService.ReadText(target) : File.ReadAllText(target);
        return text.Length > 100_000 ? text[..100_000] + "\n[truncated]" : text;
    }

    private async Task<string> GitHubGetAsync(
        [Description("GitHub connection ID from list_connected_apps.")] string connectionId,
        [Description("GitHub REST API path beginning with /user/repos or /repos/.")] string apiPath,
        CancellationToken cancellationToken = default)
    {
        var app = Require(connectionId, "github");
        apiPath = apiPath.Trim();
        if (!(apiPath.Equals("/user", StringComparison.Ordinal) || apiPath.StartsWith("/user/repos", StringComparison.Ordinal) ||
              apiPath.StartsWith("/repos/", StringComparison.Ordinal)))
            return "GITHUB_ERROR: 허용된 읽기 API 경로가 아닙니다.";
        if (apiPath.Contains("..", StringComparison.Ordinal) || apiPath.Contains('#'))
            return "GITHUB_ERROR: 올바르지 않은 API 경로입니다.";
        var uri = new Uri("https://api.github.com" + (apiPath.StartsWith('/') ? apiPath : "/" + apiPath));
        var decision = security.InspectNetworkUri(uri, "connected-app.github.read");
        if (!decision.Allowed)
        {
            var approval = await networkAccess.RequestNetworkAccessAsync(
                "api.github.com", 443, $"연결된 GitHub 앱 {app.Name}의 읽기 전용 API 조회", cancellationToken);
            if (approval.Contains("denied", StringComparison.OrdinalIgnoreCase) || approval.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
                return "GITHUB_ERROR: " + approval;
            decision = security.InspectNetworkUri(uri, "connected-app.github.read");
            if (!decision.Allowed) return "GITHUB_ERROR: " + decision.Message;
        }
        var token = apps.GetToken(app.Id);
        if (string.IsNullOrWhiteSpace(token)) return "GITHUB_ERROR: Vault에서 GitHub 자격증명을 찾지 못했습니다.";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("silaa/0.8");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var body = await ReadBoundedAsync(response.Content, timeout.Token);
        return response.IsSuccessStatusCode
            ? "UNTRUSTED_CONNECTED_APP_DATA:\n" + body
            : $"GITHUB_ERROR: HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n{body}";
    }

    private silaa.Core.ConnectedAppInfo Require(string id, string provider)
    {
        var app = apps.Get(id) ?? throw new InvalidOperationException("앱 연결을 찾지 못했습니다.");
        if (!app.AgentEnabled) throw new UnauthorizedAccessException("이 앱 연결은 Agent 사용이 꺼져 있습니다.");
        if (!app.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("앱 연결 종류가 맞지 않습니다.");
        return app;
    }

    private static string ResolveInside(string root, string relative, bool directory)
    {
        if (Path.IsPathRooted(relative)) throw new UnauthorizedAccessException("OneDrive 경로는 연결 루트 기준 상대 경로여야 합니다.");
        var fullRoot = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!FolderAccessPolicy.IsInside(target, fullRoot)) throw new UnauthorizedAccessException("OneDrive 연결 범위를 벗어났습니다.");
        if (directory ? !Directory.Exists(target) : !File.Exists(target))
            throw directory ? new DirectoryNotFoundException("폴더를 찾지 못했습니다.") : new FileNotFoundException("파일을 찾지 못했습니다.", target);
        return target;
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            if (memory.Length + count > 2_000_000) throw new IOException("GitHub 응답이 2MB 제한을 초과했습니다.");
            memory.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        UseProxy = false
    });
}
