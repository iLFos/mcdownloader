using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using McDownloader.Models;
using McDownloader.Services;
using McDownloader.Utils;

namespace McDownloader.Views;

public partial class SettingsView : UserControl
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _hideTimer;
    private bool _loading;

    public SettingsView(AppHost host)
    {
        _host = host;
        _loading = true;
        InitializeComponent();

        ParallelBox.ItemsSource = new[] { 2, 4, 6, 8, 12, 16 };
        TimeoutBox.ItemsSource = new[] { 45, 60, 90, 120 };
        LoadSettings();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            SavedHint.Visibility = Visibility.Collapsed;
        };

        _loading = false;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        WorkspaceText.Text = _host.Workspace.MinecraftDir;
        var free = _host.Workspace.GetFreeSpace();
        FreeSpaceText.Text = free > 0 ? $"磁盘剩余空间 {Format.Bytes(free)}" : "";
        AboutText.Text =
            "MC 下载器 1.0.0 —— 轻量化的《我的世界》资源下载工具。\n" +
            "支持原版 / Fabric / Quilt / Forge / NeoForge 版本安装，Modrinth 模组与光影、资源包、数据包下载，地图 zip 导入。\n" +
            "下载逻辑参考 PCL2（Plain Craft Launcher 2）的公开源码实现：版本清单与镜像改写、库与资源校验、加载器安装流程。\n" +
            "本程序不含游戏启动与账户功能，下载完成后可直接用任意启动器打开 .minecraft 目录。";

        await RefreshJavaAsync(false);
    }

    private void LoadSettings()
    {
        var cfg = _host.Config.Value;
        SourceBox.SelectedIndex = (int)cfg.Source;
        UpdateSourceHint();
        ParallelBox.SelectedItem = cfg.MaxParallel;
        if (ParallelBox.SelectedItem is null) ParallelBox.SelectedIndex = 2;
        TimeoutBox.SelectedItem = cfg.TimeoutSeconds;
        if (TimeoutBox.SelectedItem is null) TimeoutBox.SelectedIndex = 1;
        AssetsBox.IsChecked = cfg.DownloadAssets;
        JavaPathText.Text = string.IsNullOrWhiteSpace(cfg.JavaPath) ? "" : "已指定：" + cfg.JavaPath;
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateSourceHint();
        Save();
    }

    private void UpdateSourceHint()
    {
        if (SourceHint is null) return;
        SourceHint.Text = SourceBox.SelectedIndex switch
        {
            0 => "版本清单、库文件与资源优先直连 Mojang；失败时自动改用 BMCLAPI，速度可能较慢。",
            2 => "Modrinth API 与模组文件优先走 MCIM（mod.mcimirror.top），Mojang 资源仍走 BMCLAPI。",
            _ => "版本清单、库文件、资源与各加载器 Maven 优先走 BMCLAPI，失败时回退官方。"
        };
    }

    private void Save()
    {
        var cfg = _host.Config.Value;
        cfg.Source = (DownloadSource)Math.Clamp(SourceBox.SelectedIndex, 0, 2);
        cfg.MaxParallel = ParallelBox.SelectedItem is int parallel ? parallel : 8;
        cfg.TimeoutSeconds = TimeoutBox.SelectedItem is int timeout ? timeout : 30;
        cfg.DownloadAssets = AssetsBox.IsChecked == true;
        _host.Config.Save();

        SavedHint.Visibility = Visibility.Visible;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private async Task RefreshJavaAsync(bool force)
    {
        JavaHint.Text = "正在扫描本机 Java…";
        try
        {
            var runtimes = await _host.Java.ScanAsync(force, CancellationToken.None);
            JavaList.ItemsSource = runtimes;
            JavaHint.Text = runtimes.Count == 0
                ? "未找到 Java。Forge / NeoForge 安装需要 Java 17 或更高版本，请安装后点击「重新扫描」。"
                : $"共找到 {runtimes.Count} 个 Java 运行时，Forge/NeoForge 安装会自动选用满足版本要求的那个。";
        }
        catch (Exception ex)
        {
            JavaHint.Text = "扫描失败：" + ex.Message;
        }
    }

    private async void ScanJava_Click(object sender, RoutedEventArgs e) => await RefreshJavaAsync(true);

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 java.exe",
            Filter = "Java 可执行文件 (java.exe)|java.exe|可执行文件 (*.exe)|*.exe"
        };
        if (dialog.ShowDialog() != true) return;

        _host.Config.Value.JavaPath = dialog.FileName;
        _host.Config.Save();
        JavaPathText.Text = "已指定：" + dialog.FileName;
    }

    private void ClearJava_Click(object sender, RoutedEventArgs e)
    {
        _host.Config.Value.JavaPath = "";
        _host.Config.Save();
        JavaPathText.Text = "";
    }

    private void OpenMc_Click(object sender, RoutedEventArgs e) => _host.OpenInExplorer(_host.Workspace.MinecraftDir);

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        var path = _host.Workspace.Resolve("mcdownloader", "config.json");
        if (!File.Exists(path)) _host.Config.Save();
        _host.OpenInExplorer(Path.GetDirectoryName(path)!);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => _host.OpenInExplorer(_host.Workspace.LogsDir);

    private void OpenCache_Click(object sender, RoutedEventArgs e) => _host.OpenInExplorer(_host.Workspace.CacheDir);
}
