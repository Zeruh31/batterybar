using BatteryBar.Core;

namespace BatteryBar;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\BatteryBar.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // Already running; one widget is enough.
            return;
        }

        Application.ThreadException += (_, e) => Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled exception", e.ExceptionObject as Exception);

        Strings.ApplyCommandLine(args);
        ApplicationConfiguration.Initialize();
        Application.Run(new WidgetHost());
    }
}
