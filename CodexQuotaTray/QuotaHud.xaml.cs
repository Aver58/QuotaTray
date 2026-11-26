using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexQuotaCore;
using Button = System.Windows.Controls.Button;
using DrawingPoint = System.Drawing.Point;
using MenuItem = System.Windows.Controls.MenuItem;
using Point = System.Windows.Point;
using Rectangle = System.Drawing.Rectangle;

namespace CodexQuotaTray;

public partial class QuotaHud : Window
{
    public static readonly DependencyProperty LayoutEdgeProperty = DependencyProperty.Register(
        nameof(LayoutEdge), typeof(HudEdge), typeof(QuotaHud), new PropertyMetadata(HudEdge.Right));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(QuotaHud), new PropertyMetadata(false));

    private readonly Row[] _rows =
    [
        new(QuotaSource.Codex),
        new(QuotaSource.VolcengineAgentPlan),
        new(QuotaSource.Antigravity)
    ];
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private HudEdge? _dockedEdge = HudEdge.Right;
    private (Point Pointer, DrawingPoint Window)? _dragStart;
    private bool _dragging;
    private bool _detailsOpen;

    public QuotaHud()
    {
        InitializeComponent();
        SourceRows.ItemsSource = _rows;
        _collapseTimer.Tick += CollapseTimer_OnTick;
    }

    internal event Action<QuotaSource>? DetailsRequested;
    internal event Action? TopmostRequested;

    public HudEdge LayoutEdge
    {
        get => (HudEdge)GetValue(LayoutEdgeProperty);
        private set => SetValue(LayoutEdgeProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        private set => SetValue(IsExpandedProperty, value);
    }

    internal void SetAlwaysOnTop(bool enabled)
    {
        Topmost = enabled;
        TopmostOption.IsChecked = enabled;
    }

    internal void ApplySnapshot(QuotaSource source, CodexQuotaSnapshot snapshot) =>
        _rows.Single(row => row.Source == source).Update(QuotaSummary.FromSnapshot(source, snapshot));

    internal Rectangle PanelBounds()
    {
        var start = HudBody.PointToScreen(new Point());
        var end = HudBody.PointToScreen(new Point(HudBody.ActualWidth, HudBody.ActualHeight));
        return Rectangle.FromLTRB((int)Math.Round(start.X), (int)Math.Round(start.Y),
            (int)Math.Round(end.X), (int)Math.Round(end.Y));
    }

    internal void SetDetailsOpen(bool open)
    {
        if (_detailsOpen == open)
        {
            return;
        }
        _detailsOpen = open;
        if (open)
        {
            _collapseTimer.Stop();
            SetExpanded(true);
        }
        else
        {
            QueueCollapse();
        }
    }

    internal void RestorePosition(bool firstShow)
    {
        var bounds = HudPlacement.Bounds(this);
        var screen = firstShow && Screen.PrimaryScreen is { } primary ? primary : Screen.FromRectangle(bounds);
        var area = screen.WorkingArea;
        var reference = firstShow
            ? new Rectangle(area.Left + area.Width / 2, area.Top + area.Height / 2, 0, 0)
            : bounds;
        PlaceOnScreen(area, reference, _dockedEdge);
    }

    private void PlaceOnScreen(Rectangle area, Rectangle reference, HudEdge? edge)
    {
        _dockedEdge = edge;
        var side = edge ?? (reference.Left + reference.Width / 2 < area.Left + area.Width / 2
            ? HudEdge.Left : HudEdge.Right);
        LayoutEdge = side;

        UpdateLayout();
        var bounds = HudPlacement.Bounds(this);
        HudPlacement.Move(this, HudPlacement.Align(area, reference, bounds.Size, edge));
        // Moving across displays can change the physical size even though the layout is unchanged.
        var current = HudPlacement.Bounds(this);
        if (current.Size != bounds.Size)
        {
            HudPlacement.Move(this, HudPlacement.Align(area, reference, current.Size, edge));
        }
    }

    private void SetExpanded(bool expanded)
    {
        if (IsExpanded == expanded)
        {
            return;
        }

        var bounds = IsVisible ? HudPlacement.Bounds(this) : Rectangle.Empty;
        IsExpanded = expanded;
        if (bounds.IsEmpty)
        {
            return;
        }

        UpdateLayout();
        var area = Screen.FromRectangle(bounds).WorkingArea;
        var size = HudPlacement.Bounds(this).Size;
        var destination = _dockedEdge is { } edge
            ? HudPlacement.Align(area, bounds, size, edge)
            : HudPlacement.ResizeInward(area, bounds, size, LayoutEdge);
        HudPlacement.Move(this, destination);
    }

    private void HudBody_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (!_dragging)
        {
            SetExpanded(true);
        }
    }

    private void HudBody_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => QueueCollapse();

    private void QueueCollapse()
    {
        if (IsVisible && IsExpanded)
        {
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }
    }

    private void CollapseTimer_OnTick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        if (!HudBody.IsMouseOver && !_detailsOpen && !_dragging && !Actions.IsOpen)
        {
            SetExpanded(false);
        }
    }

    private void Source_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Row row })
        {
            DetailsRequested?.Invoke(row.Source);
        }
    }

    private void HudBody_OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = (PointToScreen(e.GetPosition(this)), HudPlacement.Bounds(this).Location);
        _collapseTimer.Stop();
    }

    // Buttons capture the mouse themselves. Capture here for presses on the panel around them.
    private void HudBody_OnBackgroundDown(object sender, MouseButtonEventArgs e) => HudBody.CaptureMouse();

    private void HudBody_OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            e.Handled = true;
        }
        EndDrag();
    }

    private void HudBody_OnPointerMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragStart is not { } origin || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = PointToScreen(e.GetPosition(this));
        var dx = position.X - origin.Pointer.X;
        var dy = position.Y - origin.Pointer.Y;
        if (!_dragging)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX &&
                Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY)
            {
                return;
            }

            // Taking capture from the button cancels its click before the panel begins to move.
            if (!HudBody.CaptureMouse())
            {
                return;
            }
            _dragging = true;
        }

        e.Handled = true;
        HudPlacement.Move(this, new DrawingPoint(origin.Window.X + (int)Math.Round(dx), origin.Window.Y + (int)Math.Round(dy)));
    }

    private void HudBody_OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.OriginalSource == HudBody && _dragStart is not null)
        {
            EndDrag();
        }
    }

    private void EndDrag()
    {
        var moved = _dragging;
        _dragStart = null;
        _dragging = false;
        if (HudBody.IsMouseCaptured)
        {
            HudBody.ReleaseMouseCapture();
        }

        if (moved && IsVisible)
        {
            var bounds = HudPlacement.Bounds(this);
            var area = Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
            var distance = (int)Math.Round(24 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            PlaceOnScreen(area, bounds, HudPlacement.FindSnapEdge(area, PanelBounds(), distance));
        }
        QueueCollapse();
    }

    private void Dock_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: HudEdge edge })
        {
            var bounds = HudPlacement.Bounds(this);
            PlaceOnScreen(Screen.FromRectangle(bounds).WorkingArea, bounds, edge);
        }
    }

    private void Topmost_OnClick(object sender, RoutedEventArgs e) => TopmostRequested?.Invoke();

    private void Close_OnClick(object sender, RoutedEventArgs e) => Hide();

    private void Actions_OnClosed(object? sender, RoutedEventArgs e) => QueueCollapse();

    private void Window_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            _collapseTimer.Stop();
            EndDrag();
            SetExpanded(false);
        }
    }

    private void Window_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab)
        {
            _collapseTimer.Stop();
            SetExpanded(true);
        }
    }

    private sealed class Row(QuotaSource source) : INotifyPropertyChanged
    {
        private readonly ProviderAppearance _appearance = ProviderAppearance.For(source);
        public QuotaSource Source { get; } = source;
        public string Name => _appearance.Name;
        public ImageSource Icon => _appearance.Icon;
        public QuotaSummary Summary { get; private set; } = QuotaSummary.Unavailable("正在读取额度…");
        public SolidColorBrush StatusBrush => ProviderAppearance.StatusBrush(Summary.Fraction);

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(QuotaSummary summary)
        {
            if (Summary == summary)
            {
                return;
            }

            Summary = summary;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
        }

    }
}
