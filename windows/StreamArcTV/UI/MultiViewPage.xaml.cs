using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using StreamArcTV.Data;
using StreamArcTV.Player;
using StreamArcTV.Util;

namespace StreamArcTV.UI;

/// <summary>
/// Two or four live channels at once. The selected tile carries the sound; Enter on a tile picks
/// its channel, holding Enter (or right-click) offers full screen or removal. Each tile is its own
/// player, so the provider must allow that many streams on the account.
/// </summary>
public partial class MultiViewPage : AppPage
{
    private static List<Data.Stream>? _cached;

    private readonly Prefs _prefs = Prefs.Instance;
    private readonly Service _service;
    private readonly Account _account = null!;
    private readonly bool _dead;
    private LibVLC? _libVlc;

    private class Tile
    {
        public FocusCard Card = null!;
        public VideoView View = null!;
        public TextBlock Hint = null!, Name = null!;
        public MediaPlayer? Player;
        public Data.Stream? Stream;
    }

    private readonly List<Tile> _tiles = new();
    private int _selected;
    private List<Data.Stream> _channels = new();
    private readonly Dictionary<int, string> _pendingIds = new();
    private bool _started;

    public MultiViewPage(Service service)
    {
        InitializeComponent();
        _service = service;
        var acct = _prefs.Account(service);
        if (acct == null) { _dead = true; Loaded += (_, _) => Finish(); return; }
        _account = acct;
        BtnBack.Click += (_, _) => Finish();
        BtnTiles.Click += (_, _) => { _prefs.MultiviewTiles = _prefs.MultiviewTiles == 4 ? 2 : 4; BuildGrid(); };
        BuildGrid();
        LoadChannels();
    }

    public override IInputElement? InitialFocus => _tiles.FirstOrDefault()?.Card;

    public override void OnResume() { if (!_dead) StartAll(); }
    public override void OnPause() => StopAll();
    public override void OnDestroy() { _libVlc = null; }

    // ---- Grid -------------------------------------------------------------------

