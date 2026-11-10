using System.Text.Json;
using silaa.Core;

namespace silaa.Services;

public sealed class DocumentTemplateStore
{
    private static readonly IReadOnlyDictionary<string, string> BuiltInDefaults =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["docx"] = "doc-clean",
            ["pptx"] = "silaa-mono",
            ["html"] = "html-report"
        };

    private static readonly IReadOnlyList<DocumentDesignTemplate> BuiltIns =
    [
        new("doc-clean", "Clean Report", "읽기 편한 업무 보고서와 기획 문서", "FFFFFF", "171717", "383838", "171717", "777777", "Malgun Gothic", "balanced", true, "docx"),
        new("doc-editorial", "Editorial Docs", "큰 제목과 여백을 살린 편집형 문서", "F6F1E8", "1B1B1B", "47413A", "B73A2A", "837B72", "Georgia", "editorial", true, "docx"),
        new("silaa-mono", "Silaa Mono", "엄중한 흑백 대비와 넓은 여백", "0B0B0B", "FFFFFF", "D1D1D1", "FFFFFF", "858585", "Malgun Gothic", "spacious", true, "pptx"),
        new("executive-navy", "Executive Navy", "신뢰감 있는 네이비 프레젠테이션", "F7F8FA", "14213D", "334155", "2457C5", "64748B", "Malgun Gothic", "balanced", true, "pptx"),
        new("html-report", "Web Report", "인쇄와 공유에 적합한 반응형 HTML 문서", "F7F7F5", "151515", "3F3F3F", "111111", "777777", "Arial", "balanced", true, "html"),
        new("html-dark", "Dark Canvas", "짙은 배경과 선명한 포인트의 웹 문서", "101112", "FFFFFF", "D1D5DB", "8BB4FF", "7B8491", "Arial", "grid", true, "html")
    ];

    private readonly string _path;
    private readonly string _legacyPath;
    private readonly object _gate = new();

    public DocumentTemplateStore(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        var root = Path.GetFullPath(dataRoot);
        _path = Path.Combine(root, "document-designs.json");
        _legacyPath = Path.Combine(root, "presentation-designs.json");
    }

    public IReadOnlyList<DocumentDesignTemplate> All()
    {
        lock (_gate)
        {
            var state = Load();
            return BuiltIns.Concat(state.CustomTemplates)
                .GroupBy(template => template.Id, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToList();
        }
    }

    public string DefaultId(string format)
    {
        format = NormalizeFormat(format);
        lock (_gate)
        {
            var state = Load();
            return state.DefaultTemplateIds.TryGetValue(format, out var id) &&
                   AllFor(state, format).Any(template => template.Id == id)
                ? id
                : BuiltInDefaults[format];
        }
    }

    public DocumentDesignTemplate Resolve(string format, string? id)
    {
        format = NormalizeFormat(format);
        lock (_gate)
        {
            var state = Load();
            var requested = string.IsNullOrWhiteSpace(id) || id == "default" ? DefaultId(format) : id;
            return AllFor(state, format).LastOrDefault(template => template.Id.Equals(requested, StringComparison.Ordinal))
                   ?? BuiltIns.First(template => template.Id == BuiltInDefaults[format]);
        }
    }

    public static DocumentDesignTemplate BuiltInOrDefault(string format, string? id)
    {
        format = NormalizeFormat(format);
        return BuiltIns.LastOrDefault(template => template.Format == format && template.Id == id)
               ?? BuiltIns.First(template => template.Id == BuiltInDefaults[format]);
    }

    public void SetDefault(string format, string id)
    {
        format = NormalizeFormat(format);
        lock (_gate)
        {
            var state = Load();
            if (!AllFor(state, format).Any(template => template.Id == id))
                throw new ArgumentException("해당 형식에 존재하지 않는 문서 템플릿입니다.", nameof(id));
            state.DefaultTemplateIds[format] = id;
            Save(state);
        }
    }

    public DocumentDesignTemplate Add(DocumentDesignTemplate template)
    {
        var format = NormalizeFormat(template.Format);
        lock (_gate)
        {
            var state = Load();
            var id = UniqueId(template.Id, AllIds(state));
            var saved = template with { Id = id, Format = format, IsBuiltIn = false };
            state.CustomTemplates.Add(saved);
            state.DefaultTemplateIds[format] = id;
            Save(state);
            return saved;
        }
    }

    public static string NormalizeFormat(string format) => format.Trim().TrimStart('.').ToLowerInvariant() switch
    {
        "doc" or "docs" or "word" or "docx" => "docx",
        "ppt" or "powerpoint" or "pptx" => "pptx",
        "htm" or "html" => "html",
        _ => throw new ArgumentException("지원하는 템플릿 형식은 DOCS, PPT, HTML입니다.", nameof(format))
    };

    private TemplateState Load()
    {
        var source = File.Exists(_path) ? _path : File.Exists(_legacyPath) ? _legacyPath : null;
        if (source is null) return Normalize(new TemplateState());
        try
        {
            return Normalize(JsonSerializer.Deserialize<TemplateState>(File.ReadAllText(source)) ?? new TemplateState());
        }
        catch (JsonException)
        {
            return Normalize(new TemplateState());
        }
    }

    private static TemplateState Normalize(TemplateState state)
    {
        foreach (var (format, id) in BuiltInDefaults)
            state.DefaultTemplateIds.TryAdd(format, id);
        if (!string.IsNullOrWhiteSpace(state.DefaultTemplateId))
            state.DefaultTemplateIds["pptx"] = state.DefaultTemplateId;
        state.CustomTemplates = state.CustomTemplates
            .Select(template => template with { Format = NormalizeLegacyFormat(template.Format) })
            .ToList();
        return state;
    }

    private void Save(TemplateState state)
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, true);
    }

    private static IEnumerable<DocumentDesignTemplate> AllFor(TemplateState state, string format) =>
        BuiltIns.Concat(state.CustomTemplates).Where(template => template.Format == format);

    private static IEnumerable<string> AllIds(TemplateState state) =>
        BuiltIns.Select(template => template.Id).Concat(state.CustomTemplates.Select(template => template.Id));

    private static string NormalizeLegacyFormat(string? format) => string.IsNullOrWhiteSpace(format) ? "pptx" : NormalizeFormat(format);

    private static string UniqueId(string candidate, IEnumerable<string> existing)
    {
        var baseId = new string(candidate.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray()).Trim('-');
        if (baseId.Length == 0) baseId = "custom-design";
        var used = existing.ToHashSet(StringComparer.Ordinal);
        var id = baseId;
        for (var suffix = 2; used.Contains(id); suffix++) id = $"{baseId}-{suffix}";
        return id;
    }

    private sealed class TemplateState
    {
        public Dictionary<string, string> DefaultTemplateIds { get; set; } = new(StringComparer.Ordinal);
        public string DefaultTemplateId { get; set; } = "";
        public List<DocumentDesignTemplate> CustomTemplates { get; set; } = [];
    }
}
