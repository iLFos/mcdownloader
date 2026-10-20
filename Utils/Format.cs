using System.IO;

namespace McDownloader.Utils;

public static class Format
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < Units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return index == 0 ? $"{value:0} B" : $"{value:0.##} {Units[index]}";
    }

    public static string Speed(long bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Count(long number)
    {
        if (number >= 100_000_000) return $"{number / 100_000_000d:0.##} 亿";
        if (number >= 10_000) return $"{number / 10_000d:0.##} 万";
        return number.ToString("N0");
    }

    /// <summary>去除 Windows 文件名非法字符，并限制长度。</summary>
    public static string SafeName(string? name, string fallback = "未命名")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim(' ', '.');
        if (result.Length > 60) result = result[..60];
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }
}
