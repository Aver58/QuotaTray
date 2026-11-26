using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace CodexQuotaTray;

public enum HudEdge
{
    Left,
    Right,
    Top,
    Bottom
}

internal static class HudPlacement
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

    internal static Point PlaceDetails(Rectangle area, Rectangle anchor, Size size, HudEdge edge, int gap)
    {
        return Clamp(area, size, edge switch
        {
            HudEdge.Left => new Point(anchor.Right + gap, anchor.Bottom - size.Height),
            HudEdge.Right => new Point(anchor.Left - gap - size.Width, anchor.Bottom - size.Height),
            HudEdge.Top => new Point(anchor.Left + (anchor.Width - size.Width) / 2, anchor.Bottom + gap),
            _ => new Point(anchor.Left + (anchor.Width - size.Width) / 2, anchor.Top - gap - size.Height)
        });
    }

    private static Point Clamp(Rectangle area, Size size, Point position) => new(
        Math.Max(area.Left, Math.Min(position.X, area.Right - size.Width)),
        Math.Max(area.Top, Math.Min(position.Y, area.Bottom - size.Height)));

    internal static Rectangle Bounds(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(handle, out var rect))
        {
            throw new InvalidOperationException("无法读取浮窗位置。");
        }
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    internal static void Move(Window window, Point position)
    {
        const uint flags = 0x0001 | 0x0004 | 0x0010; // Keep size, stacking order and activation.
        SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, position.X, position.Y, 0, 0, flags);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
