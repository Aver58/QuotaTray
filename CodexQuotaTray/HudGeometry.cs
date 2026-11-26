using System.Drawing;

namespace CodexQuotaTray;

public enum HudEdge { Left, Right, Top, Bottom }

internal static partial class HudPlacement
{
    internal static HudEdge? FindSnapEdge(Rectangle area, Rectangle window, int distance)
    {
        (HudEdge Edge, int Gap)[] edges =
        [
            (HudEdge.Left, window.Left - area.Left),
            (HudEdge.Right, area.Right - window.Right),
            (HudEdge.Top, window.Top - area.Top),
            (HudEdge.Bottom, area.Bottom - window.Bottom)
        ];
        var nearest = edges.MinBy(edge => edge.Gap);
        return nearest.Gap <= distance ? nearest.Edge : null;
    }

    internal static Point Align(Rectangle area, Rectangle reference, Size size, HudEdge? edge)
    {
        var x = reference.Left + (reference.Width - size.Width) / 2;
        var y = reference.Top + (reference.Height - size.Height) / 2;
        return Clamp(area, size, edge switch
        {
            HudEdge.Left => new Point(area.Left, y),
            HudEdge.Right => new Point(area.Right - size.Width, y),
            HudEdge.Top => new Point(x, area.Top),
            HudEdge.Bottom => new Point(x, area.Bottom - size.Height),
            _ => new Point(x, y)
        });
    }

    internal static Point PlaceDetails(Rectangle area, Rectangle anchor, Size size, HudEdge edge, int gap) =>
        Clamp(area, size, edge switch
        {
            HudEdge.Left => new Point(anchor.Right + gap, anchor.Bottom - size.Height),
            HudEdge.Right => new Point(anchor.Left - gap - size.Width, anchor.Bottom - size.Height),
            HudEdge.Top => new Point(anchor.Left + (anchor.Width - size.Width) / 2, anchor.Bottom + gap),
            _ => new Point(anchor.Left + (anchor.Width - size.Width) / 2, anchor.Top - gap - size.Height)
        });

    internal static Size SpaceForDetails(Rectangle area, Rectangle anchor, HudEdge edge, int gap) =>
        edge switch
        {
            HudEdge.Left => new Size(Math.Max(1, area.Right - anchor.Right - gap), area.Height),
            HudEdge.Right => new Size(Math.Max(1, anchor.Left - area.Left - gap), area.Height),
            HudEdge.Top => new Size(area.Width, Math.Max(1, area.Bottom - anchor.Bottom - gap)),
            _ => new Size(area.Width, Math.Max(1, anchor.Top - area.Top - gap))
        };

    internal static Point ResizeInward(Rectangle area, Rectangle anchor, Size size, HudEdge edge) =>
        Clamp(area, size, edge switch
        {
            HudEdge.Right => new Point(anchor.Right - size.Width, anchor.Top),
            HudEdge.Bottom => new Point(anchor.Left, anchor.Bottom - size.Height),
            _ => anchor.Location
        });

    private static Point Clamp(Rectangle area, Size size, Point position) => new(
        Math.Max(area.Left, Math.Min(position.X, area.Right - size.Width)),
        Math.Max(area.Top, Math.Min(position.Y, area.Bottom - size.Height)));
}
