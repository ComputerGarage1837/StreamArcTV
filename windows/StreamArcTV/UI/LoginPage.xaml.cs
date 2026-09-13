using System.Windows;
using System.Windows.Input;
using StreamArcTV.Data;

namespace StreamArcTV.UI;

public partial class LoginPage : AppPage
{
    private readonly Service _service;

    public LoginPage(Service service)
    {
        InitializeComponent();
        _service = service;
        TxtTitle.Text = $"Sign in to {service.Title()}";
        TxtServer.Text = service.IsConfigured() ? service.BaseUrl() : "Not configured";
        if (!service.IsConfigured())
        {
            ShowError($"{service.Title()} isn't configured in this build. Rebuild the app with the server address set.");
            BtnSignIn.IsEnabled = false;
        }
        BtnSignIn.Click += (_, _) => SignIn();
        BtnCancel.Click += (_, _) => Finish();
        InputPassword.PasswordChanged += (_, _) => PasswordHint.Visibility = InputPassword.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        InputUsername.KeyDown += (_, e) => { if (e.Key == Key.Enter) { InputPassword.Focus(); e.Handled = true; } };
        InputPassword.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SignIn(); e.Handled = true; } };
    }

    public override IInputElement InitialFocus => InputUsername;

    private async void SignIn()
    {
        var user = InputUsername.Text.Trim();
        var pass = InputPassword.Password;
        if (user.Length == 0 || pass.Length == 0) { ShowError("Enter your username and password."); return; }
        SetBusy(true);
        try
        {
            var info = await XtreamApi.Login(_service, user, pass);
            Prefs.Instance.SaveAccount(_service, info.ToAccount(user, pass));
            Nav.Replace(new BrowsePage(_service));
        }
        catch (Exception e)
        {
            ShowError(string.IsNullOrWhiteSpace(e.Message) ? "Sign in failed." : e.Message);
            SetBusy(false);
        }
    }

    private void ShowError(string msg)
    {
        TxtError.Text = msg;
        TxtError.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BtnSignIn.IsEnabled = !busy;
        InputUsername.IsEnabled = !busy;
        InputPassword.IsEnabled = !busy;
        if (busy) TxtError.Visibility = Visibility.Collapsed;
    }
}
