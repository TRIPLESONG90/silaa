using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using silaa.Core;

namespace silaa.Services;

internal sealed class VaultStore(string root)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("silaa.Vault.v1");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private string FilePath => Path.Combine(root, "vault.json");

    public IReadOnlyList<VaultEntryInfo> List()
    {
        lock (_gate) return Read().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new VaultEntryInfo { Name = item.Name, UpdatedAt = item.UpdatedAt }).ToList();
    }

    public void Save(string name, string value)
    {
        name = NormalizeName(name);
        if (string.IsNullOrEmpty(value) || value.Any(char.IsControl)) throw new ArgumentException("비밀값을 입력하세요.");
        lock (_gate)
        {
            var entries = Read();
            var encrypted = Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(value)));
            var existing = entries.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is null) entries.Add(new SavedEntry { Name = name, Protected = encrypted, UpdatedAt = DateTimeOffset.UtcNow });
            else { existing.Name = name; existing.Protected = encrypted; existing.UpdatedAt = DateTimeOffset.UtcNow; }
            Write(entries);
        }
    }

    public string? Get(string name)
    {
        name = NormalizeName(name);
        lock (_gate)
        {
            var saved = Read().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (saved is null) return null;
            try { return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(saved.Protected))); }
            catch (Exception exception) when (exception is FormatException or VaultCryptographicException) { return null; }
        }
    }

    public void Delete(string name)
    {
        name = NormalizeName(name);
        lock (_gate)
        {
            var entries = Read();
            if (entries.RemoveAll(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0) Write(entries);
        }
    }

    public bool Contains(string name)
    {
        name = NormalizeName(name);
        lock (_gate) return Read().Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private List<SavedEntry> Read()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<SavedEntry>>(File.ReadAllText(FilePath), Json) ?? [] : []; }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { throw new IOException("Vault를 읽지 못했습니다.", exception); }
    }

    private void Write(List<SavedEntry> entries)
    {
        Directory.CreateDirectory(root);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries, Json), new UTF8Encoding(false));
        File.Move(temporary, FilePath, true);
    }

    internal static string NormalizeName(string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 80 || name.Any(char.IsControl)) throw new ArgumentException("Vault 이름은 1~80자로 입력하세요.");
        return name;
    }

    private sealed class SavedEntry
    {
        public string Name { get; set; } = "";
        public string Protected { get; set; } = "";
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class VaultCryptographicException(string message) : Exception(message);

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

    private static byte[] Protect(byte[] data) => Transform(data, true);
    private static byte[] Unprotect(byte[] data) => Transform(data, false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Vault 암호화는 Windows에서만 지원합니다.");
        var input = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inputBlob = new DataBlob { Size = data.Length, Data = input.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropy.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref inputBlob, "silaa Vault", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out var output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new VaultCryptographicException($"Windows DPAPI 오류: {Marshal.GetLastWin32Error()}");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { input.Free(); entropy.Free(); }
    }
}
