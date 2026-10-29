using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeDoze.Core;

public enum SystemAction
{
    Sleep,
    Hibernate,
    Shutdown,
}

public sealed class AppSettings
{
    // ── General ──
    public bool Enabled { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    /// <summary>Check GitHub Releases, download new versions and install them when no agent is working.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>"owner/repo" whose GitHub Releases provide updates.</summary>
    public string UpdateRepository { get; set; } = "xMathyu/VibeDoze";

    // ── Agents ──
    public bool WatchClaude { get; set; } = true;
    public bool WatchCodex { get; set; } = true;
    public bool WatchOthers { get; set; } = false;
    /// <summary>Comma separated: exe names (opencode) or command-line fragments for node/bun/python agents (@google/gemini-cli).</summary>
    public string OtherAgents { get; set; } = "opencode, aider, @google/gemini-cli, @github/copilot";
    /// <summary>Agents on a server reached over SSH send their hooks through a forwarded port (see RemoteHookServer).</summary>
    public bool RemoteEnabled { get; set; } = false;
    /// <summary>Loopback port for those hooks: the same number on both ends of the RemoteForward.</summary>
    public int RemotePort { get; set; } = RemoteSetup.DefaultPort;
    /// <summary>Secret the server's hook script sends with every event. Made the first time RemoteEnabled is on.</summary>
    public string RemoteToken { get; set; } = string.Empty;

    // ── Lid ──
    public bool KeepRunningWithLidClosed { get; set; } = true;
    public SystemAction LidClosedDoneAction { get; set; } = SystemAction.Sleep;
    public bool KeepDisplayOn { get; set; } = false;
    /// <summary>ROG laptops with a Slash LED bar: animate it while working with the lid closed.</summary>
    public bool SlashWhileLidClosed { get; set; } = true;
    /// <summary>The Slash is only a status light: off while the lid is open (instead of the Armoury Crate animation).</summary>
    public bool SlashOffWhileOpen { get; set; } = true;
    /// <summary>Minutes to keep the laptop awake after the agent finished (time to read the result).</summary>
    public int FinishGraceMinutes { get; set; } = 2;

    // ── Mantener activa ──
    /// <summary>Last custom duration typed for Mantener activa.</summary>
    public int CustomTimerMinutes { get; set; } = 90;

    // ── Protection ──
    public bool ThermalGuard { get; set; } = true;
    public int ThermalThresholdC { get; set; } = 80;
    public SystemAction ThermalAction { get; set; } = SystemAction.Sleep;
    /// <summary>Plugged in (charging, gaming…) running hot is normal: only guard while on battery.</summary>
    public bool ThermalOnlyOnBattery { get; set; } = true;
    public bool BatteryGuard { get; set; } = true;
    public int BatteryMinPercent { get; set; } = 10;

    // ── Advanced (only editable in settings.json) ──
    /// <summary>CPU (% of one core) of an agent's process tree that counts as "working".</summary>
    public double CpuActiveThresholdPercent { get; set; } = 1.5;
    /// <summary>Minimum grace for agents detected only by CPU (no hooks): long model "thinking" uses no CPU.</summary>
    public int CpuOnlyMinGraceMinutes { get; set; } = 5;
    /// <summary>A hook-reported "working" turn without any hook/CPU sign of life for this long is considered over (interrupted / crashed).</summary>
    public int HookStaleMinutes { get; set; } = 10;
    /// <summary>A "working" turn with no hook event at all for this long ends, even if its process tree uses CPU.</summary>
    public int HookHardCapMinutes { get; set; } = 30;

    // ── Bookkeeping ──
    /// <summary>A hook event was received at least once (Codex requires approving new hooks in /hooks).</summary>
    public bool ClaudeHooksVerified { get; set; }
    public bool CodexHooksVerified { get; set; }
    public bool FirstRunDone { get; set; }
}

internal static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions);
                if (loaded is not null) return Sanitize(loaded);
            }
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo leer settings.json, usando valores por defecto", ex);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            AtomicWrite(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo guardar settings.json", ex);
        }
    }

    private static AppSettings Sanitize(AppSettings s)
    {
        s.FinishGraceMinutes = Math.Clamp(s.FinishGraceMinutes, 0, 60);
        s.ThermalThresholdC = Math.Clamp(s.ThermalThresholdC, 50, 105);
        s.BatteryMinPercent = Math.Clamp(s.BatteryMinPercent, 3, 50);
        s.CustomTimerMinutes = Math.Clamp(s.CustomTimerMinutes, 1, 24 * 60);
        s.CpuActiveThresholdPercent = Math.Clamp(s.CpuActiveThresholdPercent, 0.1, 50);
        s.CpuOnlyMinGraceMinutes = Math.Clamp(s.CpuOnlyMinGraceMinutes, 0, 60);
        s.HookStaleMinutes = Math.Clamp(s.HookStaleMinutes, 2, 240);
        s.HookHardCapMinutes = Math.Clamp(s.HookHardCapMinutes, 10, 480);
        s.OtherAgents ??= string.Empty;
        if (s.RemotePort is < 1024 or > 65535) s.RemotePort = RemoteSetup.DefaultPort;
        if (s.RemoteToken is null || s.RemoteToken.Any(c => !char.IsAsciiLetterOrDigit(c))) s.RemoteToken = string.Empty; // remade on use
        if (string.IsNullOrWhiteSpace(s.UpdateRepository) || s.UpdateRepository.Count(c => c == '/') != 1)
            s.UpdateRepository = "xMathyu/VibeDoze";
        return s;
    }

    internal static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
