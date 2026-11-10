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
    private IReadOnlyList<CalendarEventInfo> _allEvents = [];

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
    public IReadOnlyList<string> Views { get; } = ["다가오는 일정", "오늘", "7일"];

    [ObservableProperty] private string _summary = "일정을 불러오는 중";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _hasNoItems = true;
    [ObservableProperty] private string _selectedView = "다가오는 일정";

    partial void OnSelectedViewChanged(string value) => ApplyView();

    [RelayCommand(AllowConcurrentExecutions = false)]
    public async Task RefreshAsync()
    {
        try
        {
            var now = DateTimeOffset.Now;
            _allEvents = (await _harness.ListCalendarEventsAsync())
                .Where(item => item.EndAt.ToLocalTime() >= now.AddDays(-1))
                .OrderBy(item => item.StartAt)
                .Take(200)
                .ToList();
            ApplyView();
            Error = "";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _allEvents = [];
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
        _allEvents = _allEvents.Where(entry => entry.Id != item.Id).ToList();
        ApplyView();
    }

    private void ApplyView()
    {
        var now = DateTimeOffset.Now;
        var filtered = SelectedView switch
        {
            "오늘" => _allEvents.Where(item => item.StartAt.ToLocalTime().Date == now.Date),
            "7일" => _allEvents.Where(item => item.EndAt.ToLocalTime() >= now && item.StartAt.ToLocalTime() < now.AddDays(7)),
            _ => _allEvents.AsEnumerable()
        };
        Items.Clear();
        foreach (var item in filtered) Items.Add(new ScheduleAgendaItemViewModel(item, DeleteAsync));
        var today = _allEvents.Count(item => item.StartAt.ToLocalTime().Date == now.Date);
        Summary = $"오늘 {today}개 · 표시 {Items.Count}개";
        HasNoItems = Items.Count == 0;
    }
}

public sealed class ScheduleAgendaItemViewModel
{
    public ScheduleAgendaItemViewModel(CalendarEventInfo item, Func<ScheduleAgendaItemViewModel, Task> delete)
    {
        Id = item.Id;
        Source = item;
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
    public CalendarEventInfo Source { get; }
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
