using System.Windows;
using System.Windows.Threading;
using McDownloader.Services;

namespace McDownloader;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ShowFatal(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
        };

        try
        {
            var host = AppHost.Create();
            host.Workspace.EnsureWritable();
            host.Log.Info($"启动 MC 下载器，工作空间：{host.Workspace.MinecraftDir}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "程序无法在自身目录内初始化 .minecraft 工作空间：\n\n" + ex.Message +
                "\n\n请把程序放到有写入权限的文件夹后重试。",
                "MC 下载器", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            AppHost.Current?.Log.Error(e.Exception, "未处理异常");
        }
        catch
        {
            // 忽略
        }

        MessageBox.Show("发生了一个未处理的错误：\n\n" + e.Exception.Message,
            "MC 下载器", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void ShowFatal(Exception ex)
    {
        try
        {
            AppHost.Current?.Log.Error(ex, "致命错误");
        }
        catch
        {
            // 忽略
        }
    }
}
