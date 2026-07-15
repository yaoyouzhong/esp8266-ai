using Microsoft.Win32;

namespace AIClockBridge;

// Per-user Windows startup registration. HKCU Run needs no administrator
// rights and works for both a development build and a packaged executable.
static class StartupManager
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "AIClockBridge";

    public static string ExecutablePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            var command = key?.GetValue(ValueName) as string;
            return string.Equals(command, Quote(ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows 启动项注册表");
        if (enabled)
            key.SetValue(ValueName, Quote(ExecutablePath), RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    static string Quote(string path) => $"\"{path}\"";
}
