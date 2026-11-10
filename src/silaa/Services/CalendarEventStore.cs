using System.Globalization;
using System.Text;
using System.Text.Json;
using silaa.Core;

namespace silaa.Services;

internal sealed class CalendarEventStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private List<CalendarEventInfo> _events;

    public CalendarEventStore(string root)
    {
        _path = Path.Combine(root, "calendar-events.json");
        _events = Load();
        if (MigrateLegacyWaitingReminders(root)) Save();
    }

    public IReadOnlyList<CalendarEventInfo> List()
    {
        lock (_gate)
            return _events.OrderBy(item => item.StartAt).Select(Clone).ToList();
    }

    public CalendarEventInfo Add(
        string title,
        string startAt,
        string endAt,
        bool allDay,
        string? location,
        string? notes,
        string? reminderTaskId = null,
        string? preparationTaskId = null)
    {
        title = ValidateText(title, 120, "일정 제목", required: true);
        location = ValidateText(location, 300, "장소");
        notes = ValidateText(notes, 4_000, "메모");
        var start = ParseDateTime(startAt, "시작 시각");
        var end = ParseDateTime(endAt, "종료 시각");
        if (end <= start) throw new ArgumentException("종료 시각은 시작 시각보다 뒤여야 합니다.");
        if (end - start > TimeSpan.FromDays(366))
            throw new ArgumentException("하나의 일정은 366일보다 길 수 없습니다.");

        var now = DateTimeOffset.UtcNow;
        var item = new CalendarEventInfo
        {
            Id = $"event_{Guid.NewGuid():N}",
            Title = title,
            StartAt = start.ToUniversalTime(),
            EndAt = end.ToUniversalTime(),
            AllDay = allDay,
            Location = location,
            Notes = notes,
            ReminderTaskId = NullIfEmpty(reminderTaskId),
            PreparationTaskId = NullIfEmpty(preparationTaskId),
            CreatedAt = now,
            UpdatedAt = now
        };
        lock (_gate)
        {
            _events.Add(item);
            Save();
            return Clone(item);
        }
    }

    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_gate)
        {
            var removed = _events.RemoveAll(item => item.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    private List<CalendarEventInfo> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<CalendarEventInfo>>(File.ReadAllText(_path)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private bool MigrateLegacyWaitingReminders(string root)
    {
        var legacyPath = Path.Combine(root, "background-automation.json");
        if (!File.Exists(legacyPath)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(legacyPath));
            if (!document.RootElement.TryGetProperty("Tasks", out var tasks) ||
                tasks.ValueKind != JsonValueKind.Array) return false;
            var changed = false;
            var now = DateTimeOffset.UtcNow;
            foreach (var task in tasks.EnumerateArray())
            {
                if (!ReadString(task, "Kind").Equals("reminder", StringComparison.OrdinalIgnoreCase) ||
                    !ReadString(task, "Status").Equals("waiting", StringComparison.OrdinalIgnoreCase) ||
                    !task.TryGetProperty("DueAt", out var dueValue) ||
                    !dueValue.TryGetDateTimeOffset(out var dueAt) || dueAt <= now) continue;
                var taskId = ReadString(task, "Id");
                if (taskId.Length == 0 || _events.Any(item => item.ReminderTaskId == taskId)) continue;
                var title = ReadString(task, "Name");
                if (title.EndsWith(" 알림", StringComparison.Ordinal)) title = title[..^3].TrimEnd();
                if (title.Length == 0) title = "알림 일정";
                _events.Add(new CalendarEventInfo
                {
                    Id = $"event_{Guid.NewGuid():N}",
                    Title = title,
                    StartAt = dueAt.ToUniversalTime(),
                    EndAt = dueAt.ToUniversalTime().AddMinutes(30),
                    Notes = ReadString(task, "Instruction"),
                    ReminderTaskId = taskId,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                changed = true;
            }
            return changed;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";

    private void Save()
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_events, Json), new UTF8Encoding(false));
        File.Move(temporary, _path, true);
    }

    private static DateTimeOffset ParseDateTime(string value, string field)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed))
            throw new ArgumentException($"{field}은 ISO 8601 형식으로 입력해야 합니다.");
        return parsed;
    }

    private static string ValidateText(string? value, int maximum, string field, bool required = false)
    {
        var normalized = (value ?? "").Trim();
        if (required && normalized.Length == 0) throw new ArgumentException($"{field}을 입력하세요.");
        if (normalized.Length > maximum || normalized.Any(character => character == '\0'))
            throw new ArgumentException($"{field}은 {maximum:N0}자 이내여야 합니다.");
        return normalized;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CalendarEventInfo Clone(CalendarEventInfo item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        StartAt = item.StartAt,
        EndAt = item.EndAt,
        AllDay = item.AllDay,
        Location = item.Location,
        Notes = item.Notes,
        ReminderTaskId = item.ReminderTaskId,
        PreparationTaskId = item.PreparationTaskId,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt
    };
}
