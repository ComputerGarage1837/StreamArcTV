using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Threading;

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
        TxtBrand.Inlines = new InlineCollection
        {
            new Run("Stream Arc "),
            new Run("TV") { Foreground = Ui.Brush("AccentBrush") }
        };
        _clock.Tick += (_, _) => TxtClock.Text = DateTime.Now.ToString("h:mm tt");
        Loaded += (_, _) => { TxtClock.Text = DateTime.Now.ToString("h:mm tt"); _clock.Start(); };
        Unloaded += (_, _) => _clock.Stop();
    }

    public bool ShowClock
    {
        get => TxtClock.IsVisible;
        set => TxtClock.IsVisible = value;
    }

    /// Smaller logo and text (VOD home).
    public void SetCompact(bool compact)
    {
        ImgLogo.Width = ImgLogo.Height = compact ? 36 : 46;
        TxtBrand.FontSize = compact ? 18 : 22;
    }
}
