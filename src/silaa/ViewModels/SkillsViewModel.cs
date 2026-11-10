using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using silaa.Services;

namespace silaa.ViewModels;

public partial class SkillsViewModel : ObservableObject
{
    private readonly LocalStore _store;
    private readonly List<SkillCardViewModel> _installed = [];
    private readonly Func<Task<string?>> _pickSource;
    private readonly Func<string, string, Task<bool>> _confirmInstall;
    private readonly Action<string, string, bool> _notify;
    private readonly SkillInstallerService _installer;

    public SkillsViewModel(
        LocalStore store,
        NavigationViewModel navigation,
        Func<Task<string?>>? pickSource = null,
        Func<string, string, Task<bool>>? confirmInstall = null,
        Action<string, string, bool>? notify = null)
    {
        _store = store;
        Navigation = navigation;
        _pickSource = pickSource ?? (() => Task.FromResult<string?>(null));
        _confirmInstall = confirmInstall ?? ((_, _) => Task.FromResult(false));
        _notify = notify ?? ((_, _, _) => { });
        _installer = new SkillInstallerService(
            store.Settings.WorkspacePath, store.Root, _ => true);
        Refresh();
    }

    public NavigationViewModel Navigation { get; }
    public ObservableCollection<SkillCardViewModel> Skills { get; } = [];
    public IReadOnlyList<string> Scopes { get; } = ["프로젝트", "전체"];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _countLabel = "0개 설치됨";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyTitle = "";
    [ObservableProperty] private string _emptyDescription = "";
    [ObservableProperty] private string _installSource = "";
    [ObservableProperty] private string _installSubpath = "";
    [ObservableProperty] private string _selectedScope = "프로젝트";
    [ObservableProperty] private bool _overwrite;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private string _status = "GitHub 주소를 붙여넣거나 내 컴퓨터의 Skill 폴더를 선택하세요.";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        var selected = await _pickSource();
        if (!string.IsNullOrWhiteSpace(selected)) InstallSource = selected;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task InstallAsync()
    {
        var source = InstallSource.Trim();
        if (source.Length == 0)
        {
            Status = "설치할 GitHub 주소나 폴더를 입력하세요.";
            return;
        }

        var scope = SelectedScope == "전체" ? "global" : "project";
        var destination = scope == "global"
            ? "모든 프로젝트에서 사용할 수 있는 전체 Skill"
            : "현재 작업 폴더에서만 사용하는 프로젝트 Skill";
        var overwriteText = Overwrite
            ? " 같은 이름의 Skill이 있으면 기존 파일을 교체합니다."
            : " 같은 이름의 Skill이 있으면 설치를 중단합니다.";
        if (!await _confirmInstall(
                "Skill 설치",
                $"{destination}로 설치합니다.{overwriteText}\n\n패키지 안의 스크립트는 설치 중 실행하지 않습니다.")) return;

        IsInstalling = true;
        Status = "Skill을 검사하고 안전하게 설치하는 중…";
        try
        {
            var result = await _installer.InstallAsync(
                source, InstallSubpath, scope, Overwrite, CancellationToken.None);
            Status = $"{result.Name} 설치 완료 · {result.FileCount}개 파일 · 다음 대화부터 사용 가능";
            InstallSource = "";
            InstallSubpath = "";
            Overwrite = false;
            Refresh();
            _notify("Skill 설치 완료", $"{result.Name}을(를) 설치했습니다. 새 대화부터 자동으로 사용할 수 있습니다.", false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           UnauthorizedAccessException or DirectoryNotFoundException or
                                           FileNotFoundException or IOException or TimeoutException)
        {
            Status = exception.Message;
            _notify("Skill 설치 실패", exception.Message, true);
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        _installed.Clear();
        var bundled = Path.Combine(AppContext.BaseDirectory, "skills");
        if (!Directory.Exists(bundled)) bundled = Path.Combine(_store.Settings.WorkspacePath, "skills");
        var roots = new[]
        {
            (Path: bundled, Source: "기본 제공"),
            (Path: Path.Combine(_store.Root, "skills"), Source: "전체"),
            (Path: Path.Combine(_store.Settings.WorkspacePath, ".silaa", "skills"), Source: "프로젝트")
        };
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var root in roots.Where(item => Directory.Exists(item.Path)))
        {
            foreach (var directory in Directory.EnumerateDirectories(root.Path).OrderBy(Path.GetFileName))
            {
                if (Path.GetFileName(directory).StartsWith('.')) continue;
                var file = Path.Combine(directory, "SKILL.md");
                if (!File.Exists(file) || !seen.Add(Path.GetFullPath(file))) continue;
                var lines = File.ReadLines(file).Take(40).ToList();
                _installed.Add(new SkillCardViewModel(
                    Metadata(lines, "name") ?? Path.GetFileName(directory),
                    Metadata(lines, "description") ?? "설명 없음",
                    Path.GetFileName(directory),
                    root.Source));
            }
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var matches = _installed.Where(skill => query.Length == 0 ||
            skill.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            skill.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            skill.Folder.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        Skills.Clear();
        foreach (var skill in matches) Skills.Add(skill);
        CountLabel = $"{matches.Count}개 설치됨";
        IsEmpty = matches.Count == 0;
        EmptyTitle = _installed.Count == 0 ? "설치된 Skill이 없습니다" : "검색 결과가 없습니다";
        EmptyDescription = _installed.Count == 0
            ? "위 입력란에 GitHub 주소를 붙여넣거나 Skill 폴더를 선택해 시작하세요."
            : "다른 이름이나 작업 설명으로 다시 검색해 보세요.";
    }

    private static string? Metadata(IEnumerable<string> lines, string key)
    {
        var prefix = key + ":";
        return lines.Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?[prefix.Length..].Trim().Trim('"', '\'');
    }
}

public sealed record SkillCardViewModel(string Name, string Description, string Folder, string Source);
