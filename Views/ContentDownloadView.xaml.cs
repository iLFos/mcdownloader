using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using McDownloader.Models;
using McDownloader.Services;
using McDownloader.Utils;

namespace McDownloader.Views;

public partial class ContentDownloadView : UserControl
{
    private const int PageSize = 20;

    private readonly AppHost _host;
    private List<ContentItem> _items = new();
    private List<ContentFile> _files = new();
    private ContentItem? _selected;
    private DownloadJob? _job;
    private int _offset;
    private bool _hasMore;
    private string? _pickedZip;
    private bool _initialized;
    private int _searchRequest;

    public ContentDownloadView(AppHost host)
    {
        _host = host;
        InitializeComponent();

        TypeList.ItemsSource = new[] { "模组", "光影", "资源包", "数据包" };
        TypeList.SelectedIndex = 0;
        GameVersionBox.Items.Add("全部版本");
        GameVersionBox.SelectedIndex = 0;
        Loaded += OnLoaded;
    }

    private string ProjectType => TypeList.SelectedIndex switch
    {
        1 => "shader",
        2 => "resourcepack",
        3 => "datapack",
        _ => "mod"
    };

    private string SortValue => SortBox.SelectedIndex switch
    {
        1 => "downloads",
        2 => "newest",
        3 => "updated",
        _ => "relevance"
    };

    private string? SelectedGameVersion =>
        GameVersionBox.SelectedIndex > 0 ? GameVersionBox.SelectedItem as string : null;

    private string? SelectedLoader()
    {
        if (LoaderBox.SelectedIndex <= 0) return null;
        if (LoaderBox.SelectedItem is not ComboBoxItem item) return null;
        return (item.Content as string)?.ToLowerInvariant();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            var manifest = await _host.Catalog.GetManifestAsync(false, CancellationToken.None);
            foreach (var version in manifest.Where(v => v.Type == "release").Take(150))
                GameVersionBox.Items.Add(version.Id);
        }
        catch
        {
            // 版本清单不可用时仍可搜索，只是无法按版本筛选
        }

