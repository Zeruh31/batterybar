using static BatteryBar.NativeMethods;

namespace BatteryBar;

/// <summary>A coverage mask (0-255 per pixel) for one string, as drawn at origin (0,0) with the pen at <see cref="OriginX"/>.</summary>
/// <param name="Advance">Horizontal advance of the string (where the next string starts).</param>
/// <param name="OriginX">Mask column of the pen start (masks have a margin for glyph overhang).</param>
/// <param name="InkTop">First mask row with any coverage, or -1 when the string has no ink.</param>
/// <param name="InkBottom">Last mask row with any coverage.</param>
internal sealed record GlyphMask(int Width, int Height, int Advance, int OriginX, byte[] Coverage, int InkTop, int InkBottom)
{
    public int InkHeight => InkTop < 0 ? 0 : InkBottom - InkTop + 1;
}

/// <summary>
/// Rasterizes text with GDI's hinted grayscale antialiasing (ANTIALIASED_QUALITY) into coverage masks.
/// Layered per-pixel-alpha windows cannot use ClearType, and GDI+'s own rasterizer gives thin, soft stems at
/// taskbar sizes. GDI hinting snaps stems to whole pixels like the taskbar clock, and a contrast curve on the
/// coverage (similar to DirectWrite's enhanced contrast) keeps light-on-dark text from looking washed out.
/// </summary>
internal sealed unsafe class GdiTextRasterizer : IDisposable
{
    private const double ContrastGamma = 1.8;

    private static readonly byte[] ContrastLut = BuildLut();

    private readonly IntPtr _dc = CreateCompatibleDC(IntPtr.Zero);
    private readonly Dictionary<(string Family, int Pixels), IntPtr> _fonts = new();

    public GlyphMask Rasterize(string text, string family, int pixels)
    {
        IntPtr font = GetFont(family, pixels);
        IntPtr oldFont = SelectObject(_dc, font);
        try
        {
            GetTextExtentPoint32W(_dc, text, text.Length, out SIZE extent);
            int margin = Math.Max(2, pixels / 3);
            int width = Math.Max(1, extent.Cx + 2 * margin);
            int height = Math.Max(1, extent.Cy + 2);

            var header = new BITMAPINFOHEADER
            {
                Size = sizeof(BITMAPINFOHEADER),
                Width = width,
                Height = -height, // top-down rows
                Planes = 1,
                BitCount = 32,
            };
            IntPtr dib = CreateDIBSection(_dc, ref header, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return new GlyphMask(1, 1, extent.Cx, 0, [0], -1, -1);
            }

            IntPtr oldBitmap = SelectObject(_dc, dib);
            try
            {
                // DIB sections start zeroed (black). Draw white text; any channel is the coverage.
                SetBkMode(_dc, TRANSPARENT);
                SetTextColor(_dc, 0x00FFFFFF);
                TextOutW(_dc, margin, 1, text, text.Length);
                GdiFlush();

                var coverage = new byte[width * height];
                int inkTop = -1;
                int inkBottom = -1;
                uint* pixelsPtr = (uint*)bits;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        uint bgra = pixelsPtr[y * width + x];
                        byte g = (byte)((bgra >> 8) & 0xFF);
                        if (g == 0)
                        {
                            continue;
                        }

                        coverage[y * width + x] = ContrastLut[g];
                        if (inkTop < 0)
                        {
                            inkTop = y;
                        }

                        inkBottom = y;
                    }
                }

                return new GlyphMask(width, height, extent.Cx, margin, coverage, inkTop, inkBottom);
            }
            finally
            {
                SelectObject(_dc, oldBitmap);
                DeleteObject(dib);
            }
        }
        finally
        {
            SelectObject(_dc, oldFont);
        }
    }

    public void Dispose()
    {
        foreach (IntPtr font in _fonts.Values)
        {
            DeleteObject(font);
        }

        _fonts.Clear();
        DeleteDC(_dc);
    }

    private IntPtr GetFont(string family, int pixels)
    {
        if (!_fonts.TryGetValue((family, pixels), out IntPtr font))
        {
            // Negative height = character height in pixels (em size), the same as GraphicsUnit.Pixel in GDI+.
            font = CreateFontW(-pixels, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS,
                ANTIALIASED_QUALITY, 0, family);
            _fonts[(family, pixels)] = font;
        }

        return font;
    }

    private static byte[] BuildLut()
    {
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            lut[i] = (byte)Math.Round(255 * Math.Pow(i / 255.0, 1 / ContrastGamma));
        }

        return lut;
    }
}
