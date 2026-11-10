using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using silaa.Core;
using silaa.Services;

namespace silaa.ViewModels;

public partial class VaultViewModel : ObservableObject
{
    private readonly LocalStore _store;
    private readonly Func<string, string, Task<bool>> _confirmDelete;
    private readonly Action<string, string, bool> _notify;

    public VaultViewModel(LocalStore store, NavigationViewModel navigation,
        Func<string, string, Task<bool>> confirmDelete, Action<string, string, bool> notify)
    {
        _store = store;
        Navigation = navigation;
        _confirmDelete = confirmDelete;
        _notify = notify;
        Refresh();
    }

    public NavigationViewModel Navigation { get; }
    public ObservableCollection<VaultEntryViewModel> Entries { get; } = [];

    [ObservableProperty] private bool _isDialogOpen;
    [ObservableProperty] private string _draftName = "";
    [ObservableProperty] private string _draftSecret = "";
    [ObservableProperty] private bool _revealSecret;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = "";
    [ObservableProperty] private bool _isEmpty;

    public char SecretPasswordChar => RevealSecret ? '\0' : '●';
    public bool HasStatus => Status.Length > 0;

    partial void OnRevealSecretChanged(bool value) => OnPropertyChanged(nameof(SecretPasswordChar));

    [RelayCommand]
    private void OpenAdd()
    {
        ClearDraft();
        Status = "";
        IsDialogOpen = true;
    }

    [RelayCommand]
    private void Cancel()
    {
        ClearDraft();
        IsDialogOpen = false;
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            var name = DraftName.Trim();
            _store.Vault.Save(name, DraftSecret);
            ClearDraft();
            IsDialogOpen = false;
            Status = "Windows DPAPI(CurrentUser)로 암호화해 저장했습니다.";
            _notify("Vault 저장", $"{name} 키를 암호화해 저장했습니다.", false);
            Refresh();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or PlatformNotSupportedException)
        {
            Status = exception.Message;
            _notify("Vault 저장 실패", exception.Message, true);
        }
    }

    private void ClearDraft()
    {
        DraftName = "";
        DraftSecret = "";
        RevealSecret = false;
    }

    private void Refresh()
    {
        Entries.Clear();
        foreach (var entry in _store.Vault.List())
            Entries.Add(new VaultEntryViewModel(entry, RemoveAsync));
        IsEmpty = Entries.Count == 0;
    }

    private async Task RemoveAsync(VaultEntryInfo entry)
    {
        if (!await _confirmDelete("Vault 항목 삭제",
                $"‘{entry.Name}’ 비밀값을 이 컴퓨터에서 삭제합니다. 이 작업은 되돌릴 수 없습니다.")) return;
        _store.Vault.Delete(entry.Name);
        Status = $"{entry.Name} 항목을 삭제했습니다.";
        _notify("Vault 삭제", Status, false);
        Refresh();
    }
}

public sealed class VaultEntryViewModel
{
    public VaultEntryViewModel(VaultEntryInfo entry, Func<VaultEntryInfo, Task> remove)
    {
        Name = entry.Name;
        UpdatedAt = $"저장됨 · {entry.UpdatedAt.LocalDateTime:MM-dd HH:mm}";
        RemoveCommand = new AsyncRelayCommand(() => remove(entry));
    }

    public string Name { get; }
    public string UpdatedAt { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
}
