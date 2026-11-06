using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace TextDock;

internal static class Icons
{
    private static readonly Uri IcoUri = new("pack://application:,,,/Assets/app.ico", UriKind.Absolute);

    /// <summary>The whole .ico; WPF picks the small and large frames for the window.</summary>
    public static BitmapFrame WindowIcon()
    {
        using var stream = OpenIco();
        return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }

    /// <summary>Smallest frame at least <paramref name="pixels"/> wide, or the largest one.</summary>
    public static BitmapSource Frame(int pixels)
    {
        using var stream = OpenIco();
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames
            .OrderBy(f => f.PixelWidth >= pixels ? f.PixelWidth - pixels : 10_000 - f.PixelWidth)
            .First();
    }

    public static Drawing.Icon TrayIcon()
    {
        using var stream = OpenIco();
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public static readonly Drawing.Color UnreadRed = Drawing.Color.FromArgb(0xE5, 0x39, 0x35);
    public static readonly Drawing.Color AlertOrange = Drawing.Color.FromArgb(0xFB, 0x8C, 0x00);

    /// <summary>Tray icon variant with a colored dot (red: unread, orange: reply needed).</summary>
    public static Drawing.Icon WithDot(Drawing.Icon source, Drawing.Color color)
    {
        using var bmp = source.ToBitmap();
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float d = bmp.Width * 0.56f;
            var ring = new Drawing.RectangleF(bmp.Width - d, bmp.Height - d, d, d);
            using var white = new Drawing.SolidBrush(Drawing.Color.White);
            using var fill = new Drawing.SolidBrush(color);
            g.FillEllipse(white, ring);
            ring.Inflate(-bmp.Width * 0.08f, -bmp.Width * 0.08f);
            g.FillEllipse(fill, ring);
        }
        IntPtr handle = bmp.GetHicon();
        try
        {
            return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    /// <summary>Taskbar overlay badge with the unread count.</summary>
    public static ImageSource Badge(int count)
    {
        const int size = 32;
        var text = count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            dc.DrawEllipse(fill, new Pen(Brushes.White, 2), new Point(size / 2.0, size / 2.0), 15, 15);
            var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face,
                text.Length > 1 ? 15 : 19, Brushes.White, 1.0);
            dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static System.IO.Stream OpenIco() =>
        Application.GetResourceStream(IcoUri)?.Stream ?? throw new InvalidOperationException("app.ico resource missing");
}
