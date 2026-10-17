using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace VibeDoze.Core;

public enum IntegrationStatus
{
    NotInstalled,
    Installed,
    Outdated, // installed, but pointing to another VibeDoze.exe location
    Error,
}

/// <summary>
/// Installs hooks so agents tell VibeDoze exactly when a turn starts and ends.
/// Each hook runs "VibeDoze.exe hook &lt;agent&gt;" asynchronously, so it never slows the agent down.
/// Our entries are recognised by "vibedoze" in the command, so other hooks are left untouched.
/// </summary>
internal abstract class HookIntegration
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public abstract string Name { get; }
    public abstract string FilePath { get; }
    /// <summary>hooks.json only holds hooks; settings.json holds everything else of the user's.</summary>
    protected virtual bool DeleteFileWhenEmpty => false;
    protected abstract string[] Events { get; }
    protected abstract JsonObject CreateHandler(string exePath);
    protected abstract string CommandOf(string exePath);

    /// <summary>VibeDoze.Hook.exe takes just the agent name; the main exe (fallback) needs the "hook" verb.</summary>
    protected static string[] RelayArgs(string exePath, string agent) =>
        string.Equals(exePath, AppPaths.ExePath, StringComparison.OrdinalIgnoreCase) ? new[] { "hook", agent } : new[] { agent };

    public IntegrationStatus GetStatus()
    {
        try
        {
            var root = Load();
            if (root?["hooks"] is not JsonObject hooks) return IntegrationStatus.NotInstalled;
            string expected = CommandOf(HookRelay.Path);
            bool all = true, current = true;
            foreach (var evt in Events)
            {
                var ours = OurHandlers(hooks[evt] as JsonArray).ToList();
                if (ours.Count == 0) all = false;
                else if (!ours.Any(h => string.Equals(CommandText(h), expected, StringComparison.OrdinalIgnoreCase)))
                    current = false;
            }
            if (!all) return HasAny(hooks) ? IntegrationStatus.Outdated : IntegrationStatus.NotInstalled;
            return current ? IntegrationStatus.Installed : IntegrationStatus.Outdated;
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo leer {FilePath}", ex);
            return IntegrationStatus.Error;
        }
    }

    public void Install()
    {
        var root = Load() ?? new JsonObject();
        Backup();
        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        RemoveOurs(hooks);
        foreach (var evt in Events)
        {
            if (hooks[evt] is not JsonArray groups)
            {
                groups = new JsonArray();
                hooks[evt] = groups;
            }
            groups.Add(new JsonObject { ["hooks"] = new JsonArray(CreateHandler(HookRelay.Path)) });
        }
        Save(root);
        Log.Info($"Hooks de {Name} instalados en {FilePath}");
    }

    public void Uninstall()
    {
        var root = Load();
        if (root?["hooks"] is not JsonObject hooks) return;
        Backup();
        RemoveOurs(hooks);
        if (hooks.Count == 0) root.Remove("hooks");
        if (root.Count == 0 && DeleteFileWhenEmpty) File.Delete(FilePath);
        else Save(root);
        Log.Info($"Hooks de {Name} eliminados de {FilePath}");
    }

    private bool HasAny(JsonObject hooks) =>
        hooks.Any(kv => OurHandlers(kv.Value as JsonArray).Any());

    private static void RemoveOurs(JsonObject hooks)
    {
        foreach (var (evt, value) in hooks.ToList())
        {
            if (value is not JsonArray groups) continue;
            foreach (var group in groups.OfType<JsonObject>().ToList())
            {
                if (group["hooks"] is not JsonArray handlers) continue;
                foreach (var h in handlers.Where(IsOurs).ToList()) handlers.Remove(h);
                if (handlers.Count == 0) groups.Remove(group);
            }
            if (groups.Count == 0) hooks.Remove(evt);
        }
    }

    private static IEnumerable<JsonNode> OurHandlers(JsonArray? groups) =>
        groups is null
            ? Enumerable.Empty<JsonNode>()
            : groups.OfType<JsonObject>()
                .SelectMany(g => g["hooks"] as JsonArray ?? new JsonArray())
                .Where(IsOurs)
                .OfType<JsonNode>();

    private static bool IsOurs(JsonNode? handler) =>
        handler is JsonObject && CommandText(handler).Contains("vibedoze", StringComparison.OrdinalIgnoreCase);

    private static string CommandText(JsonNode? handler) =>
        handler?["command"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

    private JsonObject? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var text = File.ReadAllText(FilePath);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonNode.Parse(text, documentOptions: ParseOptions) as JsonObject
               ?? throw new InvalidDataException($"{FilePath} no contiene un objeto JSON");
    }

    private void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        SettingsStore.AtomicWrite(FilePath, root.ToJsonString(WriteOptions));
    }

    private void Backup()
    {
        try
        {
            var backup = FilePath + ".vibedoze-backup";
            if (File.Exists(FilePath) && !File.Exists(backup)) File.Copy(FilePath, backup);
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo respaldar {FilePath}: {ex.Message}");
        }
    }
}

