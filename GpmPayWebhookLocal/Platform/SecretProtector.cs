using System.Security.Cryptography;
using System.Text;
using GpmPayWebhookLocal.Services;

namespace GpmPayWebhookLocal.Platform;

/// <summary>
/// Mã hoá API token khi lưu xuống đĩa.
/// <list type="bullet">
/// <item>Windows: DPAPI (CurrentUser) — tương thích file cấu hình của bản WPF cũ.</item>
/// <item>macOS / Linux: AES-GCM, khoá ngẫu nhiên 256-bit lưu ở &lt;DataDir&gt;/.key với quyền 600 (chỉ user hiện tại đọc được).</item>
/// </list>
/// </summary>
public static class SecretProtector
{
    private const string AesPrefix = "aesgcm:";
    private static readonly string KeyPath = Path.Combine(AppSettings.DataDir, ".key");

    public static string Protect(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
#if WINDOWS
        return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
#else
        var key = LoadOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var cipher = new byte[bytes.Length];
        using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, bytes, cipher, tag);
        return AesPrefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
#endif
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
#if WINDOWS
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
#else
            if (!stored.StartsWith(AesPrefix, StringComparison.Ordinal) || !File.Exists(KeyPath)) return "";
            var blob = Convert.FromBase64String(stored[AesPrefix.Length..]);
            int n = AesGcm.NonceByteSizes.MaxSize, t = AesGcm.TagByteSizes.MaxSize;
            var plain = new byte[blob.Length - n - t];
            using (var aes = new AesGcm(File.ReadAllBytes(KeyPath), t))
                aes.Decrypt(blob.AsSpan(0, n), blob.AsSpan(n + t), blob.AsSpan(n, t), plain);
            return Encoding.UTF8.GetString(plain);
#endif
        }
        catch { return ""; }
    }

#if !WINDOWS
    private static byte[] LoadOrCreateKey()
    {
        if (File.Exists(KeyPath))
        {
            var existing = File.ReadAllBytes(KeyPath);
            if (existing.Length == 32) return existing;
        }
        Directory.CreateDirectory(AppSettings.DataDir);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(KeyPath, key);
        FileSecurity.RestrictToOwner(KeyPath);
        return key;
    }
#endif
}

public static class FileSecurity
{
    /// <summary>macOS / Linux: chmod 600. Windows: thư mục %LocalAppData% vốn đã chỉ user hiện tại truy cập được.</summary>
    public static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }
}
