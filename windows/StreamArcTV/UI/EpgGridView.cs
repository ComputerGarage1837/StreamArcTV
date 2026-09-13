using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamArcTV.Data;

namespace StreamArcTV.UI;

/// <summary>
/// Classic TV-guide grid: channels down the left, a time ruler across the top, programme blocks
/// sized by duration, a "now" line, and keyboard navigation (left/right between programmes,
/// up/down between channels, Enter to play, hold Enter or right-click for the context menu).
/// Everything is drawn directly so it stays smooth with hundreds of channels.
/// </summary>
public class EpgGridView : FrameworkElement
{
    public interface IListener
    {
        void OnFocusChanged(Data.Stream channel, EpgProgramme? programme);
        void OnChannelClick(Data.Stream channel);
        void OnChannelLongClick(Data.Stream channel);
        /// Called once per channel the first time it scrolls into view.
        void OnNeedEpg(Data.Stream channel);
    }

    public IListener? Listener { get; set; }

    /// Returns the channel's programmes from a preloaded guide, or null to fall back to a fetch.
    public Func<Data.Stream, List<EpgProgramme>?>? GuideLookup { get; set; }

    private HashSet<string> _favorites = new();
    public HashSet<string> Favorites { get => _favorites; set { _favorites = value; InvalidateVisual(); } }

    private List<Data.Stream> _channels = new();
    private readonly Dictionary<string, List<EpgProgramme>?> _epg = new();   // null = requested, not loaded yet
    private readonly Dictionary<string, BitmapSource?> _logos = new();

    private double _channelColW = 170;
    private const double RowH = 58;
    private const double HeaderH = 32;
    /// Scale chosen so about two hours fit across the programme area (never tighter than 3.4px/min).
    private double _pxPerMin = 3.4;
    private const double HoursAcross = 2;
    private const double Gap = 2;
    private const double Corner = 6;

    private readonly long _windowStart;
    private readonly long _windowEnd;
    private double _scrollX;
    private double _scrollY;
    private int _focusRow;
    private long _focusTime = Format.NowSec;

    private static readonly Brush BgBrush = Frozen(0xFF0A1628);
    private static readonly Brush ColBrush = Frozen(0xFF0D1F3C);
    private static readonly Brush HeaderBrush = Frozen(0xFF0F2140);
    private static readonly Pen LinePen = FrozenPen(0xFF1E3A5F, 1);
    private static readonly Pen NowPen = FrozenPen(0xFFA78BFA, 2);
    private static readonly Brush CellBrush = Frozen(0xFF2B3243);
    private static readonly Brush CellNowBrush = Frozen(0xFF3A4358);
    private static readonly Brush CellPastBrush = Frozen(0xFF222838);
    private static readonly Brush CellFocusBrush = Frozen(0xFFF8FAFC);
    private static readonly Pen FocusStroke = FrozenPen(0xFFA78BFA, 2.5);
    private static readonly Brush RowFocusBrush = Frozen(0x1AFFFFFF);
    private static readonly Brush TitleBrush = Brushes.White;
    private static readonly Brush TitleFocusBrush = Frozen(0xFF0A1628);
    private static readonly Brush SubBrush = Frozen(0xFF94A3B8);
    private static readonly Brush SubFocusBrush = Frozen(0xFF475569);
    private static readonly Brush MarkerBrush = Frozen(0xFFA78BFA);
    private static readonly Brush TimeBrush = Frozen(0xFFCBD5E1);
    private static readonly Typeface Face = new("Segoe UI");
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private bool _holdFired, _keyDown;
    private static readonly ImageSource Placeholder = Ui.Res<ImageSource>("PlaceholderImage");

