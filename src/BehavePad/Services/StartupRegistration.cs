using Microsoft.Win32;

namespace BehavePad.Services;

/// <summary>Adds or removes BehavePad from the current user's sign-in programs.</summary>
public static class StartupRegistration
{
    public const string MinimizedArgument = "--minimized";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BehavePad";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled && Environment.ProcessPath is { } path)
            {
                key.SetValue(ValueName, $"\"{path}\" {MinimizedArgument}");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not update sign-in launch: {ex.Message}");
        }
    }
}