        await SearchAsync(false);
    }

    private async Task SearchAsync(bool append)
    {
        var request = ++_searchRequest;
        var type = ProjectType;
        var loader = SelectedLoader();
        if (type is "shader" or "resourcepack" or "datapack") loader = null;

        try
        {
            MoreButton.Visibility = Visibility.Collapsed;
            if (!append) ResultCount.Text = "正在搜索…";

            var (items, total) = await _host.Modrinth.SearchAsync(
                SearchBox.Text, type, SelectedGameVersion, loader, SortValue,
                append ? _offset : 0, PageSize, CancellationToken.None);

            if (request != _searchRequest) return;

            if (append)
            {
                _items.AddRange(items);
                _offset = _items.Count;
            }
            else
            {
                _items = items;
                _offset = items.Count;
            }

            ResultList.ItemsSource = null;
            ResultList.ItemsSource = _items;

            _hasMore = _items.Count < total;
            MoreButton.Visibility = _hasMore ? Visibility.Visible : Visibility.Collapsed;
            ResultCount.Text = _items.Count == 0
                ? "没有找到匹配内容，试试更换关键词或放宽筛选条件"
                : $"共 {total} 个结果，已显示 {_items.Count} 个";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (request != _searchRequest) return;
            ResultCount.Text = "搜索失败：" + ex.Message;
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(false);

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SearchAsync(false);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (_hasMore) _ = SearchAsync(true);
    }

    private void Type_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        LoaderBox.IsEnabled = ProjectType == "mod";
        _ = SearchAsync(false);
    }

    /// <summary>
    /// 取可动画的缩放变换。XAML 模板里的 Freezable 会被 WPF 冻结，
    /// 直接动画会抛“对象已密封或已冻结”，所以在这里按实例新建。
    /// </summary>
    private static ScaleTransform EnsureScale(FrameworkElement element)
    {
        if (element.RenderTransform is ScaleTransform { IsFrozen: false } existing) return existing;

        var scale = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        return scale;
    }

    private void ResultCard_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var scale = EnsureScale(element);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 1.015, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, 1.015, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
    }

    private void ResultCard_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var scale = EnsureScale(element);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1.015, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1.015, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    private async void ResultCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ContentItem item) return;
        await SelectItemAsync(item);
    }

    private async Task SelectItemAsync(ContentItem item)
    {
        _selected = item;
        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;

        DetailTitle.Text = item.Title;
        DetailMeta.Text = $"{item.TypeText} · {item.Author} · {item.DownloadsText}";
        DetailDesc.Text = string.IsNullOrWhiteSpace(item.Description) ? "（该工程暂无简介）" : item.Description;
        DetailPath.Text = $".minecraft\\{ModrinthService.SubFolder(item.ProjectType)}\\";
        DetailIcon.Source = TryLoadImage(item.IconUrl);

        await LoadDetailVersionsAsync(item);
    }

    private async Task LoadDetailVersionsAsync(ContentItem? item = null)
    {
        item ??= _selected;
        if (item is null) return;

        var type = item.ProjectType;
        var loader = type == "mod" ? SelectedLoader() : null;
        var gameVersion = SelectedGameVersion;

        DetailVersions.ItemsSource = null;
        DetailFile.Text = "";
        DetailHint.Text = "正在获取版本列表…";

        try
        {
            var files = await _host.Modrinth.GetVersionsAsync(item.Id, gameVersion, loader, CancellationToken.None);
            if (files.Count == 0 && (gameVersion is not null || loader is not null))
            {
                files = await _host.Modrinth.GetVersionsAsync(item.Id, null, null, CancellationToken.None);
                DetailHint.Text = "当前筛选条件下没有匹配文件，已列出全部版本";
            }
            else
            {
                DetailHint.Text = "";
            }

            _files = files;
            DetailVersions.ItemsSource = files;
            if (files.Count > 0) DetailVersions.SelectedIndex = 0;
            if (files.Count == 0) DetailHint.Text = "该工程暂无可用文件";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DetailHint.Text = "获取版本失败：" + ex.Message;
        }
    }

    private void DetailVersion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DetailVersions.SelectedItem is not ContentFile file)
        {
            DetailFile.Text = "";
            return;
        }

        DetailFile.Text = $"{file.FileName}\n{Format.Bytes(file.Size)}  ·  支持 {file.GameVersions}";
        DownloadButton.Content = _job is { IsFinished: false } ? "取消下载" : "下载到本地";
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_job is { IsFinished: false })
        {
            _host.Queue.Cancel(_job);
            return;
        }

        if (_selected is null || DetailVersions.SelectedItem is not ContentFile file)
        {
            MessageBox.Show("请先选择内容与版本。", "MC 下载器", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var item = _selected;
        var subFolder = ModrinthService.SubFolder(item.ProjectType);
        var fileName = Format.SafeName(file.FileName, "download.jar");
        var target = _host.Workspace.Resolve(subFolder, fileName);

        var job = new DownloadJob(file.FileName, $"{item.Title} → {subFolder}", async (j, ct) =>
        {
            j.SetStage("下载文件…");
            await _host.Downloader.DownloadAllAsync(new[]
            {
                new FileEntry
                {
                    Url = file.Url,
                    TargetPath = target,
                    Hash = file.Sha1,
                    Size = file.Size
                }
            }, j, ct);
            j.AddLog("已保存到 " + target);
        });

        _job = job;
        DownloadProgress.DataContext = job;
        job.PropertyChanged += DownloadJob_Changed;
        _host.Queue.Enqueue(job);
        DownloadButton.Content = "取消下载";
    }

    private void DownloadJob_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DownloadJob.State) || _job is null || !_job.IsFinished) return;
        DownloadButton.Content = "下载到本地";
    }

    private void OpenTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var path = _host.Workspace.Resolve(ModrinthService.SubFolder(_selected.ProjectType));
        _host.OpenInExplorer(path);
    }

    private void OpenMods_Click(object sender, RoutedEventArgs e) => _host.OpenInExplorer(_host.Workspace.ModsDir);

    private void OpenWorldOverlay_Click(object sender, RoutedEventArgs e)
    {
        WorldOverlay.Visibility = Visibility.Visible;
        WorldUrlBox.Focus();
    }

    private void CloseWorldOverlay_Click(object sender, RoutedEventArgs e)
    {
        WorldOverlay.Visibility = Visibility.Collapsed;
        WorldUrlBox.Text = "";
        WorldNameBox.Text = "";
        WorldZipBox.Text = "";
        _pickedZip = null;
    }

    private void BrowseZip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择地图压缩包",
            Filter = "压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        _pickedZip = dialog.FileName;
        WorldZipBox.Text = dialog.FileName;
    }

    private void ImportWorld_Click(object sender, RoutedEventArgs e)
    {
        var url = WorldUrlBox.Text.Trim();
        var zip = _pickedZip;
        if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(zip))
        {
            MessageBox.Show("请填写地图直链，或选择一个本地 zip 文件。", "MC 下载器",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = WorldNameBox.Text.Trim();
        var job = new DownloadJob("导入地图存档", string.IsNullOrEmpty(name) ? url : name, async (j, ct) =>
        {
            if (!string.IsNullOrEmpty(zip))
            {
                var save = _host.Worlds.ImportLocalZip(zip, name, j);
                j.AddLog("存档名称：" + save);
            }
            else
            {
                var save = await _host.Worlds.ImportFromUrlAsync(url, name, j, ct);
                j.AddLog("存档名称：" + save);
            }
        });

        job.PropertyChanged += WorldJob_Changed;
        _host.Queue.Enqueue(job);
        CloseWorldOverlay_Click(this, new RoutedEventArgs());
    }

    private void WorldJob_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DownloadJob.State) || sender is not DownloadJob job || !job.IsFinished) return;
        var message = job.State == DownloadState.Succeeded
            ? "地图已导入到 .minecraft\\saves，可在「下载任务」查看日志。"
            : "地图导入失败：" + job.Error;
        MessageBox.Show(message, "MC 下载器", MessageBoxButton.OK,
            job.State == DownloadState.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private static BitmapImage? TryLoadImage(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(url, UriKind.Absolute);
            bitmap.DecodePixelWidth = 96;
            bitmap.DownloadFailed += (_, _) => { };
            bitmap.EndInit();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
