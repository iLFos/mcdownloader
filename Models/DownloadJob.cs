using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using McDownloader.Utils;

namespace McDownloader.Models;

public enum DownloadState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Canceled
}

/// <summary>一条下载任务：承载阶段文案、进度、速度与尾部日志，供界面临时观察。</summary>
public sealed class DownloadJob : INotifyPropertyChanged
{
    private readonly Stopwatch _speedWatch = Stopwatch.StartNew();
    private readonly object _speedLock = new();
    private long _windowBytes;
    private long _totalBytes;
    private string _stage = "等待开始";
    private double _progress;
    private long _speed;
    private DownloadState _state = DownloadState.Pending;
    private string _error = "";

    public DownloadJob(string title, string subtitle, Func<DownloadJob, CancellationToken, Task> work)
    {
        Title = title;
        Subtitle = subtitle;
        Work = work;
    }

    public string Title { get; }
    public string Subtitle { get; }

    /// <summary>本次任务实际写入的文件（取消时据此回滚，不动用户已有文件）。</summary>
    public ConcurrentDictionary<string, byte> CreatedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本次任务新建的版本目录（取消时整体删除）；为 null 表示目录原本就存在。</summary>
    public string? InstanceDirectory { get; set; }

    public void Track(string path)
    {
        if (!string.IsNullOrEmpty(path)) CreatedFiles.TryAdd(path, 0);
    }

    public int CreatedFileCount => CreatedFiles.Count;
    public Func<DownloadJob, CancellationToken, Task> Work { get; }
    public CancellationTokenSource Cts { get; } = new();
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }
    public ObservableCollection<string> LogLines { get; } = new();
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public string Stage
    {
        get => _stage;
        private set => Set(ref _stage, value);
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (Set(ref _progress, value))
            {
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(IsIndeterminate));
            }
        }
    }

    public long Speed
    {
        get => _speed;
        private set
        {
            if (Set(ref _speed, value)) OnPropertyChanged(nameof(SpeedText));
        }
    }

    public DownloadState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsFinished));
        }
    }

    public string Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public string ProgressText => Progress >= 1 ? "100%" : $"{Progress * 100:0}%";
    public string SpeedText => Speed > 0 ? Format.Bytes(Speed) + "/s" : "";
    public bool IsIndeterminate => Progress <= 0 && State == DownloadState.Running;
    public bool IsRunning => State == DownloadState.Running;
    public bool IsFinished => State is DownloadState.Succeeded or DownloadState.Failed or DownloadState.Canceled;

    public string StateText => State switch
    {
        DownloadState.Pending => "排队中",
        DownloadState.Running => "进行中",
        DownloadState.Succeeded => "已完成",
        DownloadState.Failed => "失败",
        DownloadState.Canceled => "已取消",
        _ => ""
    };

    public void SetStage(string stage)
    {
        RunOnUi(() => Stage = stage);
    }

    public void SetProgress(double value)
    {
        value = Math.Clamp(value, 0, 1);
        RunOnUi(() => Progress = value);
    }

    public void AddBytes(long count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _windowBytes, count);
        Interlocked.Add(ref _totalBytes, count);
        lock (_speedLock)
        {
            if (_speedWatch.ElapsedMilliseconds < 400) return;
            var window = Interlocked.Exchange(ref _windowBytes, 0);
            var seconds = Math.Max(0.2, _speedWatch.Elapsed.TotalSeconds);
            var speed = (long)(window / seconds);
            _speedWatch.Restart();
            RunOnUi(() => Speed = speed);
        }
    }

    public void AddLog(string line)
    {
        var text = $"[{DateTime.Now:HH:mm:ss}] {line}";
        RunOnUi(() =>
        {
            LogLines.Add(text);
            while (LogLines.Count > 200) LogLines.RemoveAt(0);
        });
    }

    public void Fail(string message)
    {
        Error = message;
        AddLog("错误：" + message);
    }

    public void Finish()
    {
        FinishedAt = DateTime.Now;
        lock (_speedLock) _speedWatch.Stop();
        RunOnUi(() => Speed = 0);
    }

    private static void RunOnUi(Action action)
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.BeginInvoke(action);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name)
    {
        var handler = PropertyChanged;
        if (handler is null) return;
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) handler(this, new PropertyChangedEventArgs(name));
        else app.Dispatcher.BeginInvoke(() => handler(this, new PropertyChangedEventArgs(name)));
    }
}
