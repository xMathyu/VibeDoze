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
    Timer,
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
/// the temperature, and then decides: keep the laptop awake? override the lid? run the timer / thermal /
/// battery / "finished with the lid closed" action?
/// </summary>
internal sealed class Engine : IDisposable
{
    private static readonly TimeSpan ThermalCountdown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TimerCountdown = TimeSpan.FromSeconds(60);
    /// <summary>Lid closed, sleep requested, still running after this long → hibernate (idle standby takes ~1 min).</summary>
    private static readonly TimeSpan SleepEscalation = TimeSpan.FromSeconds(150);

    private readonly PowerController _power = new();
    private readonly AgentScanner _scanner = new();
    private readonly AgentTracker _tracker = new();
    private readonly ThermalMonitor _thermal = new();
    private readonly HookServer _hookServer;
    private readonly ConcurrentQueue<HookMessage> _inbox = new();
    private readonly DispatcherTimer _timer;
    private readonly HwndSource _window;
    private readonly List<IntPtr> _powerNotifications = new();
    private readonly IntPtr _suspendNotification;

    private long _tick;
    private DateTime? _hotSinceUtc;
    private DateTime _thermalSnoozeUntilUtc = DateTime.MinValue;
    private bool _timerWarned;
    private bool _batteryHold;
    private DateTime? _sleepAttemptUtc;
    private bool _suspendObserved;
    private bool _disposed;
    private DateTime _actionExecutedUtc = DateTime.MinValue;
    private DateTime? _displayOnSinceUtc = DateTime.UtcNow;
    private PendingAction? _shownPending;
    /// <summary>Windows is shutting down / logging off: settings are restored and must not be re-applied.</summary>
    private DateTime _sessionEndingUntilUtc = DateTime.MinValue;

    public Engine()
    {
        Settings = SettingsStore.Load();

        // Crash recovery: a previous run may have left the lid override applied.
        var state = RuntimeStateStore.Load();
        if (state.LidOverride is not null || state.IdleOverride is not null)
        {
            Log.Warn("Se encontró un ajuste de energía sin restaurar (cierre inesperado); restaurando");
            PowerController.RestoreAllFromState();
        }
        if (state.TimerDeadlineUtc is { } deadline && deadline > DateTime.UtcNow.AddSeconds(30))
            TimerDeadlineUtc = deadline;
        else if (state.TimerDeadlineUtc is not null)
            RuntimeStateStore.Update(s => s.TimerDeadlineUtc = null);

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

        RepairIntegrations();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        Log.Info($"VibeDoze iniciado (Modern Standby: {PowerController.Capabilities.ModernStandby}, " +
                 $"hibernación: {PowerController.Capabilities.CanHibernate}, sensor: {_thermal.SensorName ?? "ninguno"})");
    }

    /// <summary>Starts the loop. Called after the UI subscribed to the events, so nothing raised early is lost.</summary>
    public void Start()
    {
        _timer.Start();
        Tick();
    }

    // ───────────── Public state (read by the UI) ─────────────

    public AppSettings Settings { get; }
    public ClaudeIntegration ClaudeHooks { get; } = new();
    public CodexIntegration CodexHooks { get; } = new();
    public ThermalMonitor Thermal => _thermal;

