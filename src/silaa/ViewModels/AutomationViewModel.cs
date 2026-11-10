using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using silaa.Core;
using silaa.Services;

namespace silaa.ViewModels;

public partial class AutomationViewModel : ObservableObject
{
    private readonly LlmHarness _harness;
    private readonly Func<string, string, Task<bool>> _confirmDelete;
    private readonly Func<BackgroundTaskInfo, Task> _openTask;
    private readonly Func<Task> _createTask;
    private readonly Func<Task> _createSchedule;

    public AutomationViewModel(LlmHarness harness, NavigationViewModel navigation,
        Func<string, string, Task<bool>> confirmDelete, Func<BackgroundTaskInfo, Task> openTask,
        Func<Task>? createTask = null, Func<Task>? createSchedule = null)
    {
        _harness = harness;
        Navigation = navigation;
        _confirmDelete = confirmDelete;
        _openTask = openTask;
        _createTask = createTask ?? (() => Task.CompletedTask);
        _createSchedule = createSchedule ?? (() => Task.CompletedTask);
    }

    public NavigationViewModel Navigation { get; }
    public ObservableCollection<ScheduleCardViewModel> Schedules { get; } = [];
    public ObservableCollection<BackgroundTaskCardViewModel> Tasks { get; } = [];

    [ObservableProperty] private string _activeLabel = "0 active · 0 cron";
    [ObservableProperty] private bool _hasNoSchedules = true;
    [ObservableProperty] private bool _hasNoTasks = true;
    [ObservableProperty] private string _error = "";

    [RelayCommand(AllowConcurrentExecutions = false)]
    public async Task RefreshAsync()
    {
        try
        {
            var tasks = await _harness.ListBackgroundTasksAsync();
            var schedules = await _harness.ListScheduledJobsAsync();
            Schedules.Clear();
            Tasks.Clear();
            foreach (var schedule in schedules)
                Schedules.Add(new ScheduleCardViewModel(schedule, ToggleScheduleAsync, RemoveScheduleAsync));
            foreach (var task in tasks.Take(60))
                Tasks.Add(new BackgroundTaskCardViewModel(task, CancelTaskAsync, _openTask));
            var active = tasks.Count(task => task.Status is "waiting" or "queued" or "running");
            ActiveLabel = $"{active} active · {schedules.Count(schedule => schedule.Enabled)} cron";
            HasNoSchedules = Schedules.Count == 0;
            HasNoTasks = Tasks.Count == 0;
            Error = "";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            Schedules.Clear();
            Tasks.Clear();
            HasNoSchedules = true;
            HasNoTasks = true;
            Error = exception.Message;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task CreateTaskAsync() => _createTask();

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task CreateScheduleAsync() => _createSchedule();

    private async Task ToggleScheduleAsync(ScheduledJobInfo schedule)
    {
        _harness.SetScheduledJobEnabled(schedule.Id, !schedule.Enabled);
        await RefreshAsync();
    }

    private async Task RemoveScheduleAsync(ScheduledJobInfo schedule)
    {
        if (!await _confirmDelete("cron 작업 삭제", $"‘{schedule.Name}’ 반복 작업을 삭제합니다.")) return;
        _harness.DeleteScheduledJob(schedule.Id);
        await RefreshAsync();
    }

    private async Task CancelTaskAsync(BackgroundTaskInfo task)
    {
        _harness.CancelBackgroundTask(task.Id);
        await RefreshAsync();
    }
}

public sealed class ScheduleCardViewModel
{
    public ScheduleCardViewModel(ScheduledJobInfo schedule,
        Func<ScheduledJobInfo, Task> toggle, Func<ScheduledJobInfo, Task> remove)
    {
        Name = schedule.Name;
        State = schedule.Enabled ? "ENABLED" : "PAUSED";
        Cron = schedule.CronExpression;
        Instruction = schedule.Instruction;
        NextRun = schedule.NextRunAt is { } nextRun
            ? $"다음 {nextRun.ToLocalTime():yyyy.MM.dd HH:mm} · {schedule.LastStatus}"
            : schedule.LastStatus;
        ToggleLabel = schedule.Enabled ? "일시중지" : "재개";
        ToggleCommand = new AsyncRelayCommand(() => toggle(schedule));
        RemoveCommand = new AsyncRelayCommand(() => remove(schedule));
    }

    public string Name { get; }
    public string State { get; }
    public string Cron { get; }
    public string Instruction { get; }
    public string NextRun { get; }
    public string ToggleLabel { get; }
    public IAsyncRelayCommand ToggleCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
}

public sealed class BackgroundTaskCardViewModel
{
    public BackgroundTaskCardViewModel(BackgroundTaskInfo task,
        Func<BackgroundTaskInfo, Task> cancel, Func<BackgroundTaskInfo, Task> open)
    {
        Name = task.Name;
        State = task.Status switch
        {
            "waiting" => "WAITING", "queued" => "QUEUED", "running" => "RUNNING", "completed" => "DONE",
            "failed" => "FAILED", "cancelled" => "CANCELLED", _ => task.Status.ToUpperInvariant()
        };
        Preview = task.Error.Length > 0 ? task.Error : task.ResultPreview.Length > 0 ? task.ResultPreview : task.Instruction;
        Metadata = task.DueAt is { } due
            ? $"알림 {due.ToLocalTime():yyyy.MM.dd HH:mm:ss} · {task.Id}"
            : $"{task.CreatedAt.ToLocalTime():yyyy.MM.dd HH:mm} · {task.Id}";
        CanCancel = task.Status is "waiting" or "queued" or "running";
        CanOpen = task.SessionId is not null;
        CancelCommand = new AsyncRelayCommand(() => cancel(task));
        OpenCommand = new AsyncRelayCommand(() => open(task));
    }

    public string Name { get; }
    public string State { get; }
    public string Preview { get; }
    public string Metadata { get; }
    public bool CanCancel { get; }
    public bool CanOpen { get; }
    public IAsyncRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand OpenCommand { get; }
}
