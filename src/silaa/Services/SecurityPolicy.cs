using System.Net;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using silaa.Core;

namespace silaa.Services;

internal static partial class NetworkAccessPolicy
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static IReadOnlyList<string> Normalize(IEnumerable<string>? endpoints, int maximum = 64)
    {
        var normalized = new List<string>();
        foreach (var endpoint in endpoints ?? [])
        {
            if (string.IsNullOrWhiteSpace(endpoint)) continue;
            var value = NormalizeEndpoint(endpoint);
            if (!normalized.Contains(value, Comparer)) normalized.Add(value);
            if (normalized.Count > maximum)
                throw new ArgumentException($"네트워크 허용 목적지는 범위별로 최대 {maximum}개까지 추가할 수 있습니다.");
        }
        return normalized;
    }

    public static IReadOnlyList<string> Combine(params IEnumerable<string>?[] scopes)
    {
        var combined = new List<string>();
        foreach (var scope in scopes)
        foreach (var endpoint in scope ?? [])
        {
            if (string.IsNullOrWhiteSpace(endpoint)) continue;
            var normalized = NormalizeEndpoint(endpoint);
            if (!combined.Contains(normalized, Comparer)) combined.Add(normalized);
        }
        return combined;
    }

    public static string NormalizeEndpoint(string host, int port = 443)
    {
        host = host.Trim();
        if (host.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(host, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
            port = uri.IsDefaultPort ? (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? 80 : 443) : uri.Port;
        }
        else
        {
            var separator = host.LastIndexOf(':');
            if (separator > 0 && host.Count(character => character == ':') == 1 &&
                int.TryParse(host[(separator + 1)..], out var parsedPort))
            {
                port = parsedPort;
                host = host[..separator];
            }
        }
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length is 0 or > 253 || port is < 1 or > 65535 ||
            Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("올바른 네트워크 호스트와 포트를 입력하세요.");
        return $"{host}:{port}";
    }

    public static bool IsAllowed(string endpoint, IReadOnlyList<string> allowedEndpoints)
    {
        var normalized = NormalizeEndpoint(endpoint);
        var separator = normalized.LastIndexOf(':');
        var host = normalized[..separator];
        var port = normalized[(separator + 1)..];
        foreach (var rule in allowedEndpoints)
        {
            var normalizedRule = NormalizeEndpoint(rule);
            if (normalizedRule.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return true;
            var ruleSeparator = normalizedRule.LastIndexOf(':');
            var ruleHost = normalizedRule[..ruleSeparator];
            if (!normalizedRule[(ruleSeparator + 1)..].Equals(port, StringComparison.Ordinal)) continue;
            if (ruleHost.StartsWith("*.", StringComparison.Ordinal) &&
                host.EndsWith(ruleHost[1..], StringComparison.OrdinalIgnoreCase) &&
                host.Count(character => character == '.') == ruleHost.Count(character => character == '.')) return true;
        }
        return false;
    }

    public static IReadOnlyList<string> ExtractEndpoints(string command)
    {
        var endpoints = new List<string>();
        foreach (Match match in UrlRegex().Matches(command))
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) continue;
            var port = uri.IsDefaultPort ? (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? 80 : 443) : uri.Port;
            var endpoint = NormalizeEndpoint(uri.Host, port);
            if (!endpoints.Contains(endpoint, Comparer)) endpoints.Add(endpoint);
        }
        return endpoints;
    }

    public static bool LooksLikeNetworkCommand(string command) => NetworkCommandRegex().IsMatch(command);
    public static bool IsAlwaysBlockedCommand(string command) => BlockedCommandRegex().IsMatch(command);

    [GeneratedRegex("https?://[^\\s'\\\"<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?ix)(?:^|[|;&\s])(?:curl|wget|ssh|scp|sftp|ftp|telnet|nc|ncat|invoke-webrequest|iwr|invoke-restmethod|irm)\b|\bgit\s+(?:clone|fetch|pull|push|ls-remote)\b|\b(?:npm|pnpm|yarn|pip|pip3|nuget)\s+(?:install|add|publish)\b|\bdotnet\s+(?:restore|tool\s+install|add\s+package)\b")]
    private static partial Regex NetworkCommandRegex();

    [GeneratedRegex(@"(?ix)(?:^|[|;&\s])(?:powershell(?:\.exe)?|pwsh(?:\.exe)?|cmd(?:\.exe)?|wsl(?:\.exe)?|bash(?:\.exe)?|mshta(?:\.exe)?|rundll32(?:\.exe)?|reg(?:\.exe)?|sc(?:\.exe)?|schtasks(?:\.exe)?|vaultcmd(?:\.exe)?|cmdkey(?:\.exe)?)\b|\b(?:invoke-expression|iex)\b|-(?:encodedcommand|enc)\b")]
    private static partial Regex BlockedCommandRegex();
}

