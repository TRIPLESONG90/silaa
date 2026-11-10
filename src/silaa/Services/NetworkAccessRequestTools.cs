using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using silaa.Core;

namespace silaa.Services;

internal sealed class NetworkAccessRequestBroker
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new(StringComparer.Ordinal);

    public async Task<string> RequestAsync(
        string sessionId,
        string host,
        int port,
        string reason,
        bool canGrantProject,
        bool persistentOnly,
        Action<ChatRunEvent> onEvent,
        CancellationToken cancellationToken)
    {
        var requestId = $"network_{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
            throw new InvalidOperationException("네트워크 권한 요청을 만들지 못했습니다.");
        var request = new NetworkAccessRequest
        {
            RequestId = requestId,
            Host = host,
            Port = port,
            Reason = reason,
            CanGrantProject = canGrantProject,
            PersistentOnly = persistentOnly
        };
        onEvent(new ChatRunEvent("network_access_request", JsonSerializer.Serialize(request), sessionId, requestId));
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try { return await completion.Task; }
        finally { _pending.TryRemove(requestId, out _); }
    }

    public bool Submit(string requestId, string scope)
    {
        scope = scope.Trim().ToLowerInvariant();
        if (scope is not ("denied" or "session" or "project" or "global")) return false;
        return _pending.TryGetValue(requestId, out var completion) && completion.TrySetResult(scope);
    }
}

internal sealed class NetworkAccessRequestTools(
    string sessionId,
    string? projectId,
    LocalStore store,
    SessionStore sessions,
    ProjectStore projects,
    SecurityPolicyContext security,
    NetworkAccessRequestBroker broker,
    Action<ChatRunEvent>? onEvent,
    RunActivityTimeout? runTimeout = null,
    bool persistentOnly = false)
{
    public IList<AITool> CreateFunctions() =>
    [
        AIFunctionFactory.Create((Func<string, int, string, CancellationToken, Task<string>>)RequestNetworkAccessAsync,
            "request_network_access",
            "Ask the user to allow one exact outbound host and port. Network is denied by default. Request only the narrow destination required for the current task."),
        AIFunctionFactory.Create((Func<string>)SecurityStatus, "security_status",
            "Show the active OpenShell-inspired process, filesystem, network, credential, and audit policy."),
        AIFunctionFactory.Create((Func<int, string>)ListSecurityAudit, "list_security_audit",
            "List recent security allow and deny decisions without commands or secret values.")
    ];

    public async Task<string> RequestNetworkAccessAsync(
        [Description("Exact DNS host or IP address, without a path.")] string host,
        [Description("Destination port, usually 443 for HTTPS.")] int port,
        [Description("Concise reason this destination is needed for the user's current request.")] string reason,
        CancellationToken cancellationToken = default)
    {
        var endpoint = NetworkAccessPolicy.NormalizeEndpoint(host, port);
        if (NetworkAccessPolicy.IsAllowed(endpoint, security.AllowedNetworkEndpoints))
            return $"Network access is already available: {endpoint}";
        reason = reason.Trim();
        if (reason.Length is < 1 or > 500) throw new ArgumentException("Reason must contain 1 to 500 characters.");
        if (onEvent is null)
            return "Interactive network approval is unavailable in this background run. Ask the user in a foreground conversation.";

        var separator = endpoint.LastIndexOf(':');
        var normalizedHost = endpoint[..separator];
        var normalizedPort = int.Parse(endpoint[(separator + 1)..]);
        string scope;
        using (runTimeout?.Pause())
        {
            scope = await broker.RequestAsync(
                sessionId, normalizedHost, normalizedPort, reason, projectId is not null,
                persistentOnly, onEvent, cancellationToken);
        }
        if (scope == "denied") return $"The user denied network access to {endpoint}.";
        if (persistentOnly && scope == "session")
            return "A recurring automation requires project or global network access.";

        switch (scope)
        {
            case "session":
            {
                var session = sessions.Get(sessionId) ?? throw new InvalidOperationException("세션을 찾지 못했습니다.");
                sessions.SetAllowedNetworkEndpoints(sessionId, session.AllowedNetworkEndpoints.Append(endpoint));
                break;
            }
            case "project":
            {
                if (projectId is null) return "Project access is unavailable in a general conversation.";
                var project = projects.Get(store.Settings.WorkspacePath, projectId)
                    ?? throw new InvalidOperationException("프로젝트를 찾지 못했습니다.");
                projects.SetAllowedNetworkEndpoints(
                    store.Settings.WorkspacePath, projectId, project.AllowedNetworkEndpoints.Append(endpoint));
                break;
            }
            case "global":
                store.SetGlobalAllowedNetworkEndpoints(store.Settings.GlobalAllowedNetworkEndpoints.Append(endpoint));
                break;
            default:
                throw new InvalidOperationException("알 수 없는 네트워크 권한 범위입니다.");
        }

        security.GrantNetworkEndpoint(endpoint);
        return $"The user granted {scope} network access to {endpoint}. Access is active for this run.";
    }

    private string SecurityStatus() => security.Describe();

    private string ListSecurityAudit(
        [Description("Maximum records from 1 to 100.")] int maximum = 30)
    {
        var records = security.RecentAudit(Math.Clamp(maximum, 1, 100));
        return records.Count == 0
            ? "No security audit records."
            : string.Join('\n', records.Select(record =>
                $"{record.Timestamp:O} {record.Decision.ToUpperInvariant()} {record.Action} {record.Resource} · {record.Reason}"));
    }
}
