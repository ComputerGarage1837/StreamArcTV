using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using StreamArcTV.Data;
using StreamArcTV.Transfer;
using StreamArcTV.UI;
using StreamArcTV.Util;

namespace StreamArcTV;

/// <summary>
/// Application entry point (the counterpart of StreamArcApp): sets up logging, the crash safety
/// net, the transfer engine and the menu-bar icon, then opens the main window (or stays in the
/// menu bar when started by a scheduled recording with --tray).
/// </summary>
public partial class App : Application
{
    public static bool StartInTray { get; private set; }
    public static MainWindow? Window { get; private set; }
    public static IClassicDesktopStyleApplicationLifetime? Desktop => Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    private static FileStream? _instanceLock;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = Desktop;
        var args = desktop?.Args ?? Array.Empty<string>();
        StartInTray = args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

        AppPaths.Ensure();
        if (!AcquireInstanceLock())
        {
            // A second copy (launchd starting a recording while the app is already open): the
            // running app's own scheduler handles the job, so this one just leaves.
            desktop?.Shutdown(0);
            base.OnFrameworkInitializationCompleted();
            return;
        }
        AppLog.Init();
        Update.Installer.CleanLeftovers();

        // Crash safety net: log it and offer the log on the next start.
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Crash(ev.ExceptionObject as Exception ?? new Exception(ev.ExceptionObject?.ToString()));
        Dispatcher.UIThread.UnhandledException += (_, ev) =>
        {
            Crash(ev.Exception);
            // Keep the app alive for recoverable UI errors; the log has the details.
            ev.Handled = true;
            try { Dialogs.Toast("Something went wrong: " + ev.Exception.Message); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, ev) => { AppLog.E("App", "unobserved task exception", ev.Exception); ev.SetObserved(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Prefs.Flush();

        Task.Run(Player.TimeshiftServer.Cleanup);
        Tray.Init();
        TransferService.RescheduleAll();
        TransferService.RenameCompleted();

        Window = new MainWindow();
        if (desktop != null)
        {
            desktop.MainWindow = Window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Cmd+Q / Quit in the application menu: a real quit, even while transfers run.
            desktop.ShutdownRequested += (_, _) => Window?.MarkForceClose();
        }
        // Clicking the Dock icon while the window is hidden in the menu bar brings it back.
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            activatable.Activated += (_, e) => { if (e.Kind == ActivationKind.Reopen) Tray.Open(); };

        if (StartInTray)
        {
            // Started by a scheduled recording: run in the background; the menu-bar icon opens the window.
            Mac.Notify("Stream Arc TV", "Recording in the background. Use the menu-bar icon to open the app.");
        }
        else
        {
            Window.Show();
        }
        Player.PlayerCore.Warm();
        base.OnFrameworkInitializationCompleted();
    }

    private static bool AcquireInstanceLock()
    {
        try
        {
            _instanceLock = new FileStream(Path.Combine(AppPaths.Data, ".instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
        catch { return true; }
    }

    /// Lets go of the single-instance lock so a freshly installed copy can start while we exit.
    public static void ReleaseInstanceLock()
    {
        try { _instanceLock?.Dispose(); } catch { }
        _instanceLock = null;
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

    /// Quits for real: flushes settings, removes the menu-bar icon and ends the process.
    public static void Quit()
    {
        Prefs.Flush();
        Tray.Dispose();
        Desktop?.Shutdown(0);
    }
}
