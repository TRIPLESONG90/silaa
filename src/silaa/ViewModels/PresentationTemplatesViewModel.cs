using System.Collections.ObjectModel;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using silaa.Core;
using silaa.Services;

namespace silaa.ViewModels;

public partial class PresentationTemplatesViewModel : ObservableObject
{
    private readonly DocumentTemplateStore _store;
    private readonly Func<string, Task<DocumentDesignTemplate>> _generateTemplate;
    private readonly Func<Task<string?>>? _pickTemplateFile;
    private readonly Func<string, string, Task<bool>> _confirm;
    private DocumentDesignTemplate? _editingTemplate;

    public PresentationTemplatesViewModel(
        NavigationViewModel navigation,
        DocumentTemplateStore store,
        Func<string, Task<DocumentDesignTemplate>> generateTemplate,
        Func<Task<string?>>? pickTemplateFile = null,
        Func<string, string, Task<bool>>? confirm = null)
    {
        Navigation = navigation;
        _store = store;
        _generateTemplate = generateTemplate;
        _pickTemplateFile = pickTemplateFile;
        _confirm = confirm ?? ((_, _) => Task.FromResult(true));
        ShowCatalog();
    }

    public NavigationViewModel Navigation { get; }
    public ObservableCollection<PresentationDesignCard> Templates { get; } = [];
    public ObservableCollection<PresentationConversationMessage> Messages { get; } = [];
    public ObservableCollection<PresentationConversationMessage> EditorMessages { get; } = [];
    public ObservableCollection<PresentationChoice> Choices { get; } = [];

    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private string _inputPlaceholder = "예: 임원 보고용 흑백 PPT, 큰 제목과 넓은 여백";
    [ObservableProperty] private string _progressText = "문서 템플릿";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isCatalog;
    [ObservableProperty] private bool _isDragOver;
    [ObservableProperty] private bool _isEditor;
    [ObservableProperty] private bool _isEditorBusy;
    [ObservableProperty] private string _editorInputText = "";
    [ObservableProperty] private string _editorStatus = "수정할 내용을 자연스럽게 입력하세요.";

    public bool IsConversation => !IsCatalog && !IsEditor;
    public bool CanReturnToCatalog => !IsCatalog;
    public bool CanCompose => IsConversation && !IsBusy;
    public bool HasChoices => Choices.Count > 0 && CanCompose;
    public bool CanEdit => IsEditor && !IsEditorBusy;
    public bool CanDeleteEditingTemplate => CanEdit && _editingTemplate is { IsBuiltIn: false };
    public string EditingTitle => _editingTemplate?.Name ?? "";
    public string EditingDescription => _editingTemplate?.Description ?? "";
    public string EditingFormat => _editingTemplate is null ? "" : FormatLabel(_editingTemplate.Format).ToUpperInvariant();
    public string EditingFont => _editingTemplate?.FontFamily ?? "";
    public string EditingLayout => _editingTemplate?.Layout.ToUpperInvariant() ?? "";
    public string EditingBackground => "#" + (_editingTemplate?.Background ?? "FFFFFF");
    public string EditingTitleColor => "#" + (_editingTemplate?.TitleColor ?? "111111");
    public string EditingBodyColor => "#" + (_editingTemplate?.BodyColor ?? "333333");
    public string EditingAccentColor => "#" + (_editingTemplate?.AccentColor ?? "111111");
    public string EditingMutedColor => "#" + (_editingTemplate?.MutedColor ?? "777777");

    partial void OnIsCatalogChanged(bool value)
    {
        OnPropertyChanged(nameof(IsConversation));
        OnPropertyChanged(nameof(CanReturnToCatalog));
        OnPropertyChanged(nameof(CanCompose));
        OnPropertyChanged(nameof(HasChoices));
    }

    partial void OnIsEditorChanged(bool value)
    {
        OnPropertyChanged(nameof(IsConversation));
        OnPropertyChanged(nameof(CanReturnToCatalog));
        OnPropertyChanged(nameof(CanCompose));
        OnPropertyChanged(nameof(CanEdit));
    }

