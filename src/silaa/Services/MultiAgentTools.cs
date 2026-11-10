using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using silaa.Core;
using AgentChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace silaa.Services;

/// <summary>
/// Creates focused child agents on top of Microsoft Agent Framework. Child agents
/// cannot recursively delegate, and the reviewer receives read-only tools only.
/// </summary>
internal sealed class MultiAgentTools(
    IChatClient chatClient,
    AppSettings settings,
    string sessionId,
    string userRequest,
    IList<AITool> workerTools,
    IList<AITool> reviewerTools,
    RunActivityTimeout runTimeout,
    Action<ChatRunEvent>? onEvent)
{
    private const int MaximumInputCharacters = 80_000;
    private const int MaximumResultCharacters = 60_000;
    private readonly SemaphoreSlim _childRunGate = new(1, 1);

    public IList<AITool> CreateFunctions() =>
    [
        AIFunctionFactory.Create(
            (Func<string, string, string, CancellationToken, Task<string>>)DelegateTaskAsync,
            "delegate_task",
            "Delegate one focused part of a complex request to a specialist child Agent. Use only when independent investigation or implementation materially helps; do not delegate simple conversation."),
        AIFunctionFactory.Create(
            (Func<string, string, CancellationToken, Task<string>>)ReviewResultAsync,
            "review_result",
            "Ask an independent read-only reviewer Agent to inspect a proposed result or completed workspace change for correctness, security, regressions, and missing verification.")
    ];

    internal async Task<string> DelegateTaskAsync(
        [Description("Short specialist role, for example researcher, implementer, tester, or documenter.")] string role,
        [Description("One concrete, bounded task with a clear expected output.")] string task,
        [Description("Only the context needed by this worker. Do not include secrets.")] string context = "",
        CancellationToken cancellationToken = default)
    {
        role = NormalizeRole(role);
        task = RequireBounded(task, nameof(task));
        context = Limit(context, MaximumInputCharacters);
        var instructions = $"""
            You are a focused {role} child Agent inside silaa. Complete only the delegated task.
            Use the provided workspace tools when evidence or implementation is required. Respect every existing
            filesystem, network, and Vault boundary. Never try to delegate to another Agent. Do not repeat the
            parent request broadly. Return a concise report containing concrete findings or changes, verification,
            and unresolved risks. Never expose secrets or hidden reasoning.
            """;
        var prompt = $"""
            Parent user request:
            {Limit(userRequest, 20_000)}

            Delegated task:
            {task}

            Relevant context:
            {context}
            """;
        return await RunChildAsync(role, task, instructions, prompt, workerTools, cancellationToken);
    }

    internal async Task<string> ReviewResultAsync(
        [Description("The proposed result, change summary, or implementation that must be reviewed.")] string proposedResult,
        [Description("Optional review focus such as security, UX, performance, or regression risk.")] string focus = "",
        CancellationToken cancellationToken = default)
    {
        proposedResult = RequireBounded(proposedResult, nameof(proposedResult));
        focus = Limit(focus, 4_000);
        const string role = "reviewer";
        const string instructions = """
            You are an independent read-only reviewer Agent inside silaa. Inspect the proposed result and the
            workspace evidence with read-only tools. Do not modify files or run commands. Check correctness,
            security boundaries, regressions, missing requirements, and verification quality. Never delegate.
            Return findings ordered by severity, then a clear approve/revise verdict. Do not expose hidden reasoning.
            """;
        var prompt = $"""
            Original user request:
            {Limit(userRequest, 20_000)}

            Proposed result:
            {Limit(proposedResult, MaximumInputCharacters)}

            Review focus:
            {focus}
            """;
        return await RunChildAsync(role, "독립 검토", instructions, prompt, reviewerTools, cancellationToken);
    }

    private async Task<string> RunChildAsync(
        string role,
        string task,
        string instructions,
        string prompt,
        IList<AITool> tools,
        CancellationToken cancellationToken)
    {
        await _childRunGate.WaitAsync(cancellationToken);
        var activityId = $"agent_{Guid.NewGuid():N}";
        var displayRole = DisplayRole(role);
        onEvent?.Invoke(new ChatRunEvent("agent_call", $"{displayRole}\n{task}", sessionId, activityId));
        onEvent?.Invoke(new ChatRunEvent("status", $"{displayRole} 실행 중…", sessionId, activityId));
        try
        {
            var options = new ChatClientAgentOptions
            {
                Name = $"silaa_{SafeAgentName(role)}",
                Description = $"silaa delegated {role} Agent",
                ChatOptions = new ChatOptions
                {
                    Instructions = instructions,
                    Tools = tools,
                    MaxOutputTokens = settings.MaxOutputTokens > 0 ? settings.MaxOutputTokens : null,
                    Temperature = settings.Temperature is { } temperature ? (float)temperature : null,
                    TopP = settings.TopP is { } topP ? (float)topP : null,
                    FrequencyPenalty = settings.FrequencyPenalty is { } frequencyPenalty ? (float)frequencyPenalty : null,
                    PresencePenalty = settings.PresencePenalty is { } presencePenalty ? (float)presencePenalty : null,
                    Seed = settings.Seed
                }
            };
            var agent = chatClient.AsAIAgent(options);
            var answer = new StringBuilder();
            var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
            var messages = new List<AgentChatMessage> { new(ChatRole.User, prompt) };
            await foreach (var update in agent.RunStreamingAsync(messages, cancellationToken: cancellationToken))
            {
                runTimeout.Reset();
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                            onEvent?.Invoke(new ChatRunEvent("reasoning", reasoning.Text, sessionId, activityId));
                            break;
                        case FunctionCallContent call:
                            if (!string.IsNullOrWhiteSpace(call.CallId)) toolNames[call.CallId] = call.Name;
                            onEvent?.Invoke(new ChatRunEvent(
                                "tool_call", FormatToolCall(call), sessionId, ChildActivityId(activityId, call.CallId)));
                            break;
                        case FunctionResultContent functionResult:
                            var name = !string.IsNullOrWhiteSpace(functionResult.CallId) &&
                                       toolNames.TryGetValue(functionResult.CallId, out var toolName)
                                ? toolName : "도구";
                            onEvent?.Invoke(new ChatRunEvent(
                                "tool_result", FormatToolResult(name, functionResult), sessionId,
                                ChildActivityId(activityId, functionResult.CallId)));
                            break;
                    }
                }
                if (!string.IsNullOrEmpty(update.Text)) answer.Append(update.Text);
            }

            var result = answer.ToString().Trim();
            if (result.Length == 0) result = "Child Agent가 빈 결과를 반환했습니다.";
            result = Limit(result, MaximumResultCharacters);
            onEvent?.Invoke(new ChatRunEvent("agent_result", $"{displayRole}\n{result}", sessionId, activityId));
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var error = $"오류: {exception.Message}";
            onEvent?.Invoke(new ChatRunEvent("agent_result", $"{displayRole}\n{error}", sessionId, activityId));
            return error;
        }
        finally
        {
            _childRunGate.Release();
        }
    }

    private static string FormatToolCall(FunctionCallContent call)
    {
        var arguments = call.Arguments is { Count: > 0 }
            ? JsonSerializer.Serialize(call.Arguments, new JsonSerializerOptions { WriteIndented = true })
            : "{}";
        return $"{call.Name}\n{arguments}";
    }

    private static string FormatToolResult(string name, FunctionResultContent result)
    {
        var value = result.Exception is not null
            ? $"오류: {result.Exception.Message}"
            : result.Result switch
            {
                null => "(결과 없음)",
                string text => text,
                _ => JsonSerializer.Serialize(result.Result, new JsonSerializerOptions { WriteIndented = true })
            };
        return $"{name}\n{Limit(value, 20_000)}";
    }

    private static string ChildActivityId(string parentId, string? childId) =>
        $"{parentId}:{(string.IsNullOrWhiteSpace(childId) ? Guid.NewGuid().ToString("N") : childId)}";

    private static string NormalizeRole(string value)
    {
        var role = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return role.Length == 0 ? "specialist" : Limit(role, 40);
    }

    private static string SafeAgentName(string role)
    {
        var value = new string(role.ToLowerInvariant().Select(character =>
            char.IsAsciiLetterOrDigit(character) ? character : '_').ToArray()).Trim('_');
        return value.Length == 0 ? "specialist" : value[..Math.Min(value.Length, 40)];
    }

    private static string DisplayRole(string role) => role.Equals("reviewer", StringComparison.OrdinalIgnoreCase)
        ? "검토 Agent"
        : $"{role} Agent";

    private static string RequireBounded(string value, string parameter)
    {
        value = value.Trim();
        if (value.Length == 0) throw new ArgumentException("A concrete task or result is required.", parameter);
        return Limit(value, MaximumInputCharacters);
    }

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "\n… (일부 생략됨)";
}
