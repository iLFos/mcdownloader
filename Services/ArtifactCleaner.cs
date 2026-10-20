using System.IO;
using McDownloader.Models;
using McDownloader.Utils;

namespace McDownloader.Services;

/// <summary>
/// 取消下载后的回滚：只删除本任务实际写入的文件与新建的版本目录，
/// 已存在且校验通过的共享库/资源不会被误删（它们没有进入 CreatedFiles）。
/// </summary>
public sealed class ArtifactCleaner
{
    private readonly WorkspaceService _workspace;
    private readonly LogService _log;

    public ArtifactCleaner(WorkspaceService workspace, LogService log)
    {
        _workspace = workspace;
        _log = log;
    }

    public (int Files, long Bytes, bool VersionRemoved) Clean(DownloadJob job)
    {
        var root = _workspace.MinecraftDir;
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var deletedFiles = 0;
        long deletedBytes = 0;
        var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in job.CreatedFiles.Keys.ToList())
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                if (File.Exists(full))
                {
                    deletedBytes += new FileInfo(full).Length;
                    File.Delete(full);
                    deletedFiles++;
                }

                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) touchedDirs.Add(dir);
            }
            catch (Exception ex)
            {
                _log.Warn($"清理文件失败：{path} {ex.Message}");
            }
        }

        var versionRemoved = false;
        if (job.InstanceDirectory is { } instanceDir && Directory.Exists(instanceDir))
        {
            try
            {
                Directory.Delete(instanceDir, true);
                versionRemoved = true;
            }
            catch (Exception ex)
            {
                _log.Warn($"移除版本目录失败：{instanceDir} {ex.Message}");
            }
        }

        // 自底向上清掉本次产生的空目录（例如 assets/objects 下的分桶）
        foreach (var dir in touchedDirs.OrderByDescending(d => d.Length))
        {
            try
            {
                var current = dir;
                while (!string.IsNullOrEmpty(current) &&
                       current.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
                    {
                        Directory.Delete(current);
                        current = Path.GetDirectoryName(current);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch
            {
                // 目录仍被占用时跳过
            }
        }

        job.CreatedFiles.Clear();
        job.InstanceDirectory = null;
        _log.Info($"取消清理完成：删除 {deletedFiles} 个文件（{Format.Bytes(deletedBytes)}）" +
                  (versionRemoved ? "，并移除未完成的版本目录" : ""));
        return (deletedFiles, deletedBytes, versionRemoved);
    }
}
