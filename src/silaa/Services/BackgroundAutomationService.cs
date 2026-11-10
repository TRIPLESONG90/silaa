using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using silaa.Core;

namespace silaa.Services;

internal sealed class BackgroundAutomationService : IAsyncDisposable
{
    private int _disposed;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<BackgroundTaskInfo, CancellationToken, Task<ChatRunResult>> _runner;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private AutomationState _state;
    private readonly Task _worker;
    private readonly Task _scheduler;

    public BackgroundAutomationService(string root, Func<BackgroundTaskInfo, CancellationToken, Task<ChatRunResult>> runner)
    {
        _path = Path.Combine(root, "background-automation.json");
        _runner = runner;
        _state = Load();
        RecoverInterruptedTasks();
        _worker = Task.Run(WorkerLoopAsync);
        _scheduler = Task.Run(SchedulerLoopAsync);
        foreach (var task in ListTasks().Where(task => task.Status == "queued")) _queue.Writer.TryWrite(task.Id);
    }

    public event Action<BackgroundTaskInfo>? TaskChanged;
    public event Action<ScheduledJobInfo>? ScheduleChanged;

    public bool HasPersistentWork
    {
        get
        {
            lock (_gate)
                return _state.Tasks.Any(task => task.Status is "waiting" or "queued" or "running") ||
                       _state.Schedules.Any(schedule => schedule.Enabled);
        }
    }

    public IReadOnlyList<BackgroundTaskInfo> ListTasks()
    {
        lock (_gate) return _state.Tasks.OrderByDescending(task => task.CreatedAt).Select(Clone).ToList();
    }

    public IReadOnlyList<ScheduledJobInfo> ListSchedules()
    {
        lock (_gate) return _state.Schedules.OrderBy(schedule => schedule.Name).Select(Clone).ToList();
    }

