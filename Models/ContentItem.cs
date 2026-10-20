using McDownloader.Utils;

namespace McDownloader.Models;

/// <summary>Modrinth 工程（模组 / 光影 / 资源包 / 数据包）。</summary>
public sealed class ContentItem
{
    public string Id { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public string ProjectType { get; init; } = "mod";
    public long Downloads { get; init; }
    public string LatestVersion { get; init; } = "";

    public string DownloadsText => Format.Count(Downloads) + " 次下载";
    public string TypeText => ProjectType switch
    {
        "mod" => "模组",
        "shader" => "光影",
        "resourcepack" => "资源包",
        "datapack" => "数据包",
        "modpack" => "整合包",
        _ => ProjectType
    };
}

/// <summary>Modrinth 工程下的某个版本及其文件。</summary>
public sealed class ContentFile
{
    public string VersionId { get; init; } = "";
    public string VersionNumber { get; init; } = "";
    public string Name { get; init; } = "";
    public string VersionType { get; init; } = "release";
    public string FileName { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public long Size { get; init; }
    public string GameVersions { get; init; } = "";
    public string Loaders { get; init; } = "";

    public string Display => $"{VersionNumber}  ·  {VersionType}  ·  {Format.Bytes(Size)}";
    public string Detail => $"{FileName}    {Format.Bytes(Size)}{(string.IsNullOrEmpty(GameVersions) ? "" : "    " + GameVersions)}";
}
