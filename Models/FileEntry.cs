namespace McDownloader.Models;

/// <summary>一个待下载文件的描述（唯一事实来源，供下载引擎消费）。</summary>
public sealed class FileEntry
{
    public required string Url { get; init; }
    public required string TargetPath { get; init; }
    /// <summary>sha1 / md5 / sha256，按长度自适应；为空则只校验大小或直接跳过。</summary>
    public string? Hash { get; init; }
    public long Size { get; init; }
    /// <summary>false 表示该文件失败不中断整体任务（如可选的资源文件）。</summary>
    public bool Required { get; init; } = true;
}
