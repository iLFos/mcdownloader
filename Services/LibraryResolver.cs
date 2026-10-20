using System.Text.Json.Nodes;

namespace McDownloader.Services;

/// <summary>版本 JSON 中的库解析：rules 过滤与 Maven 坐标转路径。</summary>
public static class LibraryResolver
{
    /// <summary>net.fabricmc:fabric-loader:0.15.11 → net/fabricmc/fabric-loader/0.15.11/fabric-loader-0.15.11.jar</summary>
    public static string MavenPath(string name)
    {
        var ext = "jar";
        var coordinate = name;
        var at = name.LastIndexOf('@');
        if (at > 0)
        {
            ext = name[(at + 1)..];
            coordinate = name[..at];
        }

        var parts = coordinate.Split(':');
        if (parts.Length < 3) throw new FormatException("无法解析的库坐标：" + name);
        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{ext}";
    }

    /// <summary>按 Minecraft 官方规则判断库是否适用于 Windows x64。</summary>
    public static bool IsAllowed(JsonObject library)
    {
        if (library["rules"] is not JsonArray rules || rules.Count == 0) return true;

        var matched = false;
        var allowed = false;
        string? firstAction = null;

        foreach (var node in rules)
        {
            if (node is not JsonObject rule) continue;
            var action = (string?)rule["action"] ?? "allow";
            firstAction ??= action;
            if (!RuleMatches(rule)) continue;
            matched = true;
            allowed = action.Equals("allow", StringComparison.OrdinalIgnoreCase);
        }

        // 与官方行为一致：无规则命中时，取首条规则的反面
        return matched ? allowed : firstAction == "disallow";
    }

    private static bool RuleMatches(JsonObject rule)
    {
        // 带 features 的规则依赖启动参数，安装阶段一律视为不匹配
        if (rule["features"] is JsonObject features && features.Count > 0) return false;
        if (rule["os"] is not JsonObject os) return true;

        var name = (string?)os["name"];
        if (!string.IsNullOrEmpty(name) && !name.Equals("windows", StringComparison.OrdinalIgnoreCase)) return false;

        var arch = (string?)os["arch"];
        if (!string.IsNullOrEmpty(arch) && !arch.Equals("x86_64", StringComparison.OrdinalIgnoreCase) &&
            !arch.Equals("x64", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    public static string[] ExtractExcludes(JsonObject library)
    {
        if (library["extract"]?["exclude"] is not JsonArray excludes) return Array.Empty<string>();
        return excludes.Select(e => (string?)e).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToArray();
    }

    public static long GetSize(JsonObject? node)
    {
        if (node?["size"] is null) return 0;
        try
        {
            return node["size"]!.GetValue<long>();
        }
        catch
        {
            return long.TryParse((string?)node["size"], out var parsed) ? parsed : 0;
        }
    }
}
