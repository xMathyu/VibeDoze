using System.Runtime.InteropServices;

namespace VibeDoze.Core;

public sealed record PowerCapabilities(bool LidPresent, bool S3, bool CanHibernate, bool ModernStandby);

/// <summary>
/// Everything that touches Windows power management:
///  • a power request that blocks idle sleep while agents work,
///  • the lid-close action override (applied to every power plan, always restorable),
///  • sleep / hibernate / shutdown.
/// </summary>
internal sealed class PowerController : IDisposable
{
    private static readonly Guid SubButtons = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static readonly Guid LidAction = new("5ca83367-6e45-459f-a27b-476b1d01c936");
    private const uint LidDoNothing = 0;

    private IntPtr _request = IntPtr.Zero;
    private string? _requestReason;
    private bool _systemSet;
    private bool _displaySet;
    private bool _usingThreadState;

    public static PowerCapabilities Capabilities { get; } = ReadCapabilities();

    public bool KeepingAwake => _systemSet;
    public bool LidOverrideActive { get; private set; }

    // ───────────── Keep awake ─────────────

    public void SetKeepAwake(bool system, bool display, string reason)
    {
        if (!system) display = false;

        if (system && _request != IntPtr.Zero && reason != _requestReason)
            ReleaseRequest(); // the reason string is fixed per request; recreate to update it

        if (system && _request == IntPtr.Zero && !_usingThreadState)
        {
            var context = new Native.REASON_CONTEXT
            {
                Version = Native.POWER_REQUEST_CONTEXT_VERSION,
                Flags = Native.POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                SimpleReasonString = reason,
            };
            _request = Native.PowerCreateRequest(ref context);
            if (_request == IntPtr.Zero || _request == new IntPtr(-1))
            {
                _request = IntPtr.Zero;
                _usingThreadState = true; // fallback: SetThreadExecutionState on the UI thread
                Log.Warn($"PowerCreateRequest falló ({Marshal.GetLastWin32Error()}), usando SetThreadExecutionState");
            }
            else
            {
                _requestReason = reason;
                _systemSet = _displaySet = false;
            }
        }

        if (_usingThreadState)
        {
            const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;
            uint flags = ES_CONTINUOUS | (system ? ES_SYSTEM_REQUIRED : 0) | (display ? ES_DISPLAY_REQUIRED : 0);
            SetThreadExecutionState(flags);
            _systemSet = system;
            _displaySet = display;
            return;
        }

        if (_request == IntPtr.Zero) return;
        Toggle(Native.PowerRequestType.SystemRequired, system, ref _systemSet);
        Toggle(Native.PowerRequestType.DisplayRequired, display, ref _displaySet);
        if (!system) ReleaseRequest();
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private void Toggle(Native.PowerRequestType type, bool on, ref bool current)
    {
        if (on == current) return;
        bool ok = on ? Native.PowerSetRequest(_request, type) : Native.PowerClearRequest(_request, type);
        if (ok) current = on;
        else Log.Warn($"PowerRequest {type} {(on ? "set" : "clear")} falló ({Marshal.GetLastWin32Error()})");
    }

    private void ReleaseRequest()
    {
        if (_request == IntPtr.Zero) return;
        if (_systemSet) Native.PowerClearRequest(_request, Native.PowerRequestType.SystemRequired);
        if (_displaySet) Native.PowerClearRequest(_request, Native.PowerRequestType.DisplayRequired);
        Native.CloseHandle(_request);
        _request = IntPtr.Zero;
        _requestReason = null;
        _systemSet = _displaySet = false;
    }

    // ───────────── Lid override ─────────────

    // Everything below can run from the UI thread, the watchdog or startup recovery: serialise it.
    private static readonly object PowerGate = new();
    // In-memory copies of what we saved, in case state.json can't be read back later.
    private static List<LidOriginal>? s_lidOriginals;
    private static List<LidOriginal>? s_idleOriginals;

    /// <summary>
    /// Sets "when I close the lid: do nothing" on every power plan (AC and DC). Nothing is changed unless the
    /// originals were saved to state.json first.
    /// </summary>
    public bool ApplyLidOverride()
    {
        if (LidOverrideActive) return true;
        lock (PowerGate)
        {
            try
            {
                if (!RuntimeStateStore.TryLoad(out var state))
                {
                    Log.Warn("state.json ilegible: no se cambia la acción de la tapa");
                    return false;
                }
                // If a previous run crashed mid-override, its saved originals are the real ones — keep them.
                var originals = state.LidOverride ?? s_lidOriginals?.ToList() ?? new List<LidOriginal>();
                foreach (var scheme in EnumerateSchemes())
                {
                    if (originals.Any(o => o.Scheme == scheme)) continue;
                    if (TryReadLid(scheme, out var ac, out var dc))
                        originals.Add(new LidOriginal { Scheme = scheme, Ac = ac, Dc = dc });
                }
                if (originals.Count == 0)
                {
                    Log.Warn("No se encontraron planes de energía para modificar la tapa");
                    return false;
                }
                if (!RuntimeStateStore.Update(s => s.LidOverride = originals))
                {
                    Log.Warn("No se pudieron guardar los valores originales: no se cambia la acción de la tapa");
                    return false;
                }
                s_lidOriginals = originals;
                Watchdog.Arm();

                foreach (var o in originals)
                    WriteLid(o.Scheme, LidDoNothing, LidDoNothing);
                ReapplyActiveScheme();

                LidOverrideActive = true;
                Log.Info($"Tapa: 'No hacer nada' aplicado en {originals.Count} plan(es)");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo cambiar la acción de la tapa", ex);
                return false;
            }
        }
    }

    /// <summary>Power plan switched (e.g. by the OEM performance app) while overriding: make sure it is covered too.</summary>
    public void EnsureOverrideOnActiveScheme()
    {
        if (!LidOverrideActive || GetActiveScheme() is not { } active) return;
        lock (PowerGate)
        {
            if (!TryReadLid(active, out var ac, out var dc) || (ac == LidDoNothing && dc == LidDoNothing)) return;
            var original = new LidOriginal { Scheme = active, Ac = ac, Dc = dc };
            bool saved = RuntimeStateStore.Update(s =>
            {
                s.LidOverride ??= new List<LidOriginal>();
                if (!s.LidOverride.Any(o => o.Scheme == active)) s.LidOverride.Add(original);
            });
            if (!saved) return;
            s_lidOriginals ??= new List<LidOriginal>();
            if (!s_lidOriginals.Any(o => o.Scheme == active)) s_lidOriginals.Add(original);
            WriteLid(active, LidDoNothing, LidDoNothing);
            ReapplyActiveScheme();
            Log.Info("Tapa: override aplicado al nuevo plan activo");
        }
    }

    public void RestoreLid() => LidOverrideActive = !RestoreLidFromState();

    /// <summary>
    /// Restores the lid actions saved in state.json (or kept in memory). Static so the watchdog and startup
    /// recovery can use it. Only values that are still "do nothing" are reverted, so a change the user made
    /// meanwhile is kept.
    /// </summary>
    public static bool RestoreLidFromState()
    {
        lock (PowerGate)
        {
            try
            {
                bool readable = RuntimeStateStore.TryLoad(out var state);
                var originals = state.LidOverride ?? s_lidOriginals;
                if (originals is null)
                {
                    DisarmWatchdogIfClean();
                    return readable;
                }

                foreach (var o in originals)
                {
                    if (!TryReadLid(o.Scheme, out var ac, out var dc)) continue;
                    uint newAc = ac == LidDoNothing ? o.Ac : ac;
                    uint newDc = dc == LidDoNothing ? o.Dc : dc;
                    WriteLid(o.Scheme, newAc, newDc);
                }
                ReapplyActiveScheme();
                s_lidOriginals = null;
                if (!RuntimeStateStore.Update(s => s.LidOverride = null))
                    Log.Warn("Tapa restaurada, pero no se pudo actualizar state.json");
                DisarmWatchdogIfClean();
                Log.Info("Tapa: acción original restaurada");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo restaurar la acción de la tapa", ex);
                return false;
            }
        }
    }

    /// <summary>Everything VibeDoze may have changed (used by the watchdog and startup recovery).</summary>
    public static bool RestoreAllFromState()
    {
        bool lid = RestoreLidFromState();
        bool idle = RestoreIdleFromState();
        return lid && idle;
    }

    private static void DisarmWatchdogIfClean()
    {
        if (s_lidOriginals is not null || s_idleOriginals is not null) return;
        if (RuntimeStateStore.TryLoad(out var state) && state.LidOverride is null && state.IdleOverride is null)
            Watchdog.Disarm();
    }

    // ───────────── Idle sleep (Modern Standby with the lid closed) ─────────────

    private static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

    public static bool IdleOverrideActive { get; private set; } = RuntimeStateStore.Load().IdleOverride is not null;

    /// <summary>
    /// Modern Standby laptops have no API to "sleep now" while the screen is already off (lid closed).
    /// The supported way in is the idle timer: shorten "sleep after" so Windows enters standby by itself.
    /// The original values (per plan) are saved first and restored on resume / lid open / exit / crash recovery.
    /// </summary>
    public static bool ArmIdleSleep(uint seconds)
    {
        lock (PowerGate)
        {
            try
            {
                if (GetActiveScheme() is not { } scheme) return false;
                var sub = SubSleep;
                var setting = StandbyIdle;
                if (Native.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint ac) != 0 ||
                    Native.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint dc) != 0)
                    return false;

                if (!RuntimeStateStore.TryLoad(out var state)) return false;
                var originals = state.IdleOverride ?? s_idleOriginals?.ToList() ?? new List<LidOriginal>();
                if (!originals.Any(o => o.Scheme == scheme))
                    originals.Add(new LidOriginal { Scheme = scheme, Ac = ac, Dc = dc });
                if (!RuntimeStateStore.Update(s => s.IdleOverride = originals))
                {
                    Log.Warn("No se pudo guardar la suspensión por inactividad original: no se cambia");
                    return false;
                }
                s_idleOriginals = originals;
                IdleOverrideActive = true;
                Watchdog.Arm();

                Native.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, seconds);
                Native.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, seconds);
                ReapplyActiveScheme();
                Log.Info($"Suspensión por inactividad en {seconds}s (antes: {ac}s enchufada / {dc}s batería)");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo acortar la suspensión por inactividad", ex);
                return false;
            }
        }
    }

    public static bool RestoreIdleFromState()
    {
        lock (PowerGate)
        {
            try
            {
                bool readable = RuntimeStateStore.TryLoad(out var state);
                var originals = state.IdleOverride ?? s_idleOriginals;
                if (originals is null)
                {
                    IdleOverrideActive = false;
                    return readable;
                }
                var sub = SubSleep;
                var setting = StandbyIdle;
                foreach (var o in originals)
                {
                    var scheme = o.Scheme;
                    Native.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, o.Ac);
                    Native.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, o.Dc);
                }
                ReapplyActiveScheme();
                s_idleOriginals = null;
                IdleOverrideActive = false;
                if (!RuntimeStateStore.Update(s => s.IdleOverride = null))
                    Log.Warn("Suspensión por inactividad restaurada, pero no se pudo actualizar state.json");
                DisarmWatchdogIfClean();
                Log.Info("Suspensión por inactividad restaurada");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo restaurar la suspensión por inactividad", ex);
                return false;
            }
        }
    }

    public static string DescribeCurrentLidAction()
    {
        if (GetActiveScheme() is not { } scheme || !TryReadLid(scheme, out var ac, out var dc)) return "desconocida";
        return ac == dc ? LidName(ac) : $"{LidName(ac)} (enchufada) / {LidName(dc)} (batería)";
    }

    private static string LidName(uint v) => v switch
    {
        0 => "No hacer nada",
        1 => "Suspender",
        2 => "Hibernar",
        3 => "Apagar",
        _ => v.ToString(),
    };

    private static List<Guid> EnumerateSchemes()
    {
        var list = new List<Guid>();
        for (uint i = 0; i < 64; i++)
        {
            var buffer = new byte[16];
            uint size = 16;
            if (Native.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, Native.ACCESS_SCHEME, i, buffer, ref size) != 0)
                break;
            list.Add(new Guid(buffer));
        }
        if (list.Count == 0 && GetActiveScheme() is { } active) list.Add(active);
        return list;
    }

    private static Guid? GetActiveScheme()
    {
        if (Native.PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { Native.LocalFree(ptr); }
    }

    private static bool TryReadLid(Guid scheme, out uint ac, out uint dc)
    {
        var sub = SubButtons;
        var setting = LidAction;
        dc = 0;
        return Native.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out ac) == 0
               && Native.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out dc) == 0;
    }

    private static void WriteLid(Guid scheme, uint ac, uint dc)
    {
        var sub = SubButtons;
        var setting = LidAction;
        uint rcAc = Native.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, ac);
        uint rcDc = Native.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, dc);
        if (rcAc != 0 || rcDc != 0)
            Log.Warn($"No se pudo escribir la acción de tapa en {scheme} (AC={rcAc}, DC={rcDc})");
    }

    private static void ReapplyActiveScheme()
    {
        if (GetActiveScheme() is not { } active) return;
        Native.PowerSetActiveScheme(IntPtr.Zero, ref active);
    }

    // ───────────── Sleep / hibernate / shutdown ─────────────

    private static PowerCapabilities ReadCapabilities()
    {
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try
        {
            for (int i = 0; i < 256; i++) Marshal.WriteByte(buffer, i, 0);
            if (!Native.GetPwrCapabilities(buffer)) return new PowerCapabilities(true, true, true, false);
            // SYSTEM_POWER_CAPABILITIES byte offsets (winnt.h)
            bool lid = Marshal.ReadByte(buffer, 2) != 0;
            bool s3 = Marshal.ReadByte(buffer, 5) != 0;
            bool s4 = Marshal.ReadByte(buffer, 6) != 0;
            bool hiberFile = Marshal.ReadByte(buffer, 8) != 0;
            bool aoac = Marshal.ReadByte(buffer, 20) != 0;
            return new PowerCapabilities(lid, s3, s4 && hiberFile, aoac);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Puts the machine to sleep / hibernate / off. Returns a human description of what was attempted.
    /// On Modern Standby (S0ix) laptops SetSuspendState(sleep) is unsupported, so we turn the display off
    /// (which enters Modern Standby) and/or rely on the shortened idle timeout; hibernation is the last resort.
    /// Runs on a worker thread (SetSuspendState blocks until resume) and touches no shared state.
    /// </summary>
    public static string Perform(SystemAction action, IntPtr hwnd, bool displayOn, bool idleArmed)
    {
        Native.EnableShutdownPrivilege();
        var caps = Capabilities;

        switch (action)
        {
            case SystemAction.Shutdown:
                if (Native.ExitWindowsEx(Native.EWX_SHUTDOWN | Native.EWX_POWEROFF | Native.EWX_FORCEIFHUNG,
                        Native.SHTDN_REASON_FLAG_PLANNED))
                    return "apagado";
                Log.Warn($"ExitWindowsEx falló ({Marshal.GetLastWin32Error()}), suspendiendo en su lugar");
                return Sleep(hwnd, displayOn, caps, idleArmed);

            case SystemAction.Hibernate:
                if (caps.CanHibernate && Native.SetSuspendState(true, false, false))
                    return "hibernación";
                Log.Warn("Hibernación no disponible, suspendiendo en su lugar");
                return Sleep(hwnd, displayOn, caps, idleArmed);

            default:
                return Sleep(hwnd, displayOn, caps, idleArmed);
        }
    }

    private static string Sleep(IntPtr hwnd, bool displayOn, PowerCapabilities caps, bool idleArmed)
    {
        if (!caps.ModernStandby)
        {
            if (caps.S3 && Native.SetSuspendState(false, false, false)) return "suspensión";
            Log.Info($"SetSuspendState(sleep) falló ({Marshal.GetLastWin32Error()})");
        }

        // Modern Standby (S0ix): SetSuspendState(sleep) is unsupported (or hibernates). Standby is entered
        // when the console display turns off, or when the idle timer expires (armed by the caller on the UI
        // thread via ArmIdleSleep). Use both.
        if (displayOn && hwnd != IntPtr.Zero)
        {
            Native.SendMessage(hwnd, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_MONITORPOWER), new IntPtr(2));
            return "suspensión (pantalla apagada)";
        }
        if (idleArmed) return "suspensión (Windows entra en reposo en ~1 min)";

        if (caps.CanHibernate && Native.SetSuspendState(true, false, false))
            return "hibernación (no se pudo suspender)";
        return "sin acción: el equipo no permitió suspender ni hibernar";
    }

    public void Dispose()
    {
        ReleaseRequest();
        if (_usingThreadState) SetThreadExecutionState(0x80000000);
        if (LidOverrideActive) RestoreLid();
        if (IdleOverrideActive) RestoreIdleFromState();
    }
}
