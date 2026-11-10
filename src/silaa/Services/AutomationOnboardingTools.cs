using System.ComponentModel;
using Microsoft.Extensions.AI;
using silaa.Core;

namespace silaa.Services;

internal sealed class AutomationOnboardingTools(
    string kind,
    string sessionId,
    string? projectId,
    ProjectInteractionBroker broker,
    BackgroundAutomationService automation,
    Action<ChatRunEvent>? onEvent,
    RunActivityTimeout? runTimeout = null)
{
    private bool _reminderMessageAsked;

    public bool Completed { get; private set; }

    public IList<AITool> CreateFunctions()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                (Func<string, string, string, int, CancellationToken, Task<string>>)AskQuestionAsync,
                "ask_automation_question",
                "Render one interactive automation survey question and wait for the user's answer."),
            AIFunctionFactory.Create(
                (Func<string, string, int, string>)CreateReminder,
                "finalize_reminder",
                "Register a one-time local notification only after confirming its message and delivery time.")
        };
        if (kind == "schedule")
            tools.Add(AIFunctionFactory.Create(
                (Func<string, string, string, string>)CreateSchedule,
                "finalize_schedule",
                "Register the recurring schedule only after the user explicitly confirms the final summary."));
        else
            tools.Add(AIFunctionFactory.Create(
                (Func<string, string, string>)CreateTask,
                "finalize_task",
                "Queue the background task only after the user explicitly confirms the final summary."));
        return tools;
    }

    private async Task<string> AskQuestionAsync(
        [Description("One concise Korean question resolving the largest remaining ambiguity.")] string question,
        [Description("2-4 suggested answers separated by |.")] string options = "",
        [Description("Short example for free-text input.")] string placeholder = "직접 입력",
        [Description("Current readiness score from 0 to 100.")] int readinessScore = 0,
        CancellationToken cancellationToken = default)
    {
        string answer;
        using (runTimeout?.Pause())
            answer = await broker.RequestAsync(
                sessionId, question, options, placeholder, readinessScore,
                kind == "schedule" ? "스케줄 생성 설문" : "작업 생성 설문",
                onEvent, cancellationToken);
        if (!string.IsNullOrWhiteSpace(answer) && IsReminderMessageQuestion(question))
            _reminderMessageAsked = true;
        return answer;
    }

    private string CreateTask(
        [Description("Short display name.")] string name,
        [Description("Self-contained work instruction without secret values.")] string instruction)
    {
        var task = automation.StartTask(instruction, projectId, name);
        Completed = true;
        onEvent?.Invoke(new ChatRunEvent("automation_created", "task", sessionId, task.Id));
        return $"Background task registered: {task.Name} ({task.Id}).";
    }

    private string CreateReminder(
        [Description("Short reminder name.")] string name,
        [Description("Exact text shown in the Windows notification.")] string message,
        [Description("Delay from now in seconds, from 1 second to 30 days.")] int delaySeconds)
    {
        if (!_reminderMessageAsked)
            throw new InvalidOperationException(
                "알림을 등록하기 전에 ask_automation_question으로 ‘무슨 내용으로 알릴까요?’를 묻고 답을 받아야 합니다.");
        var reminder = automation.CreateReminder(message, delaySeconds, name);
        Completed = true;
        onEvent?.Invoke(new ChatRunEvent("automation_created", "reminder", sessionId, reminder.Id));
        return $"Reminder registered: {reminder.Name} ({reminder.Id}).";
    }

    private static bool IsReminderMessageQuestion(string question)
    {
        var normalized = question.Replace(" ", "", StringComparison.Ordinal);
        return normalized.Contains("뭐라고", StringComparison.Ordinal) ||
               normalized.Contains("무슨내용", StringComparison.Ordinal) ||
               normalized.Contains("알림내용", StringComparison.Ordinal) ||
               normalized.Contains("알릴내용", StringComparison.Ordinal);
    }

    private string CreateSchedule(
        [Description("Short schedule name.")] string name,
        [Description("Standard numeric 5-field cron expression.")] string cronExpression,
        [Description("Self-contained instruction without secret values.")] string instruction)
    {
        var schedule = automation.CreateSchedule(name, cronExpression, instruction, projectId);
        Completed = true;
        onEvent?.Invoke(new ChatRunEvent("automation_created", "schedule", sessionId, schedule.Id));
        return $"Schedule registered: {schedule.Name} ({schedule.Id}).";
    }
}
