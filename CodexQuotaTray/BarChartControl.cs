using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CodexQuotaCore;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaPen = System.Windows.Media.Pen;
using WpfFlowDirection = System.Windows.FlowDirection;
using WpfPoint = System.Windows.Point;

namespace CodexQuotaTray;

public sealed class BarChartControl : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(
            nameof(Items),
            typeof(IReadOnlyList<DailyTokenUsage>),
            typeof(BarChartControl),
            new FrameworkPropertyMetadata(Array.Empty<DailyTokenUsage>(), FrameworkPropertyMetadataOptions.AffectsRender)
        );

    public IReadOnlyList<DailyTokenUsage> Items
    {
        get => (IReadOnlyList<DailyTokenUsage>)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 10 || height <= 10)
        {
            return;
        }

        var left = 42.0;
        var top = 22.0;
        var right = 12.0;
        var bottom = 30.0;
        var plotWidth = Math.Max(10, width - left - right);
        var plotHeight = Math.Max(10, height - top - bottom);
        var originY = top + plotHeight;

        var gridPen = new MediaPen(new SolidColorBrush(MediaColor.FromArgb(42, 132, 143, 158)), 1);
        var axisBrush = new SolidColorBrush(MediaColor.FromArgb(160, 180, 188, 200));
        var barBrush = new SolidColorBrush(MediaColor.FromRgb(45, 112, 204));

        var maxTokens = Math.Max(1, Items.Count == 0 ? 1 : Items.Max(item => item.TotalTokens));
        var maxMillions = NiceMax(maxTokens / 1_000_000.0);

        DrawLabel(dc, $"{maxMillions:0.#}", 12, top - 8, axisBrush, 12, dpi);
        DrawLabel(dc, $"{maxMillions / 2:0.#}", 12, top + plotHeight / 2 - 8, axisBrush, 12, dpi);
        DrawLabel(dc, "0", 18, originY - 8, axisBrush, 12, dpi);

        for (var i = 0; i < 3; i++)
        {
            var y = top + (plotHeight / 2) * i;
            dc.DrawLine(gridPen, new WpfPoint(left, y), new WpfPoint(left + plotWidth, y));
        }

        if (Items.Count == 0)
        {
            return;
        }

        var slot = plotWidth / Items.Count;
        var barWidth = Math.Min(18, slot * 0.42);
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            var valueMillions = item.TotalTokens / 1_000_000.0;
            var barHeight = Math.Max(2, plotHeight * Math.Min(valueMillions / maxMillions, 1));
            var x = left + i * slot + (slot - barWidth) / 2;
            var y = originY - barHeight;
            dc.DrawRoundedRectangle(barBrush, null, new Rect(x, y, barWidth, barHeight), 2, 2);

            var dayLabel = item.Date.Day.ToString(CultureInfo.InvariantCulture);
            DrawCenteredLabel(dc, dayLabel, x + barWidth / 2, originY + 8, axisBrush, 11, dpi);
        }
    }

    private static double NiceMax(double value)
    {
        if (value <= 0.1)
        {
            return 0.1;
        }

        if (value <= 1)
        {
            return 1;
        }

        if (value <= 5)
        {
            return 5;
        }

        if (value <= 10)
        {
            return 10;
        }

        if (value <= 50)
        {
            return 50;
        }

        return Math.Ceiling(value / 100.0) * 100.0;
    }

    private static void DrawLabel(DrawingContext dc, string text, double x, double y, MediaBrush brush, double size, double dpi)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            WpfFlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            brush,
            dpi
        );
        dc.DrawText(formatted, new WpfPoint(x, y));
    }

    private static void DrawCenteredLabel(DrawingContext dc, string text, double centerX, double y, MediaBrush brush, double size, double dpi)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            WpfFlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            size,
            brush,
            dpi
        );
        dc.DrawText(formatted, new WpfPoint(centerX - formatted.Width / 2, y));
    }
}
