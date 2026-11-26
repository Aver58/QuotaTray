using System.Drawing;
using System.Drawing.Drawing2D;
using CodexQuotaCore;

namespace CodexQuotaTray;

internal sealed class NativeDetails : IDisposable
{
    private const int Header = 38, Footer = 25, RowHeight = 26, Padding = 15;
    private readonly NativeWindow _window;
    private readonly HoverTip _tip = new();
    private readonly ProviderIcons _icons;
    private NativeHud? _anchor;
    private CodexQuotaSnapshot _snapshot = CodexQuotaSnapshot.Empty("正在读取额度…");
    private IReadOnlyList<QuotaDetailRow> _rows = [];
    private TokenChartBar[] _bars = [];
    private int _dpi = 96, _hoverBar = -1;
    private float _width, _height, _scroll, _scrollMax;
    private bool _week, _tracking, _placing;
    private RectangleF _close, _recent, _weekly, _chart, _balanceError;
    private Point? _drag;
    private Point _dragWindow;

    internal NativeDetails(ProviderIcons icons, bool topmost)
    {
        _icons = icons;
        _window = new NativeWindow("QuotaTray 额度详情", Win32.ExLayered | Win32.ExToolWindow);
        _window.MessageReceived = OnMessage;
        _window.SetTopmost(topmost);
    }

    internal event Action? Hidden;
    internal nint Handle => _window.Handle;
    internal bool IsVisible => _window.IsVisible;
    internal QuotaSource Source { get; private set; }
    private bool Volcano => Source == QuotaSource.VolcengineAgentPlan;
    private bool ShowChart => !Volcano || _bars.Length > 0;
    private int BalanceHeight => Volcano ? (string.IsNullOrEmpty(_snapshot.VolcanoBalanceError) ? 50 : 65) : 0;
    private float BodyHeight => 17 + _rows.Count * RowHeight + BalanceHeight + (ShowChart ? 145 : 53);

    internal void Open(QuotaSource source, CodexQuotaSnapshot snapshot, NativeHud? anchor)
    {
        _anchor = anchor;
        _scroll = 0;
        SetData(source, snapshot);
        Place(firstShow: true);
        _window.Show();
        Win32.SetForegroundWindow(Handle);
    }

    internal void ApplySnapshot(CodexQuotaSnapshot snapshot)
    {
        SetData(Source, snapshot);
        if (IsVisible) Place();
    }

    private void SetData(QuotaSource source, CodexQuotaSnapshot snapshot)
    {
        Source = source;
        _snapshot = snapshot;
        _rows = QuotaDetails.Rows(snapshot, source);
        _bars = QuotaDetails.Chart(snapshot, _week, Volcano);
        _hoverBar = -1;
        _tip.Hide();
    }

    internal void Hide()
    {
        if (!IsVisible) return;
        _tip.Hide();
        _window.Hide();
        _drag = null;
        if (Win32.GetCapture() == Handle) Win32.ReleaseCapture();
        Hidden?.Invoke();
    }

    internal void SetTopmost(bool value) => _window.SetTopmost(value);

    internal void Place(bool firstShow = false)
    {
        if (_placing || (!firstShow && !IsVisible)) return;
        _placing = true;
        try
        {
            var current = _window.Bounds;
            var focus = _anchor is { IsVisible: true } hud ? hud.Bounds.Location
                : firstShow ? Win32.Cursor : current.Location;
            var work = Win32.WorkArea(focus);
            _dpi = _anchor?.Dpi ?? Win32.Dpi(Handle);
            var gap = Win32.Px(5, _dpi);
            var available = work.Size;
            var side = _anchor?.Side ?? HudEdge.Right;
            if (_anchor is not null)
            {
                available = HudPlacement.SpaceForDetails(work, _anchor.Bounds, side, gap);
                if (available.Width < Win32.Px(258, _dpi) || available.Height < Win32.Px(118, _dpi))
                {
                    var other = side switch
                    {
                        HudEdge.Left => HudEdge.Right, HudEdge.Right => HudEdge.Left,
                        HudEdge.Top => HudEdge.Bottom, _ => HudEdge.Top
                    };
                    var opposite = HudPlacement.SpaceForDetails(work, _anchor.Bounds, other, gap);
                    if (opposite.Width * (long)opposite.Height > available.Width * (long)available.Height)
                        (available, side) = (opposite, other);
                }
            }

            _width = Math.Min(480, Math.Max(160, available.Width * 96f / _dpi));
            _height = Math.Min(Header + Footer + BodyHeight, Math.Max(104, available.Height * 96f / _dpi));
            _scrollMax = Math.Max(0, BodyHeight - (_height - Header - Footer));
            _scroll = Math.Clamp(_scroll, 0, _scrollMax);
            var size = new Size(Win32.Px(_width, _dpi), Win32.Px(_height, _dpi));
            var position = _anchor is not null
                ? HudPlacement.PlaceDetails(work, _anchor.Bounds, size, side, gap)
                : firstShow ? new Point(Math.Max(work.Left, work.Right - size.Width - 24), Math.Max(work.Top, work.Bottom - size.Height - 24))
                : HudPlacement.ResizeInward(work, current, size, HudEdge.Left);
            Render(new Rectangle(position, size));
        }
        finally
        {
            _placing = false;
        }
    }

