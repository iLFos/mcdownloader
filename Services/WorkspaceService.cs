using System.IO;

namespace McDownloader.Services;

/// <summary>
/// 工作空间守卫：程序只在 exe 所在目录内读写，所有落盘路径必须经过 <see cref="Resolve"/> 校验。
/// 参考 PCL 的 ModFolder 逻辑：程序目录下即 .minecraft，存在 versions 也视为可用目录。
/// </summary>
public sealed class WorkspaceService
{
    public WorkspaceService()
    {
        Root = Path.GetFullPath(AppContext.BaseDirectory);
        if (!Root.EndsWith(Path.DirectorySeparatorChar)) Root += Path.DirectorySeparatorChar;
    }

    /// <summary>程序所在目录（带结尾分隔符）。</summary>
    public string Root { get; }

    /// <summary>&lt;程序目录&gt;\.minecraft</summary>
    public string MinecraftDir => Path.Combine(Root, ".minecraft");

    public string VersionsDir => Path.Combine(MinecraftDir, "versions");
    public string LibrariesDir => Path.Combine(MinecraftDir, "libraries");
    public string AssetsDir => Path.Combine(MinecraftDir, "assets");
    public string ModsDir => Path.Combine(MinecraftDir, "mods");
    public string ShaderPacksDir => Path.Combine(MinecraftDir, "shaderpacks");
    public string ResourcePacksDir => Path.Combine(MinecraftDir, "resourcepacks");
    public string SavesDir => Path.Combine(MinecraftDir, "saves");
    public string DataPacksDir => Path.Combine(MinecraftDir, "datapacks");
    public string SupportDir => Path.Combine(MinecraftDir, "mcdownloader");
    public string CacheDir => Path.Combine(SupportDir, "cache");
    public string LogsDir => Path.Combine(SupportDir, "logs");

    /// <summary>把若干段路径拼到 .minecraft 下，并保证结果不逃出工作空间。</summary>
    public string Resolve(params string[] segments)
    {
        var parts = new List<string> { MinecraftDir };
        parts.AddRange(segments);
        return Guard(Path.Combine(parts.ToArray()));
    }

    /// <summary>任意基准目录下的相对路径拼接守卫（用于 zip 解压，防止 zip-slip）。</summary>
    public static string SafeCombine(string baseDirectory, string relative)
    {
        var baseFull = Path.GetFullPath(baseDirectory);
        if (!baseFull.EndsWith(Path.DirectorySeparatorChar)) baseFull += Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(baseFull, relative));
        var normalizedBase = baseFull.TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, normalizedBase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"检测到越界路径，已阻止：{relative}");
        return full;
    }

    private string Guard(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"路径超出工作空间，已阻止：{full}");
        return full;
    }

    /// <summary>创建标准 .minecraft 目录模板。</summary>
    public void EnsureLayout()
    {
        foreach (var dir in new[]
                 {
                     MinecraftDir, VersionsDir, LibrariesDir, Path.Combine(AssetsDir, "indexes"),
                     Path.Combine(AssetsDir, "objects"), Path.Combine(AssetsDir, "log_configs"),
                     ModsDir, ShaderPacksDir, ResourcePacksDir, SavesDir, DataPacksDir, CacheDir, LogsDir
                 })
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>启动自检：工作空间必须可写，否则程序无法工作。</summary>
    public void EnsureWritable()
    {
        EnsureLayout();
        var probe = Path.Combine(SupportDir, ".write-test");
        File.WriteAllText(probe, DateTime.Now.ToString("O"));
        File.Delete(probe);
    }

    /// <summary>清理遗留的临时文件（被放弃的请求可能留下 0 字节 .part）。</summary>
    public void CleanupStaleParts()
    {
        try
        {
            var limit = DateTime.Now.AddDays(-1);
            foreach (var file in Directory.EnumerateFiles(MinecraftDir, "*.part", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTime < limit) info.Delete();
                }
                catch
                {
                    // 文件仍被占用时跳过
                }
            }
        }
        catch
        {
            // 忽略
        }
    }

    public long GetFreeSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Root);
            return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    public string Relative(string fullPath)
    {
        try
        {
            return Path.GetRelativePath(MinecraftDir, fullPath);
        }
        catch
        {
            return fullPath;
        }
    }
}