    public BackgroundTaskInfo StartTask(string instruction, string? projectId, string? name = null, string? scheduleId = null)
    {
        instruction = ValidateInstruction(instruction);
        name = string.IsNullOrWhiteSpace(name) ? TitleFrom(instruction) : ValidateName(name);
        var task = new BackgroundTaskInfo
        {
            Id = $"task_{Guid.NewGuid():N}",
            Name = name,
            Instruction = instruction,
            ProjectId = projectId,
            ScheduleId = scheduleId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        lock (_gate)
        {
            _state.Tasks.Add(task);
            TrimHistory();
            Save();
        }
        _queue.Writer.TryWrite(task.Id);
        TaskChanged?.Invoke(Clone(task));
        return Clone(task);
    }

    public BackgroundTaskInfo CreateReminder(string message, int delaySeconds, string? name = null)
    {
        if (delaySeconds is < 1 or > 2_592_000)
            throw new ArgumentException("Reminder delay must be between 1 second and 30 days.");
        return CreateReminderAt(message, DateTimeOffset.UtcNow.AddSeconds(delaySeconds), name);
    }

    public BackgroundTaskInfo CreateReminderAt(string message, DateTimeOffset dueAt, string? name = null)
    {
        message = ValidateInstruction(message);
        name = string.IsNullOrWhiteSpace(name) ? TitleFrom(message) : ValidateName(name);
        var now = DateTimeOffset.UtcNow;
        dueAt = dueAt.ToUniversalTime();
        if (dueAt <= now.AddSeconds(1) || dueAt > now.AddYears(10))
            throw new ArgumentException("알람 시각은 현재부터 10년 이내의 미래여야 합니다.");
        var task = new BackgroundTaskInfo
        {
            Id = $"task_{Guid.NewGuid():N}",
            Name = name,
            Instruction = message,
            Kind = "reminder",
            Status = "waiting",
            CreatedAt = now,
            DueAt = dueAt
        };
        lock (_gate)
        {
            _state.Tasks.Add(task);
            TrimHistory();
            Save();
        }
        TaskChanged?.Invoke(Clone(task));
        return Clone(task);
    }

    public bool CancelTask(string id)
    {
        BackgroundTaskInfo? changed = null;
        lock (_gate)
        {
            var task = _state.Tasks.FirstOrDefault(item => item.Id == id);
            if (task is null || task.Status is "completed" or "failed" or "cancelled") return false;
            if (_running.TryGetValue(id, out var cancellation)) cancellation.Cancel();
            else
            {
                task.Status = "cancelled";
                task.CompletedAt = DateTimeOffset.UtcNow;
                task.Error = "사용자가 작업을 취소했습니다.";
                changed = Clone(task);
                Save();
            }
        }
        if (changed is not null) TaskChanged?.Invoke(changed);
        return true;
    }

    public ScheduledJobInfo CreateSchedule(string name, string cronExpression, string instruction, string? projectId)
    {
        name = ValidateName(name);
        instruction = ValidateInstruction(instruction);
        var cron = CronSchedule.Parse(cronExpression);
        var now = DateTimeOffset.UtcNow;
        var schedule = new ScheduledJobInfo
        {
            Id = $"cron_{Guid.NewGuid():N}",
            Name = name,
            CronExpression = cronExpression.Trim(),
            Instruction = instruction,
            ProjectId = projectId,
            CreatedAt = now,
            UpdatedAt = now,
            NextRunAt = cron.GetNext(now)
        };
        lock (_gate)
        {
            if (_state.Schedules.Count >= 100) throw new InvalidOperationException("cron 작업은 최대 100개까지 등록할 수 있습니다.");
            _state.Schedules.Add(schedule);
            Save();
        }
        ScheduleChanged?.Invoke(Clone(schedule));
        return Clone(schedule);
    }

    public ScheduledJobInfo SetScheduleEnabled(string id, bool enabled)
    {
        ScheduledJobInfo changed;
        lock (_gate)
        {
            var schedule = FindSchedule(id);
            schedule.Enabled = enabled;
            schedule.UpdatedAt = DateTimeOffset.UtcNow;
            schedule.NextRunAt = enabled ? CronSchedule.Parse(schedule.CronExpression).GetNext(DateTimeOffset.UtcNow) : null;
            schedule.LastStatus = enabled ? "대기 중" : "일시중지";
            changed = Clone(schedule);
            Save();
        }
        ScheduleChanged?.Invoke(changed);
        return changed;
    }

    public bool DeleteSchedule(string id)
    {
        ScheduledJobInfo? removed;
        lock (_gate)
        {
            removed = _state.Schedules.FirstOrDefault(schedule => schedule.Id == id);
            if (removed is null) return false;
            _state.Schedules.Remove(removed);
            Save();
        }
        removed.Enabled = false;
        removed.LastStatus = "삭제됨";
        ScheduleChanged?.Invoke(Clone(removed));
        return true;
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(_lifetime.Token))
            {
                BackgroundTaskInfo? task;
                CancellationTokenSource cancellation;
                lock (_gate)
                {
                    task = _state.Tasks.FirstOrDefault(item => item.Id == id && item.Status == "queued");
                    if (task is null) continue;
                    cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    _running[id] = cancellation;
                    task.Status = "running";
                    task.StartedAt = DateTimeOffset.UtcNow;
                    Save();
                    task = Clone(task);
                }
                TaskChanged?.Invoke(task);
                try
                {
                    if (task.Kind == "reminder")
                    {
                        FinishTask(id, "completed", null, task.Instruction, "");
                        continue;
                    }
                    var result = await _runner(task, cancellation.Token);
                    FinishTask(id, "completed", result.SessionId,
                        result.Answer.Length > 600 ? result.Answer[..600] + "…" : result.Answer, "");
                }
                catch (OperationCanceledException)
                {
                    FinishTask(id, "cancelled", null, "", "작업이 취소되었습니다.");
                }
                catch (Exception exception)
                {
                    FinishTask(id, "failed", null, "", exception.Message);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (_running.Remove(id, out var source)) source.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task SchedulerLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TriggerDueReminders();
                TriggerDueSchedules();
                await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    internal void TriggerDueReminders(DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var due = new List<BackgroundTaskInfo>();
        lock (_gate)
        {
            foreach (var task in _state.Tasks.Where(task => task.Kind == "reminder" &&
                         task.Status == "waiting" && task.DueAt <= now))
            {
                task.Status = "queued";
                due.Add(Clone(task));
            }
            if (due.Count > 0) Save();
        }
        foreach (var task in due)
        {
            _queue.Writer.TryWrite(task.Id);
            TaskChanged?.Invoke(task);
        }
    }

    internal void TriggerDueSchedules(DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var queued = new List<BackgroundTaskInfo>();
        var changedSchedules = new List<ScheduledJobInfo>();
        lock (_gate)
        {
            foreach (var schedule in _state.Schedules.Where(item => item.Enabled && item.NextRunAt <= now))
            {
                var cron = CronSchedule.Parse(schedule.CronExpression);
                schedule.LastRunAt = now;
                schedule.NextRunAt = cron.GetNext(now);
                schedule.UpdatedAt = now;
                if (_state.Tasks.Any(task => task.ScheduleId == schedule.Id && task.Status is "queued" or "running"))
                {
                    schedule.LastStatus = "이전 실행 진행 중 · 이번 실행 건너뜀";
                }
                else
                {
                    var task = new BackgroundTaskInfo
                    {
                        Id = $"task_{Guid.NewGuid():N}",
                        Name = schedule.Name,
                        Instruction = schedule.Instruction,
                        ProjectId = schedule.ProjectId,
                        ScheduleId = schedule.Id,
                        CreatedAt = now
                    };
                    _state.Tasks.Add(task);
                    schedule.LastTaskId = task.Id;
                    schedule.LastStatus = "실행 대기";
                    queued.Add(Clone(task));
                }
                changedSchedules.Add(Clone(schedule));
            }
            if (changedSchedules.Count > 0)
            {
                TrimHistory();
                Save();
            }
        }
        foreach (var task in queued)
        {
            _queue.Writer.TryWrite(task.Id);
            TaskChanged?.Invoke(task);
        }
        foreach (var schedule in changedSchedules) ScheduleChanged?.Invoke(schedule);
    }

    private void FinishTask(string id, string status, string? sessionId, string preview, string error)
    {
        BackgroundTaskInfo? changed = null;
        ScheduledJobInfo? scheduleChanged = null;
        lock (_gate)
        {
            var task = _state.Tasks.FirstOrDefault(item => item.Id == id);
            if (task is null) return;
            task.Status = status;
            task.SessionId = sessionId;
            task.ResultPreview = preview;
            task.Error = error;
            task.CompletedAt = DateTimeOffset.UtcNow;
            changed = Clone(task);
            if (task.ScheduleId is not null && _state.Schedules.FirstOrDefault(item => item.Id == task.ScheduleId) is { } schedule)
            {
                schedule.LastStatus = status switch
                {
                    "completed" => "최근 실행 완료",
                    "cancelled" => "최근 실행 취소",
                    _ => "최근 실행 실패"
                };
                schedule.UpdatedAt = DateTimeOffset.UtcNow;
                scheduleChanged = Clone(schedule);
            }
            Save();
        }
        if (changed is not null) TaskChanged?.Invoke(changed);
        if (scheduleChanged is not null) ScheduleChanged?.Invoke(scheduleChanged);
    }

    private void RecoverInterruptedTasks()
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var task in _state.Tasks.Where(task => task.Status == "running"))
            {
                task.Status = "failed";
                task.Error = "이전 silaa 프로세스가 종료되어 작업이 중단되었습니다.";
                task.CompletedAt = DateTimeOffset.UtcNow;
                changed = true;
            }
            foreach (var schedule in _state.Schedules.Where(schedule => schedule.Enabled))
            {
                try
                {
                    if (schedule.NextRunAt is null)
                    {
                        schedule.NextRunAt = CronSchedule.Parse(schedule.CronExpression).GetNext(DateTimeOffset.UtcNow);
                        changed = true;
                    }
                }
                catch (ArgumentException)
                {
                    schedule.Enabled = false;
                    schedule.LastStatus = "cron 형식 오류로 중지됨";
                    changed = true;
                }
            }
            if (changed) Save();
        }
    }