    private void Render(Rectangle bounds)
    {
        Surface.Render(_window, bounds, _dpi, g =>
        {
            Surface.Rounded(g, new RectangleF(.5f, .5f, _width - 1, _height - 1), 17, Surface.Panel, Surface.Outline);
            g.DrawImage(_icons[Source], new RectangleF(Padding, 13, 18, 18));
            Surface.Text(g, ProviderIcons.Name(Source), new RectangleF(40, 7, _width - 85, 32), 16, Surface.TextColor, true);
            _close = new RectangleF(_width - 40, 9, 24, 24);
            Surface.Text(g, "×", _close, 18, Surface.Muted, align: StringAlignment.Center);

            var clip = g.Save();
            g.SetClip(new RectangleF(Padding - 1, Header, _width - 2 * Padding + 2, _height - Header - Footer));
            var top = Header - _scroll;
            DrawRows(g, top);
            var y = top + 17 + _rows.Count * RowHeight;
            _balanceError = RectangleF.Empty;
            if (Volcano) DrawBalance(g, y);
            DrawChart(g, y + BalanceHeight);
            g.Restore(clip);

            if (_scrollMax > 0)
            {
                var scale = (_height - Header - Footer) / BodyHeight;
                var track = new RectangleF(_width - 8, Header + _scroll * scale, 2, (_height - Header - Footer) * scale);
                Surface.Rounded(g, track, 1, Surface.Divider);
            }
            var notice = _snapshot.TokenHistoryNotice;
            var status = string.IsNullOrEmpty(notice) || (Volcano && !ShowChart)
                ? _snapshot.Status : $"{_snapshot.Status} · {notice}";
            var updated = _snapshot.LatestEventAt is { } at ? $"更新 {at.LocalDateTime:MM/dd HH:mm}" : "";
            var stampWidth = updated.Length == 0 ? 0 : 112;
            Surface.Text(g, status, new RectangleF(Padding, _height - 24, _width - 2 * Padding - stampWidth, 20), 11, Surface.Muted);
            Surface.Text(g, updated, new RectangleF(_width - Padding - stampWidth, _height - 24, stampWidth, 20),
                11, Surface.Muted, align: StringAlignment.Far);
        });
    }

