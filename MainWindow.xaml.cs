using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using McDownloader.Models;
using McDownloader.Services;
using McDownloader.Utils;
using McDownloader.Views;

namespace McDownloader;

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _statusTimer;

    private VersionDownloadView? _versionView;
    private ContentDownloadView? _contentView;
    private TasksView? _tasksView;
    private SettingsView? _settingsView;

    public MainWindow()
    {
        InitializeComponent();

        ApplyAdaptiveSize();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnWindowLoaded;
        StateChanged += (_, _) => UpdateMaximizeGlyph();

        _host = AppHost.Current;
        NavList.ItemsSource = new ObservableCollection<NavEntry>
        {
            new("\uE896", "版本下载"),
            new("\uE8B7", "内容下载"),
            new("\uE81C", "下载任务"),
            new("\uE713", "设置")
        };
        NavList.SelectedIndex = 0;

        WorkspaceText.Text = _host.Workspace.MinecraftDir;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        UpdateStatus();
    }

    private void NavList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ContentHost is null) return;
        ContentHost.Content = NavList.SelectedIndex switch
        {
            0 => _versionView ??= new VersionDownloadView(_host),
            1 => _contentView ??= new ContentDownloadView(_host),
            2 => _tasksView ??= new TasksView(_host),
            3 => _settingsView ??= new SettingsView(_host),
            _ => ContentHost.Content
        };

        AnimatePageSwitch();
    }

    /// <summary>把窗口限制在屏幕可用区域内：小屏 / 高缩放下标题栏不会跑到屏幕外。</summary>
    private void ApplyAdaptiveSize()
    {
        var area = SystemParameters.WorkArea;
        var width = Math.Min(1240, Math.Max(860, area.Width - 60));
        var height = Math.Min(800, Math.Max(540, area.Height - 60));

        // 必须先放宽下限，否则小屏上 Width 会被 MinWidth 顶回原值
        MinWidth = Math.Min(900, width);
        MinHeight = Math.Min(560, height);
        MaxWidth = area.Width;
        MaxHeight = area.Height;
        Width = width;
        Height = height;

        if (WindowState == WindowState.Normal)
        {
            Left = area.Left + Math.Max(0, (area.Width - width) / 2);
            Top = area.Top + Math.Max(0, (area.Height - height) / 2);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        // 初始焦点给导航，避免文本框抢焦点导致面板被滚动、弹出输入法
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            NavList.Focus();
            _versionView?.ResetScroll();
        }));
    }

    private void AnimatePageSwitch()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ContentHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        PageTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    #region 无边框窗口：最大化贴合工作区

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var info = new MINMAXINFO();
        info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref monitorInfo))
        {
            var work = monitorInfo.rcWork;
            var screen = monitorInfo.rcMonitor;
            info.ptMaxPosition.x = work.Left - screen.Left;
            info.ptMaxPosition.y = work.Top - screen.Top;
            info.ptMaxSize.x = work.Right - work.Left;
            info.ptMaxSize.y = work.Bottom - work.Top;
            info.ptMaxTrackSize.x = work.Right - work.Left;
            info.ptMaxTrackSize.y = work.Bottom - work.Top;
            Marshal.StructureToPtr(info, lParam, true);
            handled = true;
        }

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    #endregion

    private void UpdateStatus()
    {
        var free = _host.Workspace.GetFreeSpace();
        FreeText.Text = free > 0 ? $"磁盘剩余 {Format.Bytes(free)}" : "";
        var speed = _host.Queue.Jobs.Where(j => j.State == DownloadState.Running).Sum(j => j.Speed);
        SpeedText.Text = speed > 0 ? $"{Format.Speed(speed)}" : "";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点在按钮上时交给按钮处理，避免 DragMove 抢走点击
        if (HasButtonAncestor(e.OriginalSource as DependencyObject)) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // 忽略拖动异常
            }
        }
    }

    private static bool HasButtonAncestor(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Button) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    /// <summary>同步最大化/还原图标（Win+Up、双击标题栏、Aero Snap 都会触发 StateChanged）。</summary>
    private void UpdateMaximizeGlyph()
    {
        if (MaxButton is null) return;
        var maximized = WindowState == WindowState.Maximized;
        MaxButton.Content = maximized ? "\uE923" : "\uE922";
        MaxButton.ToolTip = maximized ? "还原" : "最大化";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        _statusTimer.Stop();
        try
        {
            _host.Queue.CancelAll();
            _host.Config.Save();
            _host.Log.Info("程序退出");
        }
        catch
        {
            // 忽略
        }
        base.OnClosing(e);
    }
}

public sealed class NavEntry
{
    public NavEntry(string icon, string title)
    {
        Icon = icon;
        Title = title;
    }

    public string Icon { get; }
    public string Title { get; }
}
