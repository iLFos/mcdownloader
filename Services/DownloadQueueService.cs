using System.Collections.ObjectModel;
using McDownloader.Models;
using McDownloader.Utils;

namespace McDownloader.Services;

/// <summary>任务队列：串行执行任务，任务内部再并发下载文件。</summary>
public sealed class DownloadQueueService
{
    private readonly object _lock = new();
    private readonly Queue<DownloadJob> _pending = new();
    private readonly LogService _log;
    private readonly ArtifactCleaner _cleaner;
    private bool _pumping;

    public DownloadQueueService(LogService log, ArtifactCleaner cleaner)
    {
        _log = log;
        _cleaner = cleaner;
    }

    public ObservableCollection<DownloadJob> Jobs { get; } = new();

    public event Action? Changed;

    public DownloadJob Enqueue(DownloadJob job)
    {
        Jobs.Add(job);
        lock (_lock) _pending.Enqueue(job);
        _log.Info($"任务入队：{job.Title}");
        Changed?.Invoke();
        Pump();
        return job;
    }

    public void Cancel(DownloadJob job)
    {
        if (job.State == DownloadState.Pending)
        {
            job.State = DownloadState.Canceled;
            job.SetStage("已取消");
            job.Finish();
        }
        else if (job.State == DownloadState.Running)
        {
            job.SetStage("正在取消…");
            job.Cts.Cancel();
        }
        Changed?.Invoke();
    }

    public void CancelAll()
    {
        foreach (var job in Jobs.ToList())
        {
            if (job.IsFinished) continue;
            Cancel(job);
        }
    }

    public void ClearFinished()
    {
        foreach (var job in Jobs.Where(j => j.IsFinished).ToList()) Jobs.Remove(job);
        Changed?.Invoke();
    }

    public DownloadJob? RunningJob => Jobs.FirstOrDefault(j => j.State == DownloadState.Running);

    private async void Pump()
    {
        lock (_lock)
        {
            if (_pumping) return;
            _pumping = true;
        }

        while (true)
        {
            DownloadJob job;
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    _pumping = false;
                    return;
                }
                job = _pending.Dequeue();
            }

            if (job.State == DownloadState.Canceled)
            {
                Changed?.Invoke();
                continue;
            }

            job.StartedAt = DateTime.Now;
            job.State = DownloadState.Running;
            job.SetStage("准备中…");
            Created?.Invoke(job);
            Changed?.Invoke();

            try
            {
                await Task.Run(async () => await job.Work(job, job.Cts.Token), job.Cts.Token);
                job.SetProgress(1);
                job.State = DownloadState.Succeeded;
                job.SetStage("已完成");
                _log.Info($"任务完成：{job.Title}");
            }
            catch (OperationCanceledException)
            {
                job.State = DownloadState.Canceled;
                job.SetStage("已取消");
                job.AddLog("任务被用户取消，正在清理本次下载的文件…");
                _log.Warn($"任务取消：{job.Title}");
                await CleanupArtifactsAsync(job);
            }
            catch (Exception ex)
            {
                job.State = DownloadState.Failed;
                job.SetStage("失败");
                job.Fail(ex.Message);
                _log.Error(ex, $"任务失败：{job.Title}");
            }
            finally
            {
                job.Finish();
                Changed?.Invoke();
            }
        }
    }

    /// <summary>取消后清理本次任务写入的文件与新建版本目录（放到线程池，避免几千个文件阻塞界面）。</summary>
    private async Task CleanupArtifactsAsync(DownloadJob job)
    {
        if (job.CreatedFileCount == 0 && job.InstanceDirectory is null) return;

        job.SetStage("正在清理本次下载的文件…");
        try
        {
            var (files, bytes, versionRemoved) = await Task.Run(() => _cleaner.Clean(job));
            job.AddLog($"已清理本次下载：{files} 个文件（{Format.Bytes(bytes)}）" +
                       (versionRemoved ? "，并移除未完成的版本目录" : ""));
            job.SetStage("已取消并清理");
        }
        catch (Exception ex)
        {
            job.AddLog("清理失败：" + ex.Message);
            _log.Warn($"清理取消任务失败：{job.Title} {ex.Message}");
        }
    }

    /// <summary>任务开始执行时触发，便于界面滚动到最新任务。</summary>
    public event Action<DownloadJob>? Created;
}
