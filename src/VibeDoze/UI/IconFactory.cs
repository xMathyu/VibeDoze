using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VibeDoze.UI;

internal enum TrayState
{
    Paused,
    Idle,
    Awake,
    Held,
    Alert,
}

/// <summary>Draws the tray icons at runtime so they stay crisp at any DPI.</summary>
internal static class IconFactory
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Color ColorOf(TrayState state) => state switch
    {
        TrayState.Awake => Color.FromArgb(16, 185, 129),   // emerald
        TrayState.Held => Color.FromArgb(245, 158, 11),    // amber
        TrayState.Alert => Color.FromArgb(239, 68, 68),    // red
        TrayState.Paused => Color.FromArgb(115, 115, 115), // gray
        _ => Color.FromArgb(99, 102, 241),                 // indigo
    };

    public static Icon Create(TrayState state, int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float s = size;
            var background = ColorOf(state);
            using (var brush = new SolidBrush(state == TrayState.Paused ? Color.FromArgb(170, background) : background))
                g.FillEllipse(brush, s * 0.02f, s * 0.02f, s * 0.96f, s * 0.96f);

            using var white = new SolidBrush(Color.White);
            switch (state)
            {
                case TrayState.Awake:
                    DrawSun(g, s);
                    break;
                case TrayState.Held:
                    g.FillRectangle(white, s * 0.33f, s * 0.28f, s * 0.12f, s * 0.44f);
                    g.FillRectangle(white, s * 0.55f, s * 0.28f, s * 0.12f, s * 0.44f);
                    break;
                case TrayState.Alert:
                    g.FillRectangle(white, s * 0.44f, s * 0.20f, s * 0.12f, s * 0.38f);
                    g.FillEllipse(white, s * 0.43f, s * 0.66f, s * 0.14f, s * 0.14f);
                    break;
                default:
                    DrawMoon(g, s, white);
                    break;
            }
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void DrawMoon(Graphics g, float s, Brush brush)
    {
        using var outer = new GraphicsPath();
        outer.AddEllipse(s * 0.22f, s * 0.20f, s * 0.56f, s * 0.56f);
        using var cut = new GraphicsPath();
        cut.AddEllipse(s * 0.38f, s * 0.10f, s * 0.50f, s * 0.50f);
        using var region = new Region(outer);
        region.Exclude(cut);
        g.FillRegion(brush, region);
    }

    private static void DrawSun(Graphics g, float s)
    {
        float c = s / 2f;
        using var white = new SolidBrush(Color.White);
        float r = s * 0.15f;
        g.FillEllipse(white, c - r, c - r, r * 2, r * 2);
        using var pen = new Pen(Color.White, Math.Max(1.3f, s * 0.075f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            float x1 = c + (float)Math.Cos(a) * s * 0.25f, y1 = c + (float)Math.Sin(a) * s * 0.25f;
            float x2 = c + (float)Math.Cos(a) * s * 0.35f, y2 = c + (float)Math.Sin(a) * s * 0.35f;
            g.DrawLine(pen, x1, y1, x2, y2);
        }
    }
}
