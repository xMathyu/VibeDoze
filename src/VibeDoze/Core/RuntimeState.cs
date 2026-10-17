using System.Text.Json;

namespace VibeDoze.Core;

/// <summary>
/// State that must survive a crash: the original lid actions we overrode (so they can always be
/// restored) and the active shutdown timer.
/// </summary>
public sealed class RuntimeState
{
    public List<LidOriginal>? LidOverride { get; set; }
    /// <summary>Original "sleep after" idle timeout, while we temporarily shorten it to let Modern Standby kick in.</summary>
    public LidOriginal? IdleOverride { get; set; }
    public DateTime? TimerDeadlineUtc { get; set; }
}

public sealed class LidOriginal
{
    public Guid Scheme { get; set; }
    public uint Ac { get; set; }
    public uint Dc { get; set; }
}

internal static class RuntimeStateStore
{
    private static readonly object Gate = new();

    public static RuntimeState Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(AppPaths.StateFile))
                    return JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(AppPaths.StateFile), SettingsStore.JsonOptions)
                           ?? new RuntimeState();
            }
            catch (Exception ex)
            {
                Log.Error("state.json ilegible", ex);
            }
            return new RuntimeState();
        }
    }

    public static void Update(Action<RuntimeState> change)
    {
        lock (Gate)
        {
            var state = Load();
            change(state);
            try
            {
                SettingsStore.AtomicWrite(AppPaths.StateFile, JsonSerializer.Serialize(state, SettingsStore.JsonOptions));
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo guardar state.json", ex);
            }
        }
    }
}
