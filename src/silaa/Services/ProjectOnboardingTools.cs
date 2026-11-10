using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using silaa.Core;

namespace silaa.Services;

internal sealed class ProjectInteractionBroker
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new(StringComparer.Ordinal);

    public Task<string> RequestAsync(string sessionId, string question, string options, string placeholder,
        int specificityScore, Action<ChatRunEvent>? onEvent, CancellationToken cancellationToken) =>
        RequestAsync(sessionId, question, options, placeholder, specificityScore,
            "프로젝트 구체화", onEvent, cancellationToken);

    public async Task<string> RequestAsync(string sessionId, string question, string options, string placeholder,
        int specificityScore, string title, Action<ChatRunEvent>? onEvent, CancellationToken cancellationToken)
    {
        var requestId = $"input_{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion)) throw new InvalidOperationException("입력 요청을 만들지 못했습니다.");
        var request = new ProjectInputRequest
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Agent 설문" : title.Trim(),
            RequestId = requestId,
            Question = question.Trim(),
            Options = options.Split(['|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Placeholder = string.IsNullOrWhiteSpace(placeholder) ? "직접 입력할 수도 있습니다." : placeholder.Trim(),
            SpecificityScore = Math.Clamp(specificityScore, 0, 100)
        };
        onEvent?.Invoke(new ChatRunEvent("input_request", JsonSerializer.Serialize(request), sessionId, requestId));
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try { return await completion.Task; }
        finally { _pending.TryRemove(requestId, out _); }
    }

    public bool Submit(string requestId, string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return false;
        return _pending.TryGetValue(requestId, out var completion) && completion.TrySetResult(answer.Trim());
    }
}

internal sealed class ProjectOnboardingTools(
    string workspace,
    string sessionId,
    ProjectInteractionBroker broker,
    ProjectDraftStore drafts,
    ProjectStore projects,
    SessionStore sessions,
    Action<ChatRunEvent>? onEvent,
    RunActivityTimeout? runTimeout = null)
{
    public IList<AITool> CreateFunctions() =>
    [
        AIFunctionFactory.Create((Func<string, string, string, int, CancellationToken, Task<string>>)AskProjectQuestionAsync,
            "ask_project_question", "Render one interactive project-discovery question and wait for the user's answer."),
        AIFunctionFactory.Create((Func<string, string, int, string, string>)FinalizeProject,
            "finalize_project", "Create the project after the user confirms and specificity is at least 70.")
    ];

    private async Task<string> AskProjectQuestionAsync(
        [Description("One concise Korean question that resolves the largest remaining ambiguity.")] string question,
        [Description("2-4 suggested answers separated by |. Leave empty when suggestions are not useful.")] string options = "",
        [Description("Short example shown in the free-text field.")] string placeholder = "직접 입력",
        [Description("Current project specificity score from 0 to 100.")] int specificityScore = 0,
        CancellationToken cancellationToken = default)
    {
        string answer;
        using (runTimeout?.Pause())
        {
            answer = await broker.RequestAsync(
                sessionId, question, options, placeholder, specificityScore, "프로젝트 구체화", onEvent, cancellationToken);
        }
        drafts.Record(sessionId, question, answer, specificityScore);
        return answer;
    }

    private string FinalizeProject(
        [Description("Short project name suitable for a folder and menu label.")] string name,
        [Description("Concrete Korean summary including goal, user, input, output, and constraints.")] string summary,
        [Description("Final specificity score from 70 to 100.")] int specificityScore,
        [Description("Primary output type: app, automation, document, skill, or other.")] string outputType = "other")
    {
        if (specificityScore < 70) return "Specificity is below 70. Ask more questions before finalizing.";
        var project = projects.Create(workspace, name, summary, specificityScore, drafts.Get(sessionId), outputType);
        var projectSessionId = sessions.CreateProjectSurveySession(project, workspace);
        drafts.Remove(sessionId);
        onEvent?.Invoke(new ChatRunEvent("project_created", JsonSerializer.Serialize(project), projectSessionId, project.Id));
        return $"Project created: {project.Name} ({project.Directory})";
    }
}
