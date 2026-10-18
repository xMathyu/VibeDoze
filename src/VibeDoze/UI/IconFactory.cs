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

/// <summary>
/// Tray icons drawn at runtime: a monochrome cup that matches the taskbar (white on dark, graphite on light).
/// It steams while an agent works; the only colour is the status dot, cut out of the cup.
/// </summary>
internal static class IconFactory
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(TrayState state, int size, bool darkTaskbar)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 24f, size / 24f);

            var ink = darkTaskbar ? Color.White : Color.FromArgb(29, 29, 31);
            if (state == TrayState.Paused) ink = Color.FromArgb(darkTaskbar ? 110 : 95, ink);
            bool steam = state is TrayState.Awake or TrayState.Alert;
            DrawCup(g, ink, steam, size);

            Color? badge = state switch
            {
                TrayState.Awake => darkTaskbar ? Color.FromArgb(48, 209, 88) : Color.FromArgb(52, 199, 89),
                TrayState.Held => darkTaskbar ? Color.FromArgb(255, 159, 10) : Color.FromArgb(255, 149, 0),
                TrayState.Alert => darkTaskbar ? Color.FromArgb(255, 69, 58) : Color.FromArgb(255, 59, 48),
                _ => null,
            };
            if (badge is { } color)
            {
                // Punch a transparent ring around the badge so it reads on any taskbar colour.
                const float cx = 19.2f, cy = 19.2f, r = 4.2f, ring = 1.5f;
                var previous = g.CompositingMode;
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var clear = new SolidBrush(Color.Transparent))
                    g.FillEllipse(clear, cx - r - ring, cy - r - ring, 2 * (r + ring), 2 * (r + ring));
                g.CompositingMode = previous;
                using var fill = new SolidBrush(color);
                g.FillEllipse(fill, cx - r, cy - r, 2 * r, 2 * r);
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

    private static void DrawCup(Graphics g, Color ink, bool steam, int pixelSize)
    {
        // Slightly heavier strokes when the icon is tiny, so the handle and steam don't vanish.
        float boost = pixelSize <= 20 ? 1.18f : 1f;

        using var body = new GraphicsPath();
        body.AddLine(4.5f, 10.5f, 15.5f, 10.5f);
        body.AddLine(15.5f, 10.5f, 15.5f, 15.5f);
        body.AddBezier(15.5f, 15.5f, 15.5f, 17.7f, 13.7f, 19.5f, 11.5f, 19.5f);
        body.AddLine(11.5f, 19.5f, 8.5f, 19.5f);
        body.AddBezier(8.5f, 19.5f, 6.3f, 19.5f, 4.5f, 17.7f, 4.5f, 15.5f);
        body.CloseFigure();
        using (var brush = new SolidBrush(ink)) g.FillPath(brush, body);

        using var handle = new GraphicsPath();
        handle.AddLine(15.5f, 12f, 16.8f, 12f);
        handle.AddArc(14.5f, 12f, 4.6f, 4.6f, -90, 180);
        handle.AddLine(16.8f, 16.6f, 15.5f, 16.6f);
        using (var pen = RoundPen(ink, 1.7f * boost)) g.DrawPath(pen, handle);

        if (!steam) return;
        using var vapour = new GraphicsPath();
        foreach (float x in new[] { 8f, 12f })
        {
            vapour.StartFigure();
            vapour.AddBezier(x, 8.4f, x - 1, 7.4f, x + 1, 6.5f, x, 5.5f);
            vapour.AddBezier(x, 5.5f, x - 1, 4.5f, x + 1, 3.6f, x, 2.6f);
        }
        using (var pen = RoundPen(ink, 1.5f * boost)) g.DrawPath(pen, vapour);
    }

    private static Pen RoundPen(Color color, float width) => new(color, width)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
    };
}
