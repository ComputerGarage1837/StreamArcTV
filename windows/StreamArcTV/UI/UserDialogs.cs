using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using StreamArcTV.Data;

namespace StreamArcTV.UI;

/// The "Who's watching?" screens: pick, add, edit and remove VOD users.
public static class UserDialogs
{
    public static Brush ColorBrush(string hex)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(hex)!; } catch { return Brushes.SlateBlue; }
    }

    /// A round coloured avatar with the user's initial.
    public static FrameworkElement Avatar(string name, string color, double size)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Ellipse { Fill = ColorBrush(color) });
        var letter = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();
        g.Children.Add(new TextBlock
        {
            Text = letter, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = size * 0.5,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        return g;
    }

    /// Settles who is watching when VOD opens: asks for a first user if there is none, offers the choice
    /// once per launch if there are several.
    public static void Ensure(Action onChanged)
    {
        if (!VodUsers.HasUsers)
        {
            Create(first: true, null, () => { VodUsers.ChosenThisLaunch = true; onChanged(); });
        }
        else if (VodUsers.All().Count > 1 && !VodUsers.ChosenThisLaunch)
        {
            VodUsers.ChosenThisLaunch = true;
            Choose(onChanged);
        }
        else VodUsers.ChosenThisLaunch = true;
    }

    private static Button Row(FrameworkElement avatar, string text, bool check)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        avatar.Margin = new Thickness(0, 0, 14, 0);
        line.Children.Add(avatar);
        line.Children.Add(new TextBlock { Text = text, FontSize = 17, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("TextBrush") });
        if (check) line.Children.Add(new TextBlock { Text = "  ✓", FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("AccentBrush") });
        return new Button { Content = line, Style = Ui.Res<Style>("ListRowButton"), HorizontalContentAlignment = HorizontalAlignment.Left };
    }

    /// Pick who is watching.
    public static void Choose(Action onChanged)
    {
        var cur = VodUsers.Current;
        var panel = new StackPanel { MinWidth = 340 };
        DialogWindow? win = null;
        string action = "";
        VodUsers.User? picked = null;
        Button? first = null;
        void Done(string a, VodUsers.User? u = null) { action = a; picked = u; if (win != null) { win.Result = DialogResultKind.Positive; win.Close(); } }
        foreach (var u in VodUsers.All())
        {
            var user = u;
            var b = Row(Avatar(u.Name, u.Color, 44), u.Name, u.Id == cur?.Id);
            b.Click += (_, _) => Done("pick", user);
            panel.Children.Add(b);
            if (first == null || u.Id == cur?.Id) first = b;
        }
        var add = Row(Avatar("+", "#475569", 44), "Add user", false);
        add.Click += (_, _) => Done("add");
        panel.Children.Add(add);
        var manage = Row(Avatar("⚙", "#475569", 44), "Manage users", false);
        manage.Click += (_, _) => Done("manage");
        panel.Children.Add(manage);
        Dialogs.Custom("Who's watching?", new ScrollViewer { Content = panel, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            null, "Close", focus: first, setup: d => win = d);
        switch (action)
        {
            case "pick":
                if (picked != null && picked.Id != cur?.Id)
                {
                    VodUsers.SetActive(picked.Id);
                    Dialogs.Toast($"Watching as {picked.Name}");
                    onChanged();
                }
                break;
            case "add": Create(false, null, onChanged); break;
            case "manage": Manage(onChanged); break;
        }
    }

    /// Add a user (or, with edit, rename / recolour one).
    public static void Create(bool first, VodUsers.User? edit, Action onDone)
    {
        var color = edit?.Color ?? VodUsers.Colors[VodUsers.All().Count % VodUsers.Colors.Length];
        var name = edit?.Name ?? "";
        while (true)
        {
            var col = new StackPanel { MinWidth = 340 };
            if (first)
                col.Children.Add(new TextBlock
                {
                    Text = "Each person gets their own watched list, progress and My List. Who's watching?",
                    TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = Ui.Brush("TextMutedBrush"), Margin = new Thickness(0, 0, 0, 12),
                });
            var preview = new ContentControl { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
            var input = new TextBox { Style = Ui.Res<Style>("InputBox"), Tag = "Name", MaxLength = 20, Text = name };
            void Refresh() => preview.Content = Avatar(input.Text, color, 72);
            input.TextChanged += (_, _) => Refresh();
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
            var buttons = new List<Button>();
            foreach (var c in VodUsers.Colors)
            {
                var cc = c;
                var b = new Button
                {
                    Content = Avatar(" ", c, 28), Style = Ui.Res<Style>("RoundImageButton"), Width = 36, Height = 36,
                    Margin = new Thickness(4, 0, 4, 0), Opacity = c == color ? 1.0 : 0.45,
                };
                b.Click += (_, _) => { color = cc; foreach (var o in buttons) o.Opacity = 0.45; b.Opacity = 1.0; Refresh(); };
                buttons.Add(b); dots.Children.Add(b);
            }
            col.Children.Add(preview); col.Children.Add(input); col.Children.Add(dots);
            Refresh();
            var r = Dialogs.Custom(edit != null ? "Rename or change colour" : "Create a user", col, edit != null ? "Save" : "Create",
                first ? null : "Cancel", focus: input, cancelable: !first);
            if (r != DialogResultKind.Positive) return;
            name = input.Text.Trim();
            if (name.Length == 0) { Dialogs.Toast("Enter a name."); continue; }
            if (VodUsers.NameTaken(name, edit?.Id)) { Dialogs.Toast("Another user already has that name."); continue; }
            if (edit != null) VodUsers.Update(edit.Id, name, color);
            else { var u = VodUsers.Add(name, color); VodUsers.SetActive(u.Id); }
            onDone();
            return;
        }
    }

    /// Edit or remove users.
    public static void Manage(Action onChanged)
    {
        var users = VodUsers.All().ToList();
        var panel = new StackPanel { MinWidth = 340 };
        DialogWindow? win = null;
        VodUsers.User? picked = null;
        foreach (var u in users)
        {
            var user = u;
            var b = Row(Avatar(u.Name, u.Color, 44), u.Name, false);
            b.Click += (_, _) => { picked = user; if (win != null) { win.Result = DialogResultKind.Positive; win.Close(); } };
            panel.Children.Add(b);
        }
        Dialogs.Custom("Manage users", new ScrollViewer { Content = panel, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            null, "Close", focus: panel.Children.Count > 0 ? panel.Children[0] : null, setup: d => win = d);
        if (picked == null) return;
        var choice = Dialogs.Items(picked.Name, new[] { "Rename or change colour", "Delete user" });
        if (choice == 0) Create(false, picked, () => { onChanged(); Manage(onChanged); });
        else if (choice == 1)
        {
            if (VodUsers.All().Count <= 1) { Dialogs.Toast("There must be at least one user."); return; }
            if (Dialogs.Alert("Delete user", $"Delete {picked.Name}? Their watched list, progress and My List are removed. This cannot be undone.", "Delete", "Cancel") == DialogResultKind.Positive)
            { VodUsers.Remove(picked.Id); onChanged(); }
        }
    }
}
