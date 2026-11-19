using System.IO;
using System.Windows;

namespace SystemPerformanceWidget;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "error.log"),
                    args.ExceptionObject?.ToString() ?? "Unknown error");
            }
            catch { }
        };

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "error.log"),
                    args.Exception.ToString());
            }
            catch { }
            args.Handled = true;
        };

        bool launchedFromTask = e.Args.Any(arg =>
            string.Equals(arg, ElevatedTaskHelper.FromTaskArg, StringComparison.OrdinalIgnoreCase));

        if (!ElevatedTaskHelper.IsRunningAsAdministrator() && !launchedFromTask)
        {
            string baseDir = ElevatedTaskHelper.ResolveBaseDirectory();
            string exePath = ElevatedTaskHelper.ResolveExePath(baseDir);
            if (ElevatedTaskHelper.TryRunElevatedTask(exePath))
            {
                Shutdown();
                return;
            }
        }

        base.OnStartup(e);
    }
}
