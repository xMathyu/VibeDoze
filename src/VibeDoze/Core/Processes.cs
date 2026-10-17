using System.Runtime.InteropServices;

namespace VibeDoze.Core;

public enum AgentKind
{
    Claude,
    Codex,
    Other,
}

internal readonly record struct ProcEntry(int Pid, int ParentPid, string Name);

/// <summary>Cheap process table via Toolhelp (pid, parent pid, exe name).</summary>
internal static class ProcessTable
{
    public static Dictionary<int, ProcEntry> Snapshot()
    {
        var map = new Dictionary<int, ProcEntry>(512);
        IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var entry = new Native.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>() };
            if (!Native.Process32FirstW(snap, ref entry)) return map;
            do
            {
                int pid = (int)entry.th32ProcessID;
                if (pid != 0) map[pid] = new ProcEntry(pid, (int)entry.th32ParentProcessID, entry.szExeFile ?? string.Empty);
            } while (Native.Process32NextW(snap, ref entry));
        }
        finally
        {
            Native.CloseHandle(snap);
        }
        return map;
    }

    /// <summary>Creation time + total CPU (100 ns units). False if the process can't be opened.</summary>
    public static bool TryGetTimes(int pid, out long creation, out long cpu)
    {
        creation = cpu = 0;
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            if (!Native.GetProcessTimes(h, out creation, out _, out long kernel, out long user)) return false;
            cpu = kernel + user;
            return true;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    public static (string? Path, string? CommandLine) GetPathAndCommandLine(int pid, bool wantCommandLine)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return (null, null);
        try
        {
            return (Native.GetImagePath(h), wantCommandLine ? Native.GetCommandLine(h) : null);
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }
}

