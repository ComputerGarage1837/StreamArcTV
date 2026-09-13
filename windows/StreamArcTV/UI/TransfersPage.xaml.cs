using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using StreamArcTV.Data;
using StreamArcTV.Transfer;

namespace StreamArcTV.UI;

/// Downloads or Recordings list: progress, Play, Delete, and the folder setting.
public partial class TransfersPage : AppPage
{
    private readonly TransferType _type;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Dictionary<string, Row> _rows = new();
    private readonly Dictionary<string, string> _folderNames = new();

    private class Row
    {
        public FocusCard Card = null!;
        public TextBlock Title = null!, Subtitle = null!, Status = null!;
        public ProgressBar Progress = null!;
        public Button Play = null!, Delete = null!;
        public TransferJob Job = null!;
    }

    public TransfersPage(TransferType type)
    {
        InitializeComponent();
        _type = type;
        TxtTitle.Text = type == TransferType.RECORDING ? "Recordings" : "Downloads";
        BtnBack.Click += (_, _) => Finish();
        BtnDeleteAll.Click += (_, _) => ConfirmDeleteAll();
        RowFolder.Click += (_, _) => TransferDialogs.PickFolder(_type, _ => RenderFolder());
        RenderFolder();
        _timer.Tick += (_, _) => Refresh(keepScroll: true);
    }

    public override IInputElement? InitialFocus => _rows.Count > 0 ? List.Children.OfType<FocusCard>().FirstOrDefault() : RowFolder;

    public override void OnResume() { Refresh(); _timer.Start(); }
    public override void OnPause() => _timer.Stop();

    /// Folder names are looked up once, not per row per refresh.
    private string FolderName(string? folder)
    {
        var key = folder ?? "";
        if (!_folderNames.TryGetValue(key, out var n)) _folderNames[key] = n = Folders.Describe(folder, _type);
        return n;
    }

    private void RenderFolder()
    {
        var prefs = Prefs.Instance;
        var folder = _type == TransferType.RECORDING ? prefs.RecordingFolder : prefs.DownloadFolder;
        _folderNames.Clear();
        TxtFolder.Text = FolderName(Folders.Usable(folder) ? folder : null);
    }