    private static Brush Frozen(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(uint argb, double w)
    {
        var p = new Pen(Frozen(argb), w);
        p.Freeze();
        return p;
    }

    public EpgGridView()
    {
        var now = Format.NowSec;
        _windowStart = (now / 1800) * 1800 - 1800;          // half-hour boundary, one slot back
        _windowEnd = _windowStart + 24 * 3600;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        _ticker.Tick += (_, _) => InvalidateVisual();
        _hold.Tick += (_, _) => { _hold.Stop(); if (_keyDown) { _holdFired = true; FocusedChannel()?.Let(c => Listener?.OnChannelLongClick(c)); } };
        Loaded += (_, _) => _ticker.Start();
        Unloaded += (_, _) => _ticker.Stop();
    }

    // ---- Data ----------------------------------------------------------

    public void SetChannels(List<Data.Stream> list)
    {
        _channels = list;
        _focusRow = Math.Clamp(_focusRow, 0, Math.Max(0, list.Count - 1));
        _scrollY = 0;
        InvalidateVisual();
        if (list.Count > 0) Dispatcher.BeginInvoke(NotifyFocus);
    }

    private bool _invalidatePending;

    public void SetEpg(string streamId, List<EpgProgramme> programmes)
    {
        _epg[streamId] = programmes;
        if (!_invalidatePending)
        {
            _invalidatePending = true;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            t.Tick += (_, _) => { t.Stop(); _invalidatePending = false; InvalidateVisual(); };
            t.Start();
        }
        if (FocusedChannel()?.StreamId == streamId) NotifyFocus();
    }

    public Data.Stream? FocusedChannel() => _focusRow >= 0 && _focusRow < _channels.Count ? _channels[_focusRow] : null;

    // ---- Lifecycle -----------------------------------------------------

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        var w = ActualWidth;
        // Narrow windows: a slimmer channel column so the programmes get the width.
        _channelColW = w < 520 ? 118 : 170;
        var area = w - _channelColW;
        if (area > 0)
        {
            var focusMinutes = _scrollX / _pxPerMin;
            _pxPerMin = Math.Max(3.4, area / (HoursAcross * 60));
            _scrollX = Math.Clamp(focusMinutes * _pxPerMin, 0, MaxScrollX());
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
        NotifyFocus();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _hold.Stop(); _keyDown = false;
        InvalidateVisual();
    }

    // ---- Programme helpers ---------------------------------------------

    private List<EpgProgramme> ProgrammesFor(Data.Stream ch)
    {
        var id = ch.StreamId;
        if (id == null) return new();
        if (!_epg.ContainsKey(id))
        {
            var bulk = GuideLookup?.Invoke(ch);
            if (bulk != null) _epg[id] = bulk;
            else
            {
                _epg[id] = null;
                Listener?.OnNeedEpg(ch);
            }
        }
        return _epg[id] ?? new();
    }

    /// Call after the full guide arrives: rows that were still loading resolve from it.
    public void GuideLoaded()
    {
        foreach (var id in _epg.Where(kv => kv.Value == null || kv.Value.Count == 0).Select(kv => kv.Key).ToList()) _epg.Remove(id);
        InvalidateVisual();
        NotifyFocus();
    }

    private EpgProgramme VisiblePlaceholder(Data.Stream ch)
    {
        var loaded = ch.StreamId != null && _epg.TryGetValue(ch.StreamId, out var l) && l != null;
        return new EpgProgramme(loaded ? "No programme information" : "Loading…", "", _windowStart, _windowEnd);
    }

    /// The programme under the focus time on a row, or a placeholder covering the window.
    private EpgProgramme ProgrammeAt(Data.Stream ch, long time)
    {
        var list = ProgrammesFor(ch);
        var hit = list.FirstOrDefault(p => time >= p.Start && time < p.End);
        if (hit != null) return hit;
        var next = list.FirstOrDefault(p => p.Start > time);
        if (next != null)
        {
            // Gap: synthesize an empty block up to the next programme.
            var prevEnd = list.LastOrDefault(p => p.End <= time)?.End ?? _windowStart;
            return new EpgProgramme("", "", prevEnd, next.Start);
        }
        if (list.Count == 0) return VisiblePlaceholder(ch);
        return new EpgProgramme("", "", list[^1].End, _windowEnd);
    }

    private void NotifyFocus()
    {
        var ch = FocusedChannel();
        if (ch == null) return;
        var p = ProgrammeAt(ch, _focusTime);
        Listener?.OnFocusChanged(ch, !string.IsNullOrWhiteSpace(p.Title) && p.Title != "Loading…" && p.Title != "No programme information" ? p : null);
    }

    // ---- Geometry ------------------------------------------------------

    private double XFor(long time) => _channelColW + (time - _windowStart) / 60.0 * _pxPerMin - _scrollX;
    private double MaxScrollX() => Math.Max(0, (_windowEnd - _windowStart) / 60.0 * _pxPerMin - (ActualWidth - _channelColW));
    private double MaxScrollY() => Math.Max(0, _channels.Count * RowH - (ActualHeight - HeaderH));

    private void EnsureFocusVisible()
    {
        var ch = FocusedChannel();
        if (ch == null) return;
        var p = ProgrammeAt(ch, _focusTime);
        var startX = (Math.Max(p.Start, _windowStart) - _windowStart) / 60.0 * _pxPerMin;
        var endX = (Math.Min(p.End, _windowEnd) - _windowStart) / 60.0 * _pxPerMin;
        var viewW = ActualWidth - _channelColW;
        if (startX < _scrollX) _scrollX = startX;
        else if (Math.Min(endX, startX + viewW * 0.6) > _scrollX + viewW) _scrollX = startX - viewW * 0.25;
        _scrollX = Math.Clamp(_scrollX, 0, MaxScrollX());

        var top = _focusRow * RowH;
        var viewH = ActualHeight - HeaderH;
        if (top < _scrollY) _scrollY = top;
        else if (top + RowH > _scrollY + viewH) _scrollY = top + RowH - viewH;
        _scrollY = Math.Clamp(_scrollY, 0, MaxScrollY());
    }

    // ---- Drawing -------------------------------------------------------

    private FormattedText Text(string s, double size, Brush brush, double maxWidth, FontWeight? weight = null)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, MaxTextWidth = Math.Max(1, maxWidth)
        };
        if (weight != null) ft.SetFontWeight(weight.Value);
        return ft;
    }

    protected override void OnRender(DrawingContext c)
    {
        var width = ActualWidth; var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        c.DrawRectangle(BgBrush, null, new Rect(0, 0, width, height));
        var now = Format.NowSec;
        var focused = IsKeyboardFocused;
        var firstRow = Math.Max(0, (int)(_scrollY / RowH));
        var lastRow = Math.Min(_channels.Count - 1, (int)((_scrollY + height - HeaderH) / RowH) + 1);

        // Programme area
        c.PushClip(new RectangleGeometry(new Rect(_channelColW, HeaderH, Math.Max(0, width - _channelColW), Math.Max(0, height - HeaderH))));
        for (var row = firstRow; row <= lastRow; row++)
        {
            var ch = _channels[row];
            var top = HeaderH + row * RowH - _scrollY;
            var rowFocused = row == _focusRow && focused;
            if (rowFocused) c.DrawRectangle(RowFocusBrush, null, new Rect(_channelColW, top, width - _channelColW, RowH));
            var list = ProgrammesFor(ch);
            var blocks = list.Count == 0 ? new List<EpgProgramme> { VisiblePlaceholder(ch) } : list;
            foreach (var p in blocks)
            {
                if (p.End <= _windowStart || p.Start >= _windowEnd) continue;
                var x1 = XFor(Math.Max(p.Start, _windowStart)) + Gap / 2;
                var x2 = XFor(Math.Min(p.End, _windowEnd)) - Gap / 2;
                if (x2 < _channelColW || x1 > width) continue;
                var rect = new Rect(x1, top + Gap, Math.Max(0, x2 - x1), RowH - 2 * Gap);
                var isFocusedCell = rowFocused && _focusTime >= p.Start && _focusTime < p.End;
                var brush = isFocusedCell ? CellFocusBrush
                    : now >= p.Start && now < p.End ? CellNowBrush
                    : p.End <= now ? CellPastBrush
                    : CellBrush;
                c.DrawRoundedRectangle(brush, isFocusedCell ? FocusStroke : null, rect, Corner, Corner);
                // Text is pinned to the visible left edge so long blocks stay readable.
                var textLeft = Math.Max(rect.Left, _channelColW) + 8;
                var avail = rect.Right - textLeft - 6;
                if (avail > 20)
                {
                    var title = Text(p.Title, 14, isFocusedCell ? TitleFocusBrush : TitleBrush, avail);
                    c.DrawText(title, new Point(textLeft, top + RowH / 2 - 2 - title.Height + 4));
                    if (!string.IsNullOrWhiteSpace(p.Title) && p.End > p.Start && p.End - p.Start < 24 * 3600)
                    {
                        var sub = Text(Format.TimeRange(p.Start, p.End), 11, isFocusedCell ? SubFocusBrush : SubBrush, avail);
                        c.DrawText(sub, new Point(textLeft, top + RowH / 2 + 4));
                    }
                }
            }
        }
        // Now line
        if (now >= _windowStart && now <= _windowEnd)
        {
            var nx = XFor(now);
            c.DrawLine(NowPen, new Point(nx, HeaderH), new Point(nx, height));
        }
        c.Pop();

        // Time header with the date on the left and a marker at "now"
        c.DrawRectangle(HeaderBrush, null, new Rect(0, 0, width, HeaderH));
        var date = Text(DateTime.Now.ToString("ddd MMM d"), 12, TimeBrush, _channelColW - 16);
        c.DrawText(date, new Point(12, HeaderH - 10 - date.Height + 3));
        c.PushClip(new RectangleGeometry(new Rect(_channelColW, 0, Math.Max(0, width - _channelColW), HeaderH)));
        var t = _windowStart;
        while (t <= _windowEnd)
        {
            var x = XFor(t);
            if (x > _channelColW - 40 && x < width)
            {
                c.DrawLine(LinePen, new Point(x, HeaderH - 8), new Point(x, HeaderH));
                var lbl = Text(Format.Time(t), 12, TimeBrush, 80);
                c.DrawText(lbl, new Point(x + 6, HeaderH - 10 - lbl.Height + 3));
            }
            t += 1800;
        }
        if (now >= _windowStart && now <= _windowEnd)
        {
            var nx = XFor(now);
            var path = new StreamGeometry();
            using (var g = path.Open())
            {
                g.BeginFigure(new Point(nx - 7, HeaderH - 9), true, true);
                g.LineTo(new Point(nx + 7, HeaderH - 9), false, false);
                g.LineTo(new Point(nx, HeaderH), false, false);
            }
            path.Freeze();
            c.DrawGeometry(MarkerBrush, null, path);
        }
        c.Pop();

        // Channel column
        c.DrawRectangle(ColBrush, null, new Rect(0, 0, _channelColW, height));
        c.PushClip(new RectangleGeometry(new Rect(0, HeaderH, _channelColW, Math.Max(0, height - HeaderH))));
        var narrow = _channelColW < 150;
        for (var row = firstRow; row <= lastRow; row++)
        {
            var ch = _channels[row];
            var top = HeaderH + row * RowH - _scrollY;
            if (row == _focusRow && focused) c.DrawRectangle(RowFocusBrush, null, new Rect(0, top, _channelColW, RowH));
            c.DrawLine(LinePen, new Point(0, top + RowH), new Point(_channelColW, top + RowH));
            double logoW = narrow ? 34 : 44, logoH = narrow ? 24 : 30;
            double lx = narrow ? 6 : 8, ly = top + (RowH - logoH) / 2;
            var bmp = LogoFor(ch);
            if (bmp != null)
            {
                var scale = Math.Min(logoW / bmp.PixelWidth, logoH / bmp.PixelHeight);
                var w = bmp.PixelWidth * scale; var h = bmp.PixelHeight * scale;
                c.DrawImage(bmp, new Rect(lx + (logoW - w) / 2, ly + (logoH - h) / 2, w, h));
            }
            else
            {
                c.DrawImage(Placeholder, new Rect(lx, ly, logoW, logoH));
            }
            var name = (ch.StreamId != null && _favorites.Contains(ch.StreamId) ? "★ " : "") + (ch.Name ?? "");
            var nameAvail = _channelColW - lx - logoW - (narrow ? 10 : 16);
            var txt = Text(name, narrow ? 11.5 : 13, Brushes.White, nameAvail);
            c.DrawText(txt, new Point(lx + logoW + (narrow ? 5 : 8), top + RowH / 2 - txt.Height / 2));
        }
        c.Pop();
        c.DrawLine(LinePen, new Point(_channelColW, 0), new Point(_channelColW, height));
        c.DrawLine(LinePen, new Point(0, HeaderH), new Point(width, HeaderH));
    }

    private BitmapSource? LogoFor(Data.Stream ch)
    {
        var id = ch.StreamId;
        if (id == null) return null;
        if (_logos.TryGetValue(id, out var cached)) return cached;
        _logos[id] = null;
        var url = ch.Icon;
        if (string.IsNullOrWhiteSpace(url)) return null;
        ImageLoader.Get(url, 176, bmp => { _logos[id] = bmp; if (bmp != null) InvalidateVisual(); });
        return null;
    }

    // ---- Keyboard ------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var ch = FocusedChannel();
        if (ch == null) { base.OnKeyDown(e); return; }
        switch (e.Key)
        {
            case Key.Right:
            {
                var p = ProgrammeAt(ch, _focusTime);
                if (p.End >= _windowEnd) { e.Handled = true; return; }
                _focusTime = p.End;
                Moved(); e.Handled = true; return;
            }
            case Key.Left:
            {
                var p = ProgrammeAt(ch, _focusTime);
                var now = Format.NowSec;
                if (p.Start <= _windowStart || p.Start <= now && _focusTime <= now) return;   // let focus leave to the category list
                _focusTime = Math.Max(p.Start - 1, _windowStart);
                Moved(); e.Handled = true; return;
            }
            case Key.Down:
                if (_focusRow >= _channels.Count - 1) { e.Handled = true; return; }
                _focusRow++; Moved(); e.Handled = true; return;
            case Key.Up:
                if (_focusRow == 0) return;   // let focus leave to the top bar
                _focusRow--; Moved(); e.Handled = true; return;
            case Key.PageDown:
                _focusRow = Math.Min(_channels.Count - 1, _focusRow + Math.Max(1, (int)((ActualHeight - HeaderH) / RowH) - 1)); Moved(); e.Handled = true; return;
            case Key.PageUp:
                _focusRow = Math.Max(0, _focusRow - Math.Max(1, (int)((ActualHeight - HeaderH) / RowH) - 1)); Moved(); e.Handled = true; return;
            case Key.Home:
                _focusRow = 0; Moved(); e.Handled = true; return;
            case Key.End:
                _focusRow = Math.Max(0, _channels.Count - 1); Moved(); e.Handled = true; return;
            case Key.Enter or Key.Space:
                e.Handled = true;
                if (e.IsRepeat) return;
                _keyDown = true; _holdFired = false;
                _hold.Start();
                return;
            case Key.Apps:
                e.Handled = true;
                Listener?.OnChannelLongClick(ch);
                return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            _hold.Stop();
            if (_keyDown && !_holdFired) FocusedChannel()?.Let(c => Listener?.OnChannelClick(c));
            _keyDown = false; _holdFired = false;
            return;
        }
        base.OnKeyUp(e);
    }

    private void Moved()
    {
        EnsureFocusVisible();
        InvalidateVisual();
        NotifyFocus();
    }

    // ---- Mouse ---------------------------------------------------------

    private Point _dragStart;
    private bool _dragging, _dragMoved;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _dragStart = e.GetPosition(this);
        _dragging = true; _dragMoved = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var p = e.GetPosition(this);
        var dx = _dragStart.X - p.X; var dy = _dragStart.Y - p.Y;
        if (!_dragMoved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
        _dragMoved = true;
        _scrollX = Math.Clamp(_scrollX + dx, 0, MaxScrollX());
        _scrollY = Math.Clamp(_scrollY + dy, 0, MaxScrollY());
        _dragStart = p;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (_dragMoved) return;
        var p = e.GetPosition(this);
        if (!FocusAt(p.X, p.Y)) return;
        Moved();
        FocusedChannel()?.Let(c => Listener?.OnChannelClick(c));
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var p = e.GetPosition(this);
        if (!FocusAt(p.X, p.Y)) return;
        Focus(); Moved();
        FocusedChannel()?.Let(c => Listener?.OnChannelLongClick(c));
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _scrollX = Math.Clamp(_scrollX - e.Delta, 0, MaxScrollX());
        else _scrollY = Math.Clamp(_scrollY - e.Delta, 0, MaxScrollY());
        InvalidateVisual();
        e.Handled = true;
    }

    private bool FocusAt(double x, double y)
    {
        if (y < HeaderH) return false;
        var row = (int)((y - HeaderH + _scrollY) / RowH);
        if (row < 0 || row >= _channels.Count) return false;
        _focusRow = row;
        if (x > _channelColW)
            _focusTime = Math.Clamp(_windowStart + (long)((x - _channelColW + _scrollX) / _pxPerMin * 60), _windowStart, _windowEnd - 1);
        return true;
    }
}

internal static class Ext
{
    public static void Let<T>(this T v, Action<T> a) where T : class => a(v);
}
