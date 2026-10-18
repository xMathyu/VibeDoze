using System.Text.Json;

namespace VibeDoze.Core;

/// <summary>
/// State that must survive a crash: the original power settings we overrode (so they can always be
/// restored) and the active shutdown timer.
/// </summary>
public sealed class RuntimeState
{
    public List<LidOriginal>? LidOverride { get; set; }
    /// <summary>Original "sleep after" idle timeouts (per plan) while we shorten them to let Modern Standby kick in.</summary>
    public List<LidOriginal>? IdleOverride { get; set; }
    /// <summary>The Slash lighting while VibeDoze animates it with the lid closed.</summary>
    public SlashOriginal? Slash { get; set; }
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

    /// <summary>Best effort read (an unreadable file reads as empty — use <see cref="TryLoad"/> when it matters).</summary>
    public static RuntimeState Load() => TryLoad(out var state) ? state : new RuntimeState();

    /// <summary>False when the file exists but can't be read or parsed (never treat that as "nothing saved").</summary>
    public static bool TryLoad(out RuntimeState state)
    {
        lock (Gate)
        {
            state = new RuntimeState();
            if (!File.Exists(AppPaths.StateFile)) return true;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    state = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(AppPaths.StateFile), SettingsStore.JsonOptions)
                            ?? new RuntimeState();
                    return true;
                }
                catch (IOException)
                {
                    Thread.Sleep(50); // briefly locked by antivirus / backup
                }
                catch (Exception ex)
                {
                    Log.Error("state.json ilegible", ex);
                    return false;
                }
            }
            Log.Warn("state.json bloqueado");
            return false;
        }
    }

    /// <summary>Read-modify-write. Returns false (and writes nothing) if the current file can't be read or saved.</summary>
    public static bool Update(Action<RuntimeState> change)
    {
        lock (Gate)
        {
            if (!TryLoad(out var state)) return false; // never overwrite saved originals we couldn't read
            change(state);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    SettingsStore.AtomicWrite(AppPaths.StateFile, JsonSerializer.Serialize(state, SettingsStore.JsonOptions));
                    return true;
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    Log.Error("No se pudo guardar state.json", ex);
                    return false;
                }
            }
            Log.Warn("No se pudo guardar state.json (bloqueado)");
            return false;
        }
    }
}
