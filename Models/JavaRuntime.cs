namespace McDownloader.Models;

/// <summary>探测到的本机 Java 运行时。</summary>
public sealed class JavaRuntime
{
    public string Path { get; init; } = "";
    public int MajorVersion { get; init; }
    public string Vendor { get; init; } = "";
    public bool Is64Bit { get; init; } = true;
    public string Source { get; init; } = "";

    public string Display => $"Java {MajorVersion}  ·  {(Is64Bit ? "64 位" : "32 位")}  ·  {Vendor}  —  {Path}";
}
