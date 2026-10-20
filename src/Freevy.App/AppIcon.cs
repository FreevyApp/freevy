using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Freevy.App;

/// <summary>The Freevy mark (a lowercase f on a turquoise rounded square), drawn at runtime.</summary>
internal static class AppIcon
{
    private static Icon? _icon;

    public static Icon Get() => _icon ??= Draw(64);

    private static Icon Draw(int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var radius = size * 0.28f;
            using var path = new GraphicsPath();
            path.AddArc(0, 0, radius, radius, 180, 90);
            path.AddArc(size - radius - 1, 0, radius, radius, 270, 90);
            path.AddArc(size - radius - 1, size - radius - 1, radius, radius, 0, 90);
            path.AddArc(0, size - radius - 1, radius, radius, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(54, 222, 205));
            g.FillPath(fill, path);

            using var font = new Font("Cascadia Code", size * 0.52f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.FromArgb(12, 34, 38));
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("f", font, text, new RectangleF(0, size * 0.02f, size, size), format);
        }

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
