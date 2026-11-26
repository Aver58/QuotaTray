using System.Drawing;
using System.Drawing.Drawing2D;
using CodexQuotaCore;

namespace CodexQuotaTray;

internal sealed class NativeHud : IDisposable
{
    private const nuint CollapseTimer = 1;
    private const int RowSize = 43;
    private const int ColumnSize = 59;
    private readonly NativeWindow _window;
    private readonly HoverTip _tip = new();
    private readonly ProviderIcons _icons;
    private readonly QuotaSummary[] _readings = ProviderIcons.Sources.Select(_ => QuotaSummary.Unavailable("正在读取额度…")).ToArray();
    private HudEdge? _docked = HudEdge.Right;
    private int _dpi = 96;
    private int _highlight = -1, _focused = -1, _pressedRow = -1;
    private bool _expanded, _detailsOpen, _pressed, _dragging, _positioned, _tracking;
    private bool _topmost;
    private Point _pressPosition, _initialLocation;

    internal NativeHud(ProviderIcons icons, bool topmost)
    {
        _icons = icons;
        _topmost = topmost;
        _window = new NativeWindow("QuotaTray 额度一览", Win32.ExLayered | Win32.ExToolWindow);
        _window.MessageReceived = OnMessage;
        _window.SetTopmost(topmost);
    }

    internal event Action? PositionChanged;
    internal event Action? Hidden;
    internal event Action? TopmostRequested;
    internal event Action<QuotaSource>? DetailsRequested;
    internal nint Handle => _window.Handle;
    internal bool IsVisible => _window.IsVisible;
    internal Rectangle Bounds => _window.Bounds;
    internal HudEdge Side { get; private set; } = HudEdge.Right;
    internal int Dpi => _dpi;
    private bool Horizontal => Side is HudEdge.Top or HudEdge.Bottom;
    private Size LogicalSize => Horizontal ? new Size(187, _expanded ? 74 : 46) : new Size(_expanded ? 103 : 50, 139);
    private Size PixelSize => new(Win32.Px(LogicalSize.Width, _dpi), Win32.Px(LogicalSize.Height, _dpi));

    internal void ApplySnapshot(QuotaSource source, CodexQuotaSnapshot snapshot)
    {
        var reading = QuotaSummary.FromSnapshot(source, snapshot);
        if (reading == _readings[(int)source]) return;
        _readings[(int)source] = reading;
        if (IsVisible) Draw(Bounds.Location);
    }

    internal void Show()
    {
        var area = Win32.WorkArea(_positioned ? Center(Bounds) : Point.Empty);
        if (!_positioned) _dpi = Win32.Dpi(Handle);
        var anchor = _positioned ? Bounds : new Rectangle(Center(area), Size.Empty);
        Draw(HudPlacement.Align(area, anchor, PixelSize, _docked));
        _positioned = true;
        _window.Show();
        PositionChanged?.Invoke();
    }

    internal void Hide()
    {
        _tip.Hide();
        _window.Hide();
        Win32.KillTimer(Handle, CollapseTimer);
        if (Win32.GetCapture() == Handle) Win32.ReleaseCapture();
        _expanded = _detailsOpen = _pressed = _dragging = _tracking = false;
        _highlight = _focused = -1;
        Hidden?.Invoke();
        // Resize around the existing edge so the next show stays at the same position.
        var bounds = Bounds;
        Draw(HudPlacement.Align(Win32.WorkArea(Center(bounds)), bounds, PixelSize, _docked));
    }

    internal void SetTopmost(bool enabled)
    {
        _topmost = enabled;
        _window.SetTopmost(enabled);
    }

    internal void SetDetailsOpen(bool opened)
    {
        _detailsOpen = opened;
        if (opened) Expand(true);
        else QueueCollapse();
    }

    private void Expand(bool expanded)
    {
        if (_expanded == expanded || !IsVisible) return;
        var bounds = Bounds;
        _expanded = expanded;
        var area = Win32.WorkArea(Center(bounds));
        var point = _docked is { } edge
            ? HudPlacement.Align(area, bounds, PixelSize, edge)
            : HudPlacement.ResizeInward(area, bounds, PixelSize, Side);
        Draw(point);
        PositionChanged?.Invoke();
    }

    private void Dock(Rectangle area, HudEdge? edge)
    {
        var bounds = Bounds;
        _docked = edge;
        Side = edge ?? (Center(bounds).X < Center(area).X ? HudEdge.Left : HudEdge.Right);
        Draw(HudPlacement.Align(area, bounds, PixelSize, edge));
        PositionChanged?.Invoke();
    }

