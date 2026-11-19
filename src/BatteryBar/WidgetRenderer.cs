using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using BatteryBar.Core;
using Microsoft.Win32;

namespace BatteryBar;

/// <summary>
/// Draws the widget into a premultiplied 32-bit bitmap at the monitor's physical pixel size.
/// Every glyph is placed on whole pixels; text comes from <see cref="GdiTextRasterizer"/>.
/// </summary>
internal sealed class WidgetRenderer : IDisposable
{
    private const string MouseGlyph = "";     // Mouse
    private const string HeadsetGlyph = "";   // Headphone
    private const string GamepadGlyph = "";   // Game
    private const string OtherGlyph = "";     // Battery10
    private const string ChargingGlyph = "";  // LightningBolt

    // Windows 11 taskbar sizes at 100 %: clock text 9 pt (12 px), tray icons 16 px.
    private const float TextPixels = 12f;
    private const float IconPixels = 16f;
    private const float BoltPixels = 10f;

    private static readonly string IconFamily = PickFamily(["Segoe Fluent Icons", "Segoe MDL2 Assets"]);
    private static readonly string TextFamily = PickFamily(["Segoe UI Variable Text", "Segoe UI"]);

    private readonly GdiTextRasterizer _rasterizer = new();

    public static bool SystemUsesLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Renders the cells; height is the taskbar height so content is vertically centered on it.</summary>
    public Bitmap Render(IReadOnlyList<WidgetSlot> slots, float scale, int height, bool lightTheme, bool hover)
    {
        Color text = lightTheme ? Color.FromArgb(255, 26, 26, 26) : Color.White;
        Color low = lightTheme ? Color.FromArgb(255, 196, 43, 28) : Color.FromArgb(255, 255, 107, 74);
        Color dim = Color.FromArgb(140, text);

        int pad = Px(8, scale);
        int iconGap = Px(5, scale);
        int boltGap = Px(2, scale);
        int slotGap = Px(12, scale);
        int textPx = Px(TextPixels, scale);
        int iconPx = Px(IconPixels, scale);
        int boltPx = Px(BoltPixels, scale);

        var items = new List<(GlyphMask Mask, int X, Color Color)>();
        int x = pad;
        foreach (WidgetSlot slot in slots)
        {
            Color color = slot.IsDimmed ? dim : slot.IsLow ? low : text;
            GlyphMask icon = _rasterizer.Rasterize(GlyphFor(slot.Kind), IconFamily, iconPx);
            items.Add((icon, x, color));
            x += icon.Advance + iconGap;

            GlyphMask value = _rasterizer.Rasterize(slot.Text, TextFamily, textPx);
            items.Add((value, x, color));
            x += value.Advance;

            if (slot.IsCharging)
            {
                x += boltGap;
                GlyphMask bolt = _rasterizer.Rasterize(ChargingGlyph, IconFamily, boltPx);
                items.Add((bolt, x, color));
                x += bolt.Advance;
            }

            x += slotGap;
        }

        int width = Math.Max(1, x - slotGap + pad);
        var bitmap = new Bitmap(width, Math.Max(1, height), PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            // Alpha 1 is invisible but makes the whole rectangle hit-testable (layered windows ignore alpha 0 pixels).
            g.Clear(Color.FromArgb(1, 0, 0, 0));
            if (hover)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int inset = Px(4, scale);
                using GraphicsPath path = RoundedRect(new RectangleF(0, inset, width, height - 2 * inset), 4 * scale);
                using var brush = new SolidBrush(lightTheme ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(22, 255, 255, 255));
                g.FillPath(brush, path);
            }
        }

        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            foreach ((GlyphMask mask, int left, Color color) in items)
            {
                // Center the ink box on the bar, on a whole pixel.
                int top = mask.InkTop < 0 ? 0 : (height - mask.InkHeight) / 2 - mask.InkTop;
                Blend(data, mask, left - mask.OriginX, top, color);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    public void Dispose() => _rasterizer.Dispose();

    /// <summary>Source-over blend of a colored coverage mask into a premultiplied BGRA buffer.</summary>
    private static unsafe void Blend(BitmapData target, GlyphMask mask, int left, int top, Color color)
    {
        byte* scan0 = (byte*)target.Scan0;
        for (int my = 0; my < mask.Height; my++)
        {
            int y = top + my;
            if (y < 0 || y >= target.Height)
            {
                continue;
            }

            uint* row = (uint*)(scan0 + y * target.Stride);
            for (int mx = 0; mx < mask.Width; mx++)
            {
                int x = left + mx;
                byte coverage = mask.Coverage[my * mask.Width + mx];
                if (coverage == 0 || x < 0 || x >= target.Width)
                {
                    continue;
                }

                int a = coverage * color.A / 255;
                int r = color.R * a / 255;
                int gr = color.G * a / 255;
                int b = color.B * a / 255;
                uint dst = row[x];
                int inv = 255 - a;
                int da = (int)(dst >> 24);
                int dr = (int)((dst >> 16) & 0xFF);
                int dg = (int)((dst >> 8) & 0xFF);
                int db = (int)(dst & 0xFF);
                row[x] = (uint)(((a + da * inv / 255) << 24) | ((r + dr * inv / 255) << 16) | ((gr + dg * inv / 255) << 8) | (b + db * inv / 255));
            }
        }
    }

    private static int Px(float value, float scale) => Math.Max(1, (int)Math.Round(value * scale));

    private static string GlyphFor(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => MouseGlyph,
        DeviceKind.Headset => HeadsetGlyph,
        DeviceKind.Gamepad => GamepadGlyph,
        _ => OtherGlyph,
    };

    private static string PickFamily(string[] candidates)
    {
        using var installed = new InstalledFontCollection();
        var names = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.FirstOrDefault(names.Contains) ?? candidates[^1];
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
