using System.Text;
using System.Text.Json;
using silaa.Core;

namespace silaa.Services;

internal sealed class ConnectedAppStore(string root, VaultStore vault)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private string FilePath => Path.Combine(root, "connected-apps.json");

    public IReadOnlyList<ConnectedAppInfo> List()
    {
        lock (_gate) return Read().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public ConnectedAppInfo? Get(string id)
    {
        lock (_gate) return Read().FirstOrDefault(item => item.Id == id);
    }

    public ConnectedAppInfo SaveGitHub(string name, string account, string token, bool agentEnabled = true)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("GitHub 토큰을 입력하세요.");
        var app = Save(new ConnectedAppInfo
        {
            Id = $"app_{Guid.NewGuid():N}", Provider = "github", Name = NormalizeName(name, "GitHub"),
            Account = account.Trim(), AgentEnabled = agentEnabled, ReadOnly = true, UpdatedAt = DateTimeOffset.UtcNow
        });
        try { vault.Save(SecretName(app.Id), token.Trim()); }
        catch { Remove(app.Id); throw; }
        return app;
    }

    public ConnectedAppInfo SaveOneDrive(string name, string path, bool agentEnabled = true)
    {
        var full = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("OneDrive 동기화 폴더를 찾지 못했습니다.");
        return Save(new ConnectedAppInfo
        {
            Id = $"app_{Guid.NewGuid():N}", Provider = "onedrive", Name = NormalizeName(name, "OneDrive"),
            RootPath = full, AgentEnabled = agentEnabled, ReadOnly = true, UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var items = Read();
            if (items.RemoveAll(item => item.Id == id) > 0) Write(items);
        }
        vault.Delete(SecretName(id));
    }

    public string? GetToken(string id) => vault.Get(SecretName(id));

    private ConnectedAppInfo Save(ConnectedAppInfo app)
    {
        lock (_gate)
        {
            var items = Read();
            items.Add(app);
            Write(items);
        }
        return app;
    }

    private List<ConnectedAppInfo> Read()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<ConnectedAppInfo>>(File.ReadAllText(FilePath), Json) ?? [] : []; }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        { throw new IOException("앱 연결 목록을 읽지 못했습니다.", exception); }
    }

    private void Write(List<ConnectedAppInfo> items)
    {
        Directory.CreateDirectory(root);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(items, Json), new UTF8Encoding(false));
        File.Move(temporary, FilePath, true);
    }

    private static string NormalizeName(string value, string fallback)
    {
        value = value.Trim();
        if (value.Length == 0) value = fallback;
        if (value.Length > 80 || value.Any(char.IsControl)) throw new ArgumentException("연결 이름은 80자 이하여야 합니다.");
        return value;
    }

    private static string SecretName(string id) => $"connected-app-{id}-token";
}
