namespace VibeDoze.Core;

internal static class AppPaths
{
    public static string RoamingDir { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeDoze"));

    public static string LocalDir { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeDoze"));

    public static string SettingsFile => Path.Combine(RoamingDir, "settings.json");
    public static string StateFile => Path.Combine(LocalDir, "state.json");
    public static string LogFile => Path.Combine(LocalDir, "vibedoze.log");

    /// <summary>Full path of the running executable (the single-file exe when published).</summary>
    public static string ExePath { get; } = Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "VibeDoze.exe");

    public static string UserHome { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Per-user pipe name so two Windows users never talk to each other's instance.</summary>
    public static string PipeName { get; } = "VibeDoze-" + Environment.UserName.ToLowerInvariant();

    private static string Ensure(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { /* reported later on write */ }
        return dir;
    }
}
