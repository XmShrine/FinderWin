using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FinderWin;

public partial class App : System.Windows.Application {
    private static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "FinderWin-startup.log");

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        ApplySystemTheme();
        DispatcherUnhandledException += (_, error) => { WriteLog("WPF unhandled exception", error.Exception); error.Handled = true; System.Windows.MessageBox.Show($"此操作失败，FinderWin 将继续运行。诊断日志已写入桌面：\n{LogPath}", "Finder", MessageBoxButton.OK, MessageBoxImage.Error); };
        AppDomain.CurrentDomain.UnhandledException += (_, error) => WriteLog("AppDomain unhandled exception", error.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, error) => { WriteLog("Task exception", error.Exception); error.SetObserved(); };
        try {
            WriteLog($"Starting. OS={Environment.OSVersion}; Process={Environment.ProcessPath}; 64-bit={Environment.Is64BitProcess}");
            MainWindow = new MainWindow();
            MainWindow.Show();
            WriteLog("Main window shown.");
        } catch (Exception error) {
            WriteLog("Startup exception", error);
            System.Windows.MessageBox.Show($"Finder 无法启动。诊断日志已写入桌面：\n{LogPath}", "Finder", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    internal static void WriteLog(string message, Exception? error = null) {
        try { File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}{error}{Environment.NewLine}"); } catch { }
    }

    private void ApplySystemTheme() {
        var isDark = false;
        try { isDark = Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1)) == 0; } catch { }
        var colors = isDark
            ? new[] { ("WindowBrush", "#1C1C1E"), ("ToolbarBrush", "#20201F"), ("SidebarBrush", "#1B1B1A"), ("ContentBrush", "#1E1E1D"), ("StrokeBrush", "#3A3A38"), ("TextBrush", "#F1F1F3"), ("SecondaryTextBrush", "#A0A0A4"), ("HoverBrush", "#30302F"), ("HeaderBrush", "#222221"), ("SidebarOutlineBrush", "#484846"), ("ScrollTrackBrush", "#26000000"), ("ScrollThumbBrush", "#A58A8A8E") }
            : new[] { ("WindowBrush", "#F7F7F8"), ("ToolbarBrush", "#F5F5F6"), ("SidebarBrush", "#ECECEE"), ("ContentBrush", "#FCFCFD"), ("StrokeBrush", "#D2D2D4"), ("TextBrush", "#1C1C1E"), ("SecondaryTextBrush", "#7C7C80"), ("HoverBrush", "#E3E3E6"), ("HeaderBrush", "#F7F7F8"), ("SidebarOutlineBrush", "#00000000"), ("ScrollTrackBrush", "#0D000000"), ("ScrollThumbBrush", "#887A7A7E") };
        foreach (var (key, color) in colors) Resources[key] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }
}
