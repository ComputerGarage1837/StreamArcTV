using System.Windows;
using System.Windows.Input;
using StreamArcTV.Data;

namespace StreamArcTV.UI;

public partial class ProfilePage : AppPage
{
    private readonly Service _service;
    private readonly Prefs _prefs = Prefs.Instance;

    public ProfilePage(Service service)
    {
        InitializeComponent();
        _service = service;
        var a = _prefs.Account(service);
        if (a == null) { Loaded += (_, _) => Finish(); return; }
        TxtTitle.Text = $"{service.Title()} account";
        TxtServer.Text = service.BaseUrl();
        TxtUsername.Text = a.Username;
        TxtStatus.Text = a.Status ?? "—";
        TxtExpiry.Text = Format.Expiry(a.ExpDate);
        TxtConnections.Text = $"{a.ActiveConnections ?? "0"} / {a.MaxConnections ?? "?"}";
        TxtCreated.Text = Format.Date(a.CreatedAt);
        TxtTrial.Text = a.IsTrial ? "Yes" : "No";
        BtnBack.Click += (_, _) => Finish();
        BtnLogout.Click += (_, _) => ConfirmLogout();
    }

    public override IInputElement InitialFocus => BtnLogout;

    private void ConfirmLogout()
    {
        var r = Dialogs.Alert("Log out", $"Log out of {_service.Title()}? You'll need to sign in again to watch.", "Log out", "Cancel");
        if (r != DialogResultKind.Positive) return;
        _prefs.ClearAccount(_service);
        Nav.PopToRoot();
    }
}