    private ScheduledJobInfo FindSchedule(string id) =>
        _state.Schedules.FirstOrDefault(schedule => schedule.Id == id)
        ?? throw new InvalidOperationException("cron 작업을 찾지 못했습니다.");

    private AutomationState Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AutomationState>(File.ReadAllText(_path), Json) ?? new AutomationState()
                : new AutomationState();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new IOException("백그라운드 작업 파일을 읽지 못했습니다.", exception);
        }
    }

    private void Save()
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_state, Json), new UTF8Encoding(false));
        File.Move(temporary, _path, true);
    }

    private void TrimHistory()
    {
        var active = _state.Tasks.Where(task => task.Status is "waiting" or "queued" or "running").ToList();
        var history = _state.Tasks.Where(task => task.Status is not ("waiting" or "queued" or "running"))
            .OrderByDescending(task => task.CreatedAt).Take(200).ToList();
        _state.Tasks = active.Concat(history).ToList();
    }

    private static string ValidateInstruction(string value)
    {
        value = value.Trim();
        if (value.Length is < 1 or > 20_000) throw new ArgumentException("작업 지시는 1~20,000자여야 합니다.");
        return value;
    }

    private static string ValidateName(string? value)
    {
        value = value?.Trim() ?? "";
        if (value.Length is < 1 or > 80) throw new ArgumentException("작업 이름은 1~80자여야 합니다.");
        return value;
    }

    private static string TitleFrom(string instruction)
    {
        var title = string.Join(' ', instruction.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return title.Length > 80 ? title[..80] : title;
    }

    private static BackgroundTaskInfo Clone(BackgroundTaskInfo task) => new()
    {
        Id = task.Id, Name = task.Name, Instruction = task.Instruction, Kind = task.Kind, Status = task.Status,
        ProjectId = task.ProjectId, ScheduleId = task.ScheduleId, SessionId = task.SessionId,
        ResultPreview = task.ResultPreview, Error = task.Error, CreatedAt = task.CreatedAt,
        DueAt = task.DueAt, StartedAt = task.StartedAt, CompletedAt = task.CompletedAt
    };

    private static ScheduledJobInfo Clone(ScheduledJobInfo schedule) => new()
    {
        Id = schedule.Id, Name = schedule.Name, CronExpression = schedule.CronExpression,
        Instruction = schedule.Instruction, ProjectId = schedule.ProjectId, Enabled = schedule.Enabled,
        CreatedAt = schedule.CreatedAt, UpdatedAt = schedule.UpdatedAt, NextRunAt = schedule.NextRunAt,
        LastRunAt = schedule.LastRunAt, LastTaskId = schedule.LastTaskId, LastStatus = schedule.LastStatus
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        _lifetime.Cancel();
        CancellationTokenSource[] running;
        lock (_gate) running = _running.Values.ToArray();
        foreach (var source in running) source.Cancel();
        try { await Task.WhenAll(_worker, _scheduler); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }

    private sealed class AutomationState
    {
        public List<BackgroundTaskInfo> Tasks { get; set; } = [];
        public List<ScheduledJobInfo> Schedules { get; set; } = [];
    }
}

internal sealed class BackgroundAutomationTools(BackgroundAutomationService service, string? projectId)
{
    public IList<AITool> CreateFunctions() =>
    [
        AIFunctionFactory.Create((Func<string, string, string>)StartBackgroundTask, "start_background_task",
            "Queue an independent or long-running agent task and return immediately. Use only when the user explicitly asks for background execution."),
        AIFunctionFactory.Create((Func<string, int, string, string>)CreateReminder, "create_reminder",
            "Schedule a persistent local reminder without calling the LLM at delivery time. Before calling, always ask the user '무슨 내용으로 알릴까요?' and wait for their answer. Use this instead of making a background agent wait."),
        AIFunctionFactory.Create((Func<string>)ListBackgroundTasks, "list_background_tasks", "List recent queued, running, completed, failed, and cancelled background tasks."),
        AIFunctionFactory.Create((Func<string, string>)CancelBackgroundTask, "cancel_background_task", "Cancel one queued or running background task by ID."),
        AIFunctionFactory.Create((Func<string, string, string, string>)CreateCronJob, "create_cron_job",
            "Register a recurring agent task using a standard numeric 5-field cron expression: minute hour day-of-month month day-of-week. Times use the computer's local timezone. Use only after an explicit recurring-task request."),
        AIFunctionFactory.Create((Func<string>)ListCronJobs, "list_cron_jobs", "List recurring cron jobs, next runs, and last status."),
        AIFunctionFactory.Create((Func<string, bool, string>)SetCronJobEnabled, "set_cron_job_enabled", "Pause or resume a cron job by ID."),
        AIFunctionFactory.Create((Func<string, string>)DeleteCronJob, "delete_cron_job", "Permanently delete a cron job by ID.")
    ];

    public string StartBackgroundTask(
        [Description("Self-contained work instruction. Do not include secret values.")] string instruction,
        [Description("Short display name.")] string name = "")
    {
        var task = service.StartTask(instruction, projectId, name);
        return $"Queued {task.Id}: {task.Name}. It will run after the current foreground turn.";
    }

    public string CreateReminder(
        [Description("Notification message shown when the reminder is due.")] string message,
        [Description("Delay from now in seconds, from 1 second to 30 days.")] int delaySeconds,
        [Description("Short display name.")] string name = "")
    {
        var reminder = service.CreateReminder(message, delaySeconds, name);
        return $"Scheduled {reminder.Id} for {reminder.DueAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}.";
    }

    public string ListBackgroundTasks() => JsonSerializer.Serialize(service.ListTasks().Take(30), new JsonSerializerOptions { WriteIndented = true });

    public string CancelBackgroundTask([Description("Background task ID.")] string taskId) =>
        service.CancelTask(taskId) ? $"Cancellation requested for {taskId}." : "Task was not found or already finished.";

    public string CreateCronJob(
        [Description("Short schedule name.")] string name,
        [Description("Standard 5-field cron expression, for example '0 9 * * 1-5'.")] string cronExpression,
        [Description("Self-contained instruction to execute on every occurrence. It may include running or editing workspace programs, but never secret values.")] string instruction)
    {
        var schedule = service.CreateSchedule(name, cronExpression, instruction, projectId);
        return $"Registered {schedule.Id}. Next run: {schedule.NextRunAt?.ToLocalTime():yyyy-MM-dd HH:mm zzz}.";
    }

    public string ListCronJobs() => JsonSerializer.Serialize(service.ListSchedules(), new JsonSerializerOptions { WriteIndented = true });

    public string SetCronJobEnabled([Description("Cron job ID.")] string cronJobId, bool enabled)
    {
        var schedule = service.SetScheduleEnabled(cronJobId, enabled);
        return $"{schedule.Id} is now {(schedule.Enabled ? "enabled" : "paused")}.";
    }

    public string DeleteCronJob([Description("Cron job ID.")] string cronJobId) =>
        service.DeleteSchedule(cronJobId) ? $"Deleted {cronJobId}." : "Cron job was not found.";
}

internal sealed class CronSchedule
{
    private readonly CronField _minute;
    private readonly CronField _hour;
    private readonly CronField _day;
    private readonly CronField _month;
    private readonly CronField _dayOfWeek;

    private CronSchedule(CronField minute, CronField hour, CronField day, CronField month, CronField dayOfWeek)
    {
        _minute = minute; _hour = hour; _day = day; _month = month; _dayOfWeek = dayOfWeek;
    }

    public static CronSchedule Parse(string expression)
    {
        var fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) throw new ArgumentException("cron 표현식은 '분 시 일 월 요일' 5개 필드여야 합니다.");
        return new CronSchedule(
            CronField.Parse(fields[0], 0, 59), CronField.Parse(fields[1], 0, 23),
            CronField.Parse(fields[2], 1, 31), CronField.Parse(fields[3], 1, 12),
            CronField.Parse(fields[4], 0, 7, normalizeSunday: true));
    }

    public DateTimeOffset GetNext(DateTimeOffset after, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(after, timeZone);
        var cursor = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var end = cursor.AddYears(2);
        while (cursor < end)
        {
            if (!timeZone.IsInvalidTime(cursor) && Matches(cursor))
            {
                var offset = timeZone.GetUtcOffset(cursor);
                return new DateTimeOffset(cursor, offset).ToUniversalTime();
            }
            cursor = cursor.AddMinutes(1);
        }
        throw new ArgumentException("향후 2년 안에 실행되는 cron 시간이 없습니다.");
    }

    private bool Matches(DateTime value)
    {
        if (!_minute.Contains(value.Minute) || !_hour.Contains(value.Hour) || !_month.Contains(value.Month)) return false;
        var day = _day.Contains(value.Day);
        var weekday = _dayOfWeek.Contains((int)value.DayOfWeek);
        var dayMatch = _day.IsWildcard || _dayOfWeek.IsWildcard ? day && weekday : day || weekday;
        return dayMatch;
    }

    private sealed class CronField
    {
        private readonly HashSet<int> _values;
        public bool IsWildcard { get; }

        private CronField(HashSet<int> values, bool wildcard) { _values = values; IsWildcard = wildcard; }
        public bool Contains(int value) => _values.Contains(value);

        public static CronField Parse(string source, int minimum, int maximum, bool normalizeSunday = false)
        {
            var values = new HashSet<int>();
            var wildcard = source.Split(',').Any(part => part.Split('/')[0] == "*");
            foreach (var part in source.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var stepParts = part.Split('/');
                var parsedStep = 1;
                if (stepParts.Length > 2 || (stepParts.Length == 2 && (!int.TryParse(stepParts[1], out parsedStep) || parsedStep < 1)))
                    throw new ArgumentException($"올바르지 않은 cron 필드입니다: {source}");
                var step = stepParts.Length == 2 ? parsedStep : 1;
                var range = stepParts[0];
                int start, end;
                if (range == "*") { start = minimum; end = maximum; }
                else if (range.Contains('-'))
                {
                    var bounds = range.Split('-');
                    if (bounds.Length != 2 || !int.TryParse(bounds[0], out start) || !int.TryParse(bounds[1], out end))
                        throw new ArgumentException($"올바르지 않은 cron 범위입니다: {range}");
                }
                else if (int.TryParse(range, out start)) end = start;
                else throw new ArgumentException($"올바르지 않은 cron 값입니다: {range}");
                if (start < minimum || end > maximum || start > end)
                    throw new ArgumentException($"cron 값은 {minimum}~{maximum} 범위여야 합니다: {range}");
                for (var value = start; value <= end; value += step)
                    values.Add(normalizeSunday && value == 7 ? 0 : value);
            }
            if (values.Count == 0) throw new ArgumentException("cron 필드가 비어 있습니다.");
            return new CronField(values, wildcard);
        }
    }
}
