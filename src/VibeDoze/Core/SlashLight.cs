using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace VibeDoze.Core;

public enum SlashPattern
{
    Off,
    /// <summary>An agent is working: a light runs along the bar.</summary>
    Working,
    /// <summary>An agent waits for permission: double blink.</summary>
    Waiting,
    /// <summary>Done (grace period) or manual mode: slow breathing.</summary>
    Resting,
}

/// <summary>What the Slash showed before VibeDoze took over (saved in state.json so a crash can put it back).</summary>
public sealed class SlashOriginal
{
    /// <summary>Animation slots ("D2 03 00 0C" answer, from byte 3).</summary>
    public byte[] Mode { get; set; } = Array.Empty<byte>();
    /// <summary>Record AB: on/off, brightness, interval.</summary>
    public byte[] Options { get; set; } = Array.Empty<byte>();
    /// <summary>Record AC: custom frames.</summary>
    public byte[] Custom { get; set; } = Array.Empty<byte>();
    /// <summary>"Stay lit" flags the user has off (all of them are on while VibeDoze animates).</summary>
    public List<SlashFlag> FlagsSwitchedOn { get; set; } = new();
}

public enum SlashFlag
{
    LidClosed,
    OnBattery,
    Master,
}

/// <summary>
/// Animates the "Slash" LED bar on the lid of the ASUS ROG Zephyrus G14 2024 (USB 0B05:193B)
/// while VibeDoze keeps the laptop working with the lid closed. The rest of the time the bar is either off
/// (a status light only) or back to the user's own lighting.
/// Protocol as implemented by G-Helper and asusctl: 128-byte HID feature reports, report ID 0x5E.
/// The worker thread owns the device; the engine only says which pattern it wants.
/// </summary>
internal sealed class SlashLight : IDisposable
{
    private const int FrameMs = 50; // 20 fps: smooth on 7 segments for ~2 ms of USB I/O per frame
    private const int Segments = 7;

    private static readonly Lazy<string?> DevicePath = new(SlashHid.Find);

    /// <summary>"Probar": each pattern long enough to read it (two blinks, one full breath).</summary>
    private static readonly (SlashPattern Pattern, TimeSpan Length)[] PreviewSteps =
    {
        (SlashPattern.Working, TimeSpan.FromSeconds(5)),
        (SlashPattern.Waiting, TimeSpan.FromSeconds(3.6)),
        (SlashPattern.Resting, TimeSpan.FromSeconds(4.5)),
    };

    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private SlashPattern _wanted;
    /// <summary>Keep the bar off while it shows no pattern: a status light, not the user's decoration.</summary>
    private bool _wantDark;
    /// <summary>The bar is switched off by us (true), back to the user's lighting (false), or untouched (null).</summary>
    private bool? _dark;
    private bool _reassertDark;
    private DateTime _retryAfterUtc = DateTime.MinValue;
    private bool _disposed;

    /// <summary>Picks up "switched off" from a previous run (it stays off across a restart or a Windows session).</summary>
    public SlashLight()
    {
        if (RuntimeStateStore.TryLoad(out var state) && state.SlashDark) _dark = true;
    }

    public static bool Present => DevicePath.Value is not null;

    /// <summary>The preview's pattern at this point, Off once it's over.</summary>
    public static SlashPattern PreviewPattern(TimeSpan elapsed)
    {
        foreach (var (pattern, length) in PreviewSteps)
        {
            if (elapsed < length) return pattern;
            elapsed -= length;
        }
        return SlashPattern.Off;
    }

    /// <summary>"VibeDoze.exe slash-preview": the same preview without the app, then the bar as it was.</summary>
    public static bool RunPreview()
    {
        if (!Present) return false;
        var light = new SlashLight();
        bool dark = light._dark == true; // the app keeps it off while the laptop is in use
        var clock = Stopwatch.StartNew();
        SlashPattern pattern;
        while ((pattern = PreviewPattern(clock.Elapsed)) != SlashPattern.Off)
        {
            light.Show(pattern, dark);
            Thread.Sleep(100);
        }
        light.Release(TimeSpan.FromSeconds(2), keepDark: dark);
        return true;
    }

    /// <summary>Called every engine tick; cheap when nothing changes.</summary>
    /// <param name="darkWhenIdle">Without a pattern, keep the bar off instead of showing the user's animation.</param>
    public void Show(SlashPattern pattern, bool darkWhenIdle)
    {
        if (!Present) return;
        lock (_gate)
        {
            if (_disposed) return;
            bool changed = pattern != _wanted || darkWhenIdle != _wantDark;
            _wanted = pattern;
            _wantDark = darkWhenIdle;
            StartIfNeeded();
            if (changed) _wake.Set();
        }
    }

