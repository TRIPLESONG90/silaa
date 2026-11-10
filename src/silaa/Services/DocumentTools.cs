using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace silaa.Services;

internal sealed class DocumentTools(
    string? projectId,
    ProjectResourceStore resources,
    WorkspaceTools workspace,
    DocumentTemplateStore documentTemplates)
{
    public IList<AITool> CreateFunctions()
    {
        var tools = CreateReadOnlyFunctions().ToList();
        tools.Add(AIFunctionFactory.Create(
            (Func<string, string, string, string, string, string>)CreateDocument,
            "create_document",
            "Create a DOCX, XLSX, PPTX, HTML, PDF, Markdown, text, CSV, or JSON document in an allowed workspace path. DOCX, PPTX, and HTML use the selected visual template. PPTX slides are separated by a line containing only ---."));
        return tools;
    }

    public IList<AITool> CreateReadOnlyFunctions() =>
    [
        AIFunctionFactory.Create((Func<string, string>)ReadDocument, "read_document",
            "Extract readable text from DOCX, XLSX, PPTX, HTML, PDF, Markdown, text, CSV, or JSON in an allowed workspace path.")
    ];

    private string ReadDocument(
        [Description("Workspace-relative path, or an absolute path inside an explicitly allowed folder.")] string path)
    {
        var target = workspace.ResolveAccessiblePath(path, true);
        var text = DocumentArtifactService.ReadText(target);
        return text.Length > 150_000 ? text[..150_000] + "\n[truncated]" : text;
    }

    private string CreateDocument(
        [Description("Destination path inside the workspace or an allowed folder. The requested extension is added when omitted.")] string path,
        [Description("Document format: docx, xlsx, pptx, html, pdf, md, txt, csv, or json.")] string format,
        [Description("Document title.")] string title,
        [Description("Document content. For XLSX use CSV. For PPTX separate slides with --- and put each slide title first.")] string content,
        [Description("DOCX, PPTX, or HTML design template ID. Use 'default' to apply the user's selected template for that format.")] string template = "default")
    {
        var candidate = Path.HasExtension(path) ? path : path + "." + format.Trim().TrimStart('.');
        var target = workspace.ResolveAccessiblePath(candidate, false, write: true);
        var normalizedFormat = format.Trim().TrimStart('.').ToLowerInvariant();
        var design = normalizedFormat is "doc" or "docs" or "word" or "docx" or "ppt" or "pptx" or "powerpoint" or "htm" or "html"
            ? documentTemplates.Resolve(normalizedFormat, template)
            : null;
        var created = DocumentArtifactService.Create(target, format, title, content, template, design);
        if (projectId is not null)
            resources.Add(projectId, "file", Path.GetFileName(created), created);
        return $"Created: {created}\nVerified text preview:\n{Preview(DocumentArtifactService.ReadText(created))}";
    }

    private static string Preview(string value) => value.Length > 1500 ? value[..1500] + "\n[truncated]" : value;
}
