using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using silaa.Core;
using silaa.Services;

namespace silaa.ViewModels;

public partial class MemoryViewModel : ObservableObject
{
    private readonly LlmHarness _harness;
    private readonly string? _projectId;
    private readonly Func<string, string, Task<bool>> _confirmDelete;
    private readonly Action<string, string, bool> _notify;

    public MemoryViewModel(LlmHarness harness, string? projectId, string? projectName,
        NavigationViewModel? navigation, Func<string, string, Task<bool>> confirmDelete,
        Action<string, string, bool> notify)
    {
        _harness = harness;
        _projectId = projectId;
        Navigation = navigation;
        _confirmDelete = confirmDelete;
        _notify = notify;
        ShowNavigation = navigation is not null;
        Eyebrow = projectId is null ? "KNOWLEDGE" : "PROJECT KNOWLEDGE";
        Title = projectId is null ? "기억" : $"{projectName} · Memory";
    }

    public NavigationViewModel? Navigation { get; }
    public IReadOnlyList<string> Categories { get; } = ["fact", "decision", "preference", "constraint", "lesson"];
    public ObservableCollection<MemoryEntryViewModel> Entries { get; } = [];
    public bool ShowNavigation { get; }
    public string Eyebrow { get; }
    public string Title { get; }

    [ObservableProperty] private string _selectedCategory = "fact";
    [ObservableProperty] private string _content = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isEmpty;

    [RelayCommand(AllowConcurrentExecutions = false)]
    public async Task RefreshAsync()
    {
        Entries.Clear();
        var memories = await _harness.ListMemoryAsync(_projectId);
        foreach (var memory in memories)
            Entries.Add(new MemoryEntryViewModel(memory, RemoveAsync));
        IsEmpty = Entries.Count == 0;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SaveAsync()
    {
        try
        {
            await _harness.AddMemoryAsync(_projectId, SelectedCategory, Content);
            Content = "";
            Status = _projectId is null ? "전역 Memory에 저장했습니다." : "프로젝트 Memory에 저장했습니다.";
            _notify("Memory 저장", Status, false);
            await RefreshAsync();
        }
        catch (ArgumentException exception)
        {
            Status = exception.Message;
            _notify("Memory 저장 실패", exception.Message, true);
        }
    }

    private async Task RemoveAsync(MemoryEntry entry)
    {
        if (!await _confirmDelete("Memory 삭제", "이 기억을 삭제하면 Agent가 이후 대화에서 다시 사용할 수 없습니다.")) return;
        await _harness.RemoveMemoryAsync(entry.Id);
        Status = "Memory 항목을 삭제했습니다.";
        _notify("Memory 삭제", "선택한 기억을 삭제했습니다.", false);
        await RefreshAsync();
    }
}

public sealed class MemoryEntryViewModel
{
    public MemoryEntryViewModel(MemoryEntry entry, Func<MemoryEntry, Task> remove)
    {
        Scope = $"{entry.Category} · {(entry.ProjectId is null ? "전역" : "프로젝트")}";
        Content = entry.Content;
        RemoveCommand = new AsyncRelayCommand(() => remove(entry));
    }

    public string Scope { get; }
    public string Content { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
}