    /// <summary>Something may have switched the bar back on (Armoury Crate after a resume, a power change…).</summary>
    public void Reassert()
    {
        lock (_gate)
        {
            if (_dark != true) return;
            _reassertDark = true;
            StartIfNeeded();
        }
    }

    /// <summary>Ends any pattern now (before a suspend or exit) and waits a little for the bar to settle.</summary>
    /// <param name="keepDark">Leave the bar off if it is meant to be; otherwise the user's own lighting comes back.</param>
    public void Release(TimeSpan wait, bool keepDark)
    {
        Thread? thread;
        lock (_gate)
        {
            _wanted = SlashPattern.Off;
            _wantDark &= keepDark;
            StartIfNeeded();
            thread = _thread;
        }
        _wake.Set();
        thread?.Join(wait);
    }

    public void Dispose() => Dispose(keepDark: false);

    /// <param name="keepDark">Windows is shutting down: stay off until VibeDoze starts again.</param>
    public void Dispose(bool keepDark)
    {
        Release(TimeSpan.FromSeconds(2), keepDark);
        lock (_gate) _disposed = true;
    }

    /// <summary>Under the lock: the bar isn't in the state the engine wants.</summary>
    private bool PendingWork() =>
        _wanted != SlashPattern.Off || (_wantDark ? _dark != true || _reassertDark : _dark == true);

    /// <summary>Under the lock.</summary>
    private void StartIfNeeded()
    {
        if (_disposed || _thread is not null || !PendingWork() || DateTime.UtcNow < _retryAfterUtc) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Slash" };
        _thread.Start();
    }

