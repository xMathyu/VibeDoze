using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace VibeDoze.Core;

public enum EngineMode
{
    Paused,
    Idle,
    Awake,
    Held,
    Countdown,
}

public enum ActionSource
{
    Thermal,
    Finished,
    Battery,
    Manual,
}

public sealed class PendingAction
{
    public required SystemAction Action { get; init; }
    public required ActionSource Source { get; init; }
    public required string Reason { get; init; }
    public required DateTime ExecuteAtUtc { get; init; }
    public required DateTime CreatedUtc { get; init; }
}

/// <summary>
/// The brain. Every second it reads hook events, every 3 s it scans agent processes, every 5 s it reads
/// the temperature, and then decides: keep the laptop awake? override the lid? keep the screen and Teams active?
/// run the thermal / battery / "finished with the lid closed" action?
/// </summary>
internal sealed class Engine : IDisposable
{
    private static readonly TimeSpan ThermalCountdown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ThermalLogInterval = TimeSpan.FromMinutes(1);
    /// <summary>Lid closed, sleep requested, still running after this long → hibernate (idle standby takes ~1 min).</summary>
    private static readonly TimeSpan SleepEscalation = TimeSpan.FromSeconds(150);
    /// <summary>The same for a sleep caused by heat: it can't wait as long.</summary>
    private static readonly TimeSpan ThermalSleepEscalation = TimeSpan.FromSeconds(75);
    /// <summary>Still hot this long after a thermal action (it didn't take) → act again.</summary>
    private static readonly TimeSpan ThermalRetry = TimeSpan.FromMinutes(2);
    /// <summary>Right after waking up the lid / screen / charger state may still be the old one: wait for the messages.</summary>
    private static readonly TimeSpan ResumeSettle = TimeSpan.FromSeconds(5);
    /// <summary>A gap this long between ticks means the app was frozen (suspended), if the heartbeat stopped too.</summary>
    private static readonly TimeSpan TickGap = TimeSpan.FromSeconds(10);

    private readonly PowerController _power = new();
    private readonly SlashLight _slash;
    private readonly AgentScanner _scanner = new();
    private readonly AgentTracker _tracker = new();
    private readonly ThermalMonitor _thermal = new();
    private readonly ActivityKeeper _activity = new();
    private readonly HookServer _hookServer;
    private readonly RemoteHookServer _remote;
    private readonly ConcurrentQueue<HookMessage> _inbox = new();
    /// <summary>Agent kinds whose hooks are installed and have reported at least once.</summary>
    private readonly HashSet<AgentKind> _hookKinds = new();
    private readonly DispatcherTimer _timer;
    /// <summary>Applies the power plans a moment after a UI command, once the panel has painted (and animated).</summary>
    private readonly DispatcherTimer _settle;
    /// <summary>
    /// Beats every second on a pool thread. A late tick while it kept beating was the UI thread busy, not the
    /// app frozen: that must not count as waking up (it would cancel a thermal countdown).
    /// </summary>
    private readonly System.Threading.Timer _heartbeat;
    private long _heartbeatTicks;
    private long _heartbeatResumedTicks;
    private readonly HwndSource _window;
    private readonly List<IntPtr> _powerNotifications = new();
    private readonly IntPtr _suspendNotification;

    private long _tick;
    private DateTime _lastTickUtc = DateTime.MinValue;
    private DateTime _wokeUtc = DateTime.MinValue;
    private DateTime _thermalActedUtc = DateTime.MinValue;
    private TimeSpan _sleepEscalation = SleepEscalation;
    private DateTime _thermalSnoozeUntilUtc = DateTime.MinValue;
    private DateTime _thermalLoggedUtc = DateTime.MinValue;
    private double? _thermalLogPeakC;
    private string? _thermalConfigLogged;
    /// <summary>An agent worked while Mantener activa was on without a time limit: it ends when they're done.</summary>
    private bool _keepActiveSawWork;
    /// <summary>The last time we kept the laptop awake it was only for Mantener activa (no agent busy).</summary>
    private bool _awakeForKeepActive;
    private bool _batteryHold;
    private DateTime? _sleepAttemptUtc;
    private bool _suspendObserved;
    private bool _disposed;
    private DateTime _actionExecutedUtc = DateTime.MinValue;
    private DateTime? _displayOnSinceUtc = DateTime.UtcNow;
    private PendingAction? _shownPending;
    /// <summary>The screen came on with the lid closed right after an F15 of ours (see <see cref="OnDisplay"/>).</summary>
    private DateTime? _displayWokenByKeyUtc;
    /// <summary>The hold comes from a sleep VibeDoze didn't ask for: a screen on again is enough, even with the lid closed.</summary>
    private bool _heldByWindowsSleep;
    /// <summary>Windows is shutting down / logging off: settings are restored and must not be re-applied.</summary>
    private DateTime _sessionEndingUntilUtc = DateTime.MinValue;
    private DateTime? _slashPreviewStartUtc;

