using System.IO;
using System.Windows;

namespace WeatherWidget;

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

        base.OnStartup(e);
    }
}
