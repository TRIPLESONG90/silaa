using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;
using silaa.Core;

namespace silaa.Services;

internal sealed class CalendarOnboardingTools(
    string sessionId,
    ProjectInteractionBroker broker,
    CalendarEventStore calendar,
    BackgroundAutomationService automation,
    Action<ChatRunEvent>? onEvent,
    RunActivityTimeout? runTimeout = null)
{
    private bool _optionalAutomationAsked;

    public IList<AITool> CreateFunctions() =>
    [
        AIFunctionFactory.Create(
            (Func<string, string, string, int, CancellationToken, Task<string>>)AskQuestionAsync,
            "ask_calendar_question",
            "Render one interactive calendar question and wait for the user's answer."),
        AIFunctionFactory.Create(
            (Func<string, string, string, bool, string, string, string, int, string, string, string>)CreateEvent,
            "finalize_calendar_event",
            "Create one calendar event. Optional alarm or preparation task fields must be empty unless the user explicitly accepted them.")
    ];

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
                "일정 추가", onEvent, cancellationToken);
        if (MentionsOptionalAutomation(question)) _optionalAutomationAsked = true;
        return answer;
    }

    private string CreateEvent(
        [Description("Calendar event title.")] string title,
        [Description("Start date and time in ISO 8601 with local UTC offset.")] string startAt,
        [Description("End date and time in ISO 8601 with local UTC offset.")] string endAt,
        [Description("True for an all-day event.")] bool allDay,
        [Description("Place or online meeting location; empty when absent.")] string location = "",
        [Description("Optional event notes; empty when absent.")] string notes = "",
        [Description("Exact Windows notification text; empty when no alarm was accepted.")] string reminderMessage = "",
        [Description("Minutes before event start; zero when no alarm was accepted.")] int reminderMinutesBefore = 0,
        [Description("Preparation task display name; empty when no task was accepted.")] string preparationTaskName = "",
        [Description("Self-contained preparation instruction; empty when no task was accepted.")] string preparationTaskInstruction = "")
    {
        var hasReminder = !string.IsNullOrWhiteSpace(reminderMessage) || reminderMinutesBefore > 0;
        var hasPreparationTask = !string.IsNullOrWhiteSpace(preparationTaskName) ||
                                 !string.IsNullOrWhiteSpace(preparationTaskInstruction);
        if ((hasReminder || hasPreparationTask) && !_optionalAutomationAsked)
            throw new InvalidOperationException(
                "알람이나 준비 작업을 추가하기 전에 ask_calendar_question으로 사용자의 동의를 받아야 합니다.");
        if (!DateTimeOffset.TryParse(startAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var start))
            throw new ArgumentException("시작 시각은 ISO 8601 형식이어야 합니다.");
        if (!DateTimeOffset.TryParse(endAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var end) || end <= start)
            throw new ArgumentException("종료 시각은 시작 시각보다 뒤인 ISO 8601 값이어야 합니다.");
        if (reminderMinutesBefore < 0 || reminderMinutesBefore > 525_600)
            throw new ArgumentException("알람은 일정 시작 0분~365일 전으로 설정할 수 있습니다.");
        if (hasReminder && (string.IsNullOrWhiteSpace(reminderMessage) || reminderMinutesBefore <= 0))
            throw new ArgumentException("알람을 등록하려면 알림 내용과 몇 분 전인지 모두 입력해야 합니다.");
        if (hasPreparationTask && (string.IsNullOrWhiteSpace(preparationTaskName) ||
                                   string.IsNullOrWhiteSpace(preparationTaskInstruction)))
            throw new ArgumentException("준비 작업을 등록하려면 작업 이름과 실행 지침이 모두 필요합니다.");

        BackgroundTaskInfo? reminder = null;
        BackgroundTaskInfo? preparation = null;
        if (hasReminder)
            reminder = automation.CreateReminderAt(
                reminderMessage, start.ToUniversalTime().AddMinutes(-reminderMinutesBefore), $"{title} 알람");
        if (hasPreparationTask)
            preparation = automation.StartTask(preparationTaskInstruction, null, preparationTaskName);

        var calendarEvent = calendar.Add(
            title, startAt, endAt, allDay, location, notes, reminder?.Id, preparation?.Id);
        onEvent?.Invoke(new ChatRunEvent("calendar_created", "event", sessionId, calendarEvent.Id));
        return $"Calendar event registered: {calendarEvent.Title} ({calendarEvent.Id}).";
    }

    private static bool MentionsOptionalAutomation(string question)
    {
        var normalized = question.Replace(" ", "", StringComparison.Ordinal);
        return normalized.Contains("알람", StringComparison.Ordinal) ||
               normalized.Contains("준비작업", StringComparison.Ordinal) ||
               normalized.Contains("작업도", StringComparison.Ordinal);
    }
}