internal sealed class SecurityAuditStore(string root)
{
    private static readonly JsonSerializerOptions Json = new();
    private readonly object _gate = new();
    private string FilePath => Path.Combine(root, "security-audit.jsonl");

    public void Append(string sessionId, string action, string decision, string resource, string reason)
    {
        var record = new SecurityAuditRecord
        {
            Timestamp = DateTimeOffset.UtcNow,
            SessionId = Safe(sessionId, 128),
            Action = Safe(action, 80),
            Decision = decision is "allow" or "deny" ? decision : "deny",
            Resource = Safe(resource, 500),
            Reason = Safe(reason, 500)
        };
        lock (_gate)
        {
            Directory.CreateDirectory(root);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 5_000_000)
                File.Move(FilePath, Path.Combine(root, "security-audit.previous.jsonl"), true);
            File.AppendAllText(FilePath, JsonSerializer.Serialize(record, Json) + Environment.NewLine);
        }
    }

    public IReadOnlyList<SecurityAuditRecord> List(int maximum = 50)
    {
        maximum = Math.Clamp(maximum, 1, 200);
        lock (_gate)
        {
            if (!File.Exists(FilePath)) return [];
            return File.ReadLines(FilePath).TakeLast(maximum).Select(line =>
            {
                try { return JsonSerializer.Deserialize<SecurityAuditRecord>(line, Json); }
                catch (JsonException) { return null; }
            }).OfType<SecurityAuditRecord>().Reverse().ToList();
        }
    }

    private static string Safe(string value, int maximum) =>
        new(value.Where(character => !char.IsControl(character)).Take(maximum).ToArray());
}

