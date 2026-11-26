using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;

namespace CodexQuotaTray;

internal sealed record ProviderAppearance(string Name, ImageSource Icon)
{
    private static readonly ProviderAppearance Codex = new("Codex", LoadIcon("chatgpt.png", Color.FromRgb(0x10, 0xa3, 0x7f)));
    private static readonly ProviderAppearance Volcano = new("火山引擎", LoadIcon("ark.png"));
    private static readonly ProviderAppearance Antigravity = new("Antigravity", LoadIcon("antigravity.png"));
    private static readonly SolidColorBrush Normal = CreateBrush(0x3d, 0xdc, 0x84);
    private static readonly SolidColorBrush Warning = CreateBrush(0xff, 0xd2, 0x3f);
    private static readonly SolidColorBrush Critical = CreateBrush(0xff, 0x45, 0x3a);
    private static readonly SolidColorBrush Idle = CreateBrush(0x9a, 0x9a, 0xa0);

    internal static ProviderAppearance For(QuotaSource source) => source switch
    {
        QuotaSource.VolcengineAgentPlan => Volcano,
        QuotaSource.Antigravity => Antigravity,
        _ => Codex
    };

    // Agent HUD's dark palette, with thresholds expressed as remaining quota.
    internal static SolidColorBrush StatusBrush(double? remaining) => remaining switch
    {
        null => Idle,
        <= 0.10 => Critical,
        <= 0.30 => Warning,
        _ => Normal
    };

    private static ImageSource LoadIcon(string fileName, Color? tint = null)
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

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
