using System.IO;
using System.Windows;

namespace HardwareWidget;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "error.log"), args.ExceptionObject?.ToString() ?? "Unknown error");
        };
        DispatcherUnhandledException += (s, args) =>
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "error.log"), args.Exception.ToString());
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
