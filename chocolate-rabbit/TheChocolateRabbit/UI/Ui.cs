using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TheChocolateRabbit.UI;

/// Small helpers shared by the window, the styles and the dialogs.
public static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    public static T Res<T>(string key) => (T)Application.Current.Resources[key];

    /// Runs on the UI thread.
    public static void Post(Action a) => Application.Current?.Dispatcher.BeginInvoke(a, DispatcherPriority.Normal);

    /// <summary>
    /// Letter-spaced text (the "2px tracking" of the design): WPF has no letter-spacing property,
    /// so a thin space is put between the characters. Set <c>ui:Ui.SpacedText="MORE THAN CHOCOLATE"</c>
    /// on a TextBlock instead of Text.
    /// </summary>
    public static readonly DependencyProperty SpacedTextProperty = DependencyProperty.RegisterAttached(
        "SpacedText", typeof(string), typeof(Ui),
        new PropertyMetadata(null, (d, e) => { if (d is TextBlock tb) tb.Text = Spaced((string?)e.NewValue); }));

    public static string? GetSpacedText(DependencyObject d) => (string?)d.GetValue(SpacedTextProperty);
    public static void SetSpacedText(DependencyObject d, string? v) => d.SetValue(SpacedTextProperty, v);

    public static string Spaced(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        const char thin = ' ';
        var chars = new List<char>(s.Length * 2);
        for (var i = 0; i < s.Length; i++)
        {
            chars.Add(s[i]);
            if (i < s.Length - 1) chars.Add(thin);
        }
        return new string(chars.ToArray());
    }
}
