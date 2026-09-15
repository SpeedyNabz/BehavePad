using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BehavePad.Branding;

namespace BrandKit;

/// <summary>Renders the BehavePad vector brand art into the app icon and PNG artwork.</summary>
public static class Program
{
    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    [STAThread]
    public static int Main(string[] args)
    {
        var root = args.Length > 0 ? Path.GetFullPath(args[0]) : FindRepositoryRoot();
        var appAssets = Directory.CreateDirectory(Path.Combine(root, "src", "BehavePad", "Assets")).FullName;
        var brandAssets = Directory.CreateDirectory(Path.Combine(root, "assets", "brand")).FullName;

        var icons = IconSizes
            .Select(size => (Size: size, Bitmap: Render(BrandArt.CreateAppIcon(simplified: size <= 24), size, size, BrandArt.Size)))
            .ToList();
        WriteIco(Path.Combine(appAssets, "BehavePad.ico"), icons);

        SavePng(Render(BrandArt.CreateAppIcon(), 512, 512, BrandArt.Size), Path.Combine(brandAssets, "behavepad-icon-512.png"));
        SavePng(Render(BrandArt.CreateMark(), 512, 512, BrandArt.Size), Path.Combine(brandAssets, "behavepad-mark-512.png"));
        SavePng(RenderBanner(), Path.Combine(brandAssets, "behavepad-banner.png"));

        Console.WriteLine($"Brand assets written to {appAssets} and {brandAssets}");
        return 0;
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BehavePad.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Run BrandKit from inside the BehavePad repository or pass its path.");
    }

    private static BitmapSource Render(Drawing drawing, int width, int height, double sourceSize)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(width / sourceSize, height / sourceSize));
            dc.DrawDrawing(drawing);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource RenderBanner()
    {
        const int width = 1280, height = 640;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = new LinearGradientBrush(Color.FromRgb(0x13, 0x1D, 0x27), Color.FromRgb(0x0A, 0x0F, 0x15), new Point(0, 0), new Point(1, 1));
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));

            var glow = new RadialGradientBrush(Color.FromArgb(0x55, 0x3E, 0xE6, 0xA8), Color.FromArgb(0, 0x3E, 0xE6, 0xA8))
            {
                Center = new Point(0.25, 0.55),
                GradientOrigin = new Point(0.25, 0.55),
                RadiusX = 0.35,
                RadiusY = 0.6,
            };
            dc.DrawRectangle(glow, null, new Rect(0, 0, width, height));

            const double markSize = 380;
            dc.PushTransform(new TranslateTransform(80, (height - markSize) / 2));
            dc.PushTransform(new ScaleTransform(markSize / BrandArt.Size, markSize / BrandArt.Size));
            dc.DrawDrawing(BrandArt.CreateMark());
            dc.Pop();
            dc.Pop();

            var brandFace = new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var bodyFace = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var white = new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xF7));
            var mint = new SolidColorBrush(Color.FromRgb(0x3E, 0xE6, 0xA8));

            var behave = Text("Behave", brandFace, 118, white);
            var pad = Text("Pad", brandFace, 118, mint);
            dc.DrawText(behave, new Point(500, 188));
            dc.DrawText(pad, new Point(500 + behave.WidthIncludingTrailingWhitespace, 188));
            dc.DrawText(Text("Your controller, on its best behavior.", bodyFace, 36, new SolidColorBrush(Color.FromRgb(0xC9, 0xD5, 0xE0))), new Point(506, 340));
            dc.DrawText(Text("Finds stick drift and phantom presses, then filters them out.", bodyFace, 24, new SolidColorBrush(Color.FromRgb(0x7F, 0x92, 0xA6))), new Point(508, 400));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static FormattedText Text(string text, Typeface face, double size, Brush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, 1.0);

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Classic icon entry: 32-bit BGRA pixels bottom-up, followed by an empty AND mask.</summary>
    private static byte[] EncodeDib(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        var maskRow = (width + 31) / 32 * 4;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height * 2);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);
        writer.Write(pixels.Length + maskRow * height);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        for (var y = height - 1; y >= 0; y--)
        {
            writer.Write(pixels, y * width * 4, width * 4);
        }

        writer.Write(new byte[maskRow * height]);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteIco(string path, IReadOnlyList<(int Size, BitmapSource Bitmap)> images)
    {
        var entries = images.Select(i => (i.Size, Data: i.Size >= 256 ? EncodePng(i.Bitmap) : EncodeDib(i.Bitmap))).ToList();

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)entries.Count);

        var offset = 6 + 16 * entries.Count;
        foreach (var (size, data) in entries)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in entries)
        {
            writer.Write(data);
        }
    }
}
