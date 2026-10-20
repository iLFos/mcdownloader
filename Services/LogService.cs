using System.IO;
using System.Text;

namespace McDownloader.Services;

/// <summary>滚动文件日志（按天），保留 7 天；同时向界面广播。</summary>
public sealed class LogService
{
    private readonly object _lock = new();
    private readonly string _directory;

    public LogService(WorkspaceService workspace)
    {
        _directory = workspace.LogsDir;
        try
        {
            Directory.CreateDirectory(_directory);
            Cleanup();
        }
        catch
        {
            // 日志不可用不应阻塞程序
        }
    }

    public event Action<string>? LineWritten;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Error(Exception ex, string context)
    {
        Write("ERROR", $"{context}：{ex.GetType().Name} {ex.Message}");
        if (ex.StackTrace is not null) Write("ERROR", ex.StackTrace);
    }

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        try
        {
            lock (_lock)
            {
                File.AppendAllText(Path.Combine(_directory, $"{DateTime.Now:yyyyMMdd}.log"),
                    line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 忽略
        }

        try
        {
            LineWritten?.Invoke(line);
        }
        catch
        {
            // 忽略界面异常
        }
    }

    private void Cleanup()
    {
        var limit = DateTime.Now.AddDays(-7);
        foreach (var file in Directory.EnumerateFiles(_directory, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < limit) File.Delete(file);
            }
            catch
            {
                // 忽略
            }
        }
    }
}
