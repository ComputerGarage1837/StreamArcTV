using System.Windows;
using System.Windows.Threading;
using StreamArcTV.Data;
using StreamArcTV.Transfer;
using StreamArcTV.UI;
using StreamArcTV.Util;

namespace StreamArcTV;

/// <summary>
/// Application entry point (the counterpart of StreamArcApp): sets up logging, the crash safety
/// net, the transfer engine and the tray icon, then opens the main window (or stays in the tray
/// when started by a scheduled recording with --tray).
/// </summary>
public partial class App : Application
{
    public static bool StartInTray { get; private set; }
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
        StartInTray = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

        // Crash safety net: log it and offer the log on the next start.
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Crash(ev.ExceptionObject as Exception ?? new Exception(ev.ExceptionObject?.ToString()));
        DispatcherUnhandledException += (_, ev) =>
        {
            Crash(ev.Exception);
            // Keep the app alive for recoverable UI errors; the log has the details.
            ev.Handled = true;
            try { Dialogs.Toast("Something went wrong: " + ev.Exception.Message); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, ev) => { AppLog.E("App", "unobserved task exception", ev.Exception); ev.SetObserved(); };

        Task.Run(Player.TimeshiftServer.Cleanup);
        Tray.Init();
        TransferService.RescheduleAll();

        Window = new MainWindow();
        if (StartInTray)
        {
            // Started by a scheduled recording: run in the background; the tray icon opens the window.
            Tray.ShowBalloon("Stream Arc TV", "Recording in the background. Double-click the tray icon to open the app.");
        }
        else
        {
            Window.Show();
        }
    }

    private static void Crash(Exception e)
    {
        try
        {
            AppLog.Crash("Crash", "uncaught exception", e);
            Prefs.Instance.Crashed = true;
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray.Dispose();
        base.OnExit(e);
    }
}
