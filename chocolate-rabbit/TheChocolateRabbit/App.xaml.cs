using System.Windows;
using System.Windows.Threading;
using TheChocolateRabbit.Data;
using TheChocolateRabbit.UI;
using TheChocolateRabbit.Util;

namespace TheChocolateRabbit;

/// <summary>
/// Application entry point: sets up logging and the crash safety net, cleans up after a previous
/// in-app update, opens the newsletter window and checks the release feed once per launch.
/// </summary>
public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--apply-update"))
        {
            // Elevated helper started by the updater: swap the files and exit.
            Shutdown(Update.Installer.ApplyFromArgs(e.Args));
            return;
        }
        AppPaths.Ensure();
        AppLog.Init();
        Update.Installer.CleanLeftovers();

        // Crash safety net: log it so the next start can show where the log is.
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Crash(ev.ExceptionObject as Exception ?? new Exception(ev.ExceptionObject?.ToString()));
        DispatcherUnhandledException += (_, ev) =>
        {
            Crash(ev.Exception);
            // Keep the app alive for recoverable UI errors; the log has the details.
            ev.Handled = true;
            try { Dialogs.Toast("Something went wrong: " + ev.Exception.Message); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, ev) => { AppLog.E("App", "unobserved task exception", ev.Exception); ev.SetObserved(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Prefs.Flush();

        Window = new MainWindow();
        Window.Show();

        if (Prefs.Instance.AutoCheckUpdates)
        {
            // A moment after the window is up, so the first paint is never delayed by the network.
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (_, _) => { t.Stop(); Update.UpdateChecker.Check(manual: false); };
            t.Start();
        }
    }

    private static void Crash(Exception e)
    {
        try
        {
            AppLog.Crash("Crash", "uncaught exception", e);
            Prefs.Instance.Crashed = true;
            Prefs.Flush();
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Prefs.Flush();
        base.OnExit(e);
    }
}
