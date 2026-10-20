using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using McDownloader.Models;

namespace McDownloader.Services;

/// <summary>网络层：统一 UA、超时、镜像回退、流式下载与断点式 .part 原子替换。</summary>
public sealed class HttpService : IDisposable
{
    private const int BufferSize = 128 * 1024;
    private readonly HttpClient _client;
    private readonly ConfigService _config;
    private readonly LogService _log;

    public HttpService(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        // Modrinth 要求可识别的 User-Agent
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("McDownloader/1.0 (Minecraft resource downloader)");
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
    }

    public DownloadSource Source => _config.Value.Source;

    public async Task<string> GetStringAsync(string url, CancellationToken ct, int? timeoutSeconds = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds ?? _config.Value.TimeoutSeconds));
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cts.Token);
    }

    /// <summary>依次尝试候选地址（含镜像），任一成功即返回。</summary>
    public async Task<string> GetStringFromAnyAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var url in urls)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return await GetStringAsync(url, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{url} → {ex.Message}");
                    _log.Warn($"请求失败（第 {attempt + 1} 次）：{url} {ex.Message}");
                    if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound }) break; // 换下一个源
                    if (attempt == 0) await Task.Delay(300, ct);
                }
            }
        }
        throw new HttpRequestException("所有下载源均不可用：" + string.Join(" | ", errors.Take(3)));
    }

    public async Task<JsonNode> GetJsonAsync(string url, CancellationToken ct)
    {
        var text = await GetStringFromAnyAsync(UrlResolver.Candidates(url, Source), ct);
        return JsonNode.Parse(text) ?? throw new InvalidDataException("返回内容不是有效 JSON：" + url);
    }

    /// <summary>下载单个文件到目标路径；文件已存在且校验通过时直接返回。</summary>
    public async Task DownloadFileAsync(FileEntry entry, Action<long> onBytes, CancellationToken ct,
        Action<string>? onFileCreated = null)
    {
        if (HashUtil.Verify(entry.TargetPath, entry.Hash, entry.Size)) return;

        var directory = Path.GetDirectoryName(entry.TargetPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        Exception? lastError = null;
        foreach (var url in UrlResolver.Candidates(entry.Url, Source))
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                // 每次尝试独享临时文件，避免被放弃的请求与重试同时写同一文件
                var part = $"{entry.TargetPath}.{Guid.NewGuid().ToString("N")[..8]}.part";
                try
                {
                    var timeout = GetWallClockTimeout(entry);
                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attemptCts.CancelAfter(timeout);
                    var attemptTask = DownloadOnceAsync(url, part, entry, onBytes, attemptCts.Token);

                    // 只给大文件挂 Task.Delay 兜底。资源对象动辄几千个，逐个创建定时器会明显拖慢整体速度
                    if (entry.Size > 2 * 1024 * 1024 || entry.Size <= 0)
                    {
                        var finished = await Task.WhenAny(attemptTask, Task.Delay(timeout, ct));
                        if (finished != attemptTask)
                        {
                            _ = attemptTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                            throw new TimeoutException($"请求超时（{(int)timeout.TotalSeconds} 秒未完成）");
                        }
                    }

                    await attemptTask;
                    onFileCreated?.Invoke(entry.TargetPath);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDelete(part);
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    TryDelete(part);
                    _log.Warn($"文件下载失败（第 {attempt + 1} 次）：{Path.GetFileName(entry.TargetPath)} ← {url} {ex.Message}");
                    if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound }) break; // 换下一个源
                    await Task.Delay(200, ct);
                }
            }
        }

        throw new IOException($"下载失败：{Path.GetFileName(entry.TargetPath)}（{lastError?.Message}）", lastError);
    }

    // 镜像冷启动首次响应经常要 10~25 秒，看门狗必须比它宽松，否则会把正常请求掐断
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 单次尝试的硬超时：按体积估算但不允许无限等待（DNS/连接假死时外层兜底），120~240 秒。
    /// </summary>
    private static TimeSpan GetWallClockTimeout(FileEntry entry)
    {
        var size = entry.Size > 0 ? entry.Size : 8L * 1024 * 1024;
        var seconds = Math.Clamp(size / (300.0 * 1024), 120, 240);
        return TimeSpan.FromSeconds(seconds);
    }

    private async Task DownloadOnceAsync(string url, string part, FileEntry entry, Action<long> onBytes,
        CancellationToken ct)
    {
        // 空闲看门狗：60 秒没有新字节才放弃当前源；外层另有按体积估算的硬超时兜底
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(IdleTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watchdog.Token);
        response.EnsureSuccessStatusCode();

        try
        {
            // 资源对象只有几 KB，按 128KB 每次新分配太浪费；用共享缓冲池降低 GC 压力
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                await using (var source = await response.Content.ReadAsStreamAsync(watchdog.Token))
                await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None,
                                 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), watchdog.Token);
                        if (read <= 0) break;
                        await target.WriteAsync(buffer.AsMemory(0, read), watchdog.Token);
                        onBytes(read);
                        watchdog.CancelAfter(IdleTimeout);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (!HashUtil.Verify(part, entry.Hash, entry.Size))
                throw new InvalidDataException("文件校验失败（哈希或大小不匹配）");

            // 取消后不要再把文件落到正式路径，否则清理完又会“复活”
            ct.ThrowIfCancellationRequested();

            File.Move(part, entry.TargetPath, true);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略
        }
    }

    public void Dispose() => _client.Dispose();
}
