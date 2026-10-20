using System.IO;

namespace McDownloader.Services;

/// <summary>服务容器：整个程序只创建一个实例，视图从中取服务。</summary>
public sealed class AppHost
{
    private AppHost()
    {
        Workspace = new WorkspaceService();
        Workspace.EnsureLayout();
        Workspace.CleanupStaleParts();
        Log = new LogService(Workspace);
        Config = new ConfigService(Workspace);
        Http = new HttpService(Config, Log);
        Downloader = new DownloadService(Http, Config, Log);
        Cleaner = new ArtifactCleaner(Workspace, Log);
        Queue = new DownloadQueueService(Log, Cleaner);
        Catalog = new VersionCatalogService(Http, Workspace, Log);
        Java = new JavaScanner(Log);
        Vanilla = new VanillaInstaller(Workspace, Downloader, Catalog, Log);
        Fabric = new FabricInstaller(Workspace, Http, Downloader, Catalog, Vanilla, Log);
        Forge = new ForgeInstaller(Workspace, Downloader, Config, Java, Log);
        Modrinth = new ModrinthService(Http, Log);
        Worlds = new WorldImporter(Workspace, Downloader, Log);
    }

    public static AppHost Current { get; private set; } = null!;

    public WorkspaceService Workspace { get; }
    public ConfigService Config { get; }
    public LogService Log { get; }
    public HttpService Http { get; }
    public DownloadService Downloader { get; }
    public ArtifactCleaner Cleaner { get; }
    public DownloadQueueService Queue { get; }
    public VersionCatalogService Catalog { get; }
    public JavaScanner Java { get; }
    public VanillaInstaller Vanilla { get; }
    public FabricInstaller Fabric { get; }
    public ForgeInstaller Forge { get; }
    public ModrinthService Modrinth { get; }
    public WorldImporter Worlds { get; }

    public static AppHost Create()
    {
        Current ??= new AppHost();
        return Current;
    }

    public string MinecraftDir => Workspace.MinecraftDir;

    public void OpenInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开目录失败：{ex.Message}");
        }
    }
}
