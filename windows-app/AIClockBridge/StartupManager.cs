using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace AIClockBridge;

// Per-user Task Scheduler registration. Compared with HKCU Run this gives the
// USB/network stack time to settle after logon and retries transient failures.
static class StartupManager
{
    const string LegacyKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string TaskName = "AIClockBridge";
    static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIClockBridge", "startup.log");

    public static string ExecutablePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsEnabled => ScheduledTaskMatches() || LegacyRunMatches();

    public static void EnsureCurrentRegistration()
    {
        // One-time migration for users who enabled the old HKCU Run startup.
        if (!ScheduledTaskMatches() && LegacyRunMatches()) SetEnabled(true);
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            RegisterScheduledTask();
            DeleteLegacyRun();
        }
        else
        {
            DeleteScheduledTask();
            DeleteLegacyRun();
        }
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
        }
        catch
        {
            // Startup logging is diagnostic only.
        }
    }

    static bool LegacyRunMatches()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyKeyPath);
        var command = key?.GetValue(TaskName) as string;
        return string.Equals(command, Quote(ExecutablePath), StringComparison.OrdinalIgnoreCase);
    }

    static void DeleteLegacyRun()
    {
        using var key = Registry.CurrentUser.CreateSubKey(LegacyKeyPath, writable: true);
        key?.DeleteValue(TaskName, throwOnMissingValue: false);
    }

    static bool ScheduledTaskMatches()
    {
        object service = null, folder = null, task = null, definition = null, actions = null, action = null;
        try
        {
            service = CreateService();
            dynamic scheduler = service;
            scheduler.Connect();
            folder = scheduler.GetFolder("\\");
            task = ((dynamic)folder).GetTask(TaskName);
            if (!(bool)((dynamic)task).Enabled) return false;
            definition = ((dynamic)task).Definition;
            actions = ((dynamic)definition).Actions;
            action = ((dynamic)actions).Item(1);
            return string.Equals((string)((dynamic)action).Path, ExecutablePath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(action, actions, definition, task, folder, service);
        }
    }

    static void RegisterScheduledTask()
    {
        object service = null, folder = null, definition = null;
        object registration = null, principal = null, settings = null, triggers = null, trigger = null;
        object actions = null, action = null, registered = null;
        try
        {
            service = CreateService();
            dynamic scheduler = service;
            scheduler.Connect();
            folder = scheduler.GetFolder("\\");
            definition = scheduler.NewTask(0);
            dynamic task = definition;
            registration = task.RegistrationInfo;
            ((dynamic)registration).Description = "Start AI Clock Bridge after Windows logon";
            ((dynamic)registration).Author = WindowsIdentity.GetCurrent().Name;

            principal = task.Principal;
            ((dynamic)principal).UserId = WindowsIdentity.GetCurrent().Name;
            ((dynamic)principal).LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
            ((dynamic)principal).RunLevel = 0;  // least privilege

            settings = task.Settings;
            ((dynamic)settings).Enabled = true;
            ((dynamic)settings).StartWhenAvailable = true;
            ((dynamic)settings).DisallowStartIfOnBatteries = false;
            ((dynamic)settings).StopIfGoingOnBatteries = false;
            ((dynamic)settings).ExecutionTimeLimit = "PT0S";
            ((dynamic)settings).MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
            ((dynamic)settings).RestartCount = 3;
            // Task Scheduler enforces a one-minute minimum restart interval.
            ((dynamic)settings).RestartInterval = "PT1M";

            triggers = task.Triggers;
            trigger = ((dynamic)triggers).Create(9); // TASK_TRIGGER_LOGON
            ((dynamic)trigger).Enabled = true;
            ((dynamic)trigger).Delay = "PT10S";
            ((dynamic)trigger).UserId = WindowsIdentity.GetCurrent().Name;

            actions = task.Actions;
            action = ((dynamic)actions).Create(0); // TASK_ACTION_EXEC
            ((dynamic)action).Path = ExecutablePath;
            ((dynamic)action).Arguments = "--startup-launch";
            ((dynamic)action).WorkingDirectory = Path.GetDirectoryName(ExecutablePath);

            registered = ((dynamic)folder).RegisterTaskDefinition(
                TaskName, definition, 6, null, null, 3, null); // create or update
        }
        finally
        {
            Release(registered, action, actions, trigger, triggers, settings, principal,
                registration, definition, folder, service);
        }
    }

    static void DeleteScheduledTask()
    {
        object service = null, folder = null;
        try
        {
            service = CreateService();
            dynamic scheduler = service;
            scheduler.Connect();
            folder = scheduler.GetFolder("\\");
            try { ((dynamic)folder).DeleteTask(TaskName, 0); } catch { }
        }
        finally
        {
            Release(folder, service);
        }
    }

    static object CreateService() => Activator.CreateInstance(
        Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Windows Task Scheduler 不可用"))!;

    static void Release(params object[] values)
    {
        foreach (var value in values)
            if (value != null && Marshal.IsComObject(value))
                try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    static string Quote(string path) => $"\"{path}\"";
}
