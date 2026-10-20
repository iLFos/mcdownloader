namespace McDownloader.Models;

/// <summary>版本清单中的一项。</summary>
public sealed class GameVersion
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "release";
    public DateTimeOffset ReleaseTime { get; init; }
    public string Url { get; init; } = "";
    public bool IsInstalled { get; set; }

    public string TypeText => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古 Beta",
        "old_alpha" => "远古 Alpha",
        _ => Type
    };

    public string ReleaseText => ReleaseTime == default ? "" : ReleaseTime.LocalDateTime.ToString("yyyy-MM-dd");

    public string InstalledText => IsInstalled ? "已安装" : "";
}
