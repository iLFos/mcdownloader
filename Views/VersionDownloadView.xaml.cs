using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using McDownloader.Models;
using McDownloader.Services;
using McDownloader.Utils;

namespace McDownloader.Views;

public partial class VersionDownloadView : UserControl
{
    private readonly AppHost _host;
    private List<GameVersion> _versions = new();
    private DownloadJob? _job;
    private int _loaderRequest;

    public VersionDownloadView(AppHost host)
    {
        _host = host;
        InitializeComponent();

        LoaderList.ItemsSource = new[] { "原版", "Fabric", "Quilt", "Forge", "NeoForge" };
        LoaderList.SelectedIndex = (int)ParseLoader(host.Config.Value.LastLoader);
        AssetsBox.IsChecked = host.Config.Value.DownloadAssets;
        LoaderVersionBox.SelectionChanged += (_, _) => UpdateName();
        Loaded += OnLoaded;
    }

    /// <summary>把右侧安装面板复位到顶部（供窗口显示后调用，避免焦点滚动导致顶部被裁掉）。</summary>
    public void ResetScroll() => InstallScroll.ScrollToTop();

    private LoaderKind CurrentLoader => (LoaderKind)Math.Max(0, LoaderList.SelectedIndex);
    private GameVersion? Selected => VersionList.SelectedItem as GameVersion;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 小窗口下右侧面板可能被焦点自动滚动，这里复位到顶部
        InstallScroll.ScrollToTop();
        if (_versions.Count > 0) return;
        await LoadManifestAsync(false);

