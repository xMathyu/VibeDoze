namespace VibeDoze.UI;

internal static class Format
{
    public static string Remaining(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return $"{span.Seconds} s";
        if (span.TotalHours < 1) return $"{(int)Math.Ceiling(span.TotalMinutes)} min";
        return span.Minutes == 0 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalHours} h {span.Minutes:00} min";
    }

    public static string Duration(int minutes) =>
        minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60:00}";

    public static string Since(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "hace un momento";
        if (span.TotalHours < 1) return $"hace {(int)span.TotalMinutes} min";
        return $"hace {(int)span.TotalHours} h {span.Minutes:00} min";
    }
}
