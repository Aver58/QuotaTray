using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexQuotaCore;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using MenuItem = System.Windows.Controls.MenuItem;
using Rectangle = System.Drawing.Rectangle;

namespace CodexQuotaTray;

public partial class QuotaHud : Window
{
    public static readonly DependencyProperty LayoutEdgeProperty = DependencyProperty.Register(
        nameof(LayoutEdge), typeof(HudEdge), typeof(QuotaHud), new PropertyMetadata(HudEdge.Right));

    private readonly Row[] _rows =
    [
        new(QuotaSource.Codex, "Codex", "chatgpt.png", Color.FromRgb(0x10, 0xa3, 0x7f)),
        new(QuotaSource.VolcengineAgentPlan, "火山引擎", "ark.png"),
        new(QuotaSource.Antigravity, "Antigravity", "antigravity.png")
    ];
    private HudEdge? _dockedEdge = HudEdge.Right;
    private bool _dragging;

    public QuotaHud()
    {
        InitializeComponent();
        SourceRows.ItemsSource = _rows;
    }

    internal event Action<QuotaSource>? DetailsRequested;

    public HudEdge LayoutEdge
    {
        get => (HudEdge)GetValue(LayoutEdgeProperty);
        private set => SetValue(LayoutEdgeProperty, value);
    }

    internal bool IsPinned
    {
        get => Pin.IsChecked;
        set => Pin.IsChecked = value;
    }

    internal void ApplySnapshot(QuotaSource source, CodexQuotaSnapshot snapshot) =>
        _rows.Single(row => row.Source == source).Update(QuotaSummary.FromSnapshot(source, snapshot));

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
        var horizontal = side is HudEdge.Top or HudEdge.Bottom;
        DockPanel.SetDock(Grip, side switch
        {
            HudEdge.Left => Dock.Right,
            HudEdge.Right => Dock.Left,
            HudEdge.Top => Dock.Bottom,
            _ => Dock.Top
        });
        Grip.Width = horizontal ? double.NaN : 12;
        Grip.Height = horizontal ? 12 : double.NaN;
        GripMark.Width = horizontal ? 24 : 2;
        GripMark.Height = horizontal ? 2 : 24;

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

    private void Source_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Row row })
        {
            DetailsRequested?.Invoke(row.Source);
        }
    }

    private void Grip_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var start = HudPlacement.Bounds(this);
        _dragging = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        finally
        {
            _dragging = false;
        }

        var bounds = HudPlacement.Bounds(this);
        if (bounds.Location != start.Location)
        {
            var area = Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
            var distance = (int)Math.Round(24 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            PlaceOnScreen(area, bounds, HudPlacement.FindSnapEdge(area, bounds, distance));
        }
    }

    private void Dock_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: HudEdge edge })
        {
            var bounds = HudPlacement.Bounds(this);
            PlaceOnScreen(Screen.FromRectangle(bounds).WorkingArea, bounds, edge);
        }
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Hide();

    private void Actions_OnClosed(object? sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsActive)
            {
                HideIfTransient();
            }
        });

    private void Window_OnDeactivated(object? sender, EventArgs e)
    {
        HideIfTransient();
    }

    private void HideIfTransient()
    {
        if (!IsPinned && !_dragging && !Actions.IsOpen)
        {
            Hide();
        }
    }

    private void Window_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private static ImageSource LoadProviderMark(string fileName, Color? tint)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri($"pack://application:,,,/CodexQuotaTray;component/Assets/Providers/{fileName}");
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 96;
        bitmap.EndInit();
        bitmap.Freeze();
        if (tint is not { } color)
        {
            return bitmap;
        }

        var drawing = new DrawingGroup { OpacityMask = new ImageBrush(bitmap) { Stretch = Stretch.Uniform } };
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null, new RectangleGeometry(new Rect(0, 0, 20, 20))));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private sealed class Row(QuotaSource source, string name, string iconFile, Color? tint = null) : INotifyPropertyChanged
    {
        // Agent HUD's dark status palette. The thresholds are expressed here as remaining quota.
        private static readonly SolidColorBrush Normal = CreateBrush(0x3d, 0xdc, 0x84);
        private static readonly SolidColorBrush Warning = CreateBrush(0xff, 0xd2, 0x3f);
        private static readonly SolidColorBrush Critical = CreateBrush(0xff, 0x45, 0x3a);
        private static readonly SolidColorBrush Idle = CreateBrush(0x9a, 0x9a, 0xa0);

        public QuotaSource Source { get; } = source;
        public string Name { get; } = name;
        public ImageSource Icon { get; } = LoadProviderMark(iconFile, tint);
        public QuotaSummary Summary { get; private set; } = QuotaSummary.Unavailable("正在读取额度…");
        public SolidColorBrush StatusBrush => Summary.Fraction switch
        {
            null => Idle,
            <= 0.10 => Critical,
            <= 0.30 => Warning,
            _ => Normal
        };

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

        private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }
    }
}
