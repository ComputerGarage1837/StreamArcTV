using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace StreamArcTV.UI;

/// The header shared by the home screen and the Video on Demand home: logo, brand, Update and Settings buttons, clock.
public partial class HomeHeader : UserControl
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public Button UpdateButton => BtnUpdate;
    public Button SettingsButton => BtnSettings;

    public HomeHeader()
    {
        InitializeComponent();
        // "Stream Arc" in white with "TV" in the logo's cyan.
        TxtBrand.Inlines.Clear();
        TxtBrand.Inlines.Add(new Run("Stream Arc "));
        TxtBrand.Inlines.Add(new Run("TV") { Foreground = Ui.Brush("AccentBrush") });
        _clock.Tick += (_, _) => TxtClock.Text = DateTime.Now.ToString("h:mm tt");
        Loaded += (_, _) => { TxtClock.Text = DateTime.Now.ToString("h:mm tt"); _clock.Start(); };
        Unloaded += (_, _) => _clock.Stop();
    }

    public bool ShowClock
    {
        get => TxtClock.Visibility == Visibility.Visible;
        set => TxtClock.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// Smaller logo and text (VOD home).
    public void SetCompact(bool compact)
    {
        ImgLogo.Width = ImgLogo.Height = compact ? 36 : 46;
        TxtBrand.FontSize = compact ? 18 : 22;
    }
}