    private void Run()
    {
        SlashHid? hid = null;
        SlashOriginal? original = null;
        try
        {
            hid = SlashHid.Open(DevicePath.Value!);
            byte level = 0xFF;
            var clock = Stopwatch.StartNew();
            var shown = SlashPattern.Off;
            var frame = new byte[4 + Segments];
            frame[0] = 0xD3;
            frame[3] = Segments;
            while (true)
            {
                SlashPattern wanted;
                bool dark, reassert, isDark;
                lock (_gate)
                {
                    wanted = _wanted;
                    dark = _wantDark;
                    reassert = _reassertDark;
                    _reassertDark = false;
                    isDark = _dark == true;
                }

                if (wanted != SlashPattern.Off)
                {
                    if (original is null && (original = TakeOver(hid, out level)) is null)
                    {
                        RetryLater();
                        return;
                    }
                    if (wanted != shown)
                    {
                        shown = wanted;
                        clock.Restart();
                    }
                    Render(shown, clock.Elapsed.TotalSeconds, level, frame.AsSpan(4));
                    hid.Set(frame);
                    _wake.WaitOne(FrameMs);
                    continue;
                }

                if (original is not null)
                {
                    Restore(hid, original, dark);
                    original = null;
                    shown = SlashPattern.Off;
                }
                else if ((dark != isDark || (dark && reassert)) && !SetDark(hid, dark))
                {
                    RetryLater();
                    return;
                }

                lock (_gate)
                {
                    if (PendingWork()) continue;
                    _thread = null;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Luz Slash", ex);
            RetryLater();
        }
        finally
        {
            // Also after a failure halfway through taking over: whatever was saved goes back.
            if (hid is not null && (original ?? Saved()) is { } saved)
            {
                bool dark;
                lock (_gate) dark = _wantDark;
                Restore(hid, saved, dark);
            }
            hid?.Dispose();
            lock (_gate)
            {
                if (_thread == Thread.CurrentThread) _thread = null;
            }
        }
    }

    private void RetryLater()
    {
        lock (_gate) _retryAfterUtc = DateTime.UtcNow.AddMinutes(1);
    }

    private static SlashOriginal? Saved() => RuntimeStateStore.TryLoad(out var state) ? state.Slash : null;

    /// <summary>Saves what the bar shows, keeps it lit (battery, lid closed) and switches it to custom frames.</summary>
    private static SlashOriginal? TakeOver(SlashHid hid, out byte level)
    {
        level = 0xFF;
        if (!RuntimeStateStore.TryLoad(out var state)) return null; // never overwrite saved originals we couldn't read

        // A previous run that never restored: its originals are the user's, what the bar shows now is ours.
        var original = state.Slash;
        if (original is null)
        {
            byte[] mode = hid.Read(0xD2, 0x03, 0x00, 0x0C);
            byte[] options = hid.Read(0xD2, 0x02, 0x01, 0x08, 0xAB);
            byte[] custom = hid.Read(0xD2, 0x02, 0x01, 0x08, 0xAC);
            if (mode[1] != 0xD2 || mode[5] != 0x01 || options[5] != 0xAB || custom[5] != 0xAC)
            {
                Log.Warn("Luz Slash: respuesta inesperada del dispositivo; no se toca");
                return null;
            }
            original = new SlashOriginal
            {
                Mode = mode[3..17],
                Options = options[3..13],
                Custom = custom[3..12],
                FlagsSwitchedOn = UserFlagsOff(),
            };
            if (!RuntimeStateStore.Update(s => s.Slash = original))
            {
                Log.Warn("Luz Slash: no se pudo guardar el estado original; no se toca");
                return null;
            }
        }

        if (original.Options.Length > 7 && original.Options[7] != 0) level = original.Options[7]; // the user's brightness
        // All of them, like G-Helper before any change: on battery the bar stays dark otherwise.
        foreach (var flag in Enum.GetValues<SlashFlag>()) hid.Set(FlagCommand(flag, on: true));
        hid.Set(0xD2, 0x02, 0x01, 0x08, 0xAC);
        hid.Set(0xD3, 0x03, 0x01, 0x08, 0xAC, 0xFF, 0xFF, 0x01, 0x05, 0xFF, 0xFF);
        hid.Set(0xD4, 0x00, 0x00, 0x01, 0xAC);
        Log.Info("Luz Slash: animación de VibeDoze activada");
        return original;
    }

    private void Restore(SlashHid hid, SlashOriginal o, bool dark)
    {
        if (dark && !RuntimeStateStore.Update(s => s.SlashDark = true)) dark = false; // can't remember it: don't leave it off
        if (!RestoreCore(hid, o, dark)) return;
        lock (_gate) _dark = dark;
    }

    private static bool RestoreCore(SlashHid hid, SlashOriginal o, bool dark)
    {
        try
        {
            Apply(hid, o, dark);
            if (!RuntimeStateStore.Update(s => s.Slash = null))
                Log.Warn("Luz Slash restaurada, pero no se pudo actualizar state.json");
            Log.Info(dark ? "Luz Slash: animación terminada, la barra queda apagada" : "Luz Slash: iluminación original restaurada");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo restaurar la luz Slash", ex);
            return false;
        }
    }

    /// <summary>Off while the laptop is in use (the master switch), or back to what the user has in Armoury Crate.</summary>
    private bool SetDark(SlashHid hid, bool dark)
    {
        bool wasDark;
        lock (_gate) wasDark = _dark == true;
        if (dark && !RuntimeStateStore.Update(s => s.SlashDark = true))
        {
            Log.Warn("Luz Slash: no se pudo guardar el estado; no se apaga");
            return false;
        }
        hid.Set(FlagCommand(SlashFlag.Master, on: !dark && !UserFlagsOff().Contains(SlashFlag.Master)));
        if (!dark) RuntimeStateStore.Update(s => s.SlashDark = false);
        lock (_gate) _dark = dark;
        if (dark != wasDark)
            Log.Info(dark ? "Luz Slash: apagada mientras usas la laptop" : "Luz Slash: vuelve la iluminación de Armoury Crate");
        return true;
    }

    /// <summary>Restores the lighting saved in state.json (startup recovery, watchdog, "restore-lid").</summary>
    public static bool RestoreFromState()
    {
        if (!RuntimeStateStore.TryLoad(out var state)) return false;
        if (state.Slash is null && !state.SlashDark) return true;
        if (DevicePath.Value is not { } path) return false;
        try
        {
            using var hid = SlashHid.Open(path);
            if (state.Slash is not null)
            {
                if (!RestoreCore(hid, state.Slash, dark: false)) return false;
            }
            else
            {
                hid.Set(FlagCommand(SlashFlag.Master, on: !UserFlagsOff().Contains(SlashFlag.Master)));
                Log.Info("Luz Slash: vuelve la iluminación de Armoury Crate");
            }
            RuntimeStateStore.Update(s => s.SlashDark = false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo restaurar la luz Slash", ex);
            return false;
        }
    }

    /// <summary>The same sequence G-Helper uses to select an animation, with the user's saved values.</summary>
    private static void Apply(SlashHid hid, SlashOriginal o, bool dark)
    {
        if (o.Custom.Length == 9)
        {
            hid.Set([0xD3, 0x03, .. o.Custom]);
            hid.Set(0xD4, 0x00, 0x00, 0x01, 0xAC);
        }
        hid.Set(0xD7, 0x00, 0x00, 0x01, 0xAC);
        hid.Set(0xD2, 0x02, 0x01, 0x08, 0xAB);
        if (o.Mode.Length == 14)
        {
            hid.Set(0xD2, 0x03, 0x00, 0x0C);
            hid.Set([0xD3, 0x04, .. o.Mode]);
        }
        if (o.Options.Length == 10) hid.Set([0xD3, 0x03, .. o.Options]);
        hid.Set(0xD4, 0x00, 0x00, 0x01, 0xAB);
        foreach (var flag in Enum.GetValues<SlashFlag>())
        {
            bool on = !o.FlagsSwitchedOn.Contains(flag) && !(dark && flag == SlashFlag.Master);
            hid.Set(FlagCommand(flag, on));
        }
    }

    private static byte[] FlagCommand(SlashFlag flag, bool on)
    {
        byte value = on ? (byte)0x00 : (byte)0x80;
        return flag switch
        {
            SlashFlag.LidClosed => [0xD8, 0x00, 0x00, 0x02, 0xA5, value],
            SlashFlag.OnBattery => [0xD8, 0x01, 0x00, 0x01, value],
            _ => [0xD8, 0x02, 0x00, 0x01, value],
        };
    }

    /// <summary>
    /// These flags can be written but not read back, so take them from Armoury Crate's own record.
    /// Unknown values are the factory defaults: off with the lid closed, on with battery, master on.
    /// </summary>
    private static List<SlashFlag> UserFlagsOff()
    {
        var prefs = ArmouryCratePreferences();
        var flags = new List<SlashFlag>();
        if (!prefs.TryGetValue("StayActivatedWhenLidIsClosed", out bool lid) || !lid) flags.Add(SlashFlag.LidClosed);
        if (prefs.TryGetValue("StayActivatedOnBatteryPower", out bool battery) && !battery) flags.Add(SlashFlag.OnBattery);
        if (prefs.TryGetValue("MasterSwitch", out bool master) && !master) flags.Add(SlashFlag.Master);
        return flags;
    }

    /// <summary>"SlashLighting_PreferenceSettings" in Armoury Crate's SlashKit\&lt;model&gt;\DataCollection.json ("Key:ON").</summary>
    private static Dictionary<string, bool> ArmouryCratePreferences()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            var file = Directory.Exists(packages)
                ? Directory.EnumerateDirectories(packages, "B9ECED6F.ArmouryCrate_*")
                    .Select(p => Path.Combine(p, "LocalState", "SlashKit"))
                    .Where(Directory.Exists)
                    .SelectMany(d => Directory.EnumerateFiles(d, "DataCollection.json", SearchOption.AllDirectories))
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .FirstOrDefault()
                : null;
            if (file is null) return result;

            if (JsonNode.Parse(File.ReadAllText(file))?["SlashLighting_PreferenceSettings"] is not JsonObject settings) return result;
            foreach (var (_, value) in settings)
            {
                if (value is not JsonValue v || !v.TryGetValue<string>(out var text)) continue;
                int colon = text.LastIndexOf(':');
                if (colon <= 0) continue;
                string state = text[(colon + 1)..].Trim();
                if (state.Equals("ON", StringComparison.OrdinalIgnoreCase)) result[text[..colon]] = true;
                else if (state.Equals("OFF", StringComparison.OrdinalIgnoreCase)) result[text[..colon]] = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Luz Slash: no se pudieron leer las preferencias de Armoury Crate: {ex.Message}");
        }
        return result;
    }

    // ───────────── Patterns ─────────────

    /// <summary>One frame: perceived brightness 0..1 per segment, gamma-corrected for the LEDs.</summary>
    private static void Render(SlashPattern pattern, double t, byte level, Span<byte> segments)
    {
        for (int i = 0; i < segments.Length; i++)
        {
            double p = pattern switch
            {
                SlashPattern.Working => Comet(t, i),
                SlashPattern.Waiting => DoubleBlink(t),
                _ => Breathing(t),
            };
            segments[i] = (byte)Math.Round(level * Math.Pow(Math.Clamp(p, 0, 1), 2.2));
        }
    }

    /// <summary>A light with a fading tail runs along the bar over a faint glow: work in progress.</summary>
    private static double Comet(double t, int segment)
    {
        const double period = 1.6, tail = 3.2, lead = 0.7, glow = 0.22;
        // The head travels past the last segment so the tail clears before the next pass.
        double head = t % period / period * (Segments + tail + lead) - lead;
        double d = head - (Segments - 1 - segment); // runs in the same direction the battery level fills
        double v = d >= 0 ? 1 - d / tail : 1 + d / lead;
        return Math.Max(glow, v);
    }

    /// <summary>Two quick flashes and a pause, like a notification: open the lid, it needs you.</summary>
    private static double DoubleBlink(double t)
    {
        const double period = 1.8, flash = 0.16, gap = 0.14;
        double x = t % period;
        return Pulse(x, flash) + Pulse(x - flash - gap, flash);

        static double Pulse(double x, double width) =>
            x < 0 || x > width ? 0 : Math.Sin(Math.PI * x / width);
    }

    /// <summary>Slow breathing, like a sleeping Mac: done, about to sleep.</summary>
    private static double Breathing(double t)
    {
        const double period = 4.5, low = 0.12, high = 0.75;
        double s = 0.5 - 0.5 * Math.Cos(2 * Math.PI * t / period);
        return low + (high - low) * s;
    }

    // ───────────── HID transport ─────────────

    private sealed class SlashHid : IDisposable
    {
        private const int ReportLength = 128;
        private const byte ReportId = 0x5E;

        private readonly SafeFileHandle _handle;

        private SlashHid(SafeFileHandle handle) => _handle = handle;

        public static SlashHid Open(string path)
        {
            var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException($"No se pudo abrir la luz Slash (error {Marshal.GetLastWin32Error()})");
            return new SlashHid(handle);
        }

        public void Set(params byte[] command)
        {
            var buffer = new byte[ReportLength];
            buffer[0] = ReportId;
            command.AsSpan(0, Math.Min(command.Length, ReportLength - 1)).CopyTo(buffer.AsSpan(1));
            if (!HidD_SetFeature(_handle, buffer, ReportLength))
                throw new IOException($"La luz Slash no aceptó el comando {command[0]:X2} (error {Marshal.GetLastWin32Error()})");
        }

        /// <summary>Sends a read request and returns the device's answer (byte 0 is the report ID).</summary>
        public byte[] Read(params byte[] request)
        {
            Set(request);
            var buffer = new byte[ReportLength];
            buffer[0] = ReportId;
            if (!HidD_GetFeature(_handle, buffer, ReportLength))
                throw new IOException($"La luz Slash no respondió (error {Marshal.GetLastWin32Error()})");
            return buffer;
        }

        public void Dispose() => _handle.Dispose();

        /// <summary>
        /// The 193B collection with 128-byte feature reports (the others are firmware update / input). Older
        /// AniMe Matrix laptops reuse 193B with another protocol, so only the 2024 Slash boards qualify.
        /// </summary>
        public static string? Find()
        {
            try
            {
                if (!IsSlashBoard()) return null;
                foreach (var path in InterfacePaths())
                {
                    if (!path.Contains("vid_0b05&pid_193b", StringComparison.OrdinalIgnoreCase)) continue;
                    using var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                    if (!handle.IsInvalid && FeatureLength(handle) == ReportLength) return path;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Luz Slash: no se pudo buscar el dispositivo: {ex.Message}");
            }
            return null;
        }

        /// <summary>Only the Zephyrus G14 2024 (GA403, minus the 2025 boards, which use another device; asusctl's rule).</summary>
        private static bool IsSlashBoard()
        {
            using var bios = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            string board = $"{bios?.GetValue("BaseBoardProduct")} {bios?.GetValue("SystemProductName")}".ToUpperInvariant();
            string[] g14Of2025 = { "GA403W", "GA403UH", "GA403UM", "GA403UP", "GA403GM" };
            return board.Contains("GA403") && !g14Of2025.Any(model => board.Contains(model));
        }

        private static List<string> InterfacePaths()
        {
            var paths = new List<string>();
            HidD_GetHidGuid(out var guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (set == new IntPtr(-1)) return paths;
            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
                {
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                    if (size <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero)
                            && Marshal.PtrToStringUni(detail + 4) is { } path)
                            paths.Add(path);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return paths;
        }

        private static int FeatureLength(SafeFileHandle handle)
        {
            if (!HidD_GetPreparsedData(handle, out var data)) return 0;
            try
            {
                var caps = default(HIDP_CAPS);
                return HidP_GetCaps(data, ref caps) == HidpStatusSuccess ? caps.FeatureReportByteLength : 0;
            }
            finally
            {
                HidD_FreePreparsedData(data);
            }
        }

        private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
        private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
        private const int DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;
        private const int HidpStatusSuccess = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            public fixed ushort Rest[27]; // Reserved[17] + 10 counts
        }

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_SetFeature(SafeFileHandle device, byte[] buffer, int length);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetFeature(SafeFileHandle device, byte[] buffer, int length);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_FreePreparsedData(IntPtr data);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr data, ref HIDP_CAPS caps);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid classGuid, int index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);

        [DllImport("setupapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    }
}
