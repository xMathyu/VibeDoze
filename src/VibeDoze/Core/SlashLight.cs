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
    /// <summary>"Stay lit" flags that were off and that VibeDoze switched on.</summary>
    public List<SlashFlag> FlagsSwitchedOn { get; set; } = new();
}

public enum SlashFlag
{
    LidClosed,
    OnBattery,
    Master,
}

/// <summary>
/// Animates the "Slash" LED bar on the lid of ASUS ROG laptops (Zephyrus G14 / G16 2024, USB 0B05:193B)
/// while VibeDoze keeps the laptop working with the lid closed, then puts the user's lighting back.
/// Protocol as implemented by G-Helper and asusctl: 128-byte HID feature reports, report ID 0x5E.
/// The worker thread owns the device; the engine only says which pattern it wants.
/// </summary>
internal sealed class SlashLight : IDisposable
{
    private const int FrameMs = 50; // 20 fps: smooth on 7 segments for ~2 ms of USB I/O per frame
    private const int Segments = 7;

    private static readonly Lazy<string?> DevicePath = new(SlashHid.Find);

    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private SlashPattern _wanted;
    private DateTime _retryAfterUtc = DateTime.MinValue;
    private bool _disposed;

    /// <summary>"Probar": each pattern long enough to read it (two blinks, one full breath).</summary>
    private static readonly (SlashPattern Pattern, TimeSpan Length)[] PreviewSteps =
    {
        (SlashPattern.Working, TimeSpan.FromSeconds(5)),
        (SlashPattern.Waiting, TimeSpan.FromSeconds(3.6)),
        (SlashPattern.Resting, TimeSpan.FromSeconds(4.5)),
    };

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

    /// <summary>"VibeDoze.exe slash-preview": the same preview without the app, then the user's lighting back.</summary>
    public static bool RunPreview()
    {
        if (!Present) return false;
        using var light = new SlashLight();
        var clock = Stopwatch.StartNew();
        SlashPattern pattern;
        while ((pattern = PreviewPattern(clock.Elapsed)) != SlashPattern.Off)
        {
            light.Show(pattern);
            Thread.Sleep(100);
        }
        return true;
    }

    /// <summary>Called every engine tick; cheap when nothing changes.</summary>
    public void Show(SlashPattern pattern)
    {
        if (!Present) return;
        lock (_gate)
        {
            if (_disposed) return;
            bool changed = pattern != _wanted;
            _wanted = pattern;
            if (pattern != SlashPattern.Off && _thread is null && DateTime.UtcNow >= _retryAfterUtc)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "Slash" };
                _thread.Start();
            }
            if (changed) _wake.Set();
        }
    }

    /// <summary>Puts the user's lighting back now (before a suspend or exit), waiting a little for it.</summary>
    public void Release(TimeSpan wait)
    {
        Thread? thread;
        lock (_gate)
        {
            _wanted = SlashPattern.Off;
            thread = _thread;
        }
        _wake.Set();
        thread?.Join(wait);
    }

    public void Dispose()
    {
        Release(TimeSpan.FromSeconds(2));
        lock (_gate) _disposed = true;
    }

    private void Run()
    {
        SlashHid? hid = null;
        SlashOriginal? original = null;
        try
        {
            hid = SlashHid.Open(DevicePath.Value!);
            original = TakeOver(hid, out byte level);
            if (original is null) return;

            var clock = Stopwatch.StartNew();
            var shown = SlashPattern.Off;
            var frame = new byte[4 + Segments];
            frame[0] = 0xD3;
            frame[3] = Segments;
            while (true)
            {
                SlashPattern wanted;
                lock (_gate) wanted = _wanted;
                if (wanted == SlashPattern.Off) break;
                if (wanted != shown)
                {
                    shown = wanted;
                    clock.Restart();
                }
                Render(shown, clock.Elapsed.TotalSeconds, level, frame.AsSpan(4));
                hid.Set(frame);
                _wake.WaitOne(FrameMs);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Luz Slash", ex);
            lock (_gate) _retryAfterUtc = DateTime.UtcNow.AddMinutes(1);
        }
        finally
        {
            // Also after a failure halfway through taking over: whatever was saved goes back.
            if (hid is not null && (original ?? Saved()) is { } saved) Restore(hid, saved);
            hid?.Dispose();
            lock (_gate) _thread = null;
        }
    }

    private static SlashOriginal? Saved() => RuntimeStateStore.TryLoad(out var state) ? state.Slash : null;

    /// <summary>Saves what the bar shows, keeps it lit with the lid closed and switches it to custom frames.</summary>
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
                FlagsSwitchedOn = FlagsToSwitchOn(),
            };
            if (!RuntimeStateStore.Update(s => s.Slash = original))
            {
                Log.Warn("Luz Slash: no se pudo guardar el estado original; no se toca");
                return null;
            }
        }

        if (original.Options.Length > 7 && original.Options[7] != 0) level = original.Options[7]; // the user's brightness
        foreach (var flag in original.FlagsSwitchedOn) hid.Set(FlagCommand(flag, on: true));
        hid.Set(0xD2, 0x02, 0x01, 0x08, 0xAC);
        hid.Set(0xD3, 0x03, 0x01, 0x08, 0xAC, 0xFF, 0xFF, 0x01, 0x05, 0xFF, 0xFF);
        hid.Set(0xD4, 0x00, 0x00, 0x01, 0xAC);
        Log.Info("Luz Slash: animación de VibeDoze activada");
        return original;
    }

    private static void Restore(SlashHid hid, SlashOriginal o)
    {
        try
        {
            Apply(hid, o);
            if (!RuntimeStateStore.Update(s => s.Slash = null))
                Log.Warn("Luz Slash restaurada, pero no se pudo actualizar state.json");
            Log.Info("Luz Slash: iluminación original restaurada");
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo restaurar la luz Slash", ex);
        }
    }

    /// <summary>Restores the lighting saved in state.json (startup recovery, watchdog, "restore-lid").</summary>
    public static bool RestoreFromState()
    {
        if (!RuntimeStateStore.TryLoad(out var state)) return false;
        if (state.Slash is null) return true;
        if (DevicePath.Value is not { } path) return false;
        try
        {
            using var hid = SlashHid.Open(path);
            Restore(hid, state.Slash);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo restaurar la luz Slash", ex);
            return false;
        }
    }

    /// <summary>The same sequence G-Helper uses to select an animation, with the user's saved values.</summary>
    private static void Apply(SlashHid hid, SlashOriginal o)
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
        foreach (var flag in o.FlagsSwitchedOn) hid.Set(FlagCommand(flag, on: false));
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
    /// Unknown "stay lit with the lid closed" is assumed off (the factory default); the others are left alone.
    /// </summary>
    private static List<SlashFlag> FlagsToSwitchOn()
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

        /// <summary>Zephyrus G14 2024 (GA403U…) and G16 2024 (GU605M…), the Slash boards on 193B (asusctl).</summary>
        private static bool IsSlashBoard()
        {
            using var bios = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            string board = $"{bios?.GetValue("BaseBoardProduct")} {bios?.GetValue("SystemProductName")}";
            return board.Contains("GA403U", StringComparison.OrdinalIgnoreCase)
                   || board.Contains("GU605M", StringComparison.OrdinalIgnoreCase);
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
