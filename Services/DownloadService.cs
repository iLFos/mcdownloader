using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using McDownloader.Models;
using McDownloader.Utils;

namespace McDownloader.Services;

/// <summary>
/// 批量文件下载：并发限流、聚合进度、失败重试。
/// 参考 PCL 的 LoaderDownload：首批并发下载，失败的文件在最后单独重试一轮，
/// 避免个别镜像冷启动慢就整单失败。
/// </summary>
public sealed class DownloadService
{
    private readonly HttpService _http;
    private readonly ConfigService _config;
    private readonly LogService _log;

    public DownloadService(HttpService http, ConfigService config, LogService log)
    {
        _http = http;
        _config = config;
        _log = log;
    }

    /// <param name="progressFrom">本批文件在整个任务进度中的起点（0~1），避免分批下载时进度条回退。</param>
    /// <param name="progressTo">本批文件在整个任务进度中的终点（0~1）。</param>
    /// <param name="stageLabel">阶段文案前缀，例如「下载游戏文件」「下载资源文件」。</param>
    public async Task DownloadAllAsync(IReadOnlyList<FileEntry> files, DownloadJob job, CancellationToken ct,
        double progressFrom = 0, double progressTo = 1, string stageLabel = "下载文件")
    {
        var list = files
            .GroupBy(f => f.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (list.Count == 0)
        {
            job.SetProgress(progressTo);
            return;
        }

        long totalWeight = list.Sum(f => f.Size > 0 ? f.Size : 1024L * 1024L);
        long doneBytes = 0;
        var completed = 0;
        var total = list.Count;
        var failed = new ConcurrentQueue<FileEntry>();
        var parallel = Math.Clamp(_config.Value.MaxParallel, 1, 16);
        using var gate = new SemaphoreSlim(parallel);
        var stopwatch = Stopwatch.StartNew();
        var lastReport = 0L;

        void Report(long delta)
        {
            Interlocked.Add(ref doneBytes, delta);
            job.AddBytes(delta);
            var elapsed = stopwatch.ElapsedMilliseconds;
            if (elapsed - lastReport < 180) return;
            lastReport = elapsed;
            var ratio = totalWeight > 0 ? (double)Interlocked.Read(ref doneBytes) / totalWeight : 0;
            job.SetProgress(progressFrom + (progressTo - progressFrom) * Math.Min(1, ratio));
        }

        var tasks = list.Select(async file =>
        {
            await gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!HashUtil.Verify(file.TargetPath, file.Hash, file.Size))
                {
                    await _http.DownloadFileAsync(file, Report, ct, job.Track);
                }
                else
                {
                    Interlocked.Add(ref doneBytes, file.Size > 0 ? file.Size : 1024L * 1024L);
                }

                var done = Interlocked.Increment(ref completed);
                job.SetStage($"{stageLabel}（{done}/{total}）");
                var ratioDone = totalWeight > 0 ? (double)Interlocked.Read(ref doneBytes) / totalWeight : 0;
                job.SetProgress(progressFrom + (progressTo - progressFrom) * Math.Min(1, ratioDone));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (file.Required)
                {
                    failed.Enqueue(file);
                    job.AddLog($"暂失败，稍后重试：{Path.GetFileName(file.TargetPath)}（{ex.Message}）");
                }
                else
                {
                    job.AddLog($"跳过可选文件 {Path.GetFileName(file.TargetPath)}：{ex.Message}");
                    Interlocked.Increment(ref completed);
                }
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        // 第二轮：串行重试失败文件，给镜像冷启动留足时间
        if (!failed.IsEmpty)
        {
            var retryList = failed.ToList();
            failed.Clear();
            job.SetStage($"重试未完成文件（{retryList.Count} 个）…");
            job.AddLog($"{stageLabel}：第一轮有 {retryList.Count} 个文件未完成，开始重试");

            foreach (var file in retryList)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!HashUtil.Verify(file.TargetPath, file.Hash, file.Size))
                        await _http.DownloadFileAsync(file, Report, ct, job.Track);

                    var done = Interlocked.Increment(ref completed);
                    job.SetStage($"{stageLabel}（{done}/{total}）");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed.Enqueue(file);
                    job.AddLog($"重试仍失败：{Path.GetFileName(file.TargetPath)}（{ex.Message}）");
                }
            }
        }

        if (!failed.IsEmpty)
        {
            var names = failed.Take(3).Select(f => Path.GetFileName(f.TargetPath));
            throw new IOException(
                $"有 {failed.Count} 个文件下载失败：{string.Join("、", names)}。可在设置中切换下载源后重试。");
        }

        job.SetStage($"{stageLabel}完成（{completed}/{total}）");
        job.SetProgress(progressTo);
        _log.Info($"批量下载完成：{completed} 个文件");
    }
}
