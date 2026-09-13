using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StreamArcTV.Data;
using StreamArcTV.Transfer;

namespace StreamArcTV.UI;

/// One thing to download: title for the list, URL and file extension.
public record DownloadItem(string Title, string Subtitle, string Url, string Ext);

public static class TransferDialogs
{
    /// <summary>
    /// Queues every item into the saved download folder. The picker only appears the first time
    /// (or if the saved folder is no longer reachable); the folder is changed in Settings.
    /// </summary>
    public static void Download(IList<DownloadItem> items)
    {
        if (items.Count == 0) return;
        var prefs = Prefs.Instance;
        var current = prefs.DownloadFolder != null && Folders.Usable(prefs.DownloadFolder) ? prefs.DownloadFolder : null;
        if (current == null)
        {
            PickFolder(TransferType.DOWNLOAD, folder => EnqueueDownloads(items, folder));
            return;
        }
        EnqueueDownloads(items, current);
    }

    /// Opens the folder picker, saves the choice for this type, and continues with it (null = app folder).
    public static void PickFolder(TransferType type, Action<string?> then)
    {
        var prefs = Prefs.Instance;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = type == TransferType.RECORDING ? "Recordings folder" : "Download folder",
            InitialDirectory = (type == TransferType.RECORDING ? prefs.RecordingFolder : prefs.DownloadFolder) ?? Folders.AppDir(type)
        };
        var ok = App.Window != null ? dlg.ShowDialog(App.Window) : dlg.ShowDialog();
        if (ok == true)
        {
            var s = dlg.FolderName;
            if (type == TransferType.RECORDING) prefs.RecordingFolder = s; else prefs.DownloadFolder = s;
            then(s);
        }
        else if ((type == TransferType.RECORDING ? prefs.RecordingFolder : prefs.DownloadFolder) == null)
        {
            // Cancelled with nothing saved yet: use the app folder so the action still works.
            then(null);
        }
    }

    private static void EnqueueDownloads(IList<DownloadItem> items, string? folder)
    {
        foreach (var it in items)
        {
            var job = TransferJob.Create(TransferType.DOWNLOAD, it.Title, it.Subtitle, it.Url,
                Folders.SafeName(it.Title) + "." + (string.IsNullOrWhiteSpace(it.Ext) ? "mp4" : it.Ext), folder, 0, 0);
            TransferService.Enqueue(job);
        }
        Dialogs.Toast(items.Count == 1 ? $"Downloading {items[0].Title}." : $"Downloading {items.Count} items.");
    }

    /// Start and end clock times (to the minute), then folder and schedule.
    public static void Record(string channelName, string url)
    {
        var now = DateTime.Now;
        var startCal = now.AddMinutes(1);
        var endCal = startCal.AddHours(1);

        var panel = new StackPanel { Margin = new Thickness(4, 0, 4, 0), MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = "Start", Foreground = Ui.Brush("AccentBrush"), FontSize = 13 });
        var start = new TimeWheels(startCal.Hour, startCal.Minute);
        panel.Children.Add(start.View);
        panel.Children.Add(new TextBlock { Text = "End", Foreground = Ui.Brush("AccentBrush"), FontSize = 13, Margin = new Thickness(0, 8, 0, 0) });
        var end = new TimeWheels(endCal.Hour, endCal.Minute);
        panel.Children.Add(end.View);
        var summary = new TextBlock { Foreground = Ui.Brush("TextBrush"), FontSize = 14, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(summary);
        var prefs = Prefs.Instance;
        panel.Children.Add(new TextBlock
        {
            Text = "Folder: " + Folders.Describe(Folders.Usable(prefs.RecordingFolder) ? prefs.RecordingFolder : null, TransferType.RECORDING),
            Foreground = Ui.Brush("TextMutedBrush"), FontSize = 13, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap
        });

        (long, long) Times() => Resolve(start.Hour24, start.Minute, end.Hour24, end.Minute);
        void Summarize()
        {
            var (st, en) = Times();
            var s = DateTimeOffset.FromUnixTimeMilliseconds(st).LocalDateTime;
            var e = DateTimeOffset.FromUnixTimeMilliseconds(en).LocalDateTime;
            var sameDay = s.Date == e.Date;
            summary.Text = sameDay
                ? $"{s:ddd MMM d}   {s:h:mm tt} – {e:h:mm tt}"
                : $"{s:ddd MMM d} {s:h:mm tt} – {e:ddd MMM d} {e:h:mm tt}";
        }
        start.Changed += Summarize;
        end.Changed += Summarize;
        Summarize();

        var r = Dialogs.Custom($"Record {channelName}", panel, "Record", "Cancel", focus: start.HourBox);
        if (r != DialogResultKind.Positive) return;
        var (st2, en2) = Times();
        var current = Folders.Usable(prefs.RecordingFolder) ? prefs.RecordingFolder : null;
        if (current == null && prefs.RecordingFolder == null)
            PickFolder(TransferType.RECORDING, f => Schedule(channelName, url, st2, en2, f));
        else
            Schedule(channelName, url, st2, en2, current);
    }

    /// One hour box that runs through the whole day (12 AM … 11 AM, 12 PM … 11 PM) plus a minute box.
    private class TimeWheels
    {
        public readonly ComboBox HourBox = new() { Width = 110, Style = null };
        public readonly ComboBox MinuteBox = new() { Width = 80 };
        public readonly StackPanel View = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        public event Action? Changed;

        public TimeWheels(int hour24, int minute)
        {
            for (var h = 0; h < 24; h++)
            {
                var h12 = h % 12 == 0 ? 12 : h % 12;
                HourBox.Items.Add($"{h12} {(h < 12 ? "AM" : "PM")}");
            }
            for (var m = 0; m < 60; m++) MinuteBox.Items.Add(m.ToString("00", CultureInfo.InvariantCulture));
            HourBox.SelectedIndex = hour24;
            MinuteBox.SelectedIndex = minute;
            foreach (var cb in new[] { HourBox, MinuteBox })
            {
                cb.FontSize = 16;
                cb.MaxDropDownHeight = 260;
                cb.SelectionChanged += (_, _) => Changed?.Invoke();
            }
            View.Children.Add(HourBox);
            View.Children.Add(new TextBlock { Text = ":", Foreground = Ui.Brush("TextBrush"), FontSize = 22, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            View.Children.Add(MinuteBox);
        }

        public int Hour24 => Math.Max(0, HourBox.SelectedIndex);
        public int Minute => Math.Max(0, MinuteBox.SelectedIndex);
    }

    /// Turns start/end clock times into epoch millis: a start earlier than now means tomorrow
    /// (unless it is within the last minute, which means "now"), and an end at or before the
    /// start rolls over to the next day.
    private static (long, long) Resolve(int sh, int sm, int eh, int em)
    {
        var now = DateTimeOffset.Now;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, sh, sm, 0, now.Offset);
        var st = start.ToUnixTimeMilliseconds();
        var nowMs = now.ToUnixTimeMilliseconds();
        if (st < nowMs - 90_000) st += 24 * 3600 * 1000L;
        if (st < nowMs) st = nowMs;
        var stLocal = DateTimeOffset.FromUnixTimeMilliseconds(st).ToLocalTime();
        var end = new DateTimeOffset(stLocal.Year, stLocal.Month, stLocal.Day, eh, em, 0, stLocal.Offset);
        var en = end.ToUnixTimeMilliseconds();
        if (en <= st) en += 24 * 3600 * 1000L;
        return (st, en);
    }

    private static void Schedule(string channel, string url, long startAt, long endAt, string? folder)
    {
        var s = DateTimeOffset.FromUnixTimeMilliseconds(startAt).LocalDateTime;
        var e = DateTimeOffset.FromUnixTimeMilliseconds(endAt).LocalDateTime;
        var job = TransferJob.Create(TransferType.RECORDING, channel,
            $"{s:ddd MMM d} · {s:h:mm tt} – {e:h:mm tt}", url,
            Folders.SafeName($"{channel} {s:yyyy-MM-dd HH.mm}") + ".ts", folder, startAt, endAt);
        TransferService.Enqueue(job);
        var startsNow = startAt <= Format.NowMs + 60_000;
        Dialogs.Toast(startsNow ? $"Recording {channel}." : $"{channel} will be recorded at {s:h:mm tt}.");
    }
}
