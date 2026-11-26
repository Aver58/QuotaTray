using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexQuotaTray;

public sealed class QuotaRing : FrameworkElement
{
    private const double StrokeWidth = 2.6;
    private static readonly Pen Track = CreateTrack();

    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(QuotaRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(QuotaRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public Brush? Accent
    {
        get => (Brush?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(0, Math.Min(center.X, center.Y) - StrokeWidth / 2 - 0.5);
        drawingContext.DrawEllipse(null, Track, center, radius, radius);

        if (Accent is not { } accent || !double.IsFinite(Fraction) || Fraction <= 0 || radius <= 0)
        {
            return;
        }

        var pen = new Pen(accent, StrokeWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (Fraction >= 1)
        {
            drawingContext.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var angle = 2 * Math.PI * Fraction;
        var end = new Point(center.X + Math.Sin(angle) * radius, center.Y - Math.Cos(angle) * radius);
        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(new Point(center.X, center.Y - radius), isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, Fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        drawingContext.DrawGeometry(null, pen, arc);
    }

    private static Pen CreateTrack()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0xff, 0xff, 0xff)), StrokeWidth);
        pen.Freeze();
        return pen;
    }
}