    public Engine()
    {
        Settings = SettingsStore.Load();

        // Crash recovery: a previous run may have left the lid override applied.
        var state = RuntimeStateStore.Load();
        if (state.LidOverride is not null || state.IdleOverride is not null || state.Slash is not null)
        {
            Log.Warn("Se encontró un ajuste de energía sin restaurar (cierre inesperado); restaurando");
            PowerController.RestoreAllFromState();
        }
        _slash = new SlashLight(); // after the recovery: it picks up whether the bar was left off on purpose
        if (state.KeepActiveUntilUtc is { } until && until > DateTime.UtcNow.AddSeconds(30))
        {
            KeepActive = true;
            KeepActiveUntilUtc = until;
            Log.Info($"Mantener activa: sigue hasta las {until.ToLocalTime():HH:mm}");
        }
        else if (state.KeepActiveUntilUtc is not null)
        {
            RuntimeStateStore.Update(s => s.KeepActiveUntilUtc = null);
        }

        // Hidden top-level window: receives lid / display / power-plan / suspend notifications.
        _window = new HwndSource(new HwndSourceParameters("VibeDoze.PowerEvents")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x80, // WS_EX_TOOLWINDOW
        });
        _window.AddHook(WndProc);
        foreach (var guid in new[] { Native.GUID_LIDSWITCH_STATE_CHANGE, Native.GUID_CONSOLE_DISPLAY_STATE, Native.GUID_ACTIVE_POWERSCHEME })
        {
            var g = guid;
            var h = Native.RegisterPowerSettingNotification(_window.Handle, ref g, Native.DEVICE_NOTIFY_WINDOW_HANDLE);
            if (h != IntPtr.Zero) _powerNotifications.Add(h);
        }
        _suspendNotification = Native.RegisterSuspendResumeNotification(_window.Handle, Native.DEVICE_NOTIFY_WINDOW_HANDLE);

        _hookServer = new HookServer(m => _inbox.Enqueue(m));
        _remote = new RemoteHookServer(m => _inbox.Enqueue(m));
        SyncRemote();

