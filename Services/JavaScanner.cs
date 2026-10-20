using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using McDownloader.Models;

namespace McDownloader.Services;

/// <summary>
/// 本机 Java 探测：注册表（JavaSoft / Adoptium / Microsoft / Zulu / Corretto 等）、JAVA_HOME、PATH、
/// 常见安装目录与 .minecraft/runtime。参考 PCL 的 JavaManager 扫描器组合，但不下载 Java。
/// </summary>
public sealed class JavaScanner
{
    private static readonly Regex VersionRegex = new("version \"(\\d+)(?:\\.(\\d+))?", RegexOptions.Compiled);
    private readonly object _lock = new();
    private List<JavaRuntime>? _cache;
    private readonly LogService _log;

    public JavaScanner(LogService log)
    {
        _log = log;
    }

    public async Task<List<JavaRuntime>> ScanAsync(bool force, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!force && _cache is not null) return _cache;
        }

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in CollectCandidates()) candidates.Add(path);

        var results = new List<JavaRuntime>();
        foreach (var path in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var runtime = await ProbeAsync(path, ct);
            if (runtime is not null) results.Add(runtime);
        }

        var ordered = results
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(r => r.MajorVersion)
            .ThenByDescending(r => r.Vendor, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_lock) _cache = ordered;
        _log.Info($"Java 扫描完成，共找到 {ordered.Count} 个运行时");
        return ordered;
    }

    public JavaRuntime? GetBest(int requiredMajor)
    {
        lock (_lock)
        {
            return _cache?.Where(r => r.MajorVersion >= requiredMajor)
                .OrderBy(r => r.MajorVersion)
                .FirstOrDefault();
        }
    }

    public IReadOnlyList<JavaRuntime> Cached
    {
        get
        {
            lock (_lock) return _cache ?? new List<JavaRuntime>();
        }
    }

    private IEnumerable<string> CollectCandidates()
    {
        foreach (var home in ReadRegistryHomes()) yield return home;

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var exe = Path.Combine(javaHome.Trim('"'), "bin", "java.exe");
            if (File.Exists(exe)) yield return exe;
        }

        foreach (var dir in EnumeratePathDirectories())
        {
            var exe = Path.Combine(dir, "java.exe");
            if (File.Exists(exe)) yield return exe;
        }

        foreach (var root in CommonRoots())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(root);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                var exe = Path.Combine(child, "bin", "java.exe");
                if (File.Exists(exe)) yield return exe;
            }
        }
    }

    private static IEnumerable<string> ReadRegistryHomes()
    {
        var keys = new (RegistryHive Hive, string Path)[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\JavaSoft\Java Runtime Environment"),
            (RegistryHive.LocalMachine, @"SOFTWARE\JavaSoft\Java Development Kit"),
            (RegistryHive.LocalMachine, @"SOFTWARE\JavaSoft\JRE"),
            (RegistryHive.LocalMachine, @"SOFTWARE\JavaSoft\JDK"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\JavaSoft\Java Runtime Environment"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\JavaSoft\Java Development Kit"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Eclipse Adoptium\JDK"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Eclipse Foundation\JDK"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\JDK"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Azul Systems\Zulu"),
            (RegistryHive.LocalMachine, @"SOFTWARE\BellSoft\LibericaJDK"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Amazon Corretto"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Semeru\JDK"),
            (RegistryHive.LocalMachine, @"SOFTWARE\SapMachine\JDK")
        };

        var homes = new List<string>();
        foreach (var (hive, path) in keys)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(path);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var sub = root.OpenSubKey(name);
                    if (sub is null) continue;
                    var home = sub.GetValue("JavaHome") as string ?? sub.GetValue("Path") as string;
                    if (string.IsNullOrWhiteSpace(home)) continue;
                    var exe = Path.Combine(home.TrimEnd('\\'), "bin", "java.exe");
                    if (File.Exists(exe)) homes.Add(exe);
                }
            }
            catch
            {
                // 注册表不可访问时跳过
            }
        }
        return homes;
    }

    private static IEnumerable<string> EnumeratePathDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = entry.Trim().Trim('"');
            if (dir.Length == 0) continue;
            string exe;
            try
            {
                exe = Path.Combine(dir, "java.exe");
            }
            catch
            {
                continue;
            }
            if (File.Exists(exe)) yield return dir;
        }
    }

    private IEnumerable<string> CommonRoots()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var mcDir = Path.Combine(AppContext.BaseDirectory, ".minecraft");

        yield return Path.Combine(programFiles, "Java");
        yield return Path.Combine(programFiles, "Eclipse Adoptium");
        yield return Path.Combine(programFiles, "Eclipse Foundation");
        yield return Path.Combine(programFiles, "Microsoft");
        yield return Path.Combine(programFiles, "Zulu");
        yield return Path.Combine(programFiles, "BellSoft");
        yield return Path.Combine(programFiles, "Amazon Corretto");
        yield return Path.Combine(programFiles, "Semeru");
        yield return Path.Combine(programFiles, "SapMachine");
        yield return Path.Combine(programFilesX86, "Java");
        yield return Path.Combine(localAppData, "Programs", "Eclipse Adoptium");
        yield return Path.Combine(mcDir, "runtime");
        yield return Path.Combine(mcDir, "mcdownloader", "jre");
    }

    public async Task<JavaRuntime?> ProbeAsync(string exePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-version");

            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = await process.StandardError.ReadToEndAsync(ct) + await process.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // 忽略
                }
                return null;
            }

            var match = VersionRegex.Match(output);
            if (!match.Success) return null;
            var major = int.Parse(match.Groups[1].Value);
            if (major == 1 && match.Groups[2].Success) major = int.Parse(match.Groups[2].Value);
            if (major < 8) return null;

            var vendor = output.Contains("OpenJDK", StringComparison.OrdinalIgnoreCase) ||
                         output.Contains("OpenJDK", StringComparison.OrdinalIgnoreCase)
                ? "OpenJDK"
                : "Oracle JDK";
            if (output.Contains("Adoptium", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("Temurin", StringComparison.OrdinalIgnoreCase)) vendor = "Adoptium";
            else if (output.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) vendor = "Microsoft";
            else if (output.Contains("Zulu", StringComparison.OrdinalIgnoreCase)) vendor = "Zulu";
            else if (output.Contains("Corretto", StringComparison.OrdinalIgnoreCase)) vendor = "Corretto";

            return new JavaRuntime
            {
                Path = exePath,
                MajorVersion = major,
                Vendor = vendor,
                Is64Bit = !output.Contains("32-Bit", StringComparison.OrdinalIgnoreCase)
            };
        }
        catch
        {
            return null;
        }
    }
}
