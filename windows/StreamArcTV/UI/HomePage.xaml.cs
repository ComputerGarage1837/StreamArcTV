using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamArcTV.Data;
using StreamArcTV.Transfer;
using StreamArcTV.Update;
using StreamArcTV.Util;

namespace StreamArcTV.UI;

/// <summary>
/// Home screen. Shows the TV/tablet or phone layout depending on the user's choice (asked once
/// on first launch, changeable in Settings).
/// </summary>
public partial class HomePage : AppPage
{
    private readonly Prefs _prefs = Prefs.Instance;
    private string? _appliedLayout;
    private static bool _checkedUpdatesThisLaunch;
    private NavBar? _navBar;
    private readonly System.Windows.Threading.DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromMinutes(5) };

    public HomePage()
    {
        InitializeComponent();
        foreach (var h in new[] { HeaderTv, HeaderPhone })
        {
            h.SettingsButton.Click += (_, _) => Nav.Push(new SettingsPage());
            h.UpdateButton.Click += (_, _) => UpdateChecker.Check(manual: true);
        }
        TxtVersionTv.Text = TxtVersionPhone.Text = $"v{BuildInfo.VersionName}";
        BtnLiveTv.Click += () => Open(Service.LIVE);
        BtnLivePhone.Click += () => Open(Service.LIVE);
        BtnVodTv.Click += () => Open(Service.VOD);
        BtnVodPhone.Click += () => Open(Service.VOD);
        foreach (var c in new[] { BtnLiveTv, BtnVodTv, BtnLivePhone, BtnVodPhone })
        {
            c.GotKeyboardFocus += (_, _) => Grow(c, true);
            c.LostKeyboardFocus += (_, _) => Grow(c, false);
        }
        SizeChanged += (_, _) => ApplyLayout();
        ShowNotice(Announcement.Cached());
        _noticeTimer.Tick += (_, _) => RefreshNotice();
        Loaded += (_, _) =>
        {
            ApplyLayout();
            if (_prefs.LayoutMode == null) AskLayout();
            if (_prefs.Crashed)
            {
                _prefs.Crashed = false;
                var r = Dialogs.Alert("The app crashed last time",
                    "A log of what happened was saved (no passwords). Save or copy it so the crash can be fixed.",
                    "Save…", "Not now", "Copy");
                if (r == DialogResultKind.Positive && App.Window != null) AppLog.Share(App.Window);
                else if (r == DialogResultKind.Neutral) AppLog.Copy();
            }
            if (_prefs.AutoCheckUpdates && !_checkedUpdatesThisLaunch)
            {
                _checkedUpdatesThisLaunch = true;
                UpdateChecker.Check(manual: false);
            }
        };
    }

    public override IInputElement? InitialFocus => CurrentLayout() == "phone" ? BtnLivePhone : BtnLiveTv;

    private string CurrentLayout() => _prefs.LayoutMode ?? "tv";

    /// Grow the big cards slightly when they take focus.
    private static void Grow(FrameworkElement v, bool on)
    {
        var s = on ? 1.03 : 1.0;
        v.RenderTransform = new ScaleTransform(s, s);
    }

    private void ApplyLayout()
    {
        var mode = CurrentLayout();
        var portrait = ActualHeight > ActualWidth;
        var phone = mode == "phone";
        _appliedLayout = mode;
        TvLayout.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        PhoneLayout.Visibility = phone ? Visibility.Visible : Visibility.Collapsed;
        HeaderTv.ShowClock = !phone;
        // Portrait: the portrait scenery and the scrim over the whole picture, like the phone layout.
        var wantPortrait = portrait;
        var src = wantPortrait ? "bg_hero_port.jpg" : "bg_hero.jpg";
        if (!Equals(ImgBackdrop.Tag, src))
        {
            ImgBackdrop.Tag = src;
            ImgBackdrop.Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/{src}"));
        }
        ScrimTop.Fill = phone ? Ui.Brush("HeroScrimBrush") : Brushes.Transparent;
        // Phone in landscape: tighter hero so the cards stay on screen.
        TxtHeroPhone.FontSize = phone && !portrait ? 26 : 34;
        TxtHeroPhone.LineHeight = phone && !portrait ? 28 : 36;
        TxtHeroPhone.Margin = new Thickness(0, phone && !portrait ? 10 : 28, 0, 0);
        PhoneCards.Margin = new Thickness(0, phone && !portrait ? 14 : 22, 0, 12);
        if (_navBar == null)
        {
            _navBar = new NavBar();
            _navBar.Home += () => (phone ? BtnLivePhone : BtnLiveTv).Focus();
            _navBar.Search += PickSearch;
            _navBar.Downloads += () => Nav.Push(new TransfersPage(TransferType.DOWNLOAD));
            _navBar.Recordings += () => Nav.Push(new TransfersPage(TransferType.RECORDING));
            _navBar.Profile += PickProfile;
        }
        var wanted = phone ? NavHostPhone : NavHostTv;
        var other = phone ? NavHostTv : NavHostPhone;
        if (!ReferenceEquals(wanted.Content, _navBar)) { other.Content = null; wanted.Content = _navBar; }
    }

    private void AskLayout()
    {
        var r = Dialogs.Alert("How are you using Stream Arc TV?",
            "Pick the layout that fits this device. You can change it later in Settings.",
            "TV or tablet", "Phone", cancelable: false);
        ApplyLayoutChoice(r == DialogResultKind.Negative ? "phone" : "tv");
    }

    private void ApplyLayoutChoice(string mode)
    {
        _prefs.LayoutMode = mode;
        if (mode != _appliedLayout) { ApplyLayout(); RefreshAll(); }
        Keyboard.Focus(InitialFocus);
    }

    public override void OnResume()
    {
        RefreshNotice();
        _noticeTimer.Start();
        if (_prefs.LayoutMode != null && _prefs.LayoutMode != _appliedLayout) ApplyLayout();
        RefreshAll();
    }

    public override void OnPause() => _noticeTimer.Stop();

    private async void RefreshNotice()
    {
        Announcement.Notice? notice;
        try { notice = await Announcement.Fetch(); } catch { return; }
        if (!Finished) ShowNotice(notice);
    }

    private void ShowNotice(Announcement.Notice? notice)
    {
        NoticeTv.Show(notice);
        NoticePhone.Show(notice);
    }

    private void RefreshAll()
    {
        RefreshStatus(Service.LIVE, TxtLiveStatusTv, TxtLiveStatusPhone);
        RefreshStatus(Service.VOD, TxtVodStatusTv, TxtVodStatusPhone);
    }

    private void Open(Service service)
    {
        if (!IsResumed) return;   // a second press while the next screen is already opening
        if (_prefs.IsSignedIn(service))
            Nav.Push(service == Service.VOD ? new VodHomePage(service) : new BrowsePage(service));
        else
            Nav.Push(new LoginPage(service));
    }

    private void PickSearch()
    {
        var which = Dialogs.Items("Search in", new[] { "Live TV", "Video on Demand" });
        if (which >= 0) Open(which == 0 ? Service.LIVE : Service.VOD);
    }

    private void PickProfile()
    {
        var signedIn = ServiceInfo.All.Where(s => _prefs.IsSignedIn(s)).ToList();
        switch (signedIn.Count)
        {
            case 0: Open(Service.LIVE); break;
            case 1: Nav.Push(new ProfilePage(signedIn[0])); break;
            default:
                var which = Dialogs.Items("Profile", signedIn.Select(s => s.Title()).ToList());
                if (which >= 0) Nav.Push(new ProfilePage(signedIn[which]));
                break;
        }
    }

    private async void RefreshStatus(Service service, params TextBlock[] views)
    {
        var account = _prefs.Account(service);
        Render(account, views);
        if (account == null) return;
        // Silently refresh the account info so the expiry date stays current.
        try
        {
            var info = await XtreamApi.Login(service, account.Username, account.Password);
            var updated = info.ToAccount(account.Username, account.Password);
            _prefs.SaveAccount(service, updated);
            Render(updated, views);
        }
        catch
        {
            // Offline or server hiccup: keep showing the cached values.
        }
    }

    private static void Render(Account? account, TextBlock[] views)
    {
        foreach (var view in views)
        {
            if (account == null)
            {
                view.Text = "Not signed in";
                view.Foreground = Ui.Brush("TextMutedBrush");
                continue;
            }
            var expired = Format.IsExpired(account.ExpDate);
            view.Text = expired ? $"Expired on {Format.Expiry(account.ExpDate)}" : $"Expires: {Format.Expiry(account.ExpDate)}";
            view.Foreground = Ui.Brush(expired ? "DangerBrush" : "AccentBrush");
        }
    }
}