    public bool LidClosed { get; private set; }
    public bool DisplayOn { get; private set; } = true;
    public bool OnBattery { get; private set; }
    public int? BatteryPercent { get; private set; }
    public bool ManualAwake { get; private set; }
    public DateTime? TimerDeadlineUtc { get; private set; }
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
        SettingsStore.Save(Settings);
        Refresh();
    }

    public void SetManualAwake(bool on)
    {
        ManualAwake = on;
        Log.Info($"Modo manual: {(on ? "activado" : "desactivado")}");
        Refresh();
    }

    public void SetTimer(int minutes)
    {
        TimerDeadlineUtc = DateTime.UtcNow.AddMinutes(minutes);
        _timerWarned = minutes <= 5;
        if (Pending?.Source == ActionSource.Timer) Pending = null;
        RuntimeStateStore.Update(s => s.TimerDeadlineUtc = TimerDeadlineUtc);
        Log.Info($"Límite de tiempo: {minutes} min ({ActionName(Settings.TimerAction)} a las {TimerDeadlineUtc.Value.ToLocalTime():HH:mm})");
        Refresh();
    }

    public void ClearTimer()
    {
        ClearTimerCore();
        Refresh();
    }

    private void ClearTimerCore()
    {
        if (TimerDeadlineUtc is null) return;
        TimerDeadlineUtc = null;
        if (Pending?.Source == ActionSource.Timer) Pending = null;
        RuntimeStateStore.Update(s => s.TimerDeadlineUtc = null);
        Log.Info("Límite de tiempo cancelado");
    }

    /// <summary>User pressed "Cancel" on the countdown.</summary>
    public void CancelPending()
    {
        if (Pending is not { } p) return;
        Pending = null;
        switch (p.Source)
        {
            case ActionSource.Timer:
                ClearTimerCore();
                break;
            case ActionSource.Thermal:
                _thermalSnoozeUntilUtc = DateTime.UtcNow.AddMinutes(15);
                _hotSinceUtc = null;
                Log.Warn("Protección térmica pospuesta 15 min por el usuario");
                break;
        }
        Refresh();
    }

    public void PostponeTimer(int minutes) => SetTimer(minutes);

    public void ExecutePendingNow()
    {
        if (Pending is not { } p) return;
        Execute(p);
        Refresh();
    }

    public void SleepNow() =>
        StartAction(SystemAction.Sleep, ActionSource.Manual, "Suspendida desde VibeDoze", DateTime.UtcNow);

    /// <summary>Leave the "on hold" state (after an automatic sleep) without waiting for the lid to open.</summary>
    public void ResumeFromHold()
    {
        HoldReason = null;
        _batteryHold = false;
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed) return;
        Evaluate(DateTime.UtcNow);
        Updated?.Invoke();
    }

    // ───────────── Loop ─────────────

    private void Tick()
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;
        try
        {
            while (_inbox.TryDequeue(out var message))
                OnHook(message, now);

            if (_tick % 3 == 0) Scan(now);
            if (_tick % 5 == 0) _thermal.Sample();
            _tick++;

            ReadBattery();
            _tracker.Evaluate(now, Settings);
            Evaluate(now);
        }
        catch (Exception ex)
        {
            Log.Error("Error en el ciclo principal", ex);
        }
        Updated?.Invoke();
    }

    private void OnHook(HookMessage m, DateTime now)
    {
        _tracker.OnHook(m, now);
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
        var samples = _scanner.Scan(others, _tracker.Pinned, out var aliases);
        if (samples is null) return; // process snapshot failed: keep the last known agents
        _tracker.ApplyScan(samples, aliases, Settings.CpuActiveThresholdPercent, now);
    }

    private void ReadBattery()
    {
        var status = WinForms.SystemInformation.PowerStatus;
        OnBattery = status.PowerLineStatus == WinForms.PowerLineStatus.Offline;
        bool hasBattery = !status.BatteryChargeStatus.HasFlag(WinForms.BatteryChargeStatus.NoSystemBattery)
                          && status.BatteryLifePercent <= 1.0f;
        BatteryPercent = hasBattery ? (int)Math.Round(status.BatteryLifePercent * 100) : null;
    }

    private void Evaluate(DateTime now)
    {
        // 0) Someone is in front of the screen again (lid open, display on for 10 s, a while after our action).
        if (HoldReason is not null && Interactive && !LidClosed
            && _displayOnSinceUtc is { } displayOn && now - displayOn > TimeSpan.FromSeconds(10)
            && now - _actionExecutedUtc > TimeSpan.FromSeconds(10))
        {
            Log.Info("El usuario volvió (pantalla encendida): se reanuda VibeDoze");
            HoldReason = null;
            _sleepAttemptUtc = null;
            if (PowerController.IdleOverrideActive) PowerController.RestoreIdleFromState();
        }

        // 1) A countdown that reached zero.
        if (Pending is { } pending && now >= pending.ExecuteAtUtc)
        {
            Execute(pending);
            UpdateHeadline(Array.Empty<AgentProcess>());
            return;
        }

        var busy = _tracker.Processes.Where(a => a.Busy && IsWatched(a.Kind)).ToList();
        bool sessionEnding = now < _sessionEndingUntilUtc;
        bool want = Settings.Enabled && HoldReason is null && !sessionEnding && (ManualAwake || busy.Count > 0);

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

        // 3) Keep awake + lid override.
        if (want)
        {
            _sleepAttemptUtc = null; // agents are busy again: never escalate an old sleep request
            if (PowerController.IdleOverrideActive) PowerController.RestoreIdleFromState();
        }
        _power.SetKeepAwake(want, want && Settings.KeepDisplayOn && !LidClosed, PowerReason(busy));
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
                string why = _batteryHold ? "Batería baja con la tapa cerrada" : "Los agentes terminaron con la tapa cerrada";
                StartAction(Settings.LidClosedDoneAction, _batteryHold ? ActionSource.Battery : ActionSource.Finished, why, now);
                UpdateHeadline(busy);
                return;
            }
        }

        // 4) Shutdown timer.
        if (TimerDeadlineUtc is { } deadline)
        {
            var left = deadline - now;
            if (!_timerWarned && left <= TimeSpan.FromMinutes(5) && left > TimerCountdown)
            {
                _timerWarned = true;
                RaiseNotify("Límite de tiempo", $"En 5 minutos: {ActionName(Settings.TimerAction).ToLowerInvariant()}.");
            }
            if (left <= TimerCountdown && Pending is null)
                StartAction(Settings.TimerAction, ActionSource.Timer, "Se alcanzó el límite de tiempo", deadline);
        }

        // 5) Thermal guard: while we keep it awake, or whenever the lid is closed (bag!).
        //    By default only on battery: plugged in, high temperatures are normal (charging, gaming).
        bool guard = Settings.Enabled && Settings.ThermalGuard && _thermal.Available && now >= _thermalSnoozeUntilUtc
                     && (want || LidClosed) && (OnBattery || !Settings.ThermalOnlyOnBattery);
        if (guard)
        {
            bool hot = _thermal.TemperatureC >= Settings.ThermalThresholdC
                       || (LidClosed && _thermal.PassiveLimitPercent is < 50);
            if (!hot)
            {
                _hotSinceUtc = null;
            }
            else
            {
                _hotSinceUtc ??= now;
                if (now - _hotSinceUtc >= TimeSpan.FromSeconds(Settings.ThermalSustainSeconds)
                    && Pending?.Source != ActionSource.Thermal)
                {
                    string reason = $"Temperatura alta: {_thermal.TemperatureC:0} °C";
                    Log.Warn($"{reason} durante {Settings.ThermalSustainSeconds}s (límite {Settings.ThermalThresholdC} °C)");
                    StartAction(Settings.ThermalAction, ActionSource.Thermal, reason,
                        Interactive ? now + ThermalCountdown : now);
                }
            }
        }
        else
        {
            _hotSinceUtc = null;
            if (Pending?.Source == ActionSource.Thermal)
            {
                Log.Info("La protección térmica ya no aplica (enchufada / en pausa): se cancela la acción");
                Pending = null;
            }
        }

        // 6) A pending action the user hasn't seen (scheduled while nobody was looking): show it now,
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

        // 7) Sleep request that didn't take (lid closed, still running) → hibernate.
        if (_sleepAttemptUtc is { } attempt && !_suspendObserved)
        {
            var since = now - attempt;
            if (since > SleepEscalation + TimeSpan.FromMinutes(2)) _sleepAttemptUtc = null;
            else if (since >= SleepEscalation && Bagged)
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

    /// <summary>Someone may be looking: a screen is on (laptop panel, or an external monitor with the lid closed).</summary>
    private bool Interactive => DisplayOn;

    /// <summary>Lid closed and no screen on: the laptop is probably in a bag.</summary>
    private bool Bagged => LidClosed && !DisplayOn;

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
        if (p.Source == ActionSource.Timer) ClearTimerCore();
        _hotSinceUtc = null;
        _actionExecutedUtc = DateTime.UtcNow;

        // Stay out of the way until the user is back (lid opened / screen on / resumed by the user).
        HoldReason = p.Source switch
        {
            ActionSource.Thermal => $"{ActionName(p.Action)} por temperatura alta",
            ActionSource.Timer => $"{ActionName(p.Action)} por límite de tiempo",
            ActionSource.Battery => $"{ActionName(p.Action)} por batería baja",
            ActionSource.Finished => $"{ActionName(p.Action)} al terminar (tapa cerrada)",
            _ => $"{ActionName(p.Action)} manual",
        };

        _power.SetKeepAwake(false, false, string.Empty);
        if (_power.LidOverrideActive) _power.RestoreLid();

        if (p.Source is ActionSource.Thermal or ActionSource.Battery)
            RaiseNotify("VibeDoze", $"{p.Reason}. {ActionName(p.Action)} para proteger la laptop.");
        Log.Info($"Ejecutando: {ActionName(p.Action)} — {p.Reason}");
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
                         && PowerController.ArmIdleSleep(60);
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
            Detail = $"{HoldReason}. Vuelvo a activarme cuando abras la tapa.";
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
            Headline = LidClosed ? "Trabajando con la tapa cerrada" : "Manteniendo despierta";
            Detail = busy.Count == 0 ? "Modo manual activado" : DescribeBusy(busy);
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
        if (busy.Count == 0) return "VibeDoze: modo manual";
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
                    else if (setting.PowerSetting == Native.GUID_ACTIVE_POWERSCHEME) _power.EnsureOverrideOnActiveScheme();
                    break;
                case Native.PBT_APMSUSPEND:
                    _suspendObserved = true;
                    _sleepAttemptUtc = null;
                    Log.Info("Sistema entrando en suspensión");
                    break;
                case Native.PBT_APMRESUMEAUTOMATIC:
                    OnResume(byUser: false);
                    break;
                case Native.PBT_APMRESUMESUSPEND:
                    OnResume(byUser: true);
                    break;
                case Native.PBT_APMPOWERSTATUSCHANGE:
                    ReadBattery();
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
        if (!closed)
        {
            // The user is back: no pending escalation, no shortened idle timeout, no hold.
            _sleepAttemptUtc = null;
            if (PowerController.IdleOverrideActive) PowerController.RestoreIdleFromState();
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
        _displayOnSinceUtc = on ? DateTime.UtcNow : null;
        if (on && !LidClosed)
        {
            // Someone woke the screen: drop the 60 s idle timeout right away. Leaving the "hold" state waits
            // until the screen has stayed on for a few seconds (see Evaluate), so a blip doesn't count.
            _sleepAttemptUtc = null;
            if (PowerController.IdleOverrideActive) PowerController.RestoreIdleFromState();
        }
        Refresh();
    }

    private void OnResume(bool byUser)
    {
        Log.Info(byUser ? "Reanudado por el usuario" : "Reanudado");
        if (PowerController.IdleOverrideActive) PowerController.RestoreIdleFromState();
        _suspendObserved = true;
        _sleepAttemptUtc = null;
        _hotSinceUtc = null;
        if (Pending is not null && Pending.Source != ActionSource.Timer) Pending = null;
        if (TimerDeadlineUtc is { } d && d <= DateTime.UtcNow)
        {
            Log.Info("El límite de tiempo venció mientras dormía; se cancela");
            ClearTimerCore();
            Pending = null;
        }
        if (byUser && !LidClosed) HoldReason = null;
        Refresh();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _hookServer.Dispose();
        foreach (var h in _powerNotifications) Native.UnregisterPowerSettingNotification(h);
        if (_suspendNotification != IntPtr.Zero) Native.UnregisterSuspendResumeNotification(_suspendNotification);
        _power.Dispose();
        _thermal.Dispose();
        _window.Dispose();
        Log.Info("VibeDoze detenido");
    }
}
