using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using McDownloader.Models;
using McDownloader.Services;

namespace McDownloader.Services;

/// <summary>config.json 读写（原子写，写入失败不影响主流程）。</summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;

    public ConfigService(WorkspaceService workspace)
    {
        _path = workspace.Resolve("mcdownloader", "config.json");
        Value = Load();
    }

    public AppConfig Value { get; private set; }

    private AppConfig Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppConfig();

            var json = File.ReadAllText(_path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
            if (cfg is null) return new AppConfig();

            // 兼容旧版配置（只有布尔 MirrorFirst）
            if (JsonNode.Parse(json) is JsonObject node && node["Source"] is null &&
                node["MirrorFirst"] is JsonValue legacy && legacy.TryGetValue<bool>(out var mirrorFirst))
            {
                cfg.Source = mirrorFirst ? DownloadSource.Bmclapi : DownloadSource.Official;
            }

            return Normalize(cfg);
        }
        catch
        {
            // 配置损坏时回落到默认值，避免程序无法启动
            return new AppConfig();
        }
    }

    public void Save()
    {
        try
        {
            Value = Normalize(Value);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Value, Options));
            File.Move(temp, _path, true);
        }
        catch
        {
            // 忽略写入失败
        }
    }

    private static AppConfig Normalize(AppConfig cfg)
    {
        cfg.MaxParallel = Math.Clamp(cfg.MaxParallel, 1, 16);
        // 旧配置里的 15/30 秒对镜像冷启动来说太短，统一抬到 45 秒起
        cfg.TimeoutSeconds = Math.Clamp(cfg.TimeoutSeconds, 45, 300);
        if (!Enum.IsDefined(cfg.Source)) cfg.Source = DownloadSource.Bmclapi;
        return cfg;
    }
}
