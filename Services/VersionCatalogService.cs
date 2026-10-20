using System.IO;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using McDownloader.Models;

namespace McDownloader.Services;

public enum LoaderKind
{
    Vanilla,
    Fabric,
    Quilt,
    Forge,
    NeoForge
}

/// <summary>版本清单与各加载器版本的获取与缓存（30 分钟内存 + 磁盘缓存）。</summary>
public sealed class VersionCatalogService
{
    private const string ManifestOfficial = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    private readonly HttpService _http;
    private readonly WorkspaceService _workspace;
    private readonly LogService _log;
    private readonly Dictionary<string, List<string>> _loaderCache = new();
    private readonly object _cacheLock = new();
    private List<GameVersion>? _manifest;
    private DateTime _manifestAt;

    public VersionCatalogService(HttpService http, WorkspaceService workspace, LogService log)
    {
        _http = http;
        _workspace = workspace;
        _log = log;
    }

    public async Task<List<GameVersion>> GetManifestAsync(bool force, CancellationToken ct)
    {
        if (!force && _manifest is not null && DateTime.Now - _manifestAt < TimeSpan.FromMinutes(30))
            return _manifest;

        var cacheFile = Path.Combine(_workspace.CacheDir, "version_manifest_v2.json");
        string? json = null;

        // 30 分钟内的磁盘缓存直接使用，避免每次启动都等待网络（弱网时列表会长时间空白）
        if (!force && File.Exists(cacheFile) &&
            DateTime.Now - File.GetLastWriteTime(cacheFile) < TimeSpan.FromMinutes(30))
        {
            try
            {
                json = File.ReadAllText(cacheFile);
                _log.Info("使用本地缓存的版本清单");
            }
            catch
            {
                json = null;
            }
        }

        if (json is null)
        {
            try
            {
                json = await _http.GetStringFromAnyAsync(UrlResolver.Candidates(ManifestOfficial, _http.Source), ct);
                if (json.Length > 2000) File.WriteAllText(cacheFile, json);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("版本清单获取失败，尝试使用本地缓存：" + ex.Message);
                if (File.Exists(cacheFile)) json = File.ReadAllText(cacheFile);
            }
        }

        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("无法获取 Minecraft 版本清单，请检查网络后重试。");

        var doc = JsonNode.Parse(json)!;
        var list = new List<GameVersion>();
        if (doc["versions"] is JsonArray versions)
        {
            foreach (var node in versions)
            {
                if (node is not JsonObject item) continue;
                var id = (string?)item["id"];
                if (string.IsNullOrWhiteSpace(id)) continue;
                DateTimeOffset.TryParse((string?)item["releaseTime"], out var time);
                list.Add(new GameVersion
                {
                    Id = id,
                    Type = (string?)item["type"] ?? "release",
                    Url = (string?)item["url"] ?? "",
                    ReleaseTime = time,
                    IsInstalled = IsInstalled(id)
                });
            }
        }

        if (list.Count < 50) throw new InvalidOperationException("版本清单内容异常（条目过少），已中止。");
        _manifest = list;
        _manifestAt = DateTime.Now;
        _log.Info($"已加载版本清单，共 {list.Count} 个版本");
        return list;
    }