    partial void OnIsEditorBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanDeleteEditingTemplate));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCompose));
        OnPropertyChanged(nameof(HasChoices));
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var brief = InputText.Trim();
        if (brief.Length == 0 || !CanCompose) return;
        InputText = "";
        await GenerateAndSaveAsync(brief);
    }

    [RelayCommand]
    private void ShowCatalog() =>
        ShowCatalog("카드를 선택하면 해당 형식에서 Agent의 기본 문서 디자인으로 사용합니다.");

    [RelayCommand]
    private void AddTemplate()
    {
        IsEditor = false;
        IsCatalog = false;
        Messages.Clear();
        Choices.Clear();
        InputText = "";
        IsBusy = false;
        ProgressText = "AI 디자인 생성";
        Progress = 0.18;
        Status = "연결된 LLM이 색상·타이포·레이아웃을 직접 설계합니다.";
        Messages.Add(PresentationConversationMessage.Assistant(
            "어떤 문서 템플릿을 만들까요? Docs, PPT, HTML 중 형식과 용도, 원하는 분위기를 한 번에 편하게 적어주세요. 제가 디자인 명세를 직접 만들겠습니다."));
    }

    [RelayCommand]
    private async Task ImportTemplateAsync()
    {
        if (_pickTemplateFile is null) return;
        var path = await _pickTemplateFile();
        if (!string.IsNullOrWhiteSpace(path)) await ImportFileAsync(path);
    }

    public async Task ImportFileAsync(string path)
    {
        try
        {
            IsBusy = true;
            Status = "문서 디자인 특징을 추출하고 AI가 재사용 가능한 템플릿으로 설계하는 중…";
            var extracted = DocumentTemplateImporter.Import(path);
            var brief = $"""
                기존 {FormatLabel(extracted.Format)} 문서 파일의 디자인을 재사용 가능한 템플릿으로 재설계하세요.
                반드시 format은 {extracted.Format}으로 유지하고, 원본 파일 이름을 알아볼 수 있는 이름을 만드세요.
                원본: {Path.GetFileName(path)}
                로컬 분석 결과: 배경 #{extracted.Background}, 제목 #{extracted.TitleColor}, 본문 #{extracted.BodyColor},
                강조 #{extracted.AccentColor}, 보조 #{extracted.MutedColor}, 글꼴 {extracted.FontFamily}, 레이아웃 {extracted.Layout}.
                이 특징을 존중하되 대비와 일관성을 전문적으로 다듬으세요.
                """;
            var generated = await _generateTemplate(brief);
            var saved = _store.Add(generated with { Format = extracted.Format });
            ShowCatalog($"‘{saved.Name}’을 AI로 템플릿화하고 {FormatLabel(saved.Format)} 기본 디자인으로 설정했습니다.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"템플릿 가져오기 실패 · {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task GenerateAndSaveAsync(string brief)
    {
        brief = brief.Trim();
        if (brief.Length == 0 || IsBusy) return;
        Messages.Add(PresentationConversationMessage.User(brief));
        IsBusy = true;
        Progress = 0.64;
        Status = "AI가 문서 형식, 팔레트, 타이포그래피와 레이아웃을 설계하는 중…";
        Messages.Add(PresentationConversationMessage.Assistant(
            "요구사항을 바탕으로 미리 정해진 색상표가 아닌 새로운 디자인 명세를 만들고 있어요."));
        try
        {
            var generated = await _generateTemplate(brief);
            var saved = _store.Add(generated);
            Progress = 1;
            ShowCatalog($"AI가 만든 ‘{saved.Name}’을 추가하고 {FormatLabel(saved.Format)} 기본 템플릿으로 설정했습니다.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException)
        {
            Progress = 0.18;
            Status = $"AI 디자인 생성 실패 · {exception.Message}";
            Messages.Add(PresentationConversationMessage.Assistant(
                $"디자인을 만들지 못했어요. LLM 연결이나 요청 내용을 확인한 뒤 다시 보내주세요.\n\n{exception.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SendEditorAsync()
    {
        var request = EditorInputText.Trim();
        if (request.Length == 0 || !CanEdit || _editingTemplate is null) return;
        EditorInputText = "";
        EditorMessages.Add(PresentationConversationMessage.User(request));
        IsEditorBusy = true;
        EditorStatus = "AI가 현재 디자인을 유지하면서 수정안을 만드는 중…";
        var current = _editingTemplate;
        var brief = $$"""
            기존 {{FormatLabel(current.Format)}} 문서 디자인을 사용자의 요청에 맞게 수정하세요.
            format은 반드시 {{current.Format}}으로 유지하세요. 기존 특징을 존중하되 요청에 필요한 값은 과감히 바꾸세요.
            현재 디자인: 이름 {{current.Name}}, 설명 {{current.Description}}, 배경 #{{current.Background}},
            제목 #{{current.TitleColor}}, 본문 #{{current.BodyColor}}, 강조 #{{current.AccentColor}},
            보조 #{{current.MutedColor}}, 글꼴 {{current.FontFamily}}, 레이아웃 {{current.Layout}}.
            사용자 수정 요청: {{request}}
            """;
        try
        {
            var revised = await _generateTemplate(brief);
            _editingTemplate = revised with
            {
                Id = current.Id,
                Format = current.Format,
                IsBuiltIn = false
            };
            NotifyEditingPreview();
            EditorMessages.Add(PresentationConversationMessage.Assistant(
                $"반영했습니다. ‘{_editingTemplate.Name}’ 디자인으로 미리보기를 업데이트했어요. 더 바꾸거나 적용할 수 있습니다."));
            EditorStatus = "미리보기에 수정안이 반영됐습니다.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException)
        {
            EditorMessages.Add(PresentationConversationMessage.Assistant($"수정하지 못했어요.\n\n{exception.Message}"));
            EditorStatus = "수정 실패 · LLM 연결과 요청을 확인하세요.";
        }
        finally
        {
            IsEditorBusy = false;
        }
    }

    [RelayCommand]
    private void ApplyEditor()
    {
        if (_editingTemplate is null || IsEditorBusy) return;
        var saved = _store.Add(_editingTemplate with
        {
            Id = _editingTemplate.Id + "-edited",
            IsBuiltIn = false
        });
        ShowCatalog($"‘{saved.Name}’ 수정본을 저장하고 {FormatLabel(saved.Format)} 기본 템플릿으로 적용했습니다.");
    }

    [RelayCommand]
    private void DuplicateEditor()
    {
        if (_editingTemplate is null || IsEditorBusy) return;
        var saved = _store.Duplicate(_editingTemplate.Format, _editingTemplate.Id);
        ShowCatalog($"‘{saved.Name}’을 복제하고 기본 템플릿으로 설정했습니다.");
    }

    [RelayCommand]
    private async Task DeleteEditorAsync()
    {
        if (_editingTemplate is not { IsBuiltIn: false } template || IsEditorBusy) return;
        if (!await _confirm("템플릿 삭제", $"‘{template.Name}’ 템플릿을 삭제할까요?")) return;
        if (_store.Remove(template.Format, template.Id)) ShowCatalog($"‘{template.Name}’ 템플릿을 삭제했습니다.");
    }

    private void ShowCatalog(string message)
    {
        IsEditor = false;
        IsCatalog = true;
        Messages.Clear();
        Choices.Clear();
        Templates.Clear();
        foreach (var design in _store.All())
            Templates.Add(new PresentationDesignCard(
                design, design.Id == _store.DefaultId(design.Format), SelectTemplate));
        Status = message;
        ProgressText = "문서 템플릿";
        Progress = 0;
    }

    private void SelectTemplate(string format, string id)
    {
        _editingTemplate = _store.Resolve(format, id);
        IsCatalog = false;
        IsEditor = true;
        EditorInputText = "";
        EditorMessages.Clear();
        EditorMessages.Add(PresentationConversationMessage.Assistant(
            $"‘{_editingTemplate.Name}’을 열었어요. 왼쪽에서 원하는 변경을 말하면 오른쪽 미리보기를 업데이트하겠습니다."));
        EditorStatus = "수정 요청을 입력하거나 현재 디자인을 바로 적용하세요.";
        NotifyEditingPreview();
    }

    private void NotifyEditingPreview()
    {
        OnPropertyChanged(nameof(EditingTitle));
        OnPropertyChanged(nameof(EditingDescription));
        OnPropertyChanged(nameof(EditingFormat));
        OnPropertyChanged(nameof(EditingFont));
        OnPropertyChanged(nameof(EditingLayout));
        OnPropertyChanged(nameof(EditingBackground));
        OnPropertyChanged(nameof(EditingTitleColor));
        OnPropertyChanged(nameof(EditingBodyColor));
        OnPropertyChanged(nameof(EditingAccentColor));
        OnPropertyChanged(nameof(EditingMutedColor));
        OnPropertyChanged(nameof(CanDeleteEditingTemplate));
    }

    private static string FormatLabel(string format) => format switch
    {
        "docx" => "Docs", "pptx" => "PPT", _ => "HTML"
    };
}

public sealed class PresentationDesignCard
{
    public PresentationDesignCard(DocumentDesignTemplate design, bool isDefault, Action<string, string> select)
    {
        Id = design.Id;
        Title = design.Name;
        Description = design.Description;
        FormatLabel = design.Format switch { "docx" => "DOCS", "pptx" => "PPT", _ => "HTML" };
        Eyebrow = design.IsBuiltIn ? $"{FormatLabel} DESIGN" : $"AI {FormatLabel} DESIGN";
        PreviewTitle = design.Name;
        PreviewBody = design.Layout switch
        {
            "spacious" => "SPACE  /  TYPE  /  CONTRAST",
            "editorial" => "STORY  ·  RHYTHM  ·  DETAIL",
            "grid" => "GRID  01  —  SIGNAL",
            _ => "CLARITY  ·  STRUCTURE  ·  TRUST"
        };
        PreviewBackground = "#" + design.Background;
        PreviewForeground = "#" + design.TitleColor;
        PreviewAccent = "#" + design.AccentColor;
        PreviewMuted = "#" + design.MutedColor;
        IsDefault = isDefault;
        SelectCommand = new RelayCommand(() => select(design.Format, design.Id));
    }

    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public string Eyebrow { get; }
    public string PreviewTitle { get; }
    public string PreviewBody { get; }
    public string PreviewBackground { get; }
    public string PreviewForeground { get; }
    public string PreviewAccent { get; }
    public string PreviewMuted { get; }
    public string FormatLabel { get; }
    public bool IsDefault { get; }
    public IRelayCommand SelectCommand { get; }
}

public sealed class PresentationChoice
{
    public PresentationChoice(string label, string value, Func<string, Task> select)
    {
        Label = label;
        Value = value;
        SelectCommand = new AsyncRelayCommand(() => select(value));
    }
    public string Label { get; }
    public string Value { get; }
    public IAsyncRelayCommand SelectCommand { get; }
}

public sealed class PresentationConversationMessage
{
    private PresentationConversationMessage(string role, string text) { Role = role; Text = text; }
    public string Role { get; }
    public string Text { get; }
    public bool IsAssistant => Role == "assistant";
    public bool IsUser => Role == "user";
    public HorizontalAlignment Alignment => IsUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public static PresentationConversationMessage Assistant(string text) => new("assistant", text);
    public static PresentationConversationMessage User(string text) => new("user", text);
}

public sealed record PresentationSurvey(
    string Template, string Purpose, string Audience, string SlideCount,
    string Tone, string RequiredSections, string FileName);
