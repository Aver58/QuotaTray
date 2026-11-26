using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace CodexQuotaTray;

internal static class Surface
{
    internal static readonly Color TextColor = Color.FromArgb(0xEE, 0xF2, 0xF8);
    internal static readonly Color Muted = Color.FromArgb(0xA7, 0xB1, 0xC2);
    internal static readonly Color Panel = Color.FromArgb(248, 12, 14, 19);
    internal static readonly Color Outline = Color.FromArgb(65, 255, 255, 255);
    internal static readonly Color Hover = Color.FromArgb(0x26, 0x34, 0x4C);
    internal static readonly Color Divider = Color.FromArgb(0x2A, 0x30, 0x3B);

    internal static Color Status(double? remaining) => remaining switch
    {
        null => Color.FromArgb(0x9A, 0x9A, 0xA0),
        <= 0.10 => Color.FromArgb(0xFF, 0x45, 0x3A),
        <= 0.30 => Color.FromArgb(0xFF, 0xD2, 0x3F),
        _ => Color.FromArgb(0x3D, 0xDC, 0x84)
    };

    internal static void Render(NativeWindow window, Rectangle bounds, int dpi, Action<Graphics> draw)
    {
        var dc = Win32.CreateCompatibleDC(0);
        if (dc == 0) throw new Win32Exception();
        var header = new Win32.BitmapInfo
        {
            Size = (uint)Marshal.SizeOf<Win32.BitmapInfo>(), Width = bounds.Width, Height = -bounds.Height,
            Planes = 1, BitCount = 32
        };
        var image = Win32.CreateDIBSection(dc, ref header, 0, out var pixels, 0, 0);
        if (image == 0)
        {
            Win32.DeleteDC(dc);
            throw new Win32Exception();
        }
        var previous = Win32.SelectObject(dc, image);
        try
        {
            using (var bitmap = new Bitmap(bounds.Width, bounds.Height, bounds.Width * 4, PixelFormat.Format32bppPArgb, pixels))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                Configure(graphics);
                graphics.Clear(Color.Transparent);
                graphics.ScaleTransform(dpi / 96f, dpi / 96f);
                draw(graphics);
                graphics.Flush();
            }
            var destination = bounds.Location;
            var size = bounds.Size;
            var source = Point.Empty;
            var blend = new Win32.Blend { Alpha = 255, Format = 1 };
            if (!Win32.UpdateLayeredWindow(window.Handle, 0, ref destination, ref size, dc, ref source, 0, ref blend, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Win32.SelectObject(dc, previous);
            Win32.DeleteObject(image);
            Win32.DeleteDC(dc);
        }
    }

    private static void Configure(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    internal static void Rounded(Graphics g, RectangleF rect, float radius, Color fill, Color? outline = null)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        using var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        if (outline is { } border)
        {
            using var pen = new Pen(border);
            g.DrawPath(pen, path);
        }
    }

    internal static void Text(Graphics g, string? text, RectangleF rect, float size, Color color,
        bool bold = false, StringAlignment align = StringAlignment.Near, bool wrap = false)
    {
        if (string.IsNullOrEmpty(text) || rect.Width <= 0 || rect.Height <= 0) return;
        using var font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = align, LineAlignment = wrap ? StringAlignment.Near : StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter, FormatFlags = wrap ? 0 : StringFormatFlags.NoWrap
        };
        g.DrawString(text, font, brush, rect, format);
    }

    internal static void Line(Graphics g, Color color, float x1, float y1, float x2, float y2)
    {
        using var pen = new Pen(color);
        g.DrawLine(pen, x1, y1, x2, y2);
    }
}

internal sealed class ProviderIcons : IDisposable
{
    private readonly Bitmap[] _icons = [Load("chatgpt.png", true), Load("ark.png"), Load("antigravity.png")];

    internal static readonly QuotaSource[] Sources = [QuotaSource.Codex, QuotaSource.VolcengineAgentPlan, QuotaSource.Antigravity];
    internal Bitmap this[QuotaSource source] => _icons[(int)source];
    internal static string Name(QuotaSource source) => source switch
    {
        QuotaSource.VolcengineAgentPlan => "火山引擎",
        QuotaSource.Antigravity => "Antigravity",
        _ => "Codex"
    };

    private static Bitmap Load(string file, bool tint = false)
    {
        using var data = typeof(App).Assembly.GetManifestResourceStream($"CodexQuotaTray.Provider.{file}")!;
        using var original = Image.FromStream(data);
        var icon = new Bitmap(96, 96, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(icon))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var scale = 96f / Math.Max(original.Width, original.Height);
            var width = original.Width * scale;
            var height = original.Height * scale;
            g.DrawImage(original, (96 - width) / 2, (96 - height) / 2, width, height);
        }
        if (tint)
        {
            for (var y = 0; y < 96; y++)
                for (var x = 0; x < 96; x++)
                    icon.SetPixel(x, y, Color.FromArgb(icon.GetPixel(x, y).A, 0x10, 0xa3, 0x7f));
        }
        return icon;
    }

    public void Dispose()
    {
        foreach (var icon in _icons) icon.Dispose();
    }
}

internal sealed class HoverTip : IDisposable
{
    private readonly NativeWindow _window = new("QuotaTray 提示",
        Win32.ExLayered | Win32.ExToolWindow | Win32.ExTopmost | Win32.ExTransparent | Win32.ExNoActivate);
    private string? _heading;
    private string? _body;
    private Rectangle _bounds;

    internal void Show(string heading, string body, Point position, int dpi, bool inwardLeft = false, bool inwardAbove = false)
    {
        using var bitmap = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bitmap);
        using var titleFont = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
        using var bodyFont = new Font("Segoe UI", 12, GraphicsUnit.Pixel);
        var bodySize = g.MeasureString(body, bodyFont, 220);
        var width = Math.Clamp(Math.Max(g.MeasureString(heading, titleFont).Width, bodySize.Width) + 22, 100, 246);
        var height = bodySize.Height + 34;
        var size = new Size(Win32.Px(width, dpi), Win32.Px(height, dpi));
        var area = Win32.WorkArea(position);
        var x = inwardLeft ? position.X - size.Width : position.X;
        var y = inwardAbove ? position.Y - size.Height : position.Y;
        var bounds = new Rectangle(
            Math.Max(area.Left, Math.Min(x, area.Right - size.Width)),
            Math.Max(area.Top, Math.Min(y, area.Bottom - size.Height)), size.Width, size.Height);
        if (_window.IsVisible && _heading == heading && _body == body && _bounds == bounds) return;
        (_heading, _body, _bounds) = (heading, body, bounds);
        Surface.Render(_window, bounds, dpi, canvas =>
        {
            Surface.Rounded(canvas, new RectangleF(0.5f, 0.5f, width - 1, height - 1), 9,
                Color.FromArgb(0x20, 0x26, 0x31), Color.FromArgb(0x42, 0x53, 0x72));
            Surface.Text(canvas, heading, new RectangleF(10, 5, width - 20, 18), 13, Surface.TextColor, true);
            Surface.Text(canvas, body, new RectangleF(10, 24, width - 20, height - 27), 12, Surface.Muted, wrap: true);
        });
        _window.Show(false);
    }

    internal void Hide() => _window.Hide();
    public void Dispose() => _window.Dispose();
}
