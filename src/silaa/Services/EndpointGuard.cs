namespace silaa.Services;

public static class EndpointGuard
{
    public static Uri Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("올바른 HTTP(S) LLM API 기본 주소를 입력하세요.");

        if (uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("/chat/completions를 뺀 API 기본 주소를 입력하세요.");

        return new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + "/" }.Uri;
    }

    public static string ValidateModel(string model)
    {
        model = model.Trim();
        if (model.Length is 0 or > 256 || model.Any(char.IsControl))
            throw new ArgumentException("LLM이 사용할 모델 ID를 입력하세요.");
        return model;
    }

    public static string ValidateWorkspace(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("작업 폴더를 입력하세요.");
        var fullPath = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"작업 폴더가 없습니다: {fullPath}");
        return fullPath;
    }
}