    private void Refresh(bool keepScroll = false)
    {
        var all = TransferStore.Get().All().Where(j => j.Type == _type).ToList();
        int Rank(TransferJob j) => j.State switch { TransferState.RUNNING => 0, TransferState.QUEUED => 1, TransferState.SCHEDULED => 2, _ => 3 };
        var items = all.OrderBy(Rank).ThenBy(j => j.State == TransferState.SCHEDULED ? j.StartAt : -j.CreatedAt).ToList();
        var sameIds = items.Select(j => j.Id).SequenceEqual(_rows.Keys.OrderBy(k => List.Children.IndexOf(_rows[k].Card)));
        if (!(sameIds && keepScroll))
        {
            List.Children.Clear();
            _rows.Clear();
            foreach (var j in items)
            {
                var r = Build(j);
                _rows[j.Id] = r;
                List.Children.Add(r.Card);
            }
        }
        foreach (var j in items) if (_rows.TryGetValue(j.Id, out var r)) Bind(r, j);
        RenderNotice(all);
        TxtEmpty.Text = _type == TransferType.RECORDING
            ? "No recordings yet. Hold OK on a channel in the TV guide and choose Record."
            : "No downloads yet. Hold OK on a movie or episode and choose Download.";
        TxtEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnDeleteAll.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private Row Build(TransferJob j)
    {
        var r = new Row { Job = j };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        r.Title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        r.Subtitle = new TextBlock { FontSize = 12, Foreground = Ui.Brush("TextMutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        r.Status = new TextBlock { FontSize = 12, Foreground = Ui.Brush("AccentBrush"), Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
        r.Progress = new ProgressBar { Style = Ui.Res<Style>("ThinProgress"), Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        text.Children.Add(r.Title); text.Children.Add(r.Subtitle); text.Children.Add(r.Status); text.Children.Add(r.Progress);
        grid.Children.Add(text);
        r.Play = new Button { Content = "Play", Style = Ui.Res<Style>("SmallButton"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        r.Delete = new Button { Content = "Delete", Style = Ui.Res<Style>("SmallButton"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("DangerBrush") };
        Grid.SetColumn(r.Play, 1); Grid.SetColumn(r.Delete, 2);
        grid.Children.Add(r.Play); grid.Children.Add(r.Delete);
        r.Card = new FocusCard { Content = grid, Style = Ui.Res<Style>("StreamItem"), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(14, 10, 8, 10) };
        r.Play.Click += (_, _) => Play(r.Job);
        r.Delete.Click += (_, _) => ConfirmDelete(r.Job);
        r.Card.Click += () => { if (r.Job.State == TransferState.DONE) Play(r.Job); };
        r.Card.LongClick += () => ConfirmDelete(r.Job);
        return r;
    }

    private void Bind(Row r, TransferJob j)
    {
        r.Job = j;
        r.Title.Text = j.Title;
        r.Subtitle.Text = string.Join("  ·  ", new[] { j.Subtitle, FolderName(j.Folder) }.Where(s => !string.IsNullOrWhiteSpace(s)));
        r.Progress.Visibility = Visibility.Collapsed;
        r.Status.Text = j.State switch
        {
            TransferState.SCHEDULED => $"Scheduled for {Format.Time(j.StartAt / 1000)}",
            TransferState.QUEUED => "Waiting to start…",
            TransferState.RUNNING => Running(r, j),
            TransferState.DONE => $"Done · {Format.Size(j.Bytes)}",
            TransferState.FAILED => "Failed" + (j.Error != null ? ": " + j.Error : ""),
            _ => "Cancelled"
        };
        var playable = j.State == TransferState.DONE;
        r.Play.IsEnabled = playable;
        r.Play.Opacity = playable ? 1 : 0.4;
        r.Delete.Content = j.IsActive ? "Cancel" : "Delete";
    }

    private static string Running(Row r, TransferJob j)
    {
        r.Progress.Visibility = Visibility.Visible;
        if (j.Type == TransferType.RECORDING)
        {
            var total = Math.Max(1, j.EndAt - j.StartAt);
            var done = Math.Clamp(Format.NowMs - j.StartAt, 0, total);
            r.Progress.IsIndeterminate = false; r.Progress.Value = done * 1000 / total;
            return $"Recording until {Format.Time(j.EndAt / 1000)} · {Format.Size(j.Bytes)}";
        }
        TransferService.Live.TryGetValue(j.Id, out var lp);
        var bytes = lp?.Bytes ?? j.Bytes;
        var totalB = lp?.Total ?? j.Total;
        var speed = lp != null && lp.BytesPerSec > 0 ? $"  ·  {Format.Size((long)lp.BytesPerSec)}/s" : "";
        if (totalB > 0)
        {
            r.Progress.IsIndeterminate = false; r.Progress.Value = bytes * 1000 / totalB;
            return $"{bytes * 100 / totalB}%  ·  {Format.Size(bytes)} / {Format.Size(totalB)}{speed}" + (j.Error != null ? "\n" + j.Error : "");
        }
        r.Progress.IsIndeterminate = true;
        return Format.Size(bytes) + speed + (j.Error != null ? "\n" + j.Error : "");
    }

    private void ConfirmDeleteAll()
    {
        var all = TransferStore.Get().All().Where(j => j.Type == _type).ToList();
        if (all.Count == 0) return;
        var active = all.Count(j => j.IsActive);
        var what = _type == TransferType.RECORDING ? "recordings" : "downloads";
        var msg = $"Delete all {all.Count} {what} from this device? This cannot be undone." +
                  (active > 0 ? $"\n\n{active} in progress or scheduled will be cancelled." : "");
        if (Dialogs.Alert("Delete all", msg, "Delete all", "Cancel") != DialogResultKind.Positive) return;
        var store = TransferStore.Get();
        foreach (var j in all)
        {
            if (j.IsActive) TransferService.Cancel(j.Id);
            Folders.Delete(j.FileUri);
            store.Remove(j.Id);
        }
        Dialogs.Toast($"Deleted {all.Count} items.");
        Refresh();
    }

    /// Recordings only: what is recording right now and what is scheduled, above the list.
    private void RenderNotice(List<TransferJob> all)
    {
        if (_type != TransferType.RECORDING) { NoticeBox.Visibility = Visibility.Collapsed; return; }
        var running = all.Where(j => j.State == TransferState.RUNNING).ToList();
        var scheduled = all.Where(j => j.State == TransferState.SCHEDULED).OrderBy(j => j.StartAt).ToList();
        if (running.Count == 0 && scheduled.Count == 0) { NoticeBox.Visibility = Visibility.Collapsed; return; }
        var today = DateTime.Now.ToString("ddd MMM d");
        string When(long ms)
        {
            var d = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
            var day = d.ToString("ddd MMM d");
            return day == today ? Format.Time(ms / 1000) : $"{day} {Format.Time(ms / 1000)}";
        }
        var lines = new List<string>();
        foreach (var j in running) lines.Add($"● Recording now: {j.Title}, until {When(j.EndAt)}");
        foreach (var j in scheduled) lines.Add($"○ Scheduled: {j.Title}, {When(j.StartAt)} – {When(j.EndAt)}");
        TxtNotice.Text = string.Join("\n", lines);
        NoticeBox.Visibility = Visibility.Visible;
    }

    private void Play(TransferJob j)
    {
        if (j.State != TransferState.DONE && j.State != TransferState.RUNNING) { Dialogs.Toast("This item hasn't finished yet."); return; }
        var path = Folders.Exists(j.FileUri) ? j.FileUri : null;
        if (path == null) { Dialogs.Toast("The file is no longer on this device."); return; }
        var key = WatchProgress.DownloadKey(j.Id);
        WatchProgress.Describe(key, WatchProgress.KIND_DOWNLOAD, j.Title, null, null, j.Id, subtitle: j.Subtitle);
        Nav.Push(new PlayerPage(path, j.Title, false, key));
    }

    private void ConfirmDelete(TransferJob j)
    {
        var active = j.IsActive;
        var r = Dialogs.Alert(active ? "Cancel" : "Delete", $"Delete {j.Title} from this device?", active ? "Cancel download" : "Delete", "Keep");
        if (r != DialogResultKind.Positive) return;
        if (active) TransferService.Cancel(j.Id);
        Folders.Delete(j.FileUri);
        TransferStore.Get().Remove(j.Id);
        Refresh();
    }
}
