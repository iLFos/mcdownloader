using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using McDownloader.Models;
using McDownloader.Services;
using McDownloader.Utils;

namespace McDownloader.Views;

public partial class TasksView : UserControl
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _timer;

    public TasksView(AppHost host)
    {
        _host = host;
        InitializeComponent();

        JobList.ItemsSource = _host.Queue.Jobs;
        _host.Queue.Changed += RefreshStats;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshStats();
        _timer.Start();
        Loaded += (_, _) => RefreshStats();
        Unloaded += (_, _) => _timer.Stop();

        RefreshStats();
        SelectLatest();
    }

    private void RefreshStats()
    {
        var jobs = _host.Queue.Jobs;
        RunningCount.Text = jobs.Count(j => j.State == DownloadState.Running).ToString();
        SuccessCount.Text = jobs.Count(j => j.State == DownloadState.Succeeded).ToString();
        FailedCount.Text = jobs.Count(j => j.State == DownloadState.Failed).ToString();

        var speed = jobs.Where(j => j.State == DownloadState.Running).Sum(j => j.Speed);
        TotalSpeed.Text = speed > 0 ? Format.Speed(speed) : "—";

        EmptyHint.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (JobList.SelectedItem is null) SelectLatest();
    }

    private void SelectLatest()
    {
        if (_host.Queue.Jobs.Count == 0) return;
        JobList.SelectedItem = _host.Queue.Jobs[^1];
    }

    private void JobList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JobList.SelectedItem is not DownloadJob job)
        {
            LogList.ItemsSource = null;
            LogTitle.Text = "任务日志";
            return;
        }

        LogList.ItemsSource = job.LogLines;
        LogTitle.Text = "任务日志 · " + job.Title;
        job.LogLines.CollectionChanged -= OnLogsChanged;
        job.LogLines.CollectionChanged += OnLogsChanged;
        ScrollLogToEnd();
    }

    private void OnLogsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        ScrollLogToEnd();

    private void ScrollLogToEnd()
    {
        if (LogList.Items.Count == 0) return;
        try
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
        catch
        {
            // 忽略
        }
    }

    private void CancelJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is DownloadJob job)
            _host.Queue.Cancel(job);
    }

    private void CancelAll_Click(object sender, RoutedEventArgs e) => _host.Queue.CancelAll();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _host.Queue.ClearFinished();
        RefreshStats();
    }

    private void OpenMc_Click(object sender, RoutedEventArgs e) => _host.OpenInExplorer(_host.Workspace.MinecraftDir);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => _host.OpenInExplorer(_host.Workspace.LogsDir);
}