        RepairIntegrations();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _settle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            if (_disposed) return;
            var now = DateTime.UtcNow;
            Evaluate(now);
            UpdateSlash(now);
            Updated?.Invoke();
        };
        _heartbeat = new System.Threading.Timer(_ => Heartbeat());
        Log.Info($"VibeDoze iniciado (Modern Standby: {PowerController.Capabilities.ModernStandby}, " +
                 $"hibernación: {PowerController.Capabilities.CanHibernate}, sensor: {_thermal.SensorName ?? "ninguno"})");
        LogThermalConfig();
    }

    /// <summary>Starts the loop. Called after the UI subscribed to the events, so nothing raised early is lost.</summary>
    public void Start()
    {
        _timer.Start();
        _heartbeat.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Tick();
    }

    // ───────────── Public state (read by the UI) ─────────────

    public AppSettings Settings { get; }
    public ClaudeIntegration ClaudeHooks { get; } = new();
    public CodexIntegration CodexHooks { get; } = new();
    public RemoteHookServer Remote => _remote;
    public ThermalMonitor Thermal => _thermal;

    public bool LidClosed { get; private set; }
    public bool DisplayOn { get; private set; } = true;
    public bool OnBattery { get; private set; }
    public int? BatteryPercent { get; private set; }
    /// <summary>Mantener activa: the screen on and F15 presses (Teams stays available), the lid as when agents work.</summary>
    public bool KeepActive { get; private set; }
    /// <summary>When Mantener activa ends; null: no time limit (it ends when the agents that work meanwhile finish).</summary>
    public DateTime? KeepActiveUntilUtc { get; private set; }
    /// <summary>Minutes chosen for Mantener activa in this session (to highlight its preset).</summary>
    public int? KeepActiveMinutes { get; private set; }
    public string? HoldReason { get; private set; }
    public PendingAction? Pending { get; private set; }
    public EngineMode Mode { get; private set; }
    public string Headline { get; private set; } = "Iniciando…";
    public string Detail { get; private set; } = string.Empty;
    public string? LastAction { get; private set; }
    public bool KeepingAwake => _power.KeepingAwake;
    public bool LidOverrideActive => _power.LidOverrideActive;

    public IReadOnlyList<AgentProcess> Agents =>
        _tracker.Processes.OrderByDescending(a => a.Busy).ThenBy(a => a.Kind).ThenBy(a => a.Pid).ToList();

    public bool HookSeen(AgentKind kind) => _tracker.LastHookByKind.ContainsKey(kind);

    public bool SlashPreviewing => _slashPreviewStartUtc is not null;

    /// <summary>
    /// Nothing would be lost by restarting the app (to install an update): no agent working, nothing applied
    /// to the power settings, no countdown and not waiting for the user after an automatic sleep.
    /// </summary>
    public bool CanRestartSafely =>
        !KeepActive && !_power.KeepingAwake && !_power.LidOverrideActive && !PowerController.IdleOverrideActive
        && Pending is null && HoldReason is null && _sleepAttemptUtc is null
        && !_tracker.Processes.Any(a => a.Busy && IsWatched(a.Kind));

    public bool IsWatched(AgentKind kind) => kind switch
    {
        AgentKind.Claude => Settings.WatchClaude,
        AgentKind.Codex => Settings.WatchCodex,
        _ => Settings.WatchOthers,
    };

    /// <summary>Raised every second (and after any change).</summary>
    public event Action? Updated;
    /// <summary>A sleep/shutdown is about to happen and the user is in front of the screen: show a countdown.</summary>
    public event Action<PendingAction>? ActionPending;
    /// <summary>(title, message) for a tray notification.</summary>
    public event Action<string, string>? Notify;

    // ───────────── Commands (called by the UI) ─────────────

    public void SaveSettings()
    {
        SyncRemote();
        SettingsStore.Save(Settings);
        LogThermalConfig();
        Refresh();
    }

    /// <summary>Listens for agents on a server over SSH while that's on; the token is made the first time.</summary>
    private void SyncRemote()
    {
        if (Settings.RemoteEnabled && string.IsNullOrEmpty(Settings.RemoteToken))
        {
            Settings.RemoteToken = RemoteHookServer.NewToken();
            SettingsStore.Save(Settings);
        }
        _remote.Configure(Settings.RemoteEnabled ? Settings.RemotePort : 0, Settings.RemoteToken);
    }

    /// <summary>
    /// Mantener activa for <paramref name="minutes"/> (it lasts that long even if the agents finish earlier), or
    /// with no time limit (null): until it's turned off or the agents that work meanwhile finish.
    /// </summary>
    public void StartKeepActive(int? minutes)
    {
        KeepActive = true;
        KeepActiveMinutes = minutes;
        KeepActiveUntilUtc = minutes is { } m ? DateTime.UtcNow.AddMinutes(m) : null;
        _keepActiveSawWork = false;
        RuntimeStateStore.Update(s => s.KeepActiveUntilUtc = KeepActiveUntilUtc);
        Log.Info(KeepActiveUntilUtc is { } until
            ? $"Mantener activa: {minutes} min, hasta las {until.ToLocalTime():HH:mm}"
            : "Mantener activa: sin límite de tiempo (hasta que terminen los agentes)");
        Refresh();
    }

    public void StopKeepActive()
    {
        StopKeepActiveCore("desde VibeDoze");
        Refresh();
    }

    private void StopKeepActiveCore(string why)
    {
        if (!KeepActive) return;
        if (KeepActiveUntilUtc is not null) RuntimeStateStore.Update(s => s.KeepActiveUntilUtc = null);
        KeepActive = false;
        KeepActiveUntilUtc = null;
        KeepActiveMinutes = null;
        Log.Info($"Mantener activa: desactivada ({why})");
    }

    /// <summary>User pressed "Cancel" on the countdown.</summary>
    public void CancelPending()
    {
        if (Pending is not { } p) return;
        Pending = null;
        if (p.Source == ActionSource.Thermal)
        {
            _thermalSnoozeUntilUtc = DateTime.UtcNow.AddMinutes(15);
            Log.Warn("Protección térmica pospuesta 15 min por el usuario (o hasta que cierre la tapa)");
        }
        Refresh();
    }

    public void ExecutePendingNow()
    {
        if (Pending is not { } p) return;
        Execute(p);
        Refresh();
    }

    public void SleepNow() =>
        StartAction(SystemAction.Sleep, ActionSource.Manual, "Suspendida desde VibeDoze", DateTime.UtcNow);

    /// <summary>Plays each Slash pattern once with the lid open (Ajustes › Tapa › Probar).</summary>
    public void PreviewSlash()
    {
        _slashPreviewStartUtc = DateTime.UtcNow;
        Refresh();
    }

    /// <summary>Leave the "on hold" state (after an automatic sleep) without waiting for the lid to open.</summary>
    public void ResumeFromHold()
    {
        HoldReason = null;
        _batteryHold = false;
        Refresh();
    }

    /// <summary>
    /// Re-evaluates after a command from the UI. The panel is painted first; the slow part of <see cref="Evaluate"/>
    /// (writing "sleep after" and the lid action to every power plan: up to a second) follows a moment later, so a
    /// click answers at once instead of freezing the panel and then jumping to the new state.
    /// </summary>
    public void Refresh()
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;
        Evaluate(now, powerPlans: false);
        UpdateSlash(now);
        Updated?.Invoke();
        _settle.Stop();
        _settle.Start();
    }

    // ───────────── Loop ─────────────

    private void Tick()
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;
        try
        {
            // After a freeze (suspended) this tick can run before the resume, lid and charger messages arrive.
            if (now - _lastTickUtc > TickGap && FrozenSince(_lastTickUtc, now)) _wokeUtc = now;
            _lastTickUtc = now;

            while (_inbox.TryDequeue(out var message))
                OnHook(message, now);

            if (_tick % 3 == 0) Scan(now);
            if (_tick % 5 == 0) _thermal.Sample();
            _tick++;

            ReadBattery();
            _activity.Tick(now, PressKeys);
            _tracker.Evaluate(now, Settings, _hookKinds, _activity.UserInputUtc);
            Evaluate(now);
            UpdateSlash(now);
        }
        catch (Exception ex)
        {
            Log.Error("Error en el ciclo principal", ex);
        }
        Updated?.Invoke();
    }

    /// <summary>Runs on a pool thread. Records the gap first, so a tick that already sees the new beat sees it too.</summary>
    private void Heartbeat()
    {
        long now = DateTime.UtcNow.Ticks;
        long last = Interlocked.Read(ref _heartbeatTicks);
        if (last != 0 && now - last > TickGap.Ticks) Interlocked.Exchange(ref _heartbeatResumedTicks, now);
        Interlocked.Exchange(ref _heartbeatTicks, now);
    }

    /// <summary>
    /// The whole app stood still since <paramref name="lastTick"/> (suspended), not just the UI thread: the
    /// heartbeat hasn't come back yet either, or it already came back from a gap of its own.
    /// </summary>
    private bool FrozenSince(DateTime lastTick, DateTime now) =>
        now.Ticks - Interlocked.Read(ref _heartbeatTicks) > TickGap.Ticks
        || Interlocked.Read(ref _heartbeatResumedTicks) > lastTick.Ticks;

    private void OnHook(HookMessage m, DateTime now)
    {
        _tracker.OnHook(m, now);
        if (m.RemoteHost is not null) return; // says nothing about this laptop's hooks
        bool codex = m.Agent == "codex";
        if (codex && !Settings.CodexHooksVerified)
        {
            Settings.CodexHooksVerified = true;
            SettingsStore.Save(Settings);
        }
        else if (!codex && !Settings.ClaudeHooksVerified)
        {
            Settings.ClaudeHooksVerified = true;
            SettingsStore.Save(Settings);
        }
    }

    private void Scan(DateTime now)
    {
        var others = Settings.WatchOthers ? AgentRules.ParseOthers(Settings.OtherAgents) : Array.Empty<string>();
        RefreshHookKinds();
        var samples = _scanner.Scan(others, _tracker.Pinned, out var aliases);
        if (samples is null) return; // process snapshot failed: keep the last known agents
        _tracker.ApplyScan(samples, aliases, Settings.CpuActiveThresholdPercent, now);
    }

    private void RefreshHookKinds()
    {
        Check(AgentKind.Claude, ClaudeHooks, Settings.ClaudeHooksVerified);
        Check(AgentKind.Codex, CodexHooks, Settings.CodexHooksVerified);

        void Check(AgentKind kind, HookIntegration hooks, bool verified)
        {
            var status = hooks.GetCachedStatus();
            if (status == IntegrationStatus.Error) return; // unreadable right now (being rewritten?): keep what we knew
            if (verified && status == IntegrationStatus.Installed) _hookKinds.Add(kind);
            else _hookKinds.Remove(kind);
        }
    }

    private void UpdateSlash(DateTime now) => _slash.Show(SlashTarget(now), SlashDarkWhenIdle);

    /// <summary>The Slash is a status light: off while the laptop is in use (unless the user prefers their animation).</summary>
    private bool SlashDarkWhenIdle => Settings.Enabled && Settings.SlashWhileLidClosed && Settings.SlashOffWhileOpen;

    /// <summary>The Slash only lights up while we keep the laptop working with the lid closed (or for the preview).</summary>
    private SlashPattern SlashTarget(DateTime now)
    {
        if (_slashPreviewStartUtc is { } start)
        {
            var preview = SlashLight.PreviewPattern(now - start);
            if (preview != SlashPattern.Off) return preview;
            _slashPreviewStartUtc = null;
        }
        if (!Settings.SlashWhileLidClosed || !LidClosed || !_power.LidOverrideActive || HoldReason is not null) return SlashPattern.Off;
        var busy = _tracker.Processes.Where(a => a.Busy && IsWatched(a.Kind)).ToList();
        if (busy.Any(a => a.Working)) return SlashPattern.Working;
        return busy.Any(a => a.Waiting) ? SlashPattern.Waiting : SlashPattern.Resting; // finished (grace) or Mantener activa
    }

    /// <summary>
    /// F15 while Mantener activa keeps the laptop awake, the lid closed too (Teams stays available): never on hold
    /// or once a sleep has started, so it can't wake the screen back up.
    /// </summary>
    private bool PressKeys => KeepActive && _power.KeepingAwake && HoldReason is null;

    private void ReadBattery()
    {
        var status = WinForms.SystemInformation.PowerStatus;
        OnBattery = status.PowerLineStatus == WinForms.PowerLineStatus.Offline;
        bool hasBattery = !status.BatteryChargeStatus.HasFlag(WinForms.BatteryChargeStatus.NoSystemBattery)
                          && status.BatteryLifePercent <= 1.0f;
        BatteryPercent = hasBattery ? (int)Math.Round(status.BatteryLifePercent * 100) : null;
    }

    /// <param name="powerPlans">
    /// False skips the slow step (writing the power plans) so the caller can paint first; the next pass, a moment
    /// later or on the next tick, applies it. Every step here is idempotent, so running twice is harmless.
    /// </param>
    private void Evaluate(DateTime now, bool powerPlans = true)
    {
        // 0) Someone is in front of the screen again (lid open, display on for 10 s, awake for 10 s, a while after
        //    our action). After a sleep VibeDoze didn't ask for, a screen on with the lid closed counts too: an
        //    external monitor.
        if (HoldReason is not null && Interactive && (!LidClosed || _heldByWindowsSleep)
            && _displayOnSinceUtc is { } displayOn && now - displayOn > TimeSpan.FromSeconds(10)
            && now - _wokeUtc > TimeSpan.FromSeconds(10)
            && now - _actionExecutedUtc > TimeSpan.FromSeconds(10))
        {
            Log.Info("El usuario volvió (pantalla encendida): se reanuda VibeDoze");
            HoldReason = null;
            _sleepAttemptUtc = null;
            if (PowerController.IdleSleepArmed) PowerController.RestoreIdleFromState();
        }

        // 1) A countdown that reached zero.
        if (Pending is { } pending && now >= pending.ExecuteAtUtc)
        {
            Execute(pending);
            UpdateHeadline(Array.Empty<AgentProcess>());
            return;
        }

        var busy = _tracker.Processes.Where(a => a.Busy && IsWatched(a.Kind)).ToList();
        UpdateKeepActive(now, busy);
        bool sessionEnding = now < _sessionEndingUntilUtc;
        bool want = Settings.Enabled && HoldReason is null && !sessionEnding && (KeepActive || busy.Count > 0);

        // 2) Battery guard.
        if (_batteryHold && (!OnBattery || BatteryPercent is null || BatteryPercent > Settings.BatteryMinPercent + 5))
            _batteryHold = false;
        if (want && !_batteryHold && Settings.BatteryGuard && OnBattery && BatteryPercent is { } pct && pct <= Settings.BatteryMinPercent)
        {
            _batteryHold = true;
            Log.Warn($"Batería al {pct}%: se deja de mantener despierta");
            RaiseNotify("Batería baja", $"Batería al {pct}%. Dejo de mantener la laptop despierta.");
        }
        if (_batteryHold) want = false;

        // 3) Keep awake ("sleep after: never" too, it replaces an armed sleep) + lid override.
        if (want)
        {
            _sleepAttemptUtc = null; // agents are busy again: never escalate an old sleep request
            _awakeForKeepActive = busy.Count == 0;
        }
        // Mantener activa keeps on any screen someone may be looking at: an external monitor with the lid closed too.
        bool display = want && (KeepActive ? !Bagged : Settings.KeepDisplayOn && !LidClosed);
        _power.SetKeepAwake(want, display, PowerReason(busy));
        if (powerPlans)
        {
            SyncIdleSleep(want);
            bool wantOverride = want && Settings.KeepRunningWithLidClosed && PowerController.Capabilities.LidPresent;
            if (wantOverride && !_power.LidOverrideActive)
            {
                _power.ApplyLidOverride();
            }
            else if (!wantOverride && _power.LidOverrideActive)
            {
                _power.RestoreLid();
                // The lid is closed and nobody is using an external screen: do what closing the lid would have done.
                if (Bagged && Pending is null && HoldReason is null && !sessionEnding)
                {
                    string why = _batteryHold ? "Batería baja con la tapa cerrada"
                        : _awakeForKeepActive ? "Terminó Mantener activa con la tapa cerrada"
                        : "Los agentes terminaron con la tapa cerrada";
                    StartAction(Settings.LidClosedDoneAction, _batteryHold ? ActionSource.Battery : ActionSource.Finished, why, now);
                    UpdateHeadline(busy);
                    return;
                }
            }
        }

        // 4) Thermal guard: while we keep it awake, or whenever the lid is closed (bag!). It acts on the first
        //    reading at or over the limit, no waiting. By default only on battery: plugged in, high temperatures
        //    are normal (charging, gaming).
        bool watched = want || LidClosed;
        string? guardOff = ThermalGuardOff(now);
        if (watched) LogTemperature(now, guardOff);
        else _thermalLogPeakC = null;
        if (watched && guardOff is null)
        {
            double temp = _thermal.TemperatureC ?? 0;
            bool hot = temp >= Settings.ThermalThresholdC;
            bool throttled = LidClosed && _thermal.PassiveLimitPercent is < 50;
            if ((hot || throttled) && Pending?.Source != ActionSource.Thermal && now - _thermalActedUtc >= ThermalRetry)
            {
                string reason = hot
                    ? $"Temperatura alta: {ThermalMonitor.Format(temp)}"
                    : $"Windows limita el procesador al {_thermal.PassiveLimitPercent:0} % por calor";
                Log.Warn($"{reason} ({temp:0.0} °C, límite {Settings.ThermalThresholdC} °C)");
                StartAction(Settings.ThermalAction, ActionSource.Thermal, reason, Interactive ? now + ThermalCountdown : now);
            }
        }
        else if (Pending?.Source == ActionSource.Thermal)
        {
            Log.Info($"La protección térmica ya no aplica ({guardOff ?? "tapa abierta y sin agentes"}): se cancela la acción");
            Pending = null;
        }

        // 5) A pending action the user hasn't seen (scheduled while nobody was looking): show it now,
        //    with at least 30 s left to react.
        if (Pending is { } unseen && Interactive && !ReferenceEquals(unseen, _shownPending) && ActionPending is not null)
        {
            if (unseen.ExecuteAtUtc - now < ThermalCountdown)
            {
                Pending = unseen = new PendingAction
                {
                    Action = unseen.Action,
                    Source = unseen.Source,
                    Reason = unseen.Reason,
                    ExecuteAtUtc = now + ThermalCountdown,
                    CreatedUtc = now,
                };
            }
            _shownPending = unseen;
            ActionPending.Invoke(unseen);
        }

        // 6) Sleep request that didn't take (lid closed, still running) → hibernate.
        if (_sleepAttemptUtc is { } attempt && !_suspendObserved)
        {
            var since = now - attempt;
            if (since > _sleepEscalation + TimeSpan.FromMinutes(2)) _sleepAttemptUtc = null;
            else if (since >= _sleepEscalation && Bagged)
            {
                _sleepAttemptUtc = null;
                if (PowerController.Capabilities.CanHibernate)
                {
                    Log.Warn("La laptop no entró en suspensión con la tapa cerrada; hibernando");
                    _power.SetKeepAwake(false, false, string.Empty);
                    if (_power.LidOverrideActive) _power.RestoreLid();
                    RunPowerAction(SystemAction.Hibernate);
                }
            }
        }

        UpdateHeadline(busy);
    }

    /// <summary>
    /// Mantener activa with a time limit lasts exactly that. Without one it ends when the agents that worked
    /// meanwhile are done (the grace included), so the laptop sleeps as it would without it.
    /// </summary>
    private void UpdateKeepActive(DateTime now, IReadOnlyList<AgentProcess> busy)
    {
        if (!KeepActive) return;
        if (KeepActiveUntilUtc is { } until)
        {
            if (now >= until) StopKeepActiveCore("se acabó el tiempo");
        }
        else if (busy.Any(a => a.Working))
        {
            _keepActiveSawWork = true;
        }
        else if (_keepActiveSawWork && busy.Count == 0)
        {
            StopKeepActiveCore("los agentes terminaron");
        }
    }

    /// <summary>Why the thermal guard can't act right now (null: it can).</summary>
    private string? ThermalGuardOff(DateTime now) =>
        !Settings.Enabled ? "VibeDoze en pausa"
        : !Settings.ThermalGuard ? "protección desactivada"
        : !_thermal.Available ? "sin sensor"
        : now < _thermalSnoozeUntilUtc ? $"pospuesta hasta las {_thermalSnoozeUntilUtc.ToLocalTime():HH:mm}"
        : !OnBattery && Settings.ThermalOnlyOnBattery ? "enchufada"
        : now - _wokeUtc < ResumeSettle ? "acaba de despertar"
        : null;

    /// <summary>
    /// One line a minute while agents work or the lid is closed, with the hottest sample since the previous line:
    /// enough to tell afterwards why the thermal guard acted or not.
    /// </summary>
    private void LogTemperature(DateTime now, string? guardOff)
    {
        if (_thermal.TemperatureC is not { } temp) return;
        _thermalLogPeakC = Math.Max(_thermalLogPeakC ?? temp, temp);
        if (now - _thermalLoggedUtc < ThermalLogInterval) return;
        _thermalLoggedUtc = now;
        string guard = guardOff is null ? $"protección activa (límite {Settings.ThermalThresholdC} °C)" : $"protección en pausa: {guardOff}";
        string throttle = _thermal.PassiveLimitPercent is < 100 ? $" · Windows limita al {_thermal.PassiveLimitPercent:0} %" : string.Empty;
        Log.Info($"Temperatura {temp:0.0} °C (pico {_thermalLogPeakC:0.0} °C) · {(OnBattery ? "batería" : "enchufada")} · " +
                 $"tapa {(LidClosed ? "cerrada" : "abierta")} · {guard}{throttle}");
        _thermalLogPeakC = null;
    }

    /// <summary>Logs the thermal guard settings when they change, so the log tells which limit applied at any time.</summary>
    private void LogThermalConfig()
    {
        var s = Settings;
        string config = !s.ThermalGuard
            ? "desactivada"
            : $"{ActionName(s.ThermalAction).ToLowerInvariant()} al llegar a {s.ThermalThresholdC} °C, " +
              (s.ThermalOnlyOnBattery ? "solo con batería" : "también enchufada");
        if (config == _thermalConfigLogged) return;
        _thermalConfigLogged = config;
        Log.Info($"Protección térmica: {config}");
    }

    /// <summary>
    /// On Modern Standby the power request alone doesn't stop standby once "sleep after" expires, so it is set to
    /// never while we keep the laptop awake, and restored as soon as nothing needs it.
    /// </summary>
    private static void SyncIdleSleep(bool want)
    {
        if (want && PowerController.Capabilities.ModernStandby)
        {
            if (!PowerController.IdleSleepBlocked && !PowerController.BlockIdleSleep() && PowerController.IdleSleepArmed)
                PowerController.RestoreIdleFromState(); // couldn't block: at least don't leave the 1-min timeout
        }
        else if (PowerController.IdleSleepBlocked)
        {
            PowerController.RestoreIdleFromState();
        }
    }

    /// <summary>Someone may be looking: a screen is on (laptop panel, or an external monitor with the lid closed).</summary>
    private bool Interactive => DisplayOn && !(_displayWokenByKeyUtc is { } woke && _activity.UserInputUtc < woke);

    /// <summary>Lid closed and no screen on: the laptop is probably in a bag.</summary>
    private bool Bagged => LidClosed && !Interactive;

    private void StartAction(SystemAction action, ActionSource source, string reason, DateTime executeAtUtc)
    {
        if (Pending is not null && Pending.Source == source) return;
        var now = DateTime.UtcNow;
        Pending = new PendingAction
        {
            Action = action,
            Source = source,
            Reason = reason,
            ExecuteAtUtc = executeAtUtc,
            CreatedUtc = now,
        };
        Log.Info($"Acción programada: {ActionName(action)} ({reason}) para {executeAtUtc.ToLocalTime():HH:mm:ss}");
        if (executeAtUtc <= now)
        {
            Execute(Pending);
        }
        else if (Interactive && ActionPending is not null)
        {
            _shownPending = Pending;
            ActionPending.Invoke(Pending);
        }
    }

    private void Execute(PendingAction p)
    {
        Pending = null;
        _actionExecutedUtc = DateTime.UtcNow;
        if (p.Source == ActionSource.Thermal) _thermalActedUtc = _actionExecutedUtc;

        // Stay out of the way until the user is back (lid opened / screen on / resumed by the user).
        HoldReason = p.Source switch
        {
            ActionSource.Thermal => $"{ActionName(p.Action)} por temperatura alta",
            ActionSource.Battery => $"{ActionName(p.Action)} por batería baja",
            ActionSource.Finished => $"{ActionName(p.Action)} al terminar (tapa cerrada)",
            _ => $"{ActionName(p.Action)} manual",
        };
        _heldByWindowsSleep = false;
        StopKeepActiveCore(HoldReason.ToLowerInvariant()); // the laptop goes to sleep: no F15 waking it up again

        _power.SetKeepAwake(false, false, string.Empty);
        if (_power.LidOverrideActive) _power.RestoreLid();
        if (PowerController.IdleSleepBlocked) PowerController.RestoreIdleFromState();

        if (p.Source is ActionSource.Thermal or ActionSource.Battery)
            RaiseNotify("VibeDoze", $"{p.Reason}. {ActionName(p.Action)} para proteger la laptop.");
        Log.Info($"Ejecutando: {ActionName(p.Action)} — {p.Reason}");
        _slash.Release(TimeSpan.FromSeconds(1), keepDark: true); // the animation ends before suspending
        _sleepEscalation = p.Source == ActionSource.Thermal ? ThermalSleepEscalation : SleepEscalation;
        RunPowerAction(p.Action);
    }

    private void RunPowerAction(SystemAction action)
    {
        _suspendObserved = false;
        _sleepAttemptUtc = action == SystemAction.Sleep ? DateTime.UtcNow : null;
        IntPtr hwnd = _window.Handle;
        bool displayOn = DisplayOn;
        // Shared power state is only touched here, on the UI thread; the worker just triggers the transition.
        bool idleArmed = action == SystemAction.Sleep && PowerController.Capabilities.ModernStandby
                         && PowerController.ArmIdleSleep();
        Task.Run(() =>
        {
            try
            {
                string how = PowerController.Perform(action, hwnd, displayOn, idleArmed);
                LastAction = $"{DateTime.Now:HH:mm} · {how}";
                Log.Info($"Acción de energía: {how}");
            }
            catch (Exception ex)
            {
                Log.Error("Falló la acción de energía", ex);
            }
        });
    }

    private void UpdateHeadline(IReadOnlyList<AgentProcess> busy)
    {
        if (!Settings.Enabled)
        {
            Mode = EngineMode.Paused;
            Headline = "En pausa";
            Detail = "VibeDoze no mantiene la laptop despierta ni cambia la tapa.";
        }
        else if (Pending is { } p)
        {
            Mode = EngineMode.Countdown;
            int seconds = Math.Max(0, (int)Math.Ceiling((p.ExecuteAtUtc - DateTime.UtcNow).TotalSeconds));
            Headline = $"{ActionName(p.Action)} en {seconds} s";
            Detail = p.Reason;
        }
        else if (HoldReason is not null)
        {
            Mode = EngineMode.Held;
            Headline = "En espera";
            Detail = _heldByWindowsSleep
                ? $"{HoldReason}. Vuelvo a activarme cuando vuelvas a usar la laptop."
                : $"{HoldReason}. Vuelvo a activarme cuando abras la tapa.";
        }
        else if (_batteryHold)
        {
            Mode = EngineMode.Held;
            Headline = "Batería baja";
            Detail = $"Con {BatteryPercent}% no mantengo la laptop despierta. Conecta el cargador.";
        }
        else if (_power.KeepingAwake)
        {
            Mode = EngineMode.Awake;
            Headline = LidClosed ? "Trabajando con la tapa cerrada" : busy.Count == 0 ? "Manteniendo activa" : "Manteniendo despierta";
            Detail = busy.Count > 0 ? DescribeBusy(busy)
                : KeepActiveUntilUtc is { } until ? $"Pantalla y Teams activos hasta las {until.ToLocalTime():HH:mm}"
                : "Pantalla y Teams activos, sin límite de tiempo";
        }
        else
        {
            Mode = EngineMode.Idle;
            Headline = "En reposo";
            int watched = _tracker.Processes.Count(a => IsWatched(a.Kind));
            Detail = watched == 0
                ? "Ningún agente abierto. Me activo cuando Claude Code o Codex trabajen."
                : "Ningún agente está trabajando. Windows puede suspender normalmente.";
        }
    }

    private static string DescribeBusy(IReadOnlyList<AgentProcess> busy)
    {
        var parts = busy
            .Select(a => a.Project is { } project
                ? $"{AgentRules.DisplayName(a.Kind)} en {project} ({a.Host})"
                : $"{AgentRules.DisplayName(a.Kind)} ({a.Host})")
            .Distinct()
            .ToList();
        return parts.Count <= 2 ? string.Join(" · ", parts) : $"{parts[0]} y {parts.Count - 1} más";
    }

    private string PowerReason(IReadOnlyList<AgentProcess> busy)
    {
        if (busy.Count == 0) return "VibeDoze: mantener activa";
        var kinds = busy.Select(a => AgentRules.DisplayName(a.Kind)).Distinct();
        return $"VibeDoze: {string.Join(" y ", kinds)} trabajando";
    }

    public static string ActionName(SystemAction action) => action switch
    {
        SystemAction.Hibernate => "Hibernar",
        SystemAction.Shutdown => "Apagar",
        _ => "Suspender",
    };

    private void RaiseNotify(string title, string message)
    {
        if (Settings.ShowNotifications) Notify?.Invoke(title, message);
    }

    private void RepairIntegrations()
    {
        // If the exe moved (reinstall / update), point existing hooks to the new location.
        foreach (HookIntegration integration in new HookIntegration[] { ClaudeHooks, CodexHooks })
        {
            try
            {
                if (integration.GetStatus() != IntegrationStatus.Outdated) continue;
                integration.Install();
                if (integration is CodexIntegration)
                {
                    Settings.CodexHooksVerified = false;
                    SettingsStore.Save(Settings);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"No se pudieron actualizar los hooks de {integration.Name}", ex);
            }
        }
    }

    // ───────────── Power / session notifications ─────────────

    /// <summary>
    /// Windows is about to shut down or log off: put the power settings back now, and don't re-apply them
    /// for a while. If the shutdown gets cancelled, WM_ENDSESSION(FALSE) lifts the block right away.
    /// </summary>
    public void PrepareForSessionEnd()
    {
        if (_disposed) return;
        _sessionEndingUntilUtc = DateTime.UtcNow.AddMinutes(2);
        if (_power.LidOverrideActive) _power.RestoreLid();
        if (PowerController.IdleOverrideActive) PowerController.RestoreIdleFromState();
        _slash.Release(TimeSpan.FromSeconds(1), keepDark: true); // off until VibeDoze starts again
        Log.Info("Windows cierra la sesión: ajustes de energía restaurados");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_QUERYENDSESSION:
                PrepareForSessionEnd();
                handled = true;
                return new IntPtr(1); // never block shutdown
            case Native.WM_ENDSESSION:
                if (wParam == IntPtr.Zero)
                {
                    Log.Info("Se canceló el cierre de sesión: VibeDoze sigue activo");
                    _sessionEndingUntilUtc = DateTime.MinValue;
                }
                return IntPtr.Zero;
            case Native.WM_POWERBROADCAST:
                break;
            default:
                return IntPtr.Zero;
        }

        try
        {
            switch (wParam.ToInt32())
            {
                case Native.PBT_POWERSETTINGCHANGE:
                    var setting = Marshal.PtrToStructure<Native.POWERBROADCAST_SETTING>(lParam);
                    if (setting.PowerSetting == Native.GUID_LIDSWITCH_STATE_CHANGE) OnLid(setting.Data == 0);
                    else if (setting.PowerSetting == Native.GUID_CONSOLE_DISPLAY_STATE) OnDisplay(setting.Data != 0);
                    else if (setting.PowerSetting == Native.GUID_ACTIVE_POWERSCHEME)
                    {
                        _power.EnsureOverrideOnActiveScheme();
                        if (PowerController.IdleSleepBlocked) PowerController.BlockIdleSleep(); // the new plan must not sleep either
                    }
                    break;
                case Native.PBT_APMSUSPEND:
                    _suspendObserved = true;
                    _sleepAttemptUtc = null;
                    Log.Info("Sistema entrando en suspensión");
                    _slash.Release(TimeSpan.FromMilliseconds(500), keepDark: true);
                    OnSuspend();
                    break;
                case Native.PBT_APMRESUMEAUTOMATIC:
                    OnResume(byUser: false);
                    break;
                case Native.PBT_APMRESUMESUSPEND:
                    OnResume(byUser: true);
                    break;
                case Native.PBT_APMPOWERSTATUSCHANGE:
                    ReadBattery();
                    _slash.Reassert();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("WM_POWERBROADCAST", ex);
        }
        handled = true;
        return new IntPtr(1);
    }

    private void OnLid(bool closed)
    {
        if (LidClosed == closed && _tick > 0) return;
        LidClosed = closed;
        Log.Info(closed ? "Tapa cerrada" : "Tapa abierta");
        if (closed) EndThermalSnooze("tapa cerrada");
        if (!closed)
        {
            // The user is back: no pending escalation, no shortened idle timeout, no hold.
            _sleepAttemptUtc = null;
            _displayWokenByKeyUtc = null;
            _slash.Reassert();
            if (PowerController.IdleSleepArmed) PowerController.RestoreIdleFromState();
            if (HoldReason is not null)
            {
                Log.Info("El usuario volvió (tapa abierta): se reanuda VibeDoze");
                HoldReason = null;
            }
        }
        Refresh();
    }

    private void OnDisplay(bool on)
    {
        if (DisplayOn == on) return;
        DisplayOn = on;
        // Our F15 may light a screen with the lid closed (in a bag, a monitor that was off): nobody is looking until
        // they type or move the mouse, so the bag logic still holds.
        _displayWokenByKeyUtc = on && LidClosed && DateTime.UtcNow - _activity.PressedUtc < TimeSpan.FromSeconds(5) ? DateTime.UtcNow : null;
        if (_displayWokenByKeyUtc is not null) Log.Info("Pantalla encendida por F15 con la tapa cerrada: no cuenta como alguien mirando");
        _displayOnSinceUtc = on ? DateTime.UtcNow : null;
        if (!on && LidClosed) EndThermalSnooze("tapa cerrada y pantalla apagada");
        if (on && !LidClosed)
        {
            // Someone woke the screen: drop the 60 s idle timeout right away. Leaving the "hold" state waits
            // until the screen has stayed on for a few seconds (see Evaluate), so a blip doesn't count.
            _sleepAttemptUtc = null;
            if (PowerController.IdleSleepArmed) PowerController.RestoreIdleFromState();
        }
        Refresh();
    }

    /// <summary>
    /// "Ignore 15 min" only holds while someone is at the laptop: once nobody can see it (lid closed, or the
    /// screen off with the lid closed: it may be going into a bag) the thermal guard is back at once.
    /// </summary>
    private void EndThermalSnooze(string why)
    {
        if (DateTime.UtcNow >= _thermalSnoozeUntilUtc) return;
        _thermalSnoozeUntilUtc = DateTime.MinValue;
        Log.Info($"Protección térmica reactivada: {why}");
    }

    /// <summary>
    /// Windows goes to sleep without VibeDoze asking (power button, Start › Sleep, the lid): that wins. Until the
    /// user is back nothing keeps the laptop awake, not even an agent that runs for a moment while it is suspending
    /// or on a wake for maintenance, and the Slash stays dark. Otherwise the very next tick lit the bar again and it
    /// stayed on, frozen, the whole sleep.
    /// </summary>
    private void OnSuspend()
    {
        _slashPreviewStartUtc = null;
        StopKeepActiveCore("Windows se suspende");
        if (!Settings.Enabled || HoldReason is not null) return; // paused, or a sleep of ours
        var now = DateTime.UtcNow;
        HoldReason = "Suspendida desde Windows";
        _heldByWindowsSleep = true;
        _actionExecutedUtc = now; // only a screen turned on after this means the user is back
        Log.Info("Suspensión fuera de VibeDoze: no la mantengo despierta hasta que vuelvas");
        Evaluate(now); // the power request, the lid and "sleep after" back to normal before it sleeps
        UpdateSlash(now);
        Updated?.Invoke();
    }

    private void OnResume(bool byUser)
    {
        Log.Info(byUser ? "Reanudado por el usuario" : "Reanudado");
        _slash.Reassert();
        if (PowerController.IdleSleepArmed) PowerController.RestoreIdleFromState();
        _suspendObserved = true;
        _sleepAttemptUtc = null;
        _wokeUtc = DateTime.UtcNow;
        Pending = null;
        StopKeepActiveCore("la laptop durmió"); // in case the suspend itself went unnoticed
        // Modern Standby says "by the user" on wakes nobody asked for too: after a sleep of Windows', wait for the screen.
        if (byUser && !LidClosed && !_heldByWindowsSleep) HoldReason = null;
        Refresh();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _settle.Stop();
        _heartbeat.Dispose();
        _hookServer.Dispose();
        _remote.Dispose();
        foreach (var h in _powerNotifications) Native.UnregisterPowerSettingNotification(h);
        if (_suspendNotification != IntPtr.Zero) Native.UnregisterSuspendResumeNotification(_suspendNotification);
        _slash.Dispose(keepDark: DateTime.UtcNow < _sessionEndingUntilUtc);
        _power.Dispose();
        _thermal.Dispose();
        _window.Dispose();
        Log.Info("VibeDoze detenido");
    }
}