    private void DrawRows(Graphics g, float y)
    {
        var xReset = _width - Padding;
        var xLeft = xReset - Math.Min(95, _width * .22f);
        var xUsed = xLeft - Math.Min(57, _width * .13f);
        var xBar = xUsed - Math.Min(54, _width * .13f);
        Surface.Text(g, Volcano ? "Agent Plan · AFP" : "额度", new RectangleF(Padding, y, xBar - Padding, 16), 11, Surface.Muted);
        RightText(g, "已用", xBar, xUsed, y, 16, Surface.Muted);
        RightText(g, "剩余", xUsed, xLeft, y, 16, Surface.Muted);
        RightText(g, "重置时间", xLeft, xReset, y, 16, Surface.Muted);
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var top = y + 17 + i * RowHeight;
            var color = Surface.Status(row.Fraction);
            using var dot = new SolidBrush(color);
            g.FillEllipse(dot, Padding, top + 10, 6, 6);
            var barWidth = Math.Max(15, Math.Min(40, _width * .1f));
            Surface.Text(g, row.Name, new RectangleF(Padding + 12, top, xBar - Padding - 18 - barWidth, RowHeight), 12, Surface.TextColor, true);
            var slot = new RectangleF(xBar - barWidth, top + 11, barWidth, 4);
            Surface.Rounded(g, slot, 2, Surface.Divider);
            if (row.Fraction is > 0 and var value)
                Surface.Rounded(g, slot with { Width = (float)(barWidth * value) }, 2, color);
            RightText(g, row.Used, xBar, xUsed, top, RowHeight, Surface.Muted);
            RightText(g, row.Remaining, xUsed, xLeft, top, RowHeight, color, true);
            RightText(g, row.Reset, xLeft, xReset, top, RowHeight, Surface.Muted);
        }
    }

    private static void RightText(Graphics g, string text, float from, float to, float y, float height, Color color, bool bold = false) =>
        Surface.Text(g, text, new RectangleF(from + 2, y, to - from - 2, height), bold ? 12 : 11, color, bold, StringAlignment.Far);

    private void DrawBalance(Graphics g, float y)
    {
        var card = new RectangleF(Padding, y + 5, _width - 2 * Padding, BalanceHeight - 8);
        Surface.Rounded(g, card, 7, Color.FromArgb(0x17, 0x1C, 0x26));
        var balance = _snapshot.VolcanoBalance;
        var space = card.Width / 2;
        var available = balance is null ? "可用余额 —" : $"可用余额 {balance.AvailableBalance:N2} {balance.Currency}";
        var cash = balance is null ? "现金余额 —" : $"现金余额 {balance.CashBalance:N2} {balance.Currency}";
        Surface.Text(g, available, new RectangleF(card.X + 8, card.Y + 2, space - 10, 17), 11, Surface.TextColor);
        RightText(g, cash, card.Left + space, card.Right - 8, card.Y + 2, 17, Surface.Muted);
        var noteTop = card.Y + 19;
        if (!string.IsNullOrEmpty(_snapshot.VolcanoBalanceError))
        {
            _balanceError = new RectangleF(card.X + 8, noteTop, card.Width - 16, 16);
            Surface.Text(g, _snapshot.VolcanoBalanceError, _balanceError, 11, Color.FromArgb(0xF0, 0xAA, 0x9E));
            noteTop += 15;
        }
        Surface.Text(g, "节省计划剩余额度可在火山引擎控制台查看。",
            new RectangleF(card.X + 8, noteTop, card.Width - 16, 17), 11, Surface.Muted);
    }

    private void DrawChart(Graphics g, float y)
    {
        Surface.Line(g, Surface.Divider, Padding, y + 8, _width - Padding, y + 8);
        Surface.Text(g, Volcano ? "火山引擎 Token 消耗" : "分时 Token 消耗",
            new RectangleF(Padding, y + 13, _width - 2 * Padding - (ShowChart ? 96 : 0), 19), 13, Surface.TextColor, true);
        _recent = _weekly = _chart = RectangleF.Empty;
        if (ShowChart)
        {
            _recent = new RectangleF(_width - Padding - 91, y + 16, 52, 23);
            _weekly = new RectangleF(_width - Padding - 36, y + 16, 36, 23);
            DrawRange(g, _recent, "24 小时", !_week);
            DrawRange(g, _weekly, "7 天", _week);
        }
        Surface.Text(g, QuotaDetails.ChartSummary(_snapshot, _bars, _week, Volcano),
            new RectangleF(Padding, y + 31, _width - 2 * Padding - (ShowChart ? 94 : 0), 18), 11, Surface.Muted);
        if (!ShowChart) return;

        _chart = new RectangleF(Padding, y + 51, _width - 2 * Padding, 89);
        var baseline = _chart.Bottom - 16;
        Surface.Line(g, Surface.Divider, _chart.Left, baseline, _chart.Right, baseline);
        if (_bars.Length == 0)
        {
            Surface.Text(g, "暂无本地用量记录", _chart, 11, Surface.Muted, align: StringAlignment.Center);
            return;
        }
        var slot = _chart.Width / _bars.Length;
        var width = Math.Min(30, slot * .76f);
        var peak = Math.Max(1, _bars.Max(bar => bar.Tokens));
        for (var i = 0; i < _bars.Length; i++)
        {
            var x = _chart.X + (i + .5f) * slot;
            if (i == _hoverBar)
            {
                using var band = new SolidBrush(Color.FromArgb(0x1F, 0x29, 0x37));
                g.FillRectangle(band, x - slot / 2, _chart.Y, slot, baseline - _chart.Y);
            }
            if (_bars[i].Tokens > 0)
            {
                var h = Math.Max(1.5f, (float)((double)_bars[i].Tokens / peak) * (baseline - _chart.Y - 4));
                var bar = new RectangleF(x - width / 2, baseline - h, width, h);
                using var gradient = new LinearGradientBrush(bar, Color.FromArgb(0x64, 0xCA, 0xFA),
                    Color.FromArgb(0x67, 0x57, 0xD2), LinearGradientMode.Vertical);
                using var highlight = new SolidBrush(Color.FromArgb(0x9C, 0xE1, 0xFF));
                g.FillRectangle(i == _hoverBar ? highlight : gradient, bar);
            }
            if (_bars[i].Tick is { } tick)
            {
                var left = Math.Clamp(x - 22, _chart.Left, _chart.Right - 44);
                Surface.Text(g, tick, new RectangleF(left, baseline + 1, 44, 15), 10, Surface.Muted, align: StringAlignment.Center);
            }
        }
    }

    private static void DrawRange(Graphics g, RectangleF rect, string label, bool selected)
    {
        if (selected) Surface.Rounded(g, rect, 5, Color.FromArgb(0x2A, 0x35, 0x46));
        Surface.Text(g, label, rect, 11, selected ? Surface.TextColor : Surface.Muted, align: StringAlignment.Center);
    }

    private nint? OnMessage(uint message, nuint parameter, nint data)
    {
        switch (message)
        {
            case Win32.Close:
            case Win32.KeyDown when parameter == 27:
                Hide();
                return 0;
            case Win32.KeyDown when parameter is 9 or 37 or 39:
                SelectRange(!_week);
                return 0;
            case Win32.DisplayChange:
            case Win32.DpiChanged:
                Place();
                return 0;
            case Win32.MouseMove:
                MovePointer(Win32.MessagePosition);
                return 0;
            case Win32.MouseLeave:
                _tracking = false;
                _tip.Hide();
                if (_hoverBar >= 0)
                {
                    _hoverBar = -1;
                    if (IsVisible) Render(_window.Bounds);
                }
                return 0;
            case Win32.LeftDown:
                BeginPointer();
                return 0;
            case Win32.LeftUp:
            case Win32.CaptureChanged:
                _drag = null;
                if (Win32.GetCapture() == Handle) Win32.ReleaseCapture();
                return 0;
            case Win32.Wheel:
                _scroll = Math.Clamp(_scroll - (short)Win32.High(parameter) * .4f, 0, _scrollMax);
                _hoverBar = -1;
                _tip.Hide();
                Render(_window.Bounds);
                return 0;
            default:
                return null;
        }
    }

    private void SelectRange(bool week)
    {
        if (!ShowChart || week == _week) return;
        _week = week;
        SetData(Source, _snapshot);
        Place();
    }

    private void BeginPointer()
    {
        var bounds = _window.Bounds;
        var cursor = Win32.MessagePosition;
        var point = Win32.LogicalPoint(cursor, bounds, _dpi);
        if (_close.Contains(point)) Hide();
        else if (InsideContent(point) && _recent.Contains(point)) SelectRange(false);
        else if (InsideContent(point) && _weekly.Contains(point)) SelectRange(true);
        else if (_anchor is null && point.Y < Header)
        {
            _drag = cursor;
            _dragWindow = bounds.Location;
            Win32.SetCapture(Handle);
        }
    }

    private bool InsideContent(PointF point) => point.Y >= Header && point.Y < _height - Footer;

    private void MovePointer(Point cursor)
    {
        if (_drag is { } start)
        {
            _window.Move(new Point(_dragWindow.X + cursor.X - start.X, _dragWindow.Y + cursor.Y - start.Y));
            return;
        }
        if (!_tracking)
        {
            _tracking = true;
            Win32.TrackMouse(Handle, hover: false);
        }
        var bounds = _window.Bounds;
        var point = Win32.LogicalPoint(cursor, bounds, _dpi);
        var chartIndex = _bars.Length > 0 && InsideContent(point) && _chart.Contains(point)
            ? Math.Min(_bars.Length - 1, (int)((point.X - _chart.Left) / _chart.Width * _bars.Length)) : -1;
        if (_hoverBar != chartIndex)
        {
            _hoverBar = chartIndex;
            Render(bounds);
        }
        var tipPosition = new Point(cursor.X + Win32.Px(8, _dpi), cursor.Y - Win32.Px(8, _dpi));
        if (chartIndex >= 0)
        {
            var bar = _bars[chartIndex];
            _tip.Show(bar.Period, $"{bar.Tokens:N0} Token", tipPosition, _dpi, inwardAbove: true);
            return;
        }
        if (InsideContent(point))
        {
            var row = (int)Math.Floor((point.Y - (Header + 17 - _scroll)) / RowHeight);
            if (row >= 0 && row < _rows.Count && _rows[row].Note is { } note)
            {
                _tip.Show(_rows[row].Name, note, tipPosition, _dpi, inwardAbove: true);
                return;
            }
            if (_balanceError.Contains(point) && _snapshot.VolcanoBalanceError is { } error)
            {
                _tip.Show("余额查询", error, tipPosition, _dpi, inwardAbove: true);
                return;
            }
        }
        _tip.Hide();
    }

    public void Dispose()
    {
        _tip.Dispose();
        _window.Dispose();
    }
}
