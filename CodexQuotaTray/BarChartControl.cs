using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexQuotaTray;

public sealed class BarChartControl : FrameworkElement
{
    private const double AxisHeight = 16;
    private static readonly Typeface AxisFont = new("Segoe UI, Microsoft YaHei UI");
    private static readonly LinearGradientBrush PlotBars = CreateGradient();
    private static readonly SolidColorBrush Muted = Brush(0x97, 0xA2, 0xB1);
    private static readonly SolidColorBrush HoverBar = Brush(0x9C, 0xE1, 0xFF);
    private static readonly SolidColorBrush HoverBand = Brush(0x1F, 0x29, 0x37);
    private static readonly Pen Baseline = new(Brush(0x2D, 0x34, 0x40), 1);

    private readonly TextBlock _period = new() { Foreground = Muted, FontSize = 11 };
    private readonly TextBlock _quantity = new()
    {
        Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 0)
    };
    private readonly Popup _tip;
    private IReadOnlyList<TokenChartBar> _items = [];
    private int _hoveredIndex = -1;

    public BarChartControl()
    {
        var labels = new StackPanel();
        labels.Children.Add(_period);
        labels.Children.Add(_quantity);
        _tip = new Popup
        {
            Placement = PlacementMode.Relative,
            PlacementTarget = this,
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Child = new Border
            {
                Background = Brush(0x20, 0x25, 0x30),
                BorderBrush = Brush(0x40, 0x4C, 0x60),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(8),
                Child = labels
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                ClearHover();
            }
        };
    }

    internal void SetItems(IReadOnlyList<TokenChartBar> items)
    {
        if (_items.SequenceEqual(items))
        {
            return;
        }
        _items = items;
        if (_hoveredIndex >= _items.Count)
        {
            ClearHover();
        }
        else if (_hoveredIndex >= 0)
        {
            ShowTip(_hoveredIndex);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth;
        var baseline = ActualHeight - AxisHeight;
        if (width <= 0 || baseline <= 4)
        {
            return;
        }

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        dc.DrawLine(Baseline, new Point(0, baseline), new Point(width, baseline));
        if (_items.Count == 0)
        {
            DrawAxisText(dc, "暂无本地用量记录", width / 2, baseline / 2 - 7);
            return;
        }

        var slot = width / _items.Count;
        var peak = Math.Max(1, _items.Max(item => item.Tokens));
        var barWidth = Math.Min(30, slot * 0.76);
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var center = (i + 0.5) * slot;
            var hovered = i == _hoveredIndex;
            if (hovered)
            {
                dc.DrawRectangle(HoverBand, null, new Rect(i * slot, 0, slot, baseline));
            }
            if (item.Tokens > 0)
            {
                var height = Math.Max(1.5, item.Tokens / (double)peak * (baseline - 4));
                var bar = new Rect(center - barWidth / 2, baseline - height, barWidth, height);
                dc.DrawRoundedRectangle(hovered ? HoverBar : PlotBars, null, bar, 1.2, 1.2);
            }
            if (item.Tick is { } tick)
            {
                DrawAxisText(dc, tick, center, baseline + 3);
            }
        }
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        if (_items.Count == 0 || position.X < 0 || position.X >= ActualWidth ||
            position.Y < 0 || position.Y >= ActualHeight - AxisHeight)
        {
            ClearHover();
            return;
        }

        var index = Math.Min(_items.Count - 1, (int)(position.X / ActualWidth * _items.Count));
        if (index != _hoveredIndex)
        {
            _hoveredIndex = index;
            ShowTip(index);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ClearHover();
    }

    internal void ClearHover()
    {
        if (_hoveredIndex >= 0)
        {
            _hoveredIndex = -1;
            _tip.IsOpen = false;
            InvalidateVisual();
        }
    }

    private void ShowTip(int index)
    {
        var item = _items[index];
        _period.Text = item.Period;
        _quantity.Text = $"{item.Tokens:N0} Token";
        _tip.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = _tip.Child.DesiredSize;
        var center = ActualWidth * (index + 0.5) / _items.Count;
        _tip.HorizontalOffset = Math.Clamp(center - desired.Width / 2, 0, Math.Max(0, ActualWidth - desired.Width));
        _tip.VerticalOffset = -desired.Height - 6;
        _tip.IsOpen = true;
    }

    private void DrawAxisText(DrawingContext dc, string text, double center, double y)
    {
        var label = new FormattedText(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            AxisFont, 10, Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var x = Math.Clamp(center - label.Width / 2, 0, Math.Max(0, ActualWidth - label.Width));
        dc.DrawText(label, new Point(x, y));
    }

    private static LinearGradientBrush CreateGradient()
    {
        var brush = new LinearGradientBrush(Color.FromRgb(0x73, 0x5C, 0xD3), Color.FromRgb(0x64, 0xCA, 0xFA),
            new Point(0, 1), new Point(0, 0));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
