using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace VibeDoze.Core;

/// <summary>
/// While the lid override is active a tiny second process ("VibeDoze.exe watchdog &lt;pid&gt;") waits for the
/// main process. If VibeDoze crashes or is killed, the watchdog restores the original lid action so the
/// laptop never stays on inside a bag because of us.
/// </summary>
internal static class Watchdog
{
    private const string ReleasedEventName = @"Local\VibeDoze.OverrideReleased";
    private static Process? _process;

    public static void Arm()
    {
        try
        {
            using (var ev = new EventWaitHandle(false, EventResetMode.ManualReset, ReleasedEventName))
                ev.Reset();

            if (_process is { HasExited: false }) return;
            // Prefer the tiny relay exe (a few MB of RAM) over a second instance of the app.
            var start = HookRelay.IsLightweight
                ? new ProcessStartInfo(HookRelay.Path, $"watchdog {Environment.ProcessId} \"{AppPaths.ExePath}\"")
                : new ProcessStartInfo(AppPaths.ExePath, $"watchdog {Environment.ProcessId}");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            _process = Process.Start(start);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo iniciar el watchdog", ex);
        }
    }

    public static void Disarm()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ReleasedEventName, out var ev))
                using (ev) ev.Set();
        }
        catch
        {
            // nothing to signal
        }
    }

    public static int Run(string[] args)
    {
        if (args.Length == 0 || !int.TryParse(args[0], out int pid)) return 1;

        using var released = new EventWaitHandle(false, EventResetMode.ManualReset, ReleasedEventName);
        IntPtr handle = Native.OpenProcess(Native.SYNCHRONIZE, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            RestoreIfNeeded("proceso principal no encontrado");
            return 0;
        }

        using var parent = new ProcessWaitHandle(handle);
        int signaled = WaitHandle.WaitAny(new WaitHandle[] { released, parent });
        if (signaled == 1) RestoreIfNeeded("VibeDoze terminó inesperadamente");
        return 0;
    }

    private static void RestoreIfNeeded(string why)
    {
        var state = RuntimeStateStore.Load();
        if (state.LidOverride is null && state.IdleOverride is null) return;
        Log.Warn($"Watchdog: {why}; restaurando ajustes de energía");
        PowerController.RestoreAllFromState();
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }
}
