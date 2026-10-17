namespace VibeDoze.Core;

/// <summary>Tiny append-only log in %LOCALAPPDATA%\VibeDoze\vibedoze.log (rotated at ~1 MB).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1_000_000;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var file = AppPaths.LogFile;
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Copy(file, file + ".1", overwrite: true);
                    File.WriteAllText(file, string.Empty);
                }
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
