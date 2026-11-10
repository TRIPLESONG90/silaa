using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using silaa.Core;

namespace silaa.Services;

internal sealed class FolderAccessRequestBroker
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new(StringComparer.Ordinal);

    public async Task<string> RequestAsync(
        string sessionId,
        string path,
        string reason,
        bool canGrantProject,
        Action<ChatRunEvent> onEvent,
        CancellationToken cancellationToken)
    {
        var requestId = $"folder_{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
            throw new InvalidOperationException("폴더 권한 요청을 만들지 못했습니다.");
        var request = new FolderAccessRequest
        {
            RequestId = requestId,
            Path = path,
            Reason = reason,
            CanGrantProject = canGrantProject
        };
        onEvent(new ChatRunEvent("folder_access_request", JsonSerializer.Serialize(request), sessionId, requestId));
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

internal sealed class FolderAccessRequestTools(
    string sessionId,
    string? projectId,
    LocalStore store,
    SessionStore sessions,
    ProjectStore projects,
    WorkspaceTools workspaceTools,
    FolderAccessRequestBroker broker,
    Action<ChatRunEvent>? onEvent,
    RunActivityTimeout? runTimeout = null)
{
    public IList<AITool> CreateFunctions() =>
    [
        AIFunctionFactory.Create((Func<string, string, CancellationToken, Task<string>>)RequestFolderAccessAsync,
            "request_folder_access",
            "Ask the user to grant an existing absolute folder to file tools. Use after an access-boundary error; never use shell commands to bypass a denied folder.")
    ];

    public async Task<string> RequestFolderAccessAsync(
        [Description("Existing absolute folder path that is required.")] string path,
        [Description("Concise reason this folder is needed for the user's current request.")] string reason,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathRooted(path)) throw new ArgumentException("Folder access requests require an absolute path.");
        var normalized = FolderAccessPolicy.NormalizePath(path);
        if (!Directory.Exists(normalized)) throw new DirectoryNotFoundException("Requested folder does not exist.");
        if (workspaceTools.HasDirectoryAccess(normalized)) return $"Access is already available: {normalized}";
        reason = reason.Trim();
        if (reason.Length is < 1 or > 500) throw new ArgumentException("Reason must contain 1 to 500 characters.");
        if (onEvent is null)
            return "Interactive folder approval is unavailable in this background run. Ask the user in a foreground conversation.";

        string scope;
        using (runTimeout?.Pause())
        {
            scope = await broker.RequestAsync(
                sessionId, normalized, reason, projectId is not null, onEvent, cancellationToken);
        }
        if (scope == "denied") return $"The user denied access to {normalized}.";

        switch (scope)
        {
            case "session":
            {
                var session = sessions.Get(sessionId) ?? throw new InvalidOperationException("세션을 찾지 못했습니다.");
                sessions.SetAllowedDirectories(sessionId, session.AllowedDirectories.Append(normalized));
                break;
            }
            case "project":
            {
                if (projectId is null) return "Project access is unavailable in a general conversation.";
                var project = projects.Get(store.Settings.WorkspacePath, projectId)
                    ?? throw new InvalidOperationException("프로젝트를 찾지 못했습니다.");
                projects.SetAllowedDirectories(
                    store.Settings.WorkspacePath, projectId, project.AllowedDirectories.Append(normalized));
                break;
            }
            case "global":
                store.SetGlobalAllowedDirectories(store.Settings.GlobalAllowedDirectories.Append(normalized));
                break;
            default:
                throw new InvalidOperationException("알 수 없는 폴더 권한 범위입니다.");
        }

        workspaceTools.GrantDirectoryAccess(normalized);
        return $"The user granted {scope} file-tool access to {normalized}. Access is active for this run.";
    }
}
