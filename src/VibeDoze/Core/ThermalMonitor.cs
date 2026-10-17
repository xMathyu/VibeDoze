using System.Runtime.InteropServices;

namespace VibeDoze.Core;

/// <summary>
/// Reads the ACPI thermal zones through the "Thermal Zone Information" performance counters
/// (no admin rights needed). Reports the hottest zone and whether Windows is throttling for heat.
/// </summary>
internal sealed class ThermalMonitor : IDisposable
{
    private IntPtr _query;
    private IntPtr _temperature;
    private IntPtr _passiveLimit;
    private bool _highPrecision;

    public bool Available { get; private set; }
    public string? SensorName { get; private set; }
    public double? TemperatureC { get; private set; }
    /// <summary>% of max performance allowed by passive cooling (100 = not throttling).</summary>
    public double? PassiveLimitPercent { get; private set; }
    public double MaxSeenC { get; private set; }
    public int ZoneCount { get; private set; }

    public ThermalMonitor()
    {
        try
        {
            if (Native.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
            _highPrecision = Native.PdhAddEnglishCounter(_query,
                @"\Thermal Zone Information(*)\High Precision Temperature", IntPtr.Zero, out _temperature) == 0;
            if (!_highPrecision &&
                Native.PdhAddEnglishCounter(_query, @"\Thermal Zone Information(*)\Temperature", IntPtr.Zero, out _temperature) != 0)
            {
                _temperature = IntPtr.Zero;
            }
            if (Native.PdhAddEnglishCounter(_query, @"\Thermal Zone Information(*)\% Passive Limit", IntPtr.Zero, out _passiveLimit) != 0)
                _passiveLimit = IntPtr.Zero;
            Sample();
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo abrir los sensores térmicos", ex);
        }
    }

    public void Sample()
    {
        if (_query == IntPtr.Zero || _temperature == IntPtr.Zero) return;
        if (Native.PdhCollectQueryData(_query) != 0) return;

        var temps = ReadArray(_temperature);
        double? hottest = null;
        string? hottestName = null;
        var validZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, raw) in temps)
        {
            double celsius = _highPrecision ? raw / 10.0 - 273.15 : raw - 273.15;
            if (celsius is < 1 or > 150) continue; // empty / bogus zone
            validZones.Add(name);
            if (hottest is null || celsius > hottest)
            {
                hottest = celsius;
                hottestName = name;
            }
        }

        ZoneCount = temps.Count;
        TemperatureC = hottest is null ? null : Math.Round(hottest.Value, 1);
        SensorName = hottestName;
        Available = hottest is not null;
        if (hottest > MaxSeenC) MaxSeenC = Math.Round(hottest.Value, 1);

        if (_passiveLimit != IntPtr.Zero)
        {
            // Only zones with a real temperature reading; a dummy zone reporting 0 % must not look like throttling.
            var limits = ReadArray(_passiveLimit).Where(l => validZones.Contains(l.Name) && l.Value > 0).ToList();
            PassiveLimitPercent = limits.Count == 0 ? null : limits.Min(l => l.Value);
        }
    }

    public void ResetMax() => MaxSeenC = TemperatureC ?? 0;

    private static List<(string Name, double Value)> ReadArray(IntPtr counter)
    {
        var result = new List<(string, double)>();
        uint size = 0;
        uint format = Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100;
        uint rc = Native.PdhGetFormattedCounterArray(counter, format, ref size, out _, IntPtr.Zero);
        if (rc != Native.PDH_MORE_DATA || size == 0) return result;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.PdhGetFormattedCounterArray(counter, format, ref size, out uint count, buffer) != 0) return result;
            // PDH_FMT_COUNTERVALUE_ITEM_W { LPWSTR szName; PDH_FMT_COUNTERVALUE { DWORD CStatus; union { double … } } }
            // The union holds a double, so the inner struct is 8-aligned on both x86 and x64: 24-byte items.
            const int stride = 24, statusOffset = 8, valueOffset = 16;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buffer + i * stride;
                uint status = (uint)Marshal.ReadInt32(item, statusOffset);
                if (status != 0 && status != 1) continue; // PDH_CSTATUS_VALID_DATA / NEW_DATA
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "?";
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, valueOffset));
                result.Add((name, value));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) Native.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}
