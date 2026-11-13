using System.IO;
using System.Windows;

namespace HardwareWidget;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            File.WriteAllText("error.log", args.ExceptionObject?.ToString() ?? "Unknown error");
        };
        DispatcherUnhandledException += (s, args) =>
        {
            File.WriteAllText("error.log", args.Exception.ToString());
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
