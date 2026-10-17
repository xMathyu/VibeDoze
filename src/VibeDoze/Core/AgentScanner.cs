namespace VibeDoze.Core;

internal sealed class AgentSample
{
    public required string Key { get; init; }
    public required int Pid { get; init; }
    public required AgentKind Kind { get; init; }
    public required string Host { get; init; }
    public string? ExePath { get; init; }
    public double CpuPercent { get; init; }
    public int TreeSize { get; init; }
}

/// <summary>
/// Finds running agents (root processes only — a node wrapper that spawns codex.exe is one agent) and
/// measures the CPU of each agent's whole process tree (agent + tools it spawned: git, npm, tests…).
/// </summary>
internal sealed class AgentScanner
{
    private readonly Dictionary<(int Pid, long Creation), (AgentKind? Kind, string? Path)> _classified = new();
    private readonly Dictionary<(int Pid, long Creation), string> _hosts = new();
    private Dictionary<(int Pid, long Creation), long> _lastCpu = new();
    private long _lastScan; // FILETIME (UTC, 100 ns) — same clock as process creation times

    public static string MakeKey(int pid, long creation) => $"{pid}:{creation}";

    /// <param name="pinned">Processes reported by hooks (pid → creation, kind), tracked even if the rules don't match them.</param>
    /// <param name="aliases">Every agent process found inside a tree (e.g. codex.exe under its node wrapper) → its root key.</param>
    /// <returns>Null if the process snapshot failed (the caller keeps what it knew).</returns>
    public List<AgentSample>? Scan(IReadOnlyList<string> others, IReadOnlyDictionary<int, (long Creation, AgentKind Kind)> pinned,
        out Dictionary<string, string> aliases)
    {
        aliases = new Dictionary<string, string>();
        var table = ProcessTable.Snapshot();
        if (table.Count == 0) return null;
        long now = DateTime.UtcNow.ToFileTimeUtc();

        var times = new Dictionary<int, (long Creation, long Cpu)?>();
        (long Creation, long Cpu)? Times(int pid)
        {
            if (!times.TryGetValue(pid, out var t))
            {
                t = ProcessTable.TryGetTimes(pid, out long c, out long cpu) ? (c, cpu) : null;
                times[pid] = t;
            }
            return t;
        }

        var children = new Dictionary<int, List<int>>();
        foreach (var e in table.Values)
        {
            if (e.ParentPid == e.Pid) continue;
            if (!children.TryGetValue(e.ParentPid, out var list)) children[e.ParentPid] = list = new List<int>();
            list.Add(e.Pid);
        }

        // 1) Which processes are agents?
        var agents = new Dictionary<int, (AgentKind Kind, string? Path)>();
        var liveKeys = new HashSet<(int, long)>();
        foreach (var e in table.Values)
        {
            bool isPinned = pinned.TryGetValue(e.Pid, out var pin);
            if (!isPinned && !AgentRules.NeedsDetails(e.Name, others)) continue;
            if (Times(e.Pid) is not { } t) continue;
            var key = (e.Pid, t.Creation);
            liveKeys.Add(key);

            if (!_classified.TryGetValue(key, out var info))
            {
                var (path, cmd) = ProcessTable.GetPathAndCommandLine(e.Pid, wantCommandLine: true);
                info = (AgentRules.Classify(e.Name, path, cmd, others), path);
                _classified[key] = info;
            }

            if (isPinned && pin.Creation == t.Creation) agents[e.Pid] = (pin.Kind, info.Path);
            else if (info.Kind is { } kind) agents[e.Pid] = (kind, info.Path);
        }

        // 2) Keep only roots (an agent inside another agent's tree belongs to that tree).
        var samples = new List<AgentSample>();
        var newCpu = new Dictionary<(int, long), long>();
        long wall = _lastScan == 0 ? 0 : now - _lastScan;

        foreach (var (pid, agent) in agents)
        {
            if (HasAgentAncestor(pid, table, agents, Times)) continue;
            if (Times(pid) is not { } rootTimes) continue;

            // 3) CPU of the whole tree since the previous scan.
            string rootKeyText = MakeKey(pid, rootTimes.Creation);
            long delta = 0;
            int size = 0;
            var queue = new Queue<(int Pid, long Creation)>();
            queue.Enqueue((pid, rootTimes.Creation));
            while (queue.Count > 0 && size < 512)
            {
                var (cur, curCreation) = queue.Dequeue();
                if (Times(cur) is not { } ct || ct.Creation != curCreation) continue;
                size++;
                var key = (cur, ct.Creation);
                if (agents.ContainsKey(cur)) aliases[MakeKey(cur, ct.Creation)] = rootKeyText;
                if (_lastCpu.TryGetValue(key, out long last)) delta += Math.Max(0, ct.Cpu - last);
                else if (_lastScan != 0 && ct.Creation > _lastScan) delta += ct.Cpu; // spawned since last scan
                newCpu[key] = ct.Cpu;

                if (!children.TryGetValue(cur, out var kids)) continue;
                foreach (int kid in kids)
                {
                    // Guard against PID reuse: a child can't be older than its parent.
                    if (Times(kid) is { } kt && kt.Creation >= ct.Creation) queue.Enqueue((kid, kt.Creation));
                }
            }

            var rootKey = (pid, rootTimes.Creation);
            if (!_hosts.TryGetValue(rootKey, out var host))
                _hosts[rootKey] = host = HostDetector.Detect(pid, table);
            liveKeys.Add(rootKey);

            samples.Add(new AgentSample
            {
                Key = rootKeyText,
                Pid = pid,
                Kind = agent.Kind,
                Host = host,
                ExePath = agent.Path,
                CpuPercent = wall > 0 ? delta * 100.0 / wall : 0,
                TreeSize = size,
            });
        }

        _lastCpu = newCpu;
        _lastScan = now;
        Prune(_classified, liveKeys);
        Prune(_hosts, liveKeys);
        return samples;
    }

    private static bool HasAgentAncestor(int pid, Dictionary<int, ProcEntry> table,
        Dictionary<int, (AgentKind, string?)> agents, Func<int, (long Creation, long Cpu)?> times)
    {
        int cur = pid;
        long curCreation = times(pid)?.Creation ?? long.MaxValue;
        for (int depth = 0; depth < 16; depth++)
        {
            if (!table.TryGetValue(cur, out var e) || !table.ContainsKey(e.ParentPid)) return false;
            int parent = e.ParentPid;
            if (times(parent) is not { } pt || pt.Creation > curCreation) return false; // reused pid → chain broken
            if (agents.ContainsKey(parent)) return true;
            cur = parent;
            curCreation = pt.Creation;
        }
        return false;
    }

    private static void Prune<T>(Dictionary<(int, long), T> cache, HashSet<(int, long)> live)
    {
        if (cache.Count < 256) return;
        foreach (var key in cache.Keys.Where(k => !live.Contains(k)).ToList()) cache.Remove(key);
    }
}