internal sealed class SecurityPolicyContext(
    string sessionId,
    string dataRoot,
    IReadOnlyList<string> allowedNetworkEndpoints,
    SecurityAuditStore audit)
{
    private readonly IReadOnlyList<string> _protectedDirectories = ProtectedDirectories();
    private readonly IReadOnlyList<string> _protectedFiles = ProtectedFiles(dataRoot);

    public IReadOnlyList<string> AllowedNetworkEndpoints { get; } = allowedNetworkEndpoints;

    public string Describe() => $"OpenShell-inspired enforce mode\nNetwork: deny by default ({AllowedNetworkEndpoints.Count} allowed endpoint(s))\nProtected paths: {_protectedDirectories.Count + _protectedFiles.Count}\nProcess environment: sanitized";

    public bool IsProtectedPath(string path)
    {
        var full = FolderAccessPolicy.NormalizePath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return _protectedFiles.Any(file => full.Equals(file, comparison)) ||
               _protectedDirectories.Any(root => FolderAccessPolicy.IsInside(full, root));
    }

    public void EnsurePathAllowed(string path, bool write)
    {
        if (!IsProtectedPath(path)) return;
        audit.Append(sessionId, write ? "filesystem.write" : "filesystem.read", "deny", path,
            "Protected credential or silaa state path");
        throw new UnauthorizedAccessException("OpenShell 정책으로 보호된 자격증명 또는 silaa 상태 경로입니다.");
    }

    public CommandSecurityDecision InspectCommand(
        string command,
        string? credentialEndpoint = null,
        bool credentialPresent = false)
    {
        if (ReferencesProtectedPath(command))
        {
            audit.Append(sessionId, "filesystem.read", "deny", "protected-path", "Command referenced protected agent or credential state");
            return CommandSecurityDecision.Deny("FILESYSTEM_DENIED: 명령에서 silaa/Vault 또는 일반 자격증명 경로를 참조할 수 없습니다.");
        }
        if (NetworkAccessPolicy.IsAlwaysBlockedCommand(command))
        {
            audit.Append(sessionId, "process.exec", "deny", "blocked-process", "Nested shell or privileged credential/system utility");
            return CommandSecurityDecision.Deny("PROCESS_DENIED: 중첩 셸, 인코딩 실행 또는 시스템 자격증명 도구는 허용되지 않습니다.");
        }

        var endpoints = NetworkAccessPolicy.ExtractEndpoints(command).ToList();
        if (credentialPresent && endpoints.Count > 0 && string.IsNullOrWhiteSpace(credentialEndpoint))
        {
            audit.Append(sessionId, "credential.bind", "deny", string.Join(',', endpoints), "No endpoint binding supplied");
            return CommandSecurityDecision.Deny("CREDENTIAL_DENIED: 네트워크에서 Vault를 사용하려면 정확한 승인 목적지 host:port를 지정해야 합니다.");
        }
        if (!string.IsNullOrWhiteSpace(credentialEndpoint))
        {
            var normalizedCredentialEndpoint = NetworkAccessPolicy.NormalizeEndpoint(credentialEndpoint);
            if (!command.Contains(normalizedCredentialEndpoint[..normalizedCredentialEndpoint.LastIndexOf(':')], StringComparison.OrdinalIgnoreCase))
                return CommandSecurityDecision.Deny("CREDENTIAL_DENIED: Vault 사용 명령에는 승인된 목적지 호스트가 명시되어야 합니다.");
            if (!endpoints.Contains(normalizedCredentialEndpoint, StringComparer.OrdinalIgnoreCase)) endpoints.Add(normalizedCredentialEndpoint);
            if (endpoints.Any(endpoint => !endpoint.Equals(normalizedCredentialEndpoint, StringComparison.OrdinalIgnoreCase)))
            {
                audit.Append(sessionId, "credential.bind", "deny", string.Join(',', endpoints), "Command referenced destinations outside credential binding");
                return CommandSecurityDecision.Deny("CREDENTIAL_DENIED: Vault 명령은 하나의 승인된 목적지만 참조할 수 있습니다.");
            }
        }

        if (NetworkAccessPolicy.LooksLikeNetworkCommand(command) && endpoints.Count == 0)
        {
            audit.Append(sessionId, "network.connect", "deny", "unresolved-destination", "Network command destination was not explicit");
            return CommandSecurityDecision.Deny("NETWORK_DENIED: 목적지를 검증할 수 없습니다. URL을 명시하고 request_network_access로 정확한 호스트를 승인받으세요.");
        }

        foreach (var endpoint in endpoints)
        {
            if (NetworkAccessPolicy.IsAllowed(endpoint, AllowedNetworkEndpoints))
            {
                audit.Append(sessionId, "network.connect", "allow", endpoint, "Matched explicit endpoint policy");
                continue;
            }
            audit.Append(sessionId, "network.connect", "deny", endpoint, "No matching endpoint policy");
            return CommandSecurityDecision.Deny($"NETWORK_DENIED: {endpoint} 목적지는 허용되지 않았습니다. request_network_access를 호출하세요.");
        }

        audit.Append(sessionId, "process.exec", "allow", endpoints.Count == 0 ? "local-command" : string.Join(',', endpoints),
            "Passed tool-boundary policy");
        return CommandSecurityDecision.Allow();
    }

    public CommandSecurityDecision InspectNetworkUri(Uri uri, string action = "web.fetch")
    {
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length > 0)
            return CommandSecurityDecision.Deny("NETWORK_DENIED: 사용자 정보가 없는 HTTP(S) URL만 조회할 수 있습니다.");
        var port = uri.IsDefaultPort ? (uri.Scheme == "http" ? 80 : 443) : uri.Port;
        var endpoint = NetworkAccessPolicy.NormalizeEndpoint(uri.Host, port);
        if (!NetworkAccessPolicy.IsAllowed(endpoint, AllowedNetworkEndpoints))
        {
            audit.Append(sessionId, action, "deny", endpoint, "No matching endpoint policy");
            return CommandSecurityDecision.Deny($"NETWORK_DENIED: {endpoint} 목적지는 허용되지 않았습니다.");
        }
        audit.Append(sessionId, action, "allow", endpoint, "Matched explicit endpoint policy");
        return CommandSecurityDecision.Allow();
    }

    public void GrantNetworkEndpoint(string endpoint)
    {
        var normalized = NetworkAccessPolicy.NormalizeEndpoint(endpoint);
        if (AllowedNetworkEndpoints is List<string> mutable &&
            !mutable.Contains(normalized, StringComparer.OrdinalIgnoreCase)) mutable.Add(normalized);
        audit.Append(sessionId, "network.policy", "allow", normalized, "User approved endpoint");
    }

    public IReadOnlyList<SecurityAuditRecord> RecentAudit(int maximum = 30) => audit.List(maximum);

    private bool ReferencesProtectedPath(string command)
    {
        if (_protectedFiles.Any(file => command.Contains(file, StringComparison.OrdinalIgnoreCase))) return true;
        if (new[] { "vault.json", "llm-credential.json", "security-audit.jsonl" }
            .Any(name => command.Contains(name, StringComparison.OrdinalIgnoreCase))) return true;
        return new[] { ".ssh", ".aws", ".azure", ".kube", ".config/gcloud" }
            .Any(token => command.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    public void ApplyNetworkEnvironment(ProcessStartInfo start)
    {
        const string denyProxy = "http://127.0.0.1:9";
        start.Environment["HTTP_PROXY"] = denyProxy;
        start.Environment["HTTPS_PROXY"] = denyProxy;
        start.Environment["ALL_PROXY"] = denyProxy;
        start.Environment["http_proxy"] = denyProxy;
        start.Environment["https_proxy"] = denyProxy;
        start.Environment["all_proxy"] = denyProxy;
        var hosts = AllowedNetworkEndpoints.Select(endpoint =>
        {
            var separator = endpoint.LastIndexOf(':');
            return separator > 0 ? endpoint[..separator] : endpoint;
        }).Distinct(StringComparer.OrdinalIgnoreCase);
        var noProxy = string.Join(',', hosts);
        start.Environment["NO_PROXY"] = noProxy;
        start.Environment["no_proxy"] = noProxy;
    }

    private static IReadOnlyList<string> ProtectedFiles(string dataRoot) =>
    new[]
    {
        "vault.json", "llm-credential.json", "settings.json", "harness-sessions.json",
        "harness-projects.json", "memory.json", "security-audit.jsonl"
    }.Select(name => FolderAccessPolicy.NormalizePath(Path.Combine(dataRoot, name))).ToList();

    private static IReadOnlyList<string> ProtectedDirectories()
    {
        var roots = new List<string>();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var relative in new[] { ".ssh", ".aws", ".azure", ".kube", ".config/gcloud" })
        {
            var path = Path.Combine(profile, relative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(path)) roots.Add(FolderAccessPolicy.NormalizePath(path));
        }
        return roots;
    }
}

internal sealed record CommandSecurityDecision(bool Allowed, string Message)
{
    public static CommandSecurityDecision Allow() => new(true, "");
    public static CommandSecurityDecision Deny(string message) => new(false, message);
}