    public async Task<string> GetVanillaJsonTextAsync(GameVersion version, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(version.Url))
            throw new InvalidOperationException($"版本 {version.Id} 缺少 JSON 地址");
        return await _http.GetStringFromAnyAsync(UrlResolver.Candidates(version.Url, _http.Source), ct);
    }

    public async Task<List<string>> GetLoaderVersionsAsync(LoaderKind kind, string mcVersion, CancellationToken ct)
    {
        var key = $"{kind}:{mcVersion}";
        lock (_cacheLock)
        {
            if (_loaderCache.TryGetValue(key, out var cached)) return cached;
        }

        List<string> result = kind switch
        {
            LoaderKind.Fabric => await GetFabricLoadersAsync(mcVersion, ct),
            LoaderKind.Quilt => await GetQuiltLoadersAsync(mcVersion, ct),
            LoaderKind.Forge => await GetForgeVersionsAsync(mcVersion, ct),
            LoaderKind.NeoForge => await GetNeoForgeVersionsAsync(mcVersion, ct),
            _ => new List<string>()
        };

        lock (_cacheLock) _loaderCache[key] = result;
        return result;
    }

    private async Task<List<string>> GetFabricLoadersAsync(string mcVersion, CancellationToken ct)
    {
        var url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}";
        var json = await _http.GetJsonAsync(url, ct);
        var list = new List<string>();
        if (json is JsonArray array)
        {
            foreach (var node in array)
            {
                var version = (string?)node?["loader"]?["version"];
                if (!string.IsNullOrWhiteSpace(version)) list.Add(version);
            }
        }
        return list;
    }

    private async Task<List<string>> GetQuiltLoadersAsync(string mcVersion, CancellationToken ct)
    {
        var url = $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(mcVersion)}";
        var json = await _http.GetJsonAsync(url, ct);
        var list = new List<string>();
        if (json is JsonArray array)
        {
            foreach (var node in array)
            {
                var version = (string?)node?["version"];
                if (!string.IsNullOrWhiteSpace(version)) list.Add(version);
            }
        }
        return list;
    }

    private async Task<List<string>> GetForgeVersionsAsync(string mcVersion, CancellationToken ct)
    {
        var official = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
        var xml = await _http.GetStringFromAnyAsync(UrlResolver.Candidates(official, _http.Source), ct);
        return ParseMavenVersions(xml)
            .Where(v => v.StartsWith(mcVersion + "-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<string>> GetNeoForgeVersionsAsync(string mcVersion, CancellationToken ct)
    {
        // NeoForge 1.20.1 使用 net/neoforged/forge，其余使用 net/neoforged/neoforge
        var legacy = mcVersion == "1.20.1";
        var path = legacy ? "net/neoforged/forge" : "net/neoforged/neoforge";
        var official = $"https://maven.neoforged.net/releases/{path}/maven-metadata.xml";
        var xml = await _http.GetStringFromAnyAsync(UrlResolver.Candidates(official, _http.Source), ct);
        var all = ParseMavenVersions(xml);

        if (legacy)
            return all.Where(v => v.StartsWith("1.20.1-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).ToList();

        var prefix = NeoForgePrefix(mcVersion);
        return all.Where(v => v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>1.20.4 → "20.4."；1.21 → "21.0."。</summary>
    public static string NeoForgePrefix(string mcVersion)
    {
        var parts = mcVersion.Split('.');
        if (parts.Length < 2) return mcVersion + ".";
        var minor = parts[1];
        var patch = parts.Length > 2 ? parts[2] : "0";
        return $"{minor}.{patch}.";
    }

    private static List<string> ParseMavenVersions(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Descendants("version")
                .Select(e => e.Value.Trim())
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public bool IsInstalled(string versionId)
    {
        try
        {
            var dir = _workspace.Resolve("versions", versionId);
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.json").Any();
        }
        catch
        {
            return false;
        }
    }

    public List<string> GetInstalledInstances()
    {
        try
        {
            if (!Directory.Exists(_workspace.VersionsDir)) return new List<string>();
            return Directory.EnumerateDirectories(_workspace.VersionsDir)
                .Where(d => Directory.EnumerateFiles(d, "*.json").Any())
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static string BuildInstanceName(LoaderKind kind, string mcVersion, string loaderVersion) => kind switch
    {
        LoaderKind.Fabric => $"fabric-loader-{loaderVersion}-{mcVersion}",
        LoaderKind.Quilt => $"quilt-loader-{loaderVersion}-{mcVersion}",
        LoaderKind.Forge => $"{mcVersion}-forge-{ForgeVersionSuffix(mcVersion, loaderVersion)}",
        LoaderKind.NeoForge => mcVersion == "1.20.1"
            ? $"1.20.1-neoforge-{ForgeVersionSuffix(mcVersion, loaderVersion)}"
            : $"neoforge-{loaderVersion}",
        _ => mcVersion
    };

    private static string ForgeVersionSuffix(string mcVersion, string loaderVersion) =>
        loaderVersion.StartsWith(mcVersion + "-", StringComparison.OrdinalIgnoreCase)
            ? loaderVersion[(mcVersion.Length + 1)..]
            : loaderVersion;

    /// <summary>不同 Minecraft 版本对 Java 大版本的最低要求。</summary>
    public static int RequiredJavaMajor(string mcVersion)
    {
        var parts = mcVersion.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major)) return 8;
        if (major == 1)
        {
            if (!int.TryParse(parts[1], out var minor)) return 8;
            var patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
            if (minor > 20 || (minor == 20 && patch >= 5)) return 21;
            if (minor >= 18) return 17;
            if (minor == 17) return 16;
            return 8;
        }
        return 21;
    }
}
