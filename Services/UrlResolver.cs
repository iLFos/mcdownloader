namespace McDownloader.Services;

/// <summary>下载源：官方、BMCLAPI 国内镜像、MCIM 镜像（额外覆盖 Modrinth API/CDN）。</summary>
public enum DownloadSource
{
    /// <summary>优先官方源，失败回退国内镜像。</summary>
    Official = 0,

    /// <summary>优先 BMCLAPI（版本清单 / 库 / 资源 / Fabric、Forge Maven），失败回退官方。</summary>
    Bmclapi = 1,

    /// <summary>优先 MCIM（Modrinth API 与文件），Mojang 资源仍走 BMCLAPI，失败回退官方。</summary>
    Mcim = 2
}

/// <summary>
/// 下载源改写：把官方地址映射到国内镜像，并按所选源给出候选地址顺序（首选源 → 另一镜像 → 官方）。
/// BMCLAPI 规则对应 PCL 的 DlSourceLauncherOrMetaGet / DlSourceLibraryGet / DlSourceAssetsGet；
/// MCIM 规则对应 ModDownload.cs:2229 DlSourceModGet 与 :2240 DlSourceModDownloadGet。
/// </summary>
public static class UrlResolver
{
    private static readonly (string From, string To)[] BmclapiRules =
    {
        ("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://launcher.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://piston-data.mojang.com", "https://bmclapi2.bangbang93.com"),
        ("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/libraries"),
        ("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets"),
        ("https://maven.fabricmc.net", "https://bmclapi2.bangbang93.com/maven"),
        ("https://maven.minecraftforge.net", "https://bmclapi2.bangbang93.com/maven"),
        ("https://maven.neoforged.net/releases", "https://bmclapi2.bangbang93.com/maven"),
        ("https://maven.quiltmc.org/repository/release", "https://bmclapi2.bangbang93.com/maven"),
        ("https://meta.fabricmc.net", "https://bmclapi2.bangbang93.com/fabric-meta"),
        ("https://meta.quiltmc.org", "https://bmclapi2.bangbang93.com/quilt-meta")
    };

    private static readonly (string From, string To)[] McimRules =
    {
        ("https://api.modrinth.com", "https://mod.mcimirror.top/modrinth"),
        ("https://cdn.modrinth.com", "https://mod.mcimirror.top")
    };

    public static string DisplayName(DownloadSource source) => source switch
    {
        DownloadSource.Official => "官方源",
        DownloadSource.Mcim => "MCIM 镜像",
        _ => "BMCLAPI 镜像"
    };

    /// <summary>按所选源生成候选地址（去重、保持优先级）。</summary>
    public static IReadOnlyList<string> Candidates(string url, DownloadSource source)
    {
        if (string.IsNullOrWhiteSpace(url)) return Array.Empty<string>();

        // BMCLAPI 不代理 launchermeta/piston-meta 下的 /v1/objects（日志配置等），这类地址直接走官方；
        // piston-data 下的 /v1/objects/*/client.jar 镜像有缓存，必须保留镜像候选
        if ((url.StartsWith("https://launchermeta.mojang.com/v1/objects/", StringComparison.OrdinalIgnoreCase) ||
             url.StartsWith("https://piston-meta.mojang.com/v1/objects/", StringComparison.OrdinalIgnoreCase)))
            return new[] { url };

        var list = new List<string>(4);
        void Add(string? candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return;
            if (list.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return;
            list.Add(candidate);
        }

        // 库文件在 BMCLAPI 上有 /libraries 与 /maven 两套路径，逐个兜底
        var bmclapiCandidates = new List<string>();
        if (url.StartsWith("https://libraries.minecraft.net", StringComparison.OrdinalIgnoreCase))
        {
            var relative = url["https://libraries.minecraft.net".Length..];
            bmclapiCandidates.Add("https://bmclapi2.bangbang93.com/libraries" + relative);
            bmclapiCandidates.Add("https://bmclapi2.bangbang93.com/maven" + relative);
        }
        else if (Rewrite(url, BmclapiRules) is { } mirror)
        {
            bmclapiCandidates.Add(mirror);
        }

        var mcim = Rewrite(url, McimRules);

        switch (source)
        {
            case DownloadSource.Official:
                Add(url);
                foreach (var candidate in bmclapiCandidates) Add(candidate);
                Add(mcim);
                break;
            case DownloadSource.Mcim:
                Add(mcim);
                foreach (var candidate in bmclapiCandidates) Add(candidate);
                Add(url);
                break;
            default:
                foreach (var candidate in bmclapiCandidates) Add(candidate);
                Add(mcim);
                Add(url);
                break;
        }

        return list;
    }

    public static string? ToBmclapi(string url) => Rewrite(url, BmclapiRules);

    public static string? ToMcim(string url) => Rewrite(url, McimRules);

    private static string? Rewrite(string url, (string From, string To)[] rules)
    {
        foreach (var (from, to) in rules)
        {
            if (url.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                return to + url[from.Length..];
        }
        return null;
    }
}
