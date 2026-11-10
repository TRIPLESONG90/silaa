using System.ComponentModel;
using Microsoft.Extensions.AI;
using silaa.Core;

namespace silaa.Services;

/// <summary>
/// Gives ordinary conversations the same native choice-card interaction used by
/// guided project and calendar flows.
/// </summary>
internal sealed class UserInteractionTools(
    string sessionId,
    ProjectInteractionBroker broker,
    Action<ChatRunEvent>? onEvent,
    RunActivityTimeout? runTimeout = null)
{
    public IList<AITool> CreateFunctions() =>
    [
        AIFunctionFactory.Create(
            (Func<string, string, string, CancellationToken, Task<string>>)AskQuestionAsync,
            "ask_user_question",
            "Render one native interactive choice card and wait for the user's answer. Use whenever a user decision, confirmation, or missing value is required before continuing.")
    ];

    private async Task<string> AskQuestionAsync(
        [Description("One concise Korean question. Ask only one decision at a time.")] string question,
        [Description("2-4 short suggested answers separated by |. Leave empty only when suggestions would mislead.")] string options = "",
        [Description("Short example for the free-text field.")] string placeholder = "직접 입력",
        CancellationToken cancellationToken = default)
    {
        using (runTimeout?.Pause())
            return await broker.RequestAsync(
                sessionId, question, options, placeholder, 0,
                "선택이 필요합니다", onEvent, cancellationToken, showProgress: false);
    }
}
