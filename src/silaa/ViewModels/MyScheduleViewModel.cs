using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using silaa.Core;
using silaa.Services;

namespace silaa.ViewModels;

public partial class MyScheduleViewModel : ObservableObject
{
    private readonly LlmHarness _harness;
    private readonly Func<Task> _createEvent;
    private readonly Func<string, string, Task<bool>> _confirm;

    public MyScheduleViewModel(
        LlmHarness harness,
        NavigationViewModel navigation,
        Func<Task> createEvent,
        Func<string, string, Task<bool>>? confirm = null)
    {
        _harness = harness;
        Navigation = navigation;
        _createEvent = createEvent;
        _confirm = confirm ?? ((_, _) => Task.FromResult(true));
    }

    public NavigationViewModel Navigation { get; }
    public ObservableCollection<ScheduleAgendaItemViewModel> Items { get; } = [];

    [ObservableProperty] private string _summary = "일정을 불러오는 중";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _hasNoItems = true;

    [RelayCommand(AllowConcurrentExecutions = false)]
    public async Task RefreshAsync()
    {
        try
        {
            var now = DateTimeOffset.Now;
            var events = await _harness.ListCalendarEventsAsync();
            Items.Clear();
            foreach (var item in events
                         .Where(item => item.EndAt.ToLocalTime() >= now.AddDays(-1))
                         .OrderBy(item => item.StartAt)
                         .Take(200))
                Items.Add(new ScheduleAgendaItemViewModel(item, DeleteAsync));
            var today = Items.Count(item => item.When.Date == now.Date);
            Summary = $"오늘 {today}개 · 예정 {Items.Count}개";
            HasNoItems = Items.Count == 0;
            Error = "";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            Items.Clear();
            HasNoItems = true;
            Summary = "일정을 불러오지 못했습니다";
            Error = exception.Message;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task CreateScheduleAsync() => _createEvent();

    private async Task DeleteAsync(ScheduleAgendaItemViewModel item)
    {
        if (!await _confirm("일정 삭제", $"‘{item.Title}’ 일정을 삭제할까요?")) return;
        if (!_harness.DeleteCalendarEvent(item.Id)) return;
        Items.Remove(item);
        var now = DateTimeOffset.Now;
        Summary = $"오늘 {Items.Count(entry => entry.When.Date == now.Date)}개 · 예정 {Items.Count}개";
        HasNoItems = Items.Count == 0;
    }
}

public sealed class ScheduleAgendaItemViewModel
{
    public ScheduleAgendaItemViewModel(CalendarEventInfo item, Func<ScheduleAgendaItemViewModel, Task> delete)
    {
        Id = item.Id;
        When = item.StartAt.ToLocalTime();
        Title = item.Title;
        Kind = item.AllDay ? "하루 종일" : "일정";
        Description = string.Join(" · ", new[] { item.Location, item.Notes }.Where(value => !string.IsNullOrWhiteSpace(value)));
        Day = When.ToString("dd");
        Month = When.ToString("MM월");
        DateLabel = FormatDateLabel(item);
        HasReminder = item.ReminderTaskId is not null;
        HasPreparationTask = item.PreparationTaskId is not null;
        DeleteCommand = new AsyncRelayCommand(() => delete(this));
    }

    public string Id { get; }
    public DateTimeOffset When { get; }
    public string Title { get; }
    public string Kind { get; }
    public string Description { get; }
    public string Day { get; }
    public string Month { get; }
    public string DateLabel { get; }
    public bool HasReminder { get; }
    public bool HasPreparationTask { get; }
    public IAsyncRelayCommand DeleteCommand { get; }

    private static string FormatDateLabel(CalendarEventInfo item)
    {
        var start = item.StartAt.ToLocalTime();
        var end = item.EndAt.ToLocalTime();
        if (item.AllDay) return $"{start:yyyy.MM.dd ddd} · 하루 종일";
        var prefix = start.Date == DateTimeOffset.Now.Date
            ? "오늘"
            : start.Date == DateTimeOffset.Now.Date.AddDays(1) ? "내일" : start.ToString("yyyy.MM.dd ddd");
        return start.Date == end.Date
            ? $"{prefix} {start:HH:mm}–{end:HH:mm}"
            : $"{prefix} {start:HH:mm}–{end:MM.dd HH:mm}";
    }
}
