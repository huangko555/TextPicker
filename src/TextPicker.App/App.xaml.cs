using System.IO;
using System.Windows;

namespace TextPicker.App;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(Path.GetTempPath(), "TextPicker.DebugPanel.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;    // 面板是调试工具：记录后继续运行，不崩
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            LogCrash("UnobservedTaskException", args.Exception);
    }

    private static void LogCrash(string kind, Exception? exception)
    {
        try
        {
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {kind}\n{exception}\n\n");
        }
        catch (IOException)
        {
        }

        MessageBox.Show($"{kind}:\n{exception?.ToString() ?? "unknown"}\n\n日志：{CrashLogPath}", "TextPicker 调试面板异常", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
