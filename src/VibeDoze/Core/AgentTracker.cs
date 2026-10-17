namespace VibeDoze.Core;

public enum TurnState
{
    Idle,
    Working,
    Waiting,
}

internal sealed class Conversation
{
    public required string Id { get; init; }
    public TurnState State { get; set; } = TurnState.Idle;
    public DateTime StateSinceUtc { get; set; }
    public DateTime LastEventUtc { get; set; }
    public DateTime LastWorkingUtc { get; set; } = DateTime.MinValue;
    public long LastClientTicks { get; set; }
    public string? Cwd { get; set; }
}

/// <summary>A running agent (root process) plus the conversations its hooks reported.</summary>
internal sealed class AgentProcess
{
    public required string Key { get; init; }
    public int Pid { get; init; }
    public AgentKind Kind { get; set; }
    public string Host { get; set; } = "…";
    public string? ExePath { get; set; }
    public double CpuPercent { get; set; }
    public DateTime FirstSeenUtc { get; init; }
    public DateTime LastCpuActiveUtc { get; set; } = DateTime.MinValue;
    /// <summary>Consecutive scans (~3 s each) above the CPU threshold.</summary>
    public int CpuActiveStreak { get; set; }
    public DateTime LastHookUtc { get; set; } = DateTime.MinValue;
    public bool SeenByScanner { get; set; }
    public Dictionary<string, Conversation> Conversations { get; } = new();

    // ── computed by Evaluate ──
    public bool HasHooks => LastHookUtc != DateTime.MinValue;
    public bool Busy { get; set; }
    public DateTime? BusySinceUtc { get; set; }
    public bool Working { get; set; }
    public bool Waiting { get; set; }
    public DateTime LastWorkUtc { get; set; } = DateTime.MinValue;
    public TimeSpan Grace { get; set; }

    public string? Project => Conversations.Values
        .Where(c => !string.IsNullOrEmpty(c.Cwd))
        .OrderByDescending(c => c.LastEventUtc)
        .Select(c => Path.GetFileName(c.Cwd!.TrimEnd('\\', '/')))
        .FirstOrDefault();
}

/// <summary>
/// Combines two signals per agent:
///  • hooks (precise): UserPromptSubmit → working … Stop → idle;
///  • CPU of the agent's process tree (fallback when hooks aren't installed).
/// An agent is "busy" while working and for a grace period after it finishes.
/// </summary>
internal sealed class AgentTracker
{
    private readonly Dictionary<string, AgentProcess> _procs = new();
    private Dictionary<string, string> _aliases = new(); // agent key inside a tree → root key

    public IReadOnlyCollection<AgentProcess> Processes => _procs.Values;
    public Dictionary<AgentKind, DateTime> LastHookByKind { get; } = new();

    /// <summary>Processes known from hooks, so the scanner tracks them even if the name rules don't match.</summary>
    public IReadOnlyDictionary<int, (long Creation, AgentKind Kind)> Pinned =>
        _procs.Values
            .Where(p => p.HasHooks && p.Pid != 0 && TryParseCreation(p.Key, out _))
            .GroupBy(p => p.Pid)
            .ToDictionary(g => g.Key, g =>
            {
                TryParseCreation(g.First().Key, out long c);
                return (c, g.First().Kind);
            });

    public void ApplyScan(List<AgentSample> samples, IReadOnlyDictionary<string, string> aliases,
        double cpuThreshold, DateTime now)
    {
        _aliases = new Dictionary<string, string>(aliases);
        var seen = new HashSet<string>();
        foreach (var s in samples)
        {
            if (!_procs.TryGetValue(s.Key, out var p))
            {
                p = new AgentProcess { Key = s.Key, Pid = s.Pid, Kind = s.Kind, FirstSeenUtc = now };
                _procs[s.Key] = p;
            }
            p.Host = s.Host;
            p.ExePath = s.ExePath;
            p.CpuPercent = s.CpuPercent;
            p.SeenByScanner = true;
            if (s.CpuPercent >= cpuThreshold)
            {
                p.LastCpuActiveUtc = now;
                p.CpuActiveStreak++;
            }
            else
            {
                p.CpuActiveStreak = 0;
            }
            seen.Add(s.Key);
        }

        foreach (var p in _procs.Values.ToList())
        {
            if (seen.Contains(p.Key)) continue;

            // A hook reported a process that turned out to live inside another agent's tree: merge it.
            if (_aliases.TryGetValue(p.Key, out var rootKey) && _procs.TryGetValue(rootKey, out var root) && root != p)
            {
                foreach (var (id, conv) in p.Conversations) root.Conversations[id] = conv;
                if (p.LastHookUtc > root.LastHookUtc) root.LastHookUtc = p.LastHookUtc;
                _procs.Remove(p.Key);
                continue;
            }

            bool hookOnly = p.Pid == 0;
            if (hookOnly)
            {
                bool stale = !p.Conversations.Values.Any(c => c.State == TurnState.Working)
                             && now - p.LastHookUtc > TimeSpan.FromHours(1);
                if (stale) _procs.Remove(p.Key);
            }
            else if (p.SeenByScanner || now - p.FirstSeenUtc > TimeSpan.FromSeconds(15))
            {
                _procs.Remove(p.Key); // process exited
            }
        }
    }

