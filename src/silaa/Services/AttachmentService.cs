using System.Text;

namespace silaa.Services;

internal static class AttachmentService
{
    private const long MaxFileSize = 100L * 1024 * 1024;
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx", ".pdf", ".html", ".htm", ".md", ".txt", ".csv", ".json"
    };
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".so", ".dylib", ".bin", ".dat", ".zip", ".7z", ".rar",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".mp3", ".wav", ".mp4", ".mov"
    };

    public static string Import(string sourcePath, string workspaceRoot)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("첨부 파일을 찾을 수 없습니다.", source);
        var info = new FileInfo(source);
        if (info.Length > MaxFileSize) throw new InvalidOperationException("첨부 파일은 100MB 이하여야 합니다.");

        var directory = Path.Combine(Path.GetFullPath(workspaceRoot), ".silaa", "attachments");
        Directory.CreateDirectory(directory);
        var safeName = SafeName(info.Name);
        var destination = UniquePath(Path.Combine(directory, safeName));
        var temp = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temp, overwrite: false);
            File.Move(temp, destination);
            return destination;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static string AgentReadInstruction(string path)
    {
        var extension = Path.GetExtension(path);
        if (DocumentExtensions.Contains(extension))
            return "문서/텍스트 파일: 형식에 맞게 read_document 또는 read_file을 사용하세요.";
        if (BinaryExtensions.Contains(extension))
            return "바이너리 파일: read_document/read_file로 읽지 말고 inspect_file_metadata만 사용하세요. 확인되지 않은 내용은 추측하지 마세요.";
        return "형식 미확인: 먼저 inspect_file_metadata를 사용하고, text로 확인된 경우에만 read_file을 사용하세요.";
    }

    public static string UserVisiblePrompt(string prompt)
    {
        const string marker = "\n\n첨부 파일:\n";
        var normalized = prompt.Replace("\r\n", "\n");
        var markerIndex = normalized.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0) return prompt;

        var visible = new StringBuilder(normalized[..markerIndex].Trim());
        var fileNames = normalized[(markerIndex + marker.Length)..]
            .Split('\n')
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => Path.GetFileName(line[2..].Trim()))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (fileNames.Count == 0) return visible.ToString();
        visible.AppendLine().AppendLine().AppendLine("첨부 파일:");
        foreach (var name in fileNames) visible.Append("- ").AppendLine(name);
        return visible.ToString().TrimEnd();
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(name) ? "attachment" : name;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} {index}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
