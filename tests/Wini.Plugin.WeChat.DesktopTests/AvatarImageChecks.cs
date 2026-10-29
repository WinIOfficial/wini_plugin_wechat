using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using WindowsIsland.Plugins;

internal static class AvatarImageChecks
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var encode = typeof(PluginDesktopBroker).GetMethod("Encode", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var size in new[] { 40, 60, 80 })
        {
            using var source = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(source)) graphics.Clear(Color.CornflowerBlue);
            var bytes = (byte[])encode.Invoke(null, [source])!;
            File.WriteAllBytes(Path.Combine(output, $"opaque-{size}.png"), bytes);
            using var stream = new MemoryStream(bytes);
            using var result = new Bitmap(stream);
            foreach (var point in new[] { new Point(0, 32), new Point(63, 32), new Point(32, 0), new Point(32, 63) })
            {
                var pixel = result.GetPixel(point.X, point.Y);
                Console.WriteLine($"EDGE {size} {point.X},{point.Y}: {pixel.R},{pixel.G},{pixel.B},{pixel.A}");
                if (Math.Abs(pixel.R - Color.CornflowerBlue.R) > 2 || Math.Abs(pixel.G - Color.CornflowerBlue.G) > 2
                    || Math.Abs(pixel.B - Color.CornflowerBlue.B) > 2 || pixel.A != 255)
                    throw new InvalidOperationException("Resizing adds a false edge to an opaque portrait.");
            }
            Console.WriteLine($"PASS {size}px source keeps all four portrait edges opaque and unchanged");
        }
        using var transparent = new Bitmap(40, 40, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(transparent)) graphics.Clear(Color.FromArgb(128, 180, 100, 60));
        var alphaBytes = (byte[])encode.Invoke(null, [transparent])!;
        using var alphaStream = new MemoryStream(alphaBytes);
        using var alphaResult = new Bitmap(alphaStream);
        var alpha = alphaResult.GetPixel(63, 32);
        if (Math.Abs(alpha.A - 128) > 1 || Math.Abs(alpha.R - 180) > 3)
            throw new InvalidOperationException("Portrait transparency was flattened or darkened during encoding.");
        Console.WriteLine("PASS Portrait encoding preserves fractional alpha and unpremultiplied edge colors");
        return 0;
    }
}
