using System.Diagnostics;
using System.IO;
using McDownloader.Models;

namespace McDownloader.Services;

/// <summary>
/// Forge / NeoForge：下载官方 installer jar，调用本机 Java 以 --installClient 无界面安装。
/// 参考 PCL 的 ForgelikeInjector（ModDownloadLib.cs:1698-1743）：进度靠解析输出关键字，
/// 成功后由 versions 目录中出现的新版本 JSON 判定。
/// </summary>
public sealed class ForgeInstaller
{
    private static readonly string[] ProgressKeywords =
    {
        "Extracting", "Downloading", "Building", "Processor", "Remapping", "Unpacking", "Injecting"
    };

    private readonly WorkspaceService _workspace;
    private readonly DownloadService _download;
    private readonly ConfigService _config;
    private readonly JavaScanner _java;
    private readonly LogService _log;

    public ForgeInstaller(WorkspaceService workspace, DownloadService download, ConfigService config,
        JavaScanner java, LogService log)
    {
        _workspace = workspace;
        _download = download;
        _config = config;
        _java = java;
        _log = log;
    }

    public async Task<string> InstallAsync(LoaderKind kind, string mcVersion, string loaderVersion,
        DownloadJob job, CancellationToken ct)
    {
        var installerUrl = BuildInstallerUrl(kind, mcVersion, loaderVersion);
        var installerName = Path.GetFileName(new Uri(installerUrl).AbsolutePath);
        var installerPath = _workspace.Resolve("mcdownloader", "cache", installerName);

        job.SetStage("下载安装器…");
        job.AddLog($"安装器：{installerUrl}");
        await _download.DownloadAllAsync(new[]
        {
            new FileEntry { Url = installerUrl, TargetPath = installerPath }
        }, job, ct);

        var required = kind == LoaderKind.NeoForge
            ? Math.Max(17, VersionCatalogService.RequiredJavaMajor(mcVersion))
            : VersionCatalogService.RequiredJavaMajor(mcVersion);

        job.SetStage("查找 Java 运行时…");
        var java = await PickJavaAsync(required, ct)
                   ?? throw new InvalidOperationException(
                       $"未找到 Java {required} 或更高版本。请安装 Java 后在「设置」中指定路径，再重试。");

        EnsureLauncherProfiles();
        job.AddLog($"使用 Java {java.MajorVersion}：{java.Path}");
        var started = DateTime.Now;

        job.SetStage("运行安装器…");
        var exitCode = await RunInstallerAsync(java.Path, installerPath,
            new[] { "--installClient", _workspace.MinecraftDir }, job, ct);

        if (exitCode != 0)
        {
            job.AddLog($"带路径参数安装失败（退出码 {exitCode}），改用工作目录方式重试…");
            exitCode = await RunInstallerAsync(java.Path, installerPath, new[] { "--installClient" }, job, ct);
        }

        if (exitCode != 0)
            throw new IOException($"安装器退出码 {exitCode}，常见原因是 Java 版本不匹配或网络中断，请查看日志。");

        job.SetStage("校验安装结果…");
        var instance = FindNewVersion(started);
        if (instance is null)
            throw new IOException("安装器已结束，但 versions 目录未产生新版本，请检查安装器日志。");

        job.AddLog($"安装成功：versions/{instance}");
        _log.Info($"Forge 系安装完成：{instance}");
        return instance;
    }

    public static string BuildInstallerUrl(LoaderKind kind, string mcVersion, string loaderVersion)
    {
        if (kind == LoaderKind.NeoForge)
        {
            if (mcVersion == "1.20.1")
                return $"https://maven.neoforged.net/releases/net/neoforged/forge/{loaderVersion}/forge-{loaderVersion}-installer.jar";
            return $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar";
        }

        var full = loaderVersion.StartsWith(mcVersion + "-", StringComparison.OrdinalIgnoreCase)
            ? loaderVersion
            : $"{mcVersion}-{loaderVersion}";
        return $"https://maven.minecraftforge.net/net/minecraftforge/forge/{full}/forge-{full}-installer.jar";
    }

    private async Task<JavaRuntime?> PickJavaAsync(int required, CancellationToken ct)
    {
        var manual = _config.Value.JavaPath;
        if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual))
        {
            var probed = await _java.ProbeAsync(manual, ct);
            if (probed is not null && probed.MajorVersion >= required) return probed;
            if (probed is not null)
                _log.Warn($"指定的 Java {probed.MajorVersion} 低于要求的 {required}，继续自动选择");
        }

        var cached = _java.GetBest(required);
        if (cached is not null) return cached;

        var list = await _java.ScanAsync(true, ct);
        return list.Where(r => r.MajorVersion >= required)
            .OrderBy(r => r.MajorVersion)
            .FirstOrDefault();
    }

    /// <summary>
    /// 老版本 Forge 安装器会校验 .minecraft/launcher_profiles.json 是否存在，
    /// 缺失时直接失败（PCL 通过在临时目录伪造该文件绕过）。这里只补一个最小骨架，不覆盖已有文件。
    /// </summary>
    private void EnsureLauncherProfiles()
    {
        try
        {
            var path = Path.Combine(_workspace.MinecraftDir, "launcher_profiles.json");
            if (File.Exists(path)) return;
            File.WriteAllText(path,
                "{\"profiles\":{},\"settings\":{\"enableSnapshots\":false,\"enableAdvanced\":false},\"version\":3}");
        }
        catch (Exception ex)
        {
            _log.Warn("创建 launcher_profiles.json 失败：" + ex.Message);
        }
    }

    private async Task<int> RunInstallerAsync(string javaPath, string installerPath, string[] extraArgs,
        DownloadJob job, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(javaPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workspace.MinecraftDir,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        psi.ArgumentList.Add("-Dfile.encoding=UTF-8");
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerPath);
        foreach (var arg in extraArgs) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => HandleLine(e.Data, job);
        process.ErrorDataReceived += (_, e) => HandleLine(e.Data, job);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // 忽略
            }
            throw;
        }

        return process.ExitCode;
    }

    private static void HandleLine(string? line, DownloadJob job)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var text = line.Trim();
        if (text.Length > 400) text = text[..400] + "…";

        if (text.Contains("Extracting", StringComparison.OrdinalIgnoreCase))
            job.SetStage("解压安装器资源…");
        else if (text.Contains("Downloading", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("Download", StringComparison.OrdinalIgnoreCase))
            job.SetStage("下载安装依赖…");
        else if (text.Contains("Building", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("Processor", StringComparison.OrdinalIgnoreCase))
            job.SetStage("执行安装处理器…");
        else if (text.Contains("Remapping", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("Injecting", StringComparison.OrdinalIgnoreCase))
            job.SetStage("重映射游戏核心…");

        if (ProgressKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)) ||
            text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Exception", StringComparison.OrdinalIgnoreCase))
        {
            job.AddLog(text);
        }
    }

    private string? FindNewVersion(DateTime started)
    {
        try
        {
            if (!Directory.Exists(_workspace.VersionsDir)) return null;
            var threshold = started.AddMinutes(-1);
            return Directory.EnumerateDirectories(_workspace.VersionsDir)
                .Select(dir => new
                {
                    Name = Path.GetFileName(dir),
                    Json = Directory.EnumerateFiles(dir, "*.json").FirstOrDefault()
                })
                .Where(x => x.Json is not null && File.GetLastWriteTime(x.Json) >= threshold)
                .OrderByDescending(x => File.GetLastWriteTime(x.Json!))
                .Select(x => x.Name)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
