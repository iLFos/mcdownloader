using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using McDownloader.Models;

namespace McDownloader.Services;

/// <summary>
/// Fabric / Quilt 安装：直接取官方 profile JSON 作为版本 JSON（含 inheritsFrom），
/// 再补齐其 libraries。对应 PCL 的 McDownloadFabricLoader（ModDownloadLib.cs:2913）。
/// </summary>
public sealed class FabricInstaller
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly WorkspaceService _workspace;
    private readonly HttpService _http;
    private readonly DownloadService _download;
    private readonly VersionCatalogService _catalog;
    private readonly VanillaInstaller _vanilla;
    private readonly LogService _log;

    public FabricInstaller(WorkspaceService workspace, HttpService http, DownloadService download,
        VersionCatalogService catalog, VanillaInstaller vanilla, LogService log)
    {
        _workspace = workspace;
        _http = http;
        _download = download;
        _catalog = catalog;
        _vanilla = vanilla;
        _log = log;
    }

    public async Task<string> InstallAsync(LoaderKind kind, string mcVersion, string loaderVersion,
        string instanceName, bool downloadAssets, DownloadJob job, CancellationToken ct)
    {
        if (!_catalog.IsInstalled(mcVersion))
        {
            job.SetStage($"未发现原版 {mcVersion}，先安装原版…");
            job.AddLog($"前置依赖：原版 {mcVersion} 尚未安装，将自动补齐");
            await _vanilla.InstallAsync(mcVersion, mcVersion, downloadAssets, job, ct);
        }

        job.SetStage("获取加载器 profile…");
        var profileUrl = kind == LoaderKind.Quilt
            ? $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json"
            : $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";

        var jsonText = await _http.GetStringFromAnyAsync(UrlResolver.Candidates(profileUrl, _http.Source), ct);
        if (JsonNode.Parse(jsonText) is not JsonObject root)
            throw new InvalidDataException("加载器 profile 解析失败");

        instanceName = string.IsNullOrWhiteSpace(instanceName)
            ? VersionCatalogService.BuildInstanceName(kind, mcVersion, loaderVersion)
            : FormatName(instanceName);

        root["id"] = instanceName;
        root["inheritsFrom"] ??= mcVersion;

        var instanceDir = _workspace.Resolve("versions", instanceName);
        var instanceDirExisted = Directory.Exists(instanceDir);
        Directory.CreateDirectory(instanceDir);
        if (!instanceDirExisted) job.InstanceDirectory = instanceDir;

        // 先落 JSON，再补库文件
        var jsonPath = Path.Combine(instanceDir, instanceName + ".json");
        File.WriteAllText(jsonPath, root.ToJsonString(WriteOptions), new UTF8Encoding(false));
        job.Track(jsonPath);

        var files = new List<FileEntry>();
        if (root["libraries"] is JsonArray libraries)
        {
            foreach (var node in libraries)
            {
                if (node is not JsonObject lib || !LibraryResolver.IsAllowed(lib)) continue;

                var artifact = lib["downloads"]?["artifact"] as JsonObject;
                if (artifact is not null && (string?)artifact["url"] is { } url && (string?)artifact["path"] is { } path)
                {
                    files.Add(new FileEntry
                    {
                        Url = url,
                        TargetPath = _workspace.Resolve("libraries", path.Replace('/', Path.DirectorySeparatorChar)),
                        Hash = (string?)artifact["sha1"],
                        Size = LibraryResolver.GetSize(artifact)
                    });
                    continue;
                }

                // 仅含 classifiers 的库没有主 jar，跳过坐标拼装
                if (lib["downloads"]?["classifiers"] is JsonObject classifiers && classifiers.Count > 0) continue;
                if ((string?)lib["name"] is not { } name) continue;

                string mavenPath;
                try
                {
                    mavenPath = LibraryResolver.MavenPath(name);
                }
                catch
                {
                    continue;
                }

                var baseUrl = (string?)lib["url"] ?? "https://libraries.minecraft.net/";
                if (!baseUrl.EndsWith("/")) baseUrl += "/";
                files.Add(new FileEntry
                {
                    Url = baseUrl + mavenPath,
                    TargetPath = _workspace.Resolve("libraries", mavenPath.Replace('/', Path.DirectorySeparatorChar))
                });
            }
        }

        job.SetStage("下载加载器库文件…");
        await _download.DownloadAllAsync(files, job, ct);

        job.AddLog($"{kind} {loaderVersion} 安装完成，可用于 {mcVersion}");
        _log.Info($"{kind} 安装完成：{instanceName}");
        return instanceName;
    }

    private static string FormatName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "instance" : cleaned;
    }
}
