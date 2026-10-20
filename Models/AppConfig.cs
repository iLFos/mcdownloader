namespace McDownloader.Models;

/// <summary>持久化到 .minecraft/mcdownloader/config.json 的配置。</summary>
public sealed class AppConfig
{
    /// <summary>下载源：官方 / BMCLAPI / MCIM，失败会自动回退其它源。</summary>
    public Services.DownloadSource Source { get; set; } = Services.DownloadSource.Bmclapi;

    /// <summary>同时下载的文件数量（资源文件数量多，适当提高可明显加速）。</summary>
    public int MaxParallel { get; set; } = 8;

    /// <summary>是否下载资源文件（assets，体积较大）。</summary>
    public bool DownloadAssets { get; set; } = true;

    /// <summary>手动指定的 Java 路径，为空则使用扫描结果。</summary>
    public string JavaPath { get; set; } = "";

    /// <summary>单次请求超时（秒）。镜像首次响应可能要十几秒，默认放宽到 45 秒。</summary>
    public int TimeoutSeconds { get; set; } = 45;

    /// <summary>上次选择的加载器。</summary>
    public string LastLoader { get; set; } = "Vanilla";

    /// <summary>上次使用的实例名。</summary>
    public string LastInstanceName { get; set; } = "";
}
