using System.Runtime.InteropServices;

namespace AIClockBridge;

static class SystemIdleTime
{
    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo
    {
        public uint Size;
        public uint Tick;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LastInputInfo info);

    public static TimeSpan Current
    {
        get
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Tick));
        }
    }
}

static class ForegroundApp
{
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    public static bool IsCodex
    {
        get
        {
            try
            {
                var window = GetForegroundWindow();
                if (window == IntPtr.Zero) return false;
                GetWindowThreadProcessId(window, out var processId);
                using var process = System.Diagnostics.Process.GetProcessById((int)processId);
                if (process.ProcessName.Equals("codex", StringComparison.OrdinalIgnoreCase)) return true;
                var path = process.MainModule?.FileName ?? "";
                return process.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)
                    && path.Contains("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