/// <summary>Claude Code (CLI, VS Code / Cursor extension, desktop): ~/.claude/settings.json.</summary>
internal sealed class ClaudeIntegration : HookIntegration
{
    public override string Name => "Claude Code";

    public override string FilePath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(AppPaths.UserHome, ".claude");
            return Path.Combine(dir, "settings.json");
        }
    }

    protected override string[] Events { get; } =
    {
        "UserPromptSubmit", "PostToolUse", "PostToolUseFailure", "PermissionRequest", "Notification",
        "Stop", "StopFailure", "SessionEnd",
    };

    // Exec form (command + args): no shell involved; forward slashes also survive Git Bash if ever used.
    protected override string CommandOf(string exePath) => exePath.Replace('\\', '/');

    protected override JsonObject CreateHandler(string exePath)
    {
        var args = new JsonArray();
        foreach (var a in RelayArgs(exePath, "claude")) args.Add(a);
        return new JsonObject
        {
            ["type"] = "command",
            ["command"] = CommandOf(exePath),
            ["args"] = args,
            ["async"] = true,
        };
    }
}

/// <summary>Codex (CLI and the VS Code / Cursor extension's app-server): ~/.codex/hooks.json.</summary>
internal sealed class CodexIntegration : HookIntegration
{
    public override string Name => "Codex";
    protected override bool DeleteFileWhenEmpty => true;

    public override string FilePath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(AppPaths.UserHome, ".codex");
            return Path.Combine(dir, "hooks.json");
        }
    }

    protected override string[] Events { get; } =
    {
        "UserPromptSubmit", "PostToolUse", "PermissionRequest", "Stop", "Interrupt", "SessionEnd",
    };

    // Codex runs hooks through cmd.exe /C, and (tested with codex-cli 0.162) a quoted program path makes the
    // hook fail. So we write the path unquoted, using its 8.3 short form if it contains spaces.
    protected override string CommandOf(string exePath) =>
        $"{UnquotablePath(exePath)} {string.Join(' ', RelayArgs(exePath, "codex"))}";

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, System.Text.StringBuilder shortPath, uint size);

    private static string UnquotablePath(string path)
    {
        if (!path.Contains(' ')) return path;
        var buffer = new System.Text.StringBuilder(1024);
        uint n = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
        return n > 0 && n < buffer.Capacity && !buffer.ToString().Contains(' ') ? buffer.ToString() : $"\"{path}\"";
    }

    protected override JsonObject CreateHandler(string exePath) => new()
    {
        ["type"] = "command",
        ["command"] = CommandOf(exePath),
        ["timeout"] = 10,
        ["async"] = true,
    };
}

/// <summary>HKCU\...\Run entry — the registry is the source of truth.</summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VibeDoze";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value
               && value.Contains(AppPaths.ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, $"\"{AppPaths.ExePath}\" --background");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo cambiar el inicio con Windows", ex);
        }
    }
}
