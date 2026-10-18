using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace VibeDoze.UI;

/// <summary>
/// Apple-style palette (light / dark) published as VD.* brushes in the application resources, following
/// the Windows app theme live. The taskbar theme (tray icon colour) is tracked separately.
/// </summary>
internal static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static readonly Color Accent = Color.FromRgb(0x5E, 0x5C, 0xE6);

    public static bool AppsDark { get; private set; }
    public static bool TaskbarDark { get; private set; }

    /// <summary>Raised on the UI thread after the palette changed.</summary>
    public static event Action? Changed;

    public static void Initialize()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
    }

    private static bool? _forced;

    /// <summary>Use a fixed palette regardless of Windows (snapshots).</summary>
    public static void Force(bool dark)
    {
        _forced = dark;
        Application.Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light; // native Fluent controls too
        Apply();
    }

    private static void Apply()
    {
        bool appsDark = _forced ?? ReadDark("AppsUseLightTheme");
        bool taskbarDark = _forced ?? ReadDark("SystemUsesLightTheme");
        bool changed = appsDark != AppsDark || taskbarDark != TaskbarDark || Application.Current.Resources["VD.Panel"] is null;
        AppsDark = appsDark;
        TaskbarDark = taskbarDark;
        if (!changed) return;

        var palette = appsDark ? Dark : Light;
        foreach (var (key, color) in palette) Set(key, color);
        Set("VD.Accent", Accent);
        Set("VD.AccentHover", Color.FromRgb(0x4F, 0x4D, 0xD6));
        Set("VD.OnAccent", Colors.White);

        // Native Fluent controls (slider, combo boxes, text boxes) use the VibeDoze indigo instead of the system accent.
        var secondary = Color.FromRgb(0x70, 0x6E, 0xEA);
        var tertiary = Color.FromRgb(0x81, 0x7F, 0xED);
        foreach (var key in new[] { "AccentFillColorDefaultBrush", "AccentTextFillColorPrimaryBrush", "SliderThumbBackground",
                     "AccentFillColorSelectedTextBackgroundBrush", "TextControlFocusedBorderBrush", "ComboBoxBorderBrushFocused" })
            Set(key, Accent);
        foreach (var key in new[] { "AccentFillColorSecondaryBrush", "AccentTextFillColorSecondaryBrush", "SliderThumbBackgroundPointerOver" })
            Set(key, secondary);
        foreach (var key in new[] { "AccentFillColorTertiaryBrush", "AccentTextFillColorTertiaryBrush" })
            Set(key, tertiary);
        Changed?.Invoke();
    }

    private static void Set(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private static bool ReadDark(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(valueName) is int light && light == 0;
    }

    private static Color C(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static readonly (string, Color)[] Light =
    {
        ("VD.Panel", C(0xFFF2F2F7)), ("VD.Module", C(0xFFFFFFFF)), ("VD.ModuleHover", C(0xFFF7F7FA)),
        ("VD.Text", C(0xFF1D1D1F)), ("VD.Sub", C(0xFF6E6E73)), ("VD.Sep", C(0x1F3C3C43)),
        ("VD.Fill", C(0xFFEEEEF2)), ("VD.FillHover", C(0xFFE5E5EA)), ("VD.SegTrack", C(0xFFEEEEF2)),
        ("VD.SegOn", C(0xFFFFFFFF)), ("VD.Hair", C(0x14000000)), ("VD.Green", C(0xFF28A745)),
        ("VD.GreenDot", C(0xFF34C759)), ("VD.GreenSoft", C(0x2434C759)), ("VD.Dim", C(0xFFC7C7CC)),
        ("VD.Orange", C(0xFFFF9500)), ("VD.OrangeSoft", C(0x24FF9500)), ("VD.Red", C(0xFFFF3B30)),
        ("VD.RedSoft", C(0x24FF3B30)), ("VD.AccentSoft", C(0x245E5CE6)), ("VD.Blue", C(0xFF0A84FF)),
        ("VD.Gray", C(0xFF8E8E93)), ("VD.Sidebar", C(0xFFF2F2F7)), ("VD.Content", C(0xFFFBFBFD)),
        ("VD.Group", C(0xFFFFFFFF)), ("VD.GroupBorder", C(0x12000000)), ("VD.SwitchOff", C(0xFFE3E3E8)),
        ("VD.Field", C(0x0F000000)),
    };

    private static readonly (string, Color)[] Dark =
    {
        ("VD.Panel", C(0xFF1C1C1E)), ("VD.Module", C(0xFF2C2C2E)), ("VD.ModuleHover", C(0xFF333336)),
        ("VD.Text", C(0xFFF5F5F7)), ("VD.Sub", C(0xFF98989D)), ("VD.Sep", C(0x14FFFFFF)),
        ("VD.Fill", C(0xFF3A3A3C)), ("VD.FillHover", C(0xFF444447)), ("VD.SegTrack", C(0xFF1C1C1E)),
        ("VD.SegOn", C(0xFF636366)), ("VD.Hair", C(0x1AFFFFFF)), ("VD.Green", C(0xFF30D158)),
        ("VD.GreenDot", C(0xFF30D158)), ("VD.GreenSoft", C(0x2930D158)), ("VD.Dim", C(0xFF636366)),
        ("VD.Orange", C(0xFFFF9F0A)), ("VD.OrangeSoft", C(0x29FF9F0A)), ("VD.Red", C(0xFFFF453A)),
        ("VD.RedSoft", C(0x29FF453A)), ("VD.AccentSoft", C(0x335E5CE6)), ("VD.Blue", C(0xFF0A84FF)),
        ("VD.Gray", C(0xFF8E8E93)), ("VD.Sidebar", C(0xFF232325)), ("VD.Content", C(0xFF1C1C1E)),
        ("VD.Group", C(0xFF2C2C2E)), ("VD.GroupBorder", C(0x14FFFFFF)), ("VD.SwitchOff", C(0xFF48484A)),
        ("VD.Field", C(0x14FFFFFF)),
    };
}
