using System.Runtime.InteropServices;

namespace AIClockBridge;

// Lightweight system-wide CPU and physical-memory usage for the net page.
// Values are recomputed at most once per second; GetSystemTimes reports
// cumulative counters, so CPU is calculated from the delta between samples.
static class SystemStatsMonitor
{
    static readonly object Lock = new();
    static ulong _lastBusy;
    static ulong _lastIdle;
    static bool _hasLast;
    static (int Cpu, int Mem) _cached;
    static DateTime _cachedAt = DateTime.MinValue;

    public static (int Cpu, int Mem) Snapshot()
    {
        lock (Lock)
        {
            if ((DateTime.UtcNow - _cachedAt).TotalSeconds < 1) return _cached;
            _cachedAt = DateTime.UtcNow;

            if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            {
                var idle = ToUInt64(idleFt);
                var busy = ToUInt64(kernelFt) - idle + ToUInt64(userFt);
                if (_hasLast)
                {
                    var busyDelta = busy - _lastBusy;
                    var totalDelta = busyDelta + idle - _lastIdle;
                    if (totalDelta > 0)
                        _cached.Cpu = (int)Math.Round(busyDelta * 100.0 / totalDelta);
                }
                _lastBusy = busy;
                _lastIdle = idle;
                _hasLast = true;
            }

            var memory = new MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>(),
            };
            if (GlobalMemoryStatusEx(ref memory)) _cached.Mem = (int)memory.dwMemoryLoad;
            return _cached;
        }
    }

    static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME value) =>
        ((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