    private void BuildGrid()
    {
        StopAll();
        Tiles.Children.Clear();
        _tiles.Clear();
        var count = _prefs.MultiviewTiles;
        BtnTiles.Content = count == 4 ? "2 × 2" : "1 × 2";
        Tiles.Rows = count == 4 ? 2 : 1;
        Tiles.Columns = 2;
        var slots = _prefs.MultiviewSlots;
        for (var i = 0; i < count; i++)
        {
            var n = i;
            var tile = new Tile();
            var overlay = new Grid { Background = System.Windows.Media.Brushes.Transparent };
            tile.Hint = new TextBlock { Text = "Press Enter to choose a channel", FontSize = 14, Foreground = Ui.Brush("TextMutedBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) };
            tile.Name = new TextBlock { FontSize = 12, Foreground = Ui.Brush("TextBrush"), Visibility = Visibility.Collapsed };
            var nameBox = new Border { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x99, 0, 0, 0)), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8), Child = tile.Name };
            overlay.Children.Add(tile.Hint);
            overlay.Children.Add(nameBox);
            tile.View = new VideoView { Content = overlay, Background = System.Windows.Media.Brushes.Black };
            tile.Card = new FocusCard { Content = tile.View, Style = Ui.Res<Style>("TileCard") };
            tile.Card.Click += () => { Select(n); PickChannel(n); };
            tile.Card.LongClick += () => { Select(n); TileMenu(n); };
            tile.Card.GotKeyboardFocus += (_, _) => Select(n);
            _tiles.Add(tile);
            Tiles.Children.Add(tile.Card);
            var id = i < slots.Count ? slots[i] : "";
            if (!string.IsNullOrWhiteSpace(id)) _pendingIds[i] = id;
        }
        Select(0);
        _tiles.FirstOrDefault()?.Card.Focus();
        ApplyPending();
        if (_started) StartAll();
    }

    /// Channels saved from last time are attached once the channel list is in.
    private void ApplyPending()
    {
        if (_channels.Count == 0) return;
        foreach (var (i, id) in _pendingIds.ToList())
        {
            var ch = _channels.FirstOrDefault(c => c.Id == id);
            if (ch != null) Assign(i, ch, save: false);
            _pendingIds.Remove(i);
        }
    }

    private async void LoadChannels()
    {
        try
        {
            var all = _cached ??= await XtreamApi.Streams(_service, _account, null, ContentKind.LIVE);
            var favs = _prefs.Favorites(_service, ContentKind.LIVE);
            _channels = all.OrderBy(c => c.Id == null || !favs.Contains(c.Id)).ThenBy(c => c.Name?.ToLowerInvariant() ?? "").ToList();
            ApplyPending();
        }
        catch (Exception e)
        {
            Dialogs.Toast(string.IsNullOrWhiteSpace(e.Message) ? "Couldn't load content." : e.Message);
        }
    }

    private void Select(int i)
    {
        _selected = i;
        for (var j = 0; j < _tiles.Count; j++)
        {
            var t = _tiles[j];
            t.Card.Selected = j == i;
            try { if (t.Player != null) t.Player.Mute = j != i; } catch { }
            t.Name.Text = (j == i && t.Stream != null ? "🔊 " : "") + (t.Stream?.Name ?? "");
        }
    }

    // ---- Channel choice ---------------------------------------------------------

    private void PickChannel(int i)
    {
        if (_channels.Count == 0) { Dialogs.Toast("Loading channels…"); return; }
        var panel = new StackPanel { MinWidth = 380 };
        var filter = new TextBox { Style = Ui.Res<Style>("InputBox"), Tag = "Search channels…" };
        var list = new ListBox
        {
            Height = 320, Margin = new Thickness(0, 8, 0, 0), Background = Ui.Brush("InputBgBrush"), BorderBrush = Ui.Brush("OutlineBrush"),
            Foreground = Ui.Brush("TextBrush"), FontSize = 14
        };
        var shown = _channels;
        void Fill() { list.ItemsSource = shown.Select(c => c.Name ?? "").ToList(); }
        Fill();
        filter.TextChanged += (_, _) =>
        {
            var q = filter.Text.Trim();
            shown = q.Length == 0 ? _channels : _channels.Where(c => c.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) == true).ToList();
            Fill();
        };
        panel.Children.Add(filter);
        panel.Children.Add(list);
        DialogWindow? dlg = null;
        var chosen = -1;
        void Choose() { if (list.SelectedIndex >= 0 && dlg != null) { chosen = list.SelectedIndex; dlg.Result = DialogResultKind.Positive; dlg.Close(); } }
        list.MouseDoubleClick += (_, _) => Choose();
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Choose(); e.Handled = true; } };
        filter.KeyDown += (_, e) => { if (e.Key == Key.Down) { list.Focus(); if (list.Items.Count > 0 && list.SelectedIndex < 0) list.SelectedIndex = 0; e.Handled = true; } };
        var r = Dialogs.Custom("Choose channel", panel, "Choose", "Cancel", focus: filter, setup: d => dlg = d);
        if (r == DialogResultKind.Positive && chosen < 0) chosen = list.SelectedIndex;
        if (chosen >= 0 && chosen < shown.Count) Assign(i, shown[chosen], save: true);
    }

    private void Assign(int i, Data.Stream stream, bool save)
    {
        if (i < 0 || i >= _tiles.Count) return;
        var tile = _tiles[i];
        tile.Stream = stream;
        tile.Hint.Visibility = Visibility.Collapsed;
        tile.Name.Visibility = Visibility.Visible;
        if (save)
        {
            var slots = _prefs.MultiviewSlots;
            _prefs.MultiviewSlots = Enumerable.Range(0, 4).Select(j => j == i ? (stream.Id ?? "") : (j < _tiles.Count ? _tiles[j].Stream?.Id ?? slots[j] : slots[j])).ToList();
        }
        Select(_selected);
        if (_started) Play(tile);
    }

    private void Remove(int i)
    {
        var tile = _tiles[i];
        Release(tile);
        tile.Stream = null;
        tile.Hint.Text = "Press Enter to choose a channel";
        tile.Hint.Visibility = Visibility.Visible;
        tile.Name.Visibility = Visibility.Collapsed;
        var slots = _prefs.MultiviewSlots;
        _prefs.MultiviewSlots = Enumerable.Range(0, 4).Select(j => j == i ? "" : slots[j]).ToList();
    }

    private void TileMenu(int i)
    {
        var tile = _tiles[i];
        var hasStream = tile.Stream != null;
        var opts = hasStream ? new[] { "Full screen", "Change channel", "Remove" } : new[] { "Choose channel" };
        var which = Dialogs.Items(tile.Stream?.Name ?? "Multi-view", opts);
        if (which < 0) return;
        if (!hasStream) { PickChannel(i); return; }
        switch (which)
        {
            case 0:
                if (tile.Stream is { } s)
                {
                    string url;
                    try { url = XtreamApi.StreamUrl(_service, _account, s, _prefs.LiveFormat); } catch { return; }
                    Nav.Push(new PlayerPage(url, s.Name ?? "", true));
                }
                break;
            case 1: PickChannel(i); break;
            case 2: Remove(i); break;
        }
    }

    // ---- Players ----------------------------------------------------------------

    private void StartAll()
    {
        _started = true;
        foreach (var t in _tiles) if (t.Stream != null && t.Player == null) Play(t);
    }

    private void StopAll()
    {
        _started = false;
        foreach (var t in _tiles) Release(t);
    }

    private void Play(Tile tile)
    {
        var s = tile.Stream;
        if (s == null) return;
        Release(tile);
        string url;
        try { url = XtreamApi.StreamUrl(_service, _account, s, _prefs.LiveFormat); }
        catch (Exception e) { tile.Hint.Text = e.Message; tile.Hint.Visibility = Visibility.Visible; return; }
        try
        {
            _libVlc ??= PlayerCore.Shared();
            var p = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true };
            p.EncounteredError += (_, _) => Ui.Post(() =>
            {
                AppLog.E("MultiView", $"tile '{s.Name}' error");
                tile.Hint.Text = "Can't play this channel.\nYour provider may not allow this many streams at once.";
                tile.Hint.Visibility = Visibility.Visible;
            });
            p.Playing += (_, _) => Ui.Post(() => tile.Hint.Visibility = Visibility.Collapsed);
            var media = new Media(_libVlc, new Uri(url));
            // Small buffers: four of these must fit beside each other.
            media.AddOption(":network-caching=2000");
            media.AddOption(":prefetch-buffer-size=8192");
            media.AddOption(":http-user-agent=" + XtreamApi.USER_AGENT);
            media.AddOption(":http-reconnect");
            p.Mute = _tiles.IndexOf(tile) != _selected;
            tile.View.MediaPlayer = p;
            tile.Player = p;
            tile.Hint.Visibility = Visibility.Collapsed;
            p.Play(media);
        }
        catch (Exception e)
        {
            AppLog.E("MultiView", "tile failed", e);
            tile.Hint.Text = e.Message; tile.Hint.Visibility = Visibility.Visible;
        }
    }

    private void Release(Tile tile)
    {
        var p = tile.Player;
        tile.Player = null;
        tile.View.MediaPlayer = null;
        if (p != null) PlayerCore.DisposePlayer(p);
    }
}
