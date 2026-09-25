using System.Windows;
using System.Windows.Controls;
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
        if (!service.IsConfigured())
        {
            ShowError($"{service.Title()} isn't configured in this build. Rebuild the app with the server address set.");
            BtnSignIn.IsEnabled = false;
        }
        BtnSignIn.Click += (_, _) => SignIn();
        BtnCancel.Click += (_, _) => Finish();
        InputPassword.PasswordChanged += (_, _) => PasswordHint.Visibility = InputPassword.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        InputPasswordPlain.TextChanged += (_, _) => PasswordHint.Visibility = InputPasswordPlain.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        InputUsername.KeyDown += (_, e) => { if (e.Key == Key.Enter) { PasswordField.Focus(); e.Handled = true; } };
        InputPassword.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SignIn(); e.Handled = true; } };
        InputPasswordPlain.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SignIn(); e.Handled = true; } };
        SwitchShowPassword.Click += (_, _) => ShowPassword(SwitchShowPassword.IsChecked == true);
    }

    private bool _showPassword;
    private Control PasswordField => _showPassword ? InputPasswordPlain : InputPassword;
    private string Password => _showPassword ? InputPasswordPlain.Text : InputPassword.Password;

    /// Swaps the masked box for a plain one (and back), carrying the typed text along.
    private void ShowPassword(bool show)
    {
        if (show == _showPassword) return;
        var hadFocus = PasswordField.IsKeyboardFocusWithin;
        if (show) InputPasswordPlain.Text = InputPassword.Password; else InputPassword.Password = InputPasswordPlain.Text;
        _showPassword = show;
        InputPassword.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        InputPasswordPlain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PasswordHint.Visibility = Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hadFocus) { PasswordField.Focus(); if (show) InputPasswordPlain.CaretIndex = InputPasswordPlain.Text.Length; }
    }

    public override IInputElement InitialFocus => InputUsername;

    private async void SignIn()
    {
        var user = InputUsername.Text.Trim();
        var pass = Password;
        if (user.Length == 0 || pass.Length == 0) { ShowError("Enter your username and password."); return; }
        SetBusy(true);
        try
        {
            var info = await XtreamApi.Login(_service, user, pass);
            Prefs.Instance.SaveAccount(_service, info.ToAccount(user, pass));
            Nav.Replace(_service == Service.VOD ? new VodHomePage(_service) : new BrowsePage(_service));
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
        InputPassword.IsEnabled = InputPasswordPlain.IsEnabled = SwitchShowPassword.IsEnabled = !busy;
        if (busy) TxtError.Visibility = Visibility.Collapsed;
    }
}
