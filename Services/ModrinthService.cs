using System.Text;
using System.Text.Json.Nodes;
using McDownloader.Models;

namespace McDownloader.Services;

/// <summary>
/// Modrinth v2 API：搜索、版本列表、文件信息。
/// 对应 PCL 的 ModComp.SearchModrinth 与 _GetSubFolder 落地规则；无 API Key，仅要求 User-Agent。
/// </summary>
public sealed class ModrinthService
{
    private const string Api = "https://api.modrinth.com/v2";
    private readonly HttpService _http;
    private readonly LogService _log;

    public ModrinthService(HttpService http, LogService log)
    {
        _http = http;
        _log = log;
    }

    public async Task<(List<ContentItem> Items, int Total)> SearchAsync(string query, string projectType,
        string? gameVersion, string? loader, string sort, int offset, int limit, CancellationToken ct)
    {
        var facets = new List<string>();
        if (!string.IsNullOrEmpty(projectType)) facets.Add($"[\"project_type:{projectType}\"]");
        if (!string.IsNullOrEmpty(gameVersion)) facets.Add($"[\"versions:{gameVersion}\"]");
        if (!string.IsNullOrEmpty(loader)) facets.Add($"[\"categories:{loader}\"]");

        var url = new StringBuilder($"{Api}/search?limit={limit}&offset={offset}&index={sort}");
        if (!string.IsNullOrWhiteSpace(query)) url.Append("&query=").Append(Uri.EscapeDataString(query.Trim()));
        if (facets.Count > 0)
            url.Append("&facets=").Append(Uri.EscapeDataString("[" + string.Join(",", facets) + "]"));

        var json = await _http.GetJsonAsync(url.ToString(), ct);
        var items = new List<ContentItem>();
        if (json["hits"] is JsonArray hits)
        {
            foreach (var node in hits)
            {
                if (node is not JsonObject hit) continue;
                var id = (string?)hit["project_id"] ?? (string?)hit["slug"] ?? "";
                if (string.IsNullOrEmpty(id)) continue;
                items.Add(new ContentItem
                {
                    Id = id,
                    Slug = (string?)hit["slug"] ?? "",
                    Title = (string?)hit["title"] ?? "(未命名)",
                    Author = (string?)hit["author"] ?? "",
                    Description = (string?)hit["description"] ?? "",
                    IconUrl = (string?)hit["icon_url"] ?? "",
                    ProjectType = (string?)hit["project_type"] ?? projectType,
                    Downloads = (long?)hit["downloads"] ?? 0,
                    LatestVersion = (hit["versions"] as JsonArray)?.FirstOrDefault()?.GetValue<string>() ?? ""
                });
            }
        }

        var total = (int?)json["total_hits"] ?? items.Count;
        _log.Info($"Modrinth 搜索「{query}」返回 {items.Count} 项（共 {total}）");
        return (items, total);
    }

    public async Task<List<ContentFile>> GetVersionsAsync(string projectId, string? gameVersion, string? loader,
        CancellationToken ct)
    {
        var query = new List<string> { "limit=50" };
        if (!string.IsNullOrEmpty(gameVersion))
            query.Add("game_versions=" + Uri.EscapeDataString($"[\"{gameVersion}\"]"));
        if (!string.IsNullOrEmpty(loader))
            query.Add("loaders=" + Uri.EscapeDataString($"[\"{loader}\"]"));

        var url = $"{Api}/project/{Uri.EscapeDataString(projectId)}/version?" + string.Join("&", query);
        var json = await _http.GetJsonAsync(url, ct);
        var result = new List<ContentFile>();
        if (json is not JsonArray versions) return result;

        foreach (var node in versions)
        {
            if (node is not JsonObject version) continue;
            if (version["files"] is not JsonArray files || files.Count == 0) continue;
            if (files[0] is not JsonObject file) continue;

            result.Add(new ContentFile
            {
                VersionId = (string?)version["id"] ?? "",
                VersionNumber = (string?)version["version_number"] ?? "",
                Name = (string?)version["name"] ?? "",
                VersionType = (string?)version["version_type"] ?? "release",
                FileName = (string?)file["filename"] ?? "",
                Url = (string?)file["url"] ?? "",
                Sha1 = (string?)file["hashes"]?["sha1"] ?? "",
                Size = (long?)file["size"] ?? 0,
                GameVersions = string.Join(", ", (version["game_versions"] as JsonArray)?.Select(x => x?.GetValue<string>()) ?? Array.Empty<string>()),
                Loaders = string.Join(", ", (version["loaders"] as JsonArray)?.Select(x => x?.GetValue<string>()) ?? Array.Empty<string>())
            });
        }

        return result;
    }

    /// <summary>按工程类型决定安装子目录，与 PCL 的 _GetSubFolder 一致。</summary>
    public static string SubFolder(string projectType) => projectType switch
    {
        "shader" => "shaderpacks",
        "resourcepack" => "resourcepacks",
        "datapack" => "datapacks",
        "modpack" => "modpacks",
        _ => "mods"
    };
}
