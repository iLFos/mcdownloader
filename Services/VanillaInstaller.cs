using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using McDownloader.Models;

namespace McDownloader.Services;

/// <summary>
/// 原版安装：版本 JSON → client jar、libraries（含 natives）、assets 索引与资源对象、日志配置。
/// 对应 PCL 的 DlClientFix / DlClientJarGet / McAssetsGetIndex。
/// </summary>
public sealed class VanillaInstaller
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly WorkspaceService _workspace;
    private readonly DownloadService _download;
    private readonly VersionCatalogService _catalog;
    private readonly LogService _log;

    public VanillaInstaller(WorkspaceService workspace, DownloadService download,
        VersionCatalogService catalog, LogService log)
    {
        _workspace = workspace;
        _download = download;
        _catalog = catalog;
        _log = log;
    }

    public async Task<string> InstallAsync(string mcVersion, string instanceName, bool downloadAssets,
        DownloadJob job, CancellationToken ct)
    {
        job.SetStage("读取版本清单…");
        var manifest = await _catalog.GetManifestAsync(false, ct);
        var version = manifest.FirstOrDefault(v => v.Id.Equals(mcVersion, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException($"版本清单中不存在 {mcVersion}");
        return await InstallAsync(version, instanceName, downloadAssets, job, ct);
    }

    public async Task<string> InstallAsync(GameVersion version, string instanceName, bool downloadAssets,
        DownloadJob job, CancellationToken ct)
    {
        job.SetStage($"下载 {version.Id} 的版本 JSON…");
        var jsonText = await _catalog.GetVanillaJsonTextAsync(version, ct);
        return await InstallFromJsonAsync(jsonText, version.Id, instanceName, downloadAssets, job, ct);
    }

    private async Task<string> InstallFromJsonAsync(string jsonText, string mcVersion, string instanceName,
        bool downloadAssets, DownloadJob job, CancellationToken ct)
    {
        if (JsonNode.Parse(jsonText) is not JsonObject root)
            throw new InvalidDataException("版本 JSON 解析失败");

        instanceName = string.IsNullOrWhiteSpace(instanceName) ? mcVersion : FormatName(instanceName);
        var instanceDir = _workspace.Resolve("versions", instanceName);
        var instanceDirExisted = Directory.Exists(instanceDir);
        Directory.CreateDirectory(instanceDir);
        // 只有本次新建的版本目录才在取消时整体删除，避免误删用户已有实例
        if (!instanceDirExisted) job.InstanceDirectory = instanceDir;

        var files = new List<FileEntry>();
        var natives = new List<(string Jar, string[] Excludes)>();

        // 1. 客户端主 jar
        if (root["downloads"]?["client"] is JsonObject client && (string?)client["url"] is { } jarUrl)
        {
            files.Add(new FileEntry
            {
                Url = jarUrl,
                TargetPath = Path.Combine(instanceDir, instanceName + ".jar"),
                Hash = (string?)client["sha1"],
                Size = LibraryResolver.GetSize(client)
            });
        }
        else
        {
            job.AddLog($"警告：{mcVersion} 未提供 client jar 下载信息");
        }

        // 2. 依赖库（含旧版 natives）
        if (root["libraries"] is JsonArray libraries)
        {
            foreach (var node in libraries)
            {
                if (node is not JsonObject lib || !LibraryResolver.IsAllowed(lib)) continue;
                var artifact = lib["downloads"]?["artifact"] as JsonObject;
                var relPath = (string?)artifact?["path"];
                var url = (string?)artifact?["url"];
                // 只有 classifiers 的库（如旧版 jinput-platform）没有主 jar，不能按坐标拼出并不存在的文件
                var hasClassifiers = lib["downloads"]?["classifiers"] is JsonObject classifiers && classifiers.Count > 0;

                if (!string.IsNullOrEmpty(relPath) && !string.IsNullOrEmpty(url))
                {
                    files.Add(new FileEntry
                    {
                        Url = url,
                        TargetPath = _workspace.Resolve("libraries", relPath.Replace('/', Path.DirectorySeparatorChar)),
                        Hash = (string?)artifact!["sha1"],
                        Size = LibraryResolver.GetSize(artifact)
                    });
                }
                else if (!hasClassifiers && (string?)lib["name"] is { } name)
                {
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

                if (lib["natives"]?["windows"] is JsonValue nativeValue)
                {
                    var classifier = nativeValue.GetValue<string>().Replace("${arch}", "64");
                    if (lib["downloads"]?["classifiers"]?[classifier] is JsonObject nativeArtifact &&
                        (string?)nativeArtifact["url"] is { } nativeUrl &&
                        (string?)nativeArtifact["path"] is { } nativePath)
                    {
                        var target = _workspace.Resolve("libraries", nativePath.Replace('/', Path.DirectorySeparatorChar));
                        files.Add(new FileEntry
                        {
                            Url = nativeUrl,
                            TargetPath = target,
                            Hash = (string?)nativeArtifact["sha1"],
                            Size = LibraryResolver.GetSize(nativeArtifact)
                        });
                        natives.Add((target, LibraryResolver.ExtractExcludes(lib)));
                    }
                }
            }
        }

        // 3. 资源索引
        string? assetIndexId = null;
        JsonObject? assetIndexNode = null;
        if (root["assetIndex"] is JsonObject indexInfo)
        {
            assetIndexNode = indexInfo;
            assetIndexId = (string?)indexInfo["id"];
            if ((string?)indexInfo["url"] is { } indexUrl && !string.IsNullOrEmpty(assetIndexId))
            {
                files.Add(new FileEntry
                {
                    Url = indexUrl,
                    TargetPath = _workspace.Resolve("assets", "indexes", assetIndexId + ".json"),
                    Hash = (string?)indexInfo["sha1"],
                    Size = LibraryResolver.GetSize(indexInfo)
                });
            }
        }

        // 4. 日志配置
        if (root["logging"]?["client"]?["file"] is JsonObject logFile &&
            (string?)logFile["url"] is { } logUrl && (string?)logFile["id"] is { } logId)
        {
            files.Add(new FileEntry
            {
                Url = logUrl,
                TargetPath = _workspace.Resolve("assets", "log_configs", logId),
                Hash = (string?)logFile["sha1"],
                Size = LibraryResolver.GetSize(logFile),
                // 日志配置只影响游戏控制台格式，缺失不应阻断安装
                Required = false
            });
        }

        // 5. 先写版本 JSON（id 改为实例名）。与 PCL 一致：JSON 先落盘，
        //    即使后续文件下载失败，版本也能被 PCL/其它启动器看到并「补全文件」。
        root["id"] = instanceName;
        var jsonPath = Path.Combine(instanceDir, instanceName + ".json");
        File.WriteAllText(jsonPath, root.ToJsonString(WriteOptions), new UTF8Encoding(false));
        job.Track(jsonPath);

        job.SetStage("下载游戏核心文件…");
        await _download.DownloadAllAsync(files, job, ct, 0, 0.35, "下载游戏文件");

        // 6. 解压旧版 natives
        if (natives.Count > 0)
        {
            job.SetStage("解压原生库…");
            ExtractNatives(natives, Path.Combine(instanceDir, instanceName + "-natives"), job);
        }

        // 7. 资源文件
        if (downloadAssets && assetIndexId is not null)
        {
            await DownloadAssetsAsync(assetIndexId, job, ct);
        }
        else if (!downloadAssets)
        {
            job.AddLog("已跳过资源文件下载（可在设置中开启）");
        }

        job.AddLog($"版本 {instanceName} 安装完成");
        _log.Info($"原版安装完成：{instanceName}");
        return instanceName;
    }

    private async Task DownloadAssetsAsync(string assetIndexId, DownloadJob job, CancellationToken ct)
    {
        var indexPath = _workspace.Resolve("assets", "indexes", assetIndexId + ".json");
        if (!File.Exists(indexPath))
        {
            job.AddLog("资源索引缺失，跳过资源下载");
            return;
        }

        JsonObject? index;
        try
        {
            index = JsonNode.Parse(File.ReadAllText(indexPath)) as JsonObject;
        }
        catch (Exception ex)
        {
            job.AddLog("资源索引解析失败：" + ex.Message);
            return;
        }

        if (index?["objects"] is not JsonObject objects) return;

        var files = new List<FileEntry>();
        foreach (var (_, node) in objects)
        {
            var hash = (string?)node?["hash"];
            if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;
            files.Add(new FileEntry
            {
                Url = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                TargetPath = _workspace.Resolve("assets", "objects", hash[..2], hash),
                Hash = hash,
                Size = LibraryResolver.GetSize(node as JsonObject),
                Required = false
            });
        }

        job.AddLog($"资源文件共 {files.Count} 个对象（首次安装耗时较长，再次安装会跳过已存在文件）");
        await _download.DownloadAllAsync(files, job, ct, 0.35, 1, "下载资源文件");

        // 旧版本（1.7 及以前）需要虚拟资源目录
        var isVirtual = index["virtual"] is JsonValue v && v.TryGetValue<bool>(out var vv) && vv;
        var mapToResources = index["map_to_resources"] is JsonValue m && m.TryGetValue<bool>(out var mm) && mm;
        if (isVirtual || mapToResources)
        {
            job.SetStage("建立旧版资源映射…");
            var baseDir = isVirtual
                ? _workspace.Resolve("assets", "virtual", assetIndexId)
                : _workspace.Resolve("resources");
            foreach (var (name, node) in objects)
            {
                var hash = (string?)node?["hash"];
                if (string.IsNullOrEmpty(hash) || hash.Length < 2) continue;
                var source = _workspace.Resolve("assets", "objects", hash[..2], hash);
                if (!File.Exists(source)) continue;
                var target = WorkspaceService.SafeCombine(baseDir, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) File.Copy(source, target);
            }
        }
    }

    private static void ExtractNatives(List<(string Jar, string[] Excludes)> natives, string targetDir, DownloadJob job)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var (jar, excludes) in natives)
        {
            if (!File.Exists(jar))
            {
                job.AddLog("缺少原生库：" + Path.GetFileName(jar));
                continue;
            }

            try
            {
                using var archive = ZipFile.OpenRead(jar);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var fullName = entry.FullName.Replace('\\', '/');
                    if (fullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (excludes.Any(e => fullName.StartsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                    var target = WorkspaceService.SafeCombine(targetDir, fullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, true);
                }
            }
            catch (Exception ex)
            {
                job.AddLog("解压原生库失败：" + ex.Message);
            }
        }
    }

    private static string FormatName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "instance" : cleaned;
    }
}
