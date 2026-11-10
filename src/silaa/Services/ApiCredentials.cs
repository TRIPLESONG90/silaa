using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace silaa.Services;

internal sealed class ApiCredentials(string root)
{
    private sealed class CredentialFile
    {
        public int Version { get; set; } = 2;
        public List<SavedKey> Keys { get; set; } = [];
    }

    private sealed class SavedKey
    {
        public string Id { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string Protected { get; set; } = "";
    }

    private sealed record LegacySavedKey(string Endpoint, string Protected);

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("silaa.NativeHarness.v1");
    private static readonly byte[] LegacyEntropy = Convert.FromBase64String("c2lsYWEuT3BlbkNvZGVIYXJuZXNzLnYx");
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private readonly object _gate = new();
    private string FilePath => Path.Combine(root, "llm-credential.json");

    public bool HasKey(string connectionId, string endpoint) =>
        !string.IsNullOrEmpty(Get(connectionId, endpoint));

    public string? Get(string connectionId, string endpoint)
    {
        lock (_gate)
        {
            var normalizedId = NormalizeId(connectionId);
            var normalizedEndpoint = NormalizeEndpoint(endpoint);
            var file = Read();
            var key = file.Keys.FirstOrDefault(item => item.Id == normalizedId)
                      ?? file.Keys.FirstOrDefault(item => item.Id.Length == 0 && item.Endpoint == normalizedEndpoint);
            if (key is null) return null;
            try
            {
                var encrypted = Convert.FromBase64String(key.Protected);
                try { return Encoding.UTF8.GetString(Unprotect(encrypted, Entropy)); }
                catch (CredentialCryptographicException)
                {
                    return Encoding.UTF8.GetString(Unprotect(encrypted, LegacyEntropy));
                }
            }
            catch (Exception exception) when (exception is FormatException or CredentialCryptographicException or ArgumentException)
            {
                return null;
            }
        }
    }

    public void Save(string connectionId, string endpoint, string value)
    {
        var normalizedId = NormalizeId(connectionId);
        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        value = value.Trim();
        if (value.Length == 0 || value.Any(char.IsControl))
            throw new ArgumentException("올바른 API 키를 입력하세요.");

        lock (_gate)
        {
            Directory.CreateDirectory(root);
            try
            {
                var file = Read();
                file.Keys.RemoveAll(item => item.Id == normalizedId);
                file.Keys.Add(new SavedKey
                {
                    Id = normalizedId,
                    Endpoint = normalizedEndpoint,
                    Protected = Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(value), Entropy))
                });
                Write(file);
            }
            catch (CredentialCryptographicException exception)
            {
                throw new IOException("API 키를 Windows 사용자 자격 증명으로 암호화하지 못했습니다.", exception);
            }
        }
    }

    public void Delete(string connectionId, string endpoint)
    {
        var normalizedId = NormalizeId(connectionId);
        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        lock (_gate)
        {
            var file = Read();
            file.Keys.RemoveAll(item => item.Id == normalizedId ||
                                        item.Id.Length == 0 && item.Endpoint == normalizedEndpoint);
            Write(file);
        }
    }

    public void MigrateLegacy(string connectionId, string endpoint)
    {
        var normalizedId = NormalizeId(connectionId);
        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        lock (_gate)
        {
            var file = Read();
            if (file.Keys.Any(item => item.Id == normalizedId)) return;
            var legacy = file.Keys.FirstOrDefault(item => item.Id.Length == 0 && item.Endpoint == normalizedEndpoint);
            if (legacy is null) return;
            legacy.Id = normalizedId;
            Write(file);
        }
    }

    private CredentialFile Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return new CredentialFile();
            var content = File.ReadAllText(FilePath);
            using var document = JsonDocument.Parse(content);
            var isCollection = document.RootElement.ValueKind == JsonValueKind.Object &&
                               document.RootElement.EnumerateObject().Any(property =>
                                   property.Name.Equals("keys", StringComparison.OrdinalIgnoreCase));
            if (isCollection)
            {
                var file = JsonSerializer.Deserialize<CredentialFile>(content, Json) ?? new CredentialFile();
                file.Keys ??= [];
                return file;
            }

            var legacy = JsonSerializer.Deserialize<LegacySavedKey>(content, Json);
            return legacy is null
                ? new CredentialFile()
                : new CredentialFile
                {
                    Keys =
                    [
                        new SavedKey
                        {
                            Endpoint = NormalizeEndpoint(legacy.Endpoint),
                            Protected = legacy.Protected
                        }
                    ]
                };
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new CredentialFile();
        }
    }

    private void Write(CredentialFile file)
    {
        if (file.Keys.Count == 0)
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            return;
        }
        AtomicWrite(FilePath, JsonSerializer.Serialize(file, Json));
    }

    private static string NormalizeId(string connectionId)
    {
        var normalized = connectionId.Trim();
        if (normalized.Length is 0 or > 128 || normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            throw new ArgumentException("올바르지 않은 LLM 연결 ID입니다.");
        return normalized;
    }

    private static string NormalizeEndpoint(string endpoint) => EndpointGuard.Parse(endpoint).AbsoluteUri;

    private static void AtomicWrite(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private sealed class CredentialCryptographicException(string message) : Exception(message);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static byte[] Protect(byte[] data, byte[] entropy) => Transform(data, entropy, true);
    private static byte[] Unprotect(byte[] data, byte[] entropy) => Transform(data, entropy, false);

    private static byte[] Transform(byte[] data, byte[] entropyBytes, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("API 키 암호화는 Windows에서만 지원합니다.");
        var input = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropy = GCHandle.Alloc(entropyBytes, GCHandleType.Pinned);
        try
        {
            var inputBlob = new DataBlob { Size = data.Length, Data = input.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Size = entropyBytes.Length, Data = entropy.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref inputBlob, "silaa LLM API key", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out var output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CredentialCryptographicException($"Windows DPAPI 오류: {Marshal.GetLastWin32Error()}");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            input.Free();
            entropy.Free();
        }
    }
}
