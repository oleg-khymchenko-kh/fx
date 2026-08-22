using System.Threading.Tasks;
using System.Windows;

namespace FXViewer;

public partial class App : Application
{
    private readonly HashSet<string> _shown = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write("UI", args.Exception);
            args.Handled = true;
            ShowOnce(args.Exception.Message);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write("Background", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write("Task", args.Exception);
            args.SetObserved();
        };
    }

    private void ShowOnce(string message)
    {
        if (!_shown.Add(message)) return;
        MessageBox.Show(
            message + "\n\nDetails are in fxviewer-crash.log. The app keeps running.",
            "FXViewer error", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
