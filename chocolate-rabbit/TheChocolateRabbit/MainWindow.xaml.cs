using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TheChocolateRabbit.Data;
using TheChocolateRabbit.Newsletter;
using TheChocolateRabbit.UI;
using TheChocolateRabbit.Update;
using TheChocolateRabbit.Util;

namespace TheChocolateRabbit;

/// <summary>
/// The newsletter sign-up page: hero photo, logo, "Join Our Newsletter", name and email, SUBSCRIBE.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(3200) };
    private bool _forceClose;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBox.Visibility = Visibility.Collapsed; };
        RestorePlacement();
        SizeChanged += (_, _) => Relayout();
        Loaded += (_, _) =>
        {
            Relayout();
            UpdateHints();
            NameBox.Focus();
            if (Prefs.Instance.Crashed)
            {
                Prefs.Instance.Crashed = false;
                var r = Dialogs.Alert("Sorry about that", "The app closed unexpectedly last time. The log may help find out why.", "Save log…", "Close");
                if (r == DialogResultKind.Positive) AppLog.Share(this);
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; }
            else if (e.Key == Key.Escape && WindowStyle == WindowStyle.None) { ToggleFullScreen(); e.Handled = true; }
        };
    }

    // ---- Layout ----------------------------------------------------------

    /// Two columns (hero on the left, form on the right) when there is room; a single column,
    /// form over the photo, when the window is narrow.
    private void Relayout()
    {
        var narrow = ActualWidth < 1000;
        if (narrow)
        {
            HeroCol.Width = new GridLength(0);
            FormCol.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(Logo, 1);
            Grid.SetColumn(BottomLeft, 1);
            Logo.Width = 150;
            JoinText.FontSize = 60; JoinText.Margin = new Thickness(0, 0, 0, -22);
            NewsletterText.FontSize = 80;
            BodyText.FontSize = 17; BodyText.LineHeight = 27;
            Form.Width = Math.Max(320, Math.Min(480, ActualWidth - 96));
        }
        else
        {
            HeroCol.Width = new GridLength(1, GridUnitType.Star);
            FormCol.Width = new GridLength(ActualWidth >= 1200 ? 540 : 500);
            Grid.SetColumn(Logo, 0);
            Grid.SetColumn(BottomLeft, 0);
            Logo.Width = ActualWidth >= 1200 ? 190 : 160;
            JoinText.FontSize = ActualWidth >= 1200 ? 76 : 66; JoinText.Margin = new Thickness(0, 0, 0, -30);
            NewsletterText.FontSize = ActualWidth >= 1200 ? 104 : 88;
            BodyText.FontSize = 19; BodyText.LineHeight = 30;
            Form.Width = 480;
        }
        // Keep the photo anchored a little left of centre so the bunny stays beside the form.
        Hero.HorizontalAlignment = narrow ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    }

    private void ToggleFullScreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize; WindowState = WindowState.Normal;
        }
        else
        {
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized;
        }
    }

    // ---- Form --------------------------------------------------------------

    private void Field_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHints();
        if (ErrorText.Visibility == Visibility.Visible) { ErrorText.Visibility = Visibility.Collapsed; }
    }

    private void UpdateHints()
    {
        NameHint.Visibility = string.IsNullOrEmpty(NameBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        EmailHint.Visibility = string.IsNullOrEmpty(EmailBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (sender == NameBox) EmailBox.Focus();
        else Subscribe();
    }

    private void Subscribe_Click(object sender, RoutedEventArgs e) => Subscribe();

    private void Subscribe()
    {
        if (_busy) return;
        var name = NameBox.Text.Trim();
        var email = EmailBox.Text.Trim();
        var problem = Signup.Validate(name, email);
        if (problem != null)
        {
            ErrorText.Text = problem;
            ErrorText.Visibility = Visibility.Visible;
            (string.IsNullOrWhiteSpace(name) ? NameBox : EmailBox).Focus();
            return;
        }
        _busy = true;
        SubscribeButton.IsEnabled = false;
        try
        {
            var entry = new Signup.Entry(name, email, DateTimeOffset.Now);
            Signup.SaveLocal(entry);
            var opened = Signup.OpenMail(entry);
            var first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
            if (opened)
            {
                Dialogs.Alert("Almost there",
                    $"Thank you, {first}! Your email app has opened with your sign-up addressed to {BuildInfo.NewsletterEmail}.\n\n" +
                    "Press Send in your email app to finish subscribing.");
                NameBox.Clear(); EmailBox.Clear();
                NameBox.Focus();
            }
            else
            {
                // No email app on this PC: show the details and offer to copy them.
                var r = Dialogs.Alert("Send us your details",
                    $"Windows has no email app set up, so please email the details below to {BuildInfo.NewsletterEmail} yourself:\n\n" +
                    Signup.Body(entry),
                    "Copy details", "Close");
                if (r == DialogResultKind.Positive)
                {
                    try { Clipboard.SetText($"To: {BuildInfo.NewsletterEmail}\r\nSubject: {Signup.Subject(entry)}\r\n\r\n{Signup.Body(entry)}"); ShowToast("Copied. Paste it into an email to " + BuildInfo.NewsletterEmail); }
                    catch (Exception ex) { ShowToast("Couldn't copy: " + ex.Message); }
                }
            }
        }
        finally
        {
            _busy = false;
            SubscribeButton.IsEnabled = true;
        }
    }

    // ---- Menu --------------------------------------------------------------

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var prefs = Prefs.Instance;
        var items = new List<string>
        {
            "Check for updates",
            prefs.AutoCheckUpdates ? "Check for updates on launch: On" : "Check for updates on launch: Off",
            "Release notes and downloads",
            "Export log…",
            "About The Chocolate Rabbit",
            "Exit"
        };
        switch (Dialogs.Items("Menu", items, "Close"))
        {
            case 0: UpdateChecker.Check(manual: true); break;
            case 1:
                prefs.AutoCheckUpdates = !prefs.AutoCheckUpdates;
                ShowToast(prefs.AutoCheckUpdates ? "The app will check for updates when it starts." : "The app won't check for updates on its own.");
                break;
            case 2: OpenUrl($"https://github.com/{BuildInfo.GitHubRepo}/releases"); break;
            case 3: AppLog.Share(this); break;
            case 4:
                Dialogs.Alert($"{BuildInfo.AppName} v{BuildInfo.VersionName}",
                    "Join Our Newsletter — be the first to hear about new chocolates, seasonal collections, exclusive offers and sweet stories from our world.\n\n" +
                    $"Sign-ups are sent to {BuildInfo.NewsletterEmail}.\n" +
                    $"Updates come from github.com/{BuildInfo.GitHubRepo}.\n\n" +
                    $"Settings and logs: {AppPaths.Data}");
                break;
            case 5: Close(); break;
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Dialogs.Toast("Couldn't open the browser: " + e.Message); }
    }

    // ---- Toast and window state --------------------------------------------

    public void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastBox.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void ForceClose() { _forceClose = true; Close(); }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SavePlacement();
        base.OnClosing(e);
        if (!e.Cancel && !_forceClose) Application.Current.Shutdown();
    }

    private void RestorePlacement()
    {
        try
        {
            var p = Prefs.Instance.WindowPlacement?.Split(',');
            if (p == null || p.Length < 5) return;
            var left = double.Parse(p[0]); var top = double.Parse(p[1]);
            var width = double.Parse(p[2]); var height = double.Parse(p[3]);
            if (width < MinWidth || height < MinHeight) return;
            // Only when the saved position is still on a screen.
            if (left + width < SystemParameters.VirtualScreenLeft + 40 || top + height < SystemParameters.VirtualScreenTop + 40 ||
                left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 ||
                top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left; Top = top; Width = width; Height = height;
            if (p[4] == "max") WindowState = WindowState.Maximized;
        }
        catch { }
    }

    private void SavePlacement()
    {
        try
        {
            if (WindowStyle == WindowStyle.None) return;   // full screen: keep the previous placement
            var b = RestoreBounds;
            Prefs.Instance.WindowPlacement = $"{b.Left},{b.Top},{b.Width},{b.Height},{(WindowState == WindowState.Maximized ? "max" : "normal")}";
        }
        catch { }
    }
}