    private static Point Center(Rectangle rect) => new(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);

    private void Draw(Point point)
    {
        var bounds = new Rectangle(point, PixelSize);
        var size = LogicalSize;
        Surface.Render(_window, bounds, _dpi, g =>
        {
            Surface.Rounded(g, new RectangleF(.5f, .5f, size.Width - 1, size.Height - 1), _expanded ? 17 : 25,
                Color.FromArgb(209, 0, 0, 0), Color.FromArgb(36, 255, 255, 255));
            for (var i = 0; i < _readings.Length; i++)
            {
                var row = Slot(i);
                if (_expanded && (_highlight == i || _focused == i))
                    Surface.Rounded(g, row, 7, _highlight == i ? Surface.Hover : Color.Transparent,
                        _focused == i ? Color.FromArgb(0x87, 0x9B, 0xB9) : null);

                var reading = _readings[i];
                var color = Surface.Status(reading.Fraction);
                var badge = Badge(row);
                var circle = new RectangleF(badge.X + 2, badge.Y + 2, 28, 28);
                using var track = new Pen(Color.FromArgb(0x34, 0x3D, 0x4D), 2.6f);
                g.DrawEllipse(track, circle);
                if (reading.Fraction is > 0 and var fraction)
                {
                    using var line = new Pen(color, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    if (fraction >= 1) g.DrawEllipse(line, circle);
                    else g.DrawArc(line, circle, -90, (float)(fraction * 360));
                }
                g.DrawImage(_icons[ProviderIcons.Sources[i]], new RectangleF(badge.X + 7, badge.Y + 7, 18, 18));
                if (_expanded) DrawReading(g, row, reading.Value, color);
            }
        });
    }

    private RectangleF Slot(int index) => Horizontal
        ? new RectangleF(5 + index * ColumnSize, 5, ColumnSize, LogicalSize.Height - 10)
        : new RectangleF(5, 5 + index * RowSize, LogicalSize.Width - 10, RowSize);

    private RectangleF Badge(RectangleF row)
    {
        const int size = 32;
        if (Horizontal)
        {
            var top = !_expanded ? row.Y + 2 : Side == HudEdge.Top ? row.Top + 2 : row.Bottom - 34;
            return new(row.X + (row.Width - size) / 2, top, size, size);
        }
        var left = !_expanded ? row.X + 4 : Side == HudEdge.Left ? row.Left + 4 : row.Right - 36;
        return new(left, row.Y + (row.Height - size) / 2, size, size);
    }

    private void DrawReading(Graphics graphics, RectangleF row, string value, Color color)
    {
        RectangleF bounds;
        if (Horizontal)
            bounds = new(row.X + 1, Side == HudEdge.Top ? row.Bottom - 26 : row.Top + 1, row.Width - 2, 25);
        else
            bounds = new(Side == HudEdge.Left ? row.Left + 38 : row.Left + 3, row.Top, row.Width - 41, row.Height);
        Surface.Text(graphics, value, bounds, value.Length > 4 ? 16 : 20, color, align: StringAlignment.Center);
    }

    private int Hit(Point screen)
    {
        var logical = Win32.LogicalPoint(screen, Bounds, _dpi);
        for (var i = 0; i < _readings.Length; i++)
            if (Slot(i).Contains(logical)) return i;
        return -1;
    }

    private void QueueCollapse()
    {
        Win32.KillTimer(Handle, CollapseTimer);
        if (IsVisible && !_detailsOpen) Win32.SetTimer(Handle, CollapseTimer, 180);
    }

    private nint? OnMessage(uint message, nuint parameter, nint data)
    {
        switch (message)
        {
            case Win32.MouseMove:
                MovePointer(Win32.MessagePosition);
                return 0;
            case Win32.MouseHover:
                ShowTip();
                return 0;
            case Win32.MouseLeave:
                _tracking = false;
                _highlight = -1;
                _tip.Hide();
                if (IsVisible) Draw(Bounds.Location);
                QueueCollapse();
                return 0;
            case Win32.LeftDown:
                _tip.Hide();
                _focused = -1;
                _pressed = true;
                _pressPosition = Win32.MessagePosition;
                _initialLocation = Bounds.Location;
                _pressedRow = Hit(_pressPosition);
                Win32.SetCapture(Handle);
                return 0;
            case Win32.LeftUp:
                FinishPointer(click: true);
                return 0;
            case Win32.CaptureChanged:
                if (_pressed) FinishPointer(click: false);
                return 0;
            case Win32.ContextMenu:
            case Win32.RightUp:
                ShowMenu();
                return 0;
            case Win32.Timer when parameter == CollapseTimer:
                Win32.KillTimer(Handle, CollapseTimer);
                if (!_detailsOpen && !_pressed && !Bounds.Contains(Win32.Cursor)) Expand(false);
                return 0;
            case Win32.KeyDown:
                return HandleKey(parameter);
            case Win32.Close:
                Hide();
                return 0;
            case Win32.DpiChanged:
                _dpi = Win32.Low(parameter);
                if (IsVisible)
                {
                    if (_dragging) Draw(Bounds.Location);
                    else Dock(Win32.WorkArea(Center(Bounds)), _docked);
                }
                return 0;
            case Win32.DisplayChange:
                if (IsVisible) Dock(Win32.WorkArea(Center(Bounds)), _docked);
                return 0;
            default:
                return null;
        }
    }

    private nint? HandleKey(nuint key)
    {
        if (key == 27)
        {
            Hide();
            return 0;
        }
        if (key is 9 or 40 or 39 or 38 or 37)
        {
            var direction = key is 38 or 37 ? -1 : 1;
            _focused = (_focused + direction + _readings.Length) % _readings.Length;
            Expand(true);
            Draw(Bounds.Location);
            return 0;
        }
        if ((key is 13 or 32) && _focused >= 0)
        {
            DetailsRequested?.Invoke(ProviderIcons.Sources[_focused]);
            return 0;
        }
        return null;
    }

    private void MovePointer(Point point)
    {
        if (_pressed)
        {
            DragTo(point);
            return;
        }

        Win32.KillTimer(Handle, CollapseTimer);
        Expand(true);
        var hover = Hit(point);
        if (!_tracking || hover != _highlight)
        {
            _tip.Hide();
            _tracking = true;
            _highlight = hover;
            Win32.TrackMouse(Handle);
            Draw(Bounds.Location);
        }
    }

    private void FinishPointer(bool click)
    {
        if (!_pressed) return;
        var point = Win32.MessagePosition;
        if (click) DragTo(point);
        var dragged = _dragging;
        _pressed = _dragging = false;
        if (Win32.GetCapture() == Handle) Win32.ReleaseCapture();
        if (dragged)
        {
            var area = Win32.WorkArea(point);
            Dock(area, HudPlacement.FindSnapEdge(area, Bounds, Win32.Px(24, _dpi)));
        }
        else if (click && _pressedRow >= 0 && _pressedRow == Hit(point))
        {
            DetailsRequested?.Invoke(ProviderIcons.Sources[_pressedRow]);
        }
        QueueCollapse();
    }

    private void DragTo(Point point)
    {
        var dx = point.X - _pressPosition.X;
        var dy = point.Y - _pressPosition.Y;
        _dragging |= Math.Abs(dx) >= Win32.Px(4, _dpi) || Math.Abs(dy) >= Win32.Px(4, _dpi);
        if (!_dragging) return;
        _window.Move(new Point(_initialLocation.X + dx, _initialLocation.Y + dy));
        PositionChanged?.Invoke();
    }

    private void ShowMenu()
    {
        _tip.Hide();
        using var menu = new PopupMenu();
        menu.Add(1, "贴靠左边");
        menu.Add(2, "贴靠右边");
        menu.Add(3, "贴靠上边");
        menu.Add(4, "贴靠下边");
        menu.Separator();
        menu.Add(5, "置顶浮窗", _topmost);
        menu.Add(6, "收起浮窗");
        var choice = menu.Show(Handle, Win32.Cursor);
        if (choice is >= 1 and <= 4) Dock(Win32.WorkArea(Center(Bounds)), (HudEdge)(choice - 1));
        if (choice == 5) TopmostRequested?.Invoke();
        if (choice == 6) Hide();
        QueueCollapse();
    }

    private void ShowTip()
    {
        if (_pressed || !IsVisible || _highlight < 0) return;
        var bounds = Bounds;
        var point = Win32.Cursor;
        var gap = Win32.Px(6, _dpi);
        var position = Side switch
        {
            HudEdge.Left => new Point(bounds.Right + gap, point.Y - gap),
            HudEdge.Right => new Point(bounds.Left - gap, point.Y - gap),
            HudEdge.Top => new Point(point.X, bounds.Bottom + gap),
            _ => new Point(point.X, bounds.Top - gap)
        };
        _tip.Show(ProviderIcons.Name(ProviderIcons.Sources[_highlight]), _readings[_highlight].Details,
            position, _dpi, Side == HudEdge.Right, Side == HudEdge.Bottom);
    }

    public void Dispose()
    {
        _tip.Dispose();
        _window.Dispose();
    }
}
