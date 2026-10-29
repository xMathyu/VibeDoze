using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeDoze.Core;

namespace VibeDoze.UI;

/// <summary>
/// "VibeDoze.exe snapshot [folder] [light|dark]" — renders the panel, every settings section, the countdown
/// and the tray icons to PNG files off-screen (works with the screen locked). A design QA aid.
/// </summary>
internal static class Snapshot
{
    public static void Render(Engine engine, App app, string dir)
    {
        Directory.CreateDirectory(dir);
        string theme = ThemeManager.AppsDark ? "dark" : "light";

        var panel = new MainWindow(engine, app);
        panel.RenderNow();
        Save((FrameworkElement)panel.Content, 452, null, null, Path.Combine(dir, $"panel-{theme}.png"));

        var settings = new SettingsWindow(engine, app);
        foreach (var section in new[] { "SecGeneral", "SecAgents", "SecLid", "SecTimer", "SecProtection", "SecUpdates" })
        {
            settings.ShowSection(section);
            Save((FrameworkElement)settings.Content, 920, 660, settings.Background,
                Path.Combine(dir, $"settings-{section[3..].ToLowerInvariant()}-{theme}.png"));
        }

        // The SSH steps only show with the option on: Agentes once more with it on, in memory only (never saved).
        bool remote = engine.Settings.RemoteEnabled;
        try
        {
            engine.Settings.RemoteEnabled = true;
            settings.ShowSection("SecAgents");
            settings.RenderNow();
            Save((FrameworkElement)settings.Content, 920, 1000, settings.Background, Path.Combine(dir, $"settings-agents-ssh-{theme}.png"));
        }
        finally
        {
            engine.Settings.RemoteEnabled = remote;
        }

        var now = DateTime.UtcNow;
        var pending = new PendingAction
        {
            Action = SystemAction.Sleep,
            Source = ActionSource.Thermal,
            Reason = "Temperatura alta: 82 °C",
            ExecuteAtUtc = now.AddSeconds(18),
            CreatedUtc = now.AddSeconds(-12),
        };
        var countdown = new CountdownWindow(engine, pending, preview: true);
        Save((FrameworkElement)countdown.Content, 512, null, null, Path.Combine(dir, $"countdown-{theme}.png"));

        SaveTrayIcons(Path.Combine(dir, "tray-icons.png"));
        Log.Info($"Capturas guardadas en {dir}");
    }

    private static void Save(FrameworkElement root, double width, double? height, Brush? background, string path)
    {
        root.Measure(new Size(width, height ?? double.PositiveInfinity));
        var size = new Size(width, height ?? root.DesiredSize.Height);
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        if (background is not null)
        {
            var fill = new DrawingVisual();
            using (var dc = fill.RenderOpen()) dc.DrawRectangle(background, null, new Rect(size));
            bitmap.Render(fill);
        }
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void SaveTrayIcons(string path)
    {
        const int size = 64, pad = 24;
        var states = Enum.GetValues<TrayState>();
        using var sheet = new System.Drawing.Bitmap(pad + states.Length * (size + pad), 2 * (size + 2 * pad));
        using var g = System.Drawing.Graphics.FromImage(sheet);
        foreach (var (dark, row) in new[] { (true, 0), (false, 1) })
        {
            using var bg = new System.Drawing.SolidBrush(dark ? System.Drawing.Color.FromArgb(32, 32, 32) : System.Drawing.Color.FromArgb(243, 243, 243));
            g.FillRectangle(bg, 0, row * (size + 2 * pad), sheet.Width, size + 2 * pad);
            for (int i = 0; i < states.Length; i++)
            {
                using var icon = IconFactory.Create(states[i], size, dark);
                g.DrawIcon(icon, new System.Drawing.Rectangle(pad + i * (size + pad), row * (size + 2 * pad) + pad, size, size));
            }
        }
        sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