/// The Home / Search / Downloads / Recordings / Profile bar at the bottom of the home screen.
public class NavBar : Border
{
    public event Action? Home, Search, Downloads, Recordings, Profile;

    public NavBar()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0E, 0x15, 0x26));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x36, 0x50));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(22);
        Padding = new Thickness(0, 2, 0, 2);
        var grid = new UniformGrid { Rows = 1 };
        grid.Children.Add(Item("img_nav_home.png", null, "Home", true, () => Home?.Invoke()));
        grid.Children.Add(Item("img_nav_search.png", null, "Search", false, () => Search?.Invoke()));
        grid.Children.Add(Item("img_nav_downloads.png", null, "Downloads", false, () => Downloads?.Invoke()));
        grid.Children.Add(Item(null, "rec", "Recordings", false, () => Recordings?.Invoke()));
        grid.Children.Add(Item("img_nav_profile.png", null, "Profile", false, () => Profile?.Invoke()));
        Child = grid;
    }

    private static FocusCard Item(string? png, string? icon, string label, bool selected, Action click)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        if (png != null)
            stack.Children.Add(new Image { Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/{png}")), Width = 22, Height = 22, Stretch = Stretch.Uniform });
        else
        {
            var g = new Grid { Width = 22, Height = 22 };
            g.Children.Add(new System.Windows.Shapes.Path { Data = Ui.Res<Geometry>("IconRecRing"), Stroke = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)), StrokeThickness = 2, Stretch = Stretch.Uniform, Margin = new Thickness(1) });
            g.Children.Add(new System.Windows.Shapes.Path { Data = Ui.Res<Geometry>("IconRecDot"), Fill = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)), Stretch = Stretch.Uniform, Margin = new Thickness(6) });
            stack.Children.Add(g);
        }
        stack.Children.Add(new TextBlock { Text = label, FontSize = 13, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = selected ? Ui.Brush("PurpleBrush") : new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)) });
        stack.Children.Add(new Border { Width = 36, Height = 3, Margin = new Thickness(0, 3, 0, 0), CornerRadius = new CornerRadius(2), Background = selected ? Ui.Brush("HeroAccentBarBrush") : Brushes.Transparent });
        var card = new FocusCard { Content = stack, Style = Ui.Res<Style>("NavItem") };
        card.Click += click;
        return card;
    }
}
