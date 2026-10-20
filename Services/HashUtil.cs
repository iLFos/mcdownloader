using System.IO;
using System.Security.Cryptography;

namespace McDownloader.Services;

/// <summary>
/// 文件校验：按哈希长度自适应 MD5(32) / SHA1(40) / SHA256(64)，
/// 与 PCL 的 ModBase.FileChecker 一致——哈希命中时忽略大小差异。
/// </summary>
public static class HashUtil
{
    public static bool Verify(string path, string? hash, long expectedSize)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            if (string.IsNullOrWhiteSpace(hash))
                return expectedSize <= 0 || info.Length == expectedSize;

            var actual = Compute(path, hash.Length);
            var ok = string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase);
            if (ok) return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static string Compute(string path, int hashLength)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var hex = hashLength switch
        {
            64 => Convert.ToHexString(SHA256.HashData(stream)),
            32 => Convert.ToHexString(MD5.HashData(stream)),
            _ => Convert.ToHexString(SHA1.HashData(stream))
        };
        return hex.ToLowerInvariant();
    }
}