    public void OnHook(HookMessage m, DateTime now)
    {
        var kind = m.Agent == "codex" ? AgentKind.Codex : AgentKind.Claude;
        LastHookByKind[kind] = now;

        string key = m.AgentPid != 0
            ? AgentScanner.MakeKey(m.AgentPid, m.AgentCreation)
            : $"hook:{m.Agent}:{m.SessionId ?? "default"}";
        if (_aliases.TryGetValue(key, out var root)) key = root;

        if (!_procs.TryGetValue(key, out var p))
        {
            p = new AgentProcess { Key = key, Pid = m.AgentPid, Kind = kind, FirstSeenUtc = now };
            if (m.AgentPid == 0) p.Host = "desconocido";
            _procs[key] = p;
        }
        p.LastHookUtc = now;

        string convId = string.IsNullOrEmpty(m.SessionId) ? "default" : m.SessionId;
        if (IsEvent(m.Event, "SessionEnd"))
        {
            p.Conversations.Remove(convId);
            return;
        }

        if (!p.Conversations.TryGetValue(convId, out var conv))
        {
            conv = new Conversation { Id = convId, StateSinceUtc = now };
            p.Conversations[convId] = conv;
        }
        if (m.ClientTicks != 0 && m.ClientTicks < conv.LastClientTicks) return; // older async hook arriving late
        conv.LastClientTicks = Math.Max(conv.LastClientTicks, m.ClientTicks);
        conv.LastEventUtc = now;
        if (!string.IsNullOrEmpty(m.Cwd)) conv.Cwd = m.Cwd;

        if (MapEvent(m.Event, m.NotificationType) is not { } next) return;
        if (next != conv.State)
        {
            Log.Info($"{AgentRules.DisplayName(kind)} (pid {m.AgentPid}): {conv.State} → {next} [{m.Event}]");
            if (conv.State == TurnState.Working) conv.LastWorkingUtc = now;
            conv.State = next;
            conv.StateSinceUtc = now;
        }
        if (conv.State == TurnState.Working) conv.LastWorkingUtc = now;
    }

    public void Evaluate(DateTime now, AppSettings settings)
    {
        var stale = TimeSpan.FromMinutes(settings.HookStaleMinutes);
        var hardCap = TimeSpan.FromMinutes(settings.HookHardCapMinutes);
        foreach (var p in _procs.Values)
        {
            bool working = false, waiting = false;
            var lastWork = DateTime.MinValue;

            foreach (var conv in p.Conversations.Values)
            {
                // Permission prompt answered → the tool runs and the tree uses CPU again. Require sustained
                // CPU (2+ scans) so an idle MCP server or watcher in the tree doesn't fake it.
                if (conv.State == TurnState.Waiting && p.CpuActiveStreak >= 2
                    && p.LastCpuActiveUtc > conv.StateSinceUtc.AddSeconds(5))
                {
                    conv.State = TurnState.Working;
                    conv.StateSinceUtc = p.LastCpuActiveUtc;
                }

                if (conv.State == TurnState.Working)
                {
                    var lastSign = Max(conv.LastEventUtc, p.LastCpuActiveUtc);
                    // Interrupted (Esc fires no Stop), crashed, or ended with an error that fired no hook.
                    // CPU alone (dev servers, watchers left running) can't keep a turn alive forever: an active
                    // turn produces tool hooks well within the hard cap.
                    if (now - lastSign > stale || now - conv.LastEventUtc > hardCap)
                    {
                        conv.State = TurnState.Idle;
                        conv.StateSinceUtc = now;
                        conv.LastWorkingUtc = Min(lastSign, conv.LastEventUtc + hardCap);
                        Log.Info($"{AgentRules.DisplayName(p.Kind)}: turno sin señales del agente, se da por terminado");
                    }
                    else
                    {
                        working = true;
                        conv.LastWorkingUtc = now;
                    }
                }
                waiting |= conv.State == TurnState.Waiting;
                lastWork = Max(lastWork, conv.LastWorkingUtc);
            }

            if (!p.HasHooks)
            {
                // CPU-only detection.
                working = now - p.LastCpuActiveUtc < TimeSpan.FromSeconds(10);
                lastWork = Max(lastWork, p.LastCpuActiveUtc);
            }

            int graceMinutes = p.HasHooks
                ? settings.FinishGraceMinutes
                : Math.Max(settings.FinishGraceMinutes, settings.CpuOnlyMinGraceMinutes);
            p.Grace = TimeSpan.FromMinutes(graceMinutes);
            p.Working = working;
            p.Waiting = waiting && !working;
            p.LastWorkUtc = lastWork;

            bool busy = working || (lastWork != DateTime.MinValue && now - lastWork < p.Grace);
            if (busy && !p.Busy) p.BusySinceUtc = now;
            if (!busy) p.BusySinceUtc = null;
            p.Busy = busy;
        }
    }

    private static TurnState? MapEvent(string evt, string? notificationType)
    {
        switch (evt.ToLowerInvariant())
        {
            case "userpromptsubmit":
            case "userpromptexpansion":
            case "pretooluse":
            case "posttooluse":
            case "posttoolusefailure":
            case "posttoolbatch":
            case "subagentstart":
            case "precompact":
            case "postcompact":
                return TurnState.Working;
            case "permissionrequest":
                return TurnState.Waiting;
            case "notification":
                return (notificationType ?? string.Empty).ToLowerInvariant() switch
                {
                    "permission_prompt" or "elicitation_dialog" => TurnState.Waiting,
                    "idle_prompt" => TurnState.Idle,
                    _ => null,
                };
            case "stop":
            case "stopfailure":
            case "interrupt":
            case "agent-turn-complete":
                return TurnState.Idle;
            default:
                return null;
        }
    }

    private static bool IsEvent(string evt, string name) => string.Equals(evt, name, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseCreation(string key, out long creation)
    {
        creation = 0;
        int i = key.IndexOf(':');
        return i > 0 && !key.StartsWith("hook:") && long.TryParse(key.AsSpan(i + 1), out creation);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
