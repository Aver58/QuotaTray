using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace CodexQuotaTray;

internal static partial class HudPlacement
{
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
