using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;

namespace CodexQuotaTray;

internal static class TrayIconFactory
{
    public static Icon Create(int percent, bool isAvailable = true)
    {
        percent = Math.Clamp(percent, 0, 100);
        var iconBytes = BuildIco(
            new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 },
            size => DrawGauge(size, percent, isAvailable)
        );

        using var stream = new MemoryStream(iconBytes);
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    private static Bitmap DrawGauge(int size, int percent, bool isAvailable)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);

        DrawArc(graphics, size, percent, isAvailable);
        DrawPercentText(graphics, size, percent, isAvailable);
        return bitmap;
    }

    private static void DrawArc(Graphics graphics, int size, int percent, bool isAvailable)
    {
        var stroke = Math.Max(2.6f, size * 0.16f);
        var arcSize = size * 0.86f;
        var arcX = (size - arcSize) / 2f;
        var arcY = size * 0.03f;
        var arcRect = new RectangleF(arcX, arcY, arcSize, arcSize);
        const float startAngle = 200f;
        const float sweepAngle = 140f;
        using var fillPen = new Pen(Color.FromArgb(54, 231, 95), stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        if (isAvailable)
        {
            graphics.DrawArc(fillPen, arcRect, startAngle, sweepAngle * percent / 100f);
        }
    }

    private static void DrawPercentText(Graphics graphics, int size, int percent, bool isAvailable)
    {
        var text = isAvailable ? $"{percent}%" : "--";
        var maxWidth = size * 1.28f;
        var maxHeight = size * 0.86f;
        var fontSize = size * 1.56f;
        using var textBrush = new SolidBrush(Color.White);

        using var font = FitFont(graphics, text, fontSize, maxWidth, maxHeight, FontStyle.Bold);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };

        var rect = new RectangleF(-size * 0.14f, size * 0.2f, size * 1.28f, size * 0.78f);
        graphics.DrawString(text, font, textBrush, rect, format);
    }

    private static Font FitFont(Graphics graphics, string text, float initialSize, float maxWidth, float maxHeight, FontStyle style)
    {
        var current = initialSize;
        while (current > 5)
        {
            var font = new Font("Segoe UI", current, style, GraphicsUnit.Pixel);
            var measured = graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            if (measured.Width <= maxWidth && measured.Height <= maxHeight)
            {
                return font;
            }

            font.Dispose();
            current -= 0.8f;
        }

        return new Font("Segoe UI", Math.Max(5, current), style, GraphicsUnit.Pixel);
    }

    private static byte[] BuildIco(IEnumerable<int> sizes, Func<int, Bitmap> draw)
    {
        var images = new List<(int Size, byte[] Png)>();
        foreach (var size in sizes)
        {
            using var bitmap = draw(size);
            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
            images.Add((size, pngStream.ToArray()));
        }

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);

        var offset = 6 + images.Count * 16;
        foreach (var image in images)
        {
            writer.Write((byte)(image.Size >= 256 ? 0 : image.Size));
            writer.Write((byte)(image.Size >= 256 ? 0 : image.Size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(image.Png.Length);
            writer.Write(offset);
            offset += image.Png.Length;
        }

        foreach (var image in images)
        {
            writer.Write(image.Png);
        }

        return output.ToArray();
    }
}
