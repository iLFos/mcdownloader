using System.IO;
using System.IO.Compression;
using McDownloader.Models;
using McDownloader.Utils;

namespace McDownloader.Services;

/// <summary>
/// 地图导入：直链下载 zip 或本地 zip → 校验 level.dat → 解压到 saves/{名称}。
/// 解压前用 SafeCombine 做前缀校验，防止 zip-slip 越界写入。
/// </summary>
public sealed class WorldImporter
{
    private readonly WorkspaceService _workspace;
    private readonly DownloadService _download;
    private readonly LogService _log;

    public WorldImporter(WorkspaceService workspace, DownloadService download, LogService log)
    {
        _workspace = workspace;
        _download = download;
        _log = log;
    }

    public async Task<string> ImportFromUrlAsync(string url, string? name, DownloadJob job, CancellationToken ct)
    {
        var fileName = GetFileNameFromUrl(url);
        var temp = _workspace.Resolve("mcdownloader", "cache", "worlds", fileName);
        job.SetStage("下载地图压缩包…");
        await _download.DownloadAllAsync(new[]
        {
            new FileEntry { Url = url, TargetPath = temp }
        }, job, ct);

        job.SetStage("解压地图…");
        return ImportZip(temp, name ?? Path.GetFileNameWithoutExtension(fileName), job);
    }

    public string ImportLocalZip(string zipPath, string? name, DownloadJob job)
    {
        job.SetStage("校验并解压地图…");
        return ImportZip(zipPath, name, job);
    }

    private string ImportZip(string zipPath, string? name, DownloadJob job)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("找不到地图压缩包", zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries
            .Select(e => new { Entry = e, Full = e.FullName.Replace('\\', '/') })
            .Where(x => !string.IsNullOrEmpty(x.Full))
            .ToList();

        // level.dat 必须位于根目录或唯一一层子目录内
        var level = entries.FirstOrDefault(x => x.Full.Equals("level.dat", StringComparison.OrdinalIgnoreCase))
                    ?? entries.FirstOrDefault(x =>
                        x.Full.EndsWith("/level.dat", StringComparison.OrdinalIgnoreCase) &&
                        x.Full.Count(c => c == '/') == 1);

        if (level is null)
            throw new InvalidDataException("这不是有效的世界存档：压缩包内未找到 level.dat（请确认是地图而不是资源包）。");

        var rootPrefix = "";
        var slash = level.Full.LastIndexOf('/');
        if (slash >= 0) rootPrefix = level.Full[..(slash + 1)];

        var saveName = Format.SafeName(
            !string.IsNullOrWhiteSpace(name) ? name :
            rootPrefix.Length > 0 ? rootPrefix.TrimEnd('/') :
            Path.GetFileNameWithoutExtension(zipPath));

        var targetDir = _workspace.Resolve("saves", saveName);
        if (Directory.Exists(targetDir))
            throw new IOException($"存档「{saveName}」已存在，请修改名称后再试。");

        Directory.CreateDirectory(targetDir);

        var count = 0;
        foreach (var item in entries)
        {
            if (rootPrefix.Length > 0 &&
                !item.Full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var relative = item.Full[rootPrefix.Length..];
            if (string.IsNullOrEmpty(relative)) continue;

            var target = WorkspaceService.SafeCombine(targetDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(item.Entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            item.Entry.ExtractToFile(target, true);
            job.Track(target);
            count++;
        }

        job.AddLog($"地图「{saveName}」导入完成，共解压 {count} 个文件");
        _log.Info($"地图导入完成：saves/{saveName}（{count} 个文件）");
        return saveName;
    }

    private static string GetFileNameFromUrl(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name)) name = "world.zip";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name += ".zip";
            return Format.SafeName(name, "world.zip");
        }
        catch
        {
            return "world.zip";
        }
    }
}