        // 布局完成、焦点稳定后再复位一次，避免顶部被裁掉
        await Task.Delay(400);
        InstallScroll.ScrollToTop();
    }

    private async Task LoadManifestAsync(bool force)
    {
        try
        {
            CountText.Text = "正在获取版本清单…";
            var list = await _host.Catalog.GetManifestAsync(force, CancellationToken.None);
            _versions = list;
            ApplyFilter();
            // 首次加载默认选中最新的正式版，而不是列表首位的快照
            if (!force && (VersionList.SelectedItem is not GameVersion current || current.Type != "release"))
            {
                var release = VersionList.Items.OfType<GameVersion>().FirstOrDefault(v => v.Type == "release");
                if (release is not null)
                {
                    VersionList.SelectedItem = release;
                    VersionList.ScrollIntoView(release);
                }
            }
        }
        catch (Exception ex)
        {
            CountText.Text = "获取失败";
            MessageBox.Show("获取版本清单失败：\n\n" + ex.Message + "\n\n可在「设置」中切换下载源后重试。",
                "MC 下载器", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<GameVersion> query = _versions;
        switch (TypeFilter.SelectedIndex)
        {
            case 1:
                query = query.Where(v => v.Type == "release");
                break;
            case 2:
                query = query.Where(v => v.Type == "snapshot");
                break;
        }

        var keyword = SearchBox.Text.Trim();
        if (keyword.Length > 0)
            query = query.Where(v => v.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase));

        var list = query.ToList();
        VersionList.ItemsSource = list;
        CountText.Text = $"共 {list.Count} 个版本 · 已安装 {list.Count(v => v.IsInstalled)} 个";
        if (list.Count > 0 && VersionList.SelectedIndex < 0) VersionList.SelectedIndex = 0;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (VersionList is not null) ApplyFilter();
    }

    private void TypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionList is not null) ApplyFilter();
    }

    private void Version_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var version = Selected;
        if (version is null) return;

        SelectedVersionText.Text = version.Id;
        SelectedVersionTypeText.Text = $"{version.TypeText} · 发布于 {version.ReleaseText}";
        UpdateName();
        _ = ReloadLoaderVersionsAsync();
    }

    private async void Loader_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LoaderVersionBox is null) return;
        _host.Config.Value.LastLoader = CurrentLoader.ToString();
        await ReloadLoaderVersionsAsync();
    }

    private async Task ReloadLoaderVersionsAsync()
    {
        var request = ++_loaderRequest;
        var kind = CurrentLoader;
        var version = Selected;

        LoaderVersionBox.ItemsSource = null;
        if (kind == LoaderKind.Vanilla)
        {
            LoaderVersionBox.IsEnabled = false;
            LoaderHint.Text = "原版直接下载官方客户端，无需加载器";
            UpdateName();
            return;
        }

        LoaderVersionBox.IsEnabled = true;
        if (version is null) return;

        LoaderHint.Text = "正在获取加载器版本…";
        try
        {
            var list = await _host.Catalog.GetLoaderVersionsAsync(kind, version.Id, CancellationToken.None);
            if (request != _loaderRequest) return;

            LoaderVersionBox.ItemsSource = list;
            if (list.Count > 0) LoaderVersionBox.SelectedIndex = 0;
            LoaderHint.Text = list.Count == 0
                ? "该游戏版本暂不支持此加载器，请换一个版本"
                : $"共 {list.Count} 个可用版本，默认选中最新的";
            UpdateName();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (request != _loaderRequest) return;
            LoaderHint.Text = "获取加载器版本失败：" + ex.Message;
        }
    }

    private void UpdateName()
    {
        var kind = CurrentLoader;
        var version = Selected;
        if (version is null)
        {
            TargetPathText.Text = "";
            return;
        }

        var loaderVersion = LoaderVersionBox.SelectedItem as string;
        if (kind == LoaderKind.Vanilla)
        {
            NameBox.IsEnabled = true;
            NameBox.Text = version.Id;
        }
        else if (kind is LoaderKind.Fabric or LoaderKind.Quilt)
        {
            NameBox.IsEnabled = true;
            if (!string.IsNullOrEmpty(loaderVersion))
                NameBox.Text = VersionCatalogService.BuildInstanceName(kind, version.Id, loaderVersion);
        }
        else
        {
            NameBox.IsEnabled = false;
            NameBox.Text = "（由安装器自动命名）";
        }

        UpdateTargetPath();
    }

    private void Name_TextChanged(object sender, TextChangedEventArgs e) => UpdateTargetPath();

    private void UpdateTargetPath()
    {
        var kind = CurrentLoader;
        TargetPathText.Text = kind is LoaderKind.Forge or LoaderKind.NeoForge
            ? @".minecraft\versions\（安装器自动创建，如 1.20.1-forge-47.2.0）"
            : @".minecraft\versions\" + Format.SafeName(NameBox.Text);
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_job is { IsFinished: false })
        {
            _host.Queue.Cancel(_job);
            return;
        }

        var version = Selected;
        if (version is null)
        {
            MessageBox.Show("请先在左侧选择一个游戏版本。", "MC 下载器", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var kind = CurrentLoader;
        var loaderVersion = LoaderVersionBox.SelectedItem as string;
        if (kind != LoaderKind.Vanilla && string.IsNullOrWhiteSpace(loaderVersion))
        {
            MessageBox.Show("请选择加载器版本。", "MC 下载器", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var downloadAssets = AssetsBox.IsChecked == true;
        var name = NameBox.Text.Trim();
        if (kind is LoaderKind.Vanilla or LoaderKind.Fabric or LoaderKind.Quilt)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("请填写安装名称。", "MC 下载器", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var target = Path.Combine(_host.Workspace.VersionsDir, Format.SafeName(name));
            if (Directory.Exists(target))
            {
                var choice = MessageBox.Show($"versions\\{name} 已存在，继续将覆盖同名文件，是否继续？",
                    "确认覆盖", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (choice != MessageBoxResult.Yes) return;
            }
        }
        else
        {
            name = $"{version.Id} + {LoaderText(kind)}";
        }

        var title = kind == LoaderKind.Vanilla
            ? version.Id
            : $"{version.Id}  ·  {LoaderText(kind)} {loaderVersion}";

        var job = new DownloadJob(title, name, async (j, ct) =>
        {
            switch (kind)
            {
                case LoaderKind.Vanilla:
                    await _host.Vanilla.InstallAsync(version.Id, name, downloadAssets, j, ct);
                    break;
                case LoaderKind.Fabric:
                case LoaderKind.Quilt:
                    await _host.Fabric.InstallAsync(kind, version.Id, loaderVersion!, name, downloadAssets, j, ct);
                    break;
                default:
                    await _host.Forge.InstallAsync(kind, version.Id, loaderVersion!, j, ct);
                    break;
            }
        });

        _job = job;
        ProgressPanel.DataContext = job;
        LogList.DataContext = job;
        job.PropertyChanged += Job_PropertyChanged;
        job.LogLines.CollectionChanged += (_, _) => ScrollLogToEnd();

        _host.Queue.Enqueue(job);
        InstallButton.Content = "取消任务";
        LogList.Visibility = Visibility.Visible;

        _host.Config.Value.LastLoader = kind.ToString();
        _host.Config.Value.DownloadAssets = downloadAssets;
        _host.Config.Save();
    }

    private void ScrollLogToEnd()
    {
        if (LogList.Items.Count == 0) return;
        try
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
        catch
        {
            // 忽略滚动异常
        }
    }

    private void Job_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DownloadJob.State) || _job is null) return;
        if (!_job.IsFinished) return;

        InstallButton.Content = "开始下载";
        RefreshInstalled();
    }

    private void RefreshInstalled()
    {
        foreach (var version in _versions) version.IsInstalled = _host.Catalog.IsInstalled(version.Id);
        VersionList.Items.Refresh();
        CountText.Text = $"共 {VersionList.Items.Count} 个版本 · 已安装 {_versions.Count(v => v.IsInstalled)} 个";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadManifestAsync(true);

    private void OpenMc_Click(object sender, RoutedEventArgs e) => _host.OpenInExplorer(_host.Workspace.MinecraftDir);

    private static LoaderKind ParseLoader(string text) =>
        Enum.TryParse<LoaderKind>(text, true, out var kind) ? kind : LoaderKind.Vanilla;

    private static string LoaderText(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => "Fabric",
        LoaderKind.Quilt => "Quilt",
        LoaderKind.Forge => "Forge",
        LoaderKind.NeoForge => "NeoForge",
        _ => "原版"
    };
}