/// <summary>Decides whether a process is a coding agent (Claude Code, Codex, or a user-listed one).</summary>
internal static class AgentRules
{
    private static readonly HashSet<string> ScriptHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "node.exe", "bun.exe", "deno.exe", "python.exe", "pythonw.exe", "python3.exe", "py.exe", "uv.exe", "uvx.exe",
    };

    public static bool NeedsDetails(string exeName, IReadOnlyList<string> others) =>
        exeName.Equals("claude.exe", StringComparison.OrdinalIgnoreCase)
        || exeName.StartsWith("codex", StringComparison.OrdinalIgnoreCase)
        || ScriptHosts.Contains(exeName)
        || others.Any(o => IsExeName(o) && exeName.Equals(o + ".exe", StringComparison.OrdinalIgnoreCase));

    public static bool IsScriptHost(string exeName) => ScriptHosts.Contains(exeName);

    /// <param name="others">User-listed extra agents (exe names or command-line fragments).</param>
    public static AgentKind? Classify(string exeName, string? path, string? commandLine, IReadOnlyList<string> others)
    {
        string name = exeName.ToLowerInvariant();
        string p = (path ?? string.Empty).ToLowerInvariant();
        string cmd = (commandLine ?? string.Empty).Replace('\\', '/').ToLowerInvariant();

        if (name == "claude.exe")
        {
            // Claude Desktop (Electron) is also claude.exe — skip it, its Claude Code sessions run their own binary.
            if (p.Contains(@"\anthropicclaude\") || p.Contains(@"\windowsapps\") || cmd.Contains("--type=")) return null;
            return AgentKind.Claude;
        }

        if (name == "codex.exe" || (name.StartsWith("codex-") && name.EndsWith("-pc-windows-msvc.exe")))
        {
            if (cmd.Contains("--type=") || p.Contains(@"\windowsapps\")) return null; // Codex desktop app shell
            return AgentKind.Codex;
        }

        if (ScriptHosts.Contains(name) && cmd.Length > 0)
        {
            if (cmd.Contains("@anthropic-ai/claude-code") || cmd.Contains("claude-code/cli.js")) return AgentKind.Claude;
            if (cmd.Contains("@openai/codex")) return AgentKind.Codex;
        }

        foreach (var other in others)
        {
            if (IsExeName(other))
            {
                if (name == other + ".exe") return AgentKind.Other;
            }
            else if (ScriptHosts.Contains(name) && cmd.Contains(other.Replace('\\', '/')))
            {
                return AgentKind.Other;
            }
        }
        return null;
    }

    public static IReadOnlyList<string> ParseOthers(string? list) =>
        (list ?? string.Empty)
            .Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant().Replace(".exe", string.Empty))
            .Where(s => s.Length >= 3)
            .Distinct()
            .ToList();

    private static bool IsExeName(string other) => other.IndexOfAny(new[] { '/', '\\', '@', ' ' }) < 0;

    public static string DisplayName(AgentKind kind) => kind switch
    {
        AgentKind.Claude => "Claude Code",
        AgentKind.Codex => "Codex",
        _ => "Agente",
    };
}

/// <summary>Finds the agent that launched a hook relay by walking up its parent processes.</summary>
internal static class AgentLocator
{
    public static (int Pid, long Creation) FindAncestor(int startPid, AgentKind wanted)
    {
        var table = ProcessTable.Snapshot();
        int cur = startPid;
        if (!ProcessTable.TryGetTimes(cur, out long curCreation, out _)) curCreation = long.MaxValue;
        for (int depth = 0; depth < 12; depth++)
        {
            if (!table.TryGetValue(cur, out var e) || !table.TryGetValue(e.ParentPid, out var parent)) break;
            // A parent can't be younger than its child; otherwise the PID was reused.
            if (!ProcessTable.TryGetTimes(parent.Pid, out long pc, out _) || pc > curCreation) break;
            var (path, cmd) = ProcessTable.GetPathAndCommandLine(parent.Pid, wantCommandLine: true);
            if (AgentRules.Classify(parent.Name, path, cmd, Array.Empty<string>()) == wanted) return (parent.Pid, pc);
            cur = parent.Pid;
            curCreation = pc;
        }
        return (0, 0);
    }
}

/// <summary>Where an agent runs: an IDE (VS Code, Cursor…) or a terminal.</summary>
internal static class HostDetector
{
    private static readonly Dictionary<string, string> Ides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code.exe"] = "VS Code",
        ["code - insiders.exe"] = "VS Code Insiders",
        ["cursor.exe"] = "Cursor",
        ["windsurf.exe"] = "Windsurf",
        ["antigravity.exe"] = "Antigravity",
        ["trae.exe"] = "Trae",
        ["kiro.exe"] = "Kiro",
        ["vscodium.exe"] = "VSCodium",
        ["positron.exe"] = "Positron",
        ["void.exe"] = "Void",
        ["zed.exe"] = "Zed",
        ["devenv.exe"] = "Visual Studio",
        ["idea64.exe"] = "IntelliJ IDEA",
        ["pycharm64.exe"] = "PyCharm",
        ["webstorm64.exe"] = "WebStorm",
        ["rider64.exe"] = "Rider",
        ["goland64.exe"] = "GoLand",
        ["phpstorm64.exe"] = "PhpStorm",
        ["clion64.exe"] = "CLion",
        ["rustrover64.exe"] = "RustRover",
        ["studio64.exe"] = "Android Studio",
        ["fleet.exe"] = "Fleet",
        ["claude.exe"] = "Claude Desktop",
    };

    private static readonly Dictionary<string, string> Terminals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["windowsterminal.exe"] = "Windows Terminal",
        ["wt.exe"] = "Windows Terminal",
        ["wezterm-gui.exe"] = "WezTerm",
        ["alacritty.exe"] = "Alacritty",
        ["warp.exe"] = "Warp",
        ["tabby.exe"] = "Tabby",
        ["hyper.exe"] = "Hyper",
        ["conemu64.exe"] = "ConEmu",
        ["mintty.exe"] = "Git Bash",
        ["ghostty.exe"] = "Ghostty",
    };

    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "sh.exe", "zsh.exe", "fish.exe", "nu.exe", "wsl.exe",
        "wslhost.exe", "conhost.exe", "openconsole.exe",
    };

    public static string Detect(int agentPid, IReadOnlyDictionary<int, ProcEntry> table)
    {
        bool viaShell = false;
        int pid = agentPid;
        var seen = new HashSet<int>();
        for (int depth = 0; depth < 16; depth++)
        {
            if (!table.TryGetValue(pid, out var self) || !seen.Add(pid)) break;
            if (!table.TryGetValue(self.ParentPid, out var parent)) break;
            string name = parent.Name;

            if (name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("services.exe", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
                break;
            if (Ides.TryGetValue(name, out var ide)) return viaShell ? $"{ide} · terminal" : ide;
            if (Terminals.TryGetValue(name, out var term)) return term;
            if (Shells.Contains(name)) viaShell = true;
            pid = parent.Pid;
        }
        return viaShell ? "Terminal" : "Segundo plano";
    }
}
