using BehavePad.Ipc;
using Microsoft.Win32;

namespace BehavePad.Services;

/// <summary>
/// Adds or removes BehavePad's background agent from the current user's sign-in programs. It registers the
/// agent, not the window, so signing in starts the part that filters and nothing else.
/// </summary>
public static class StartupRegistration
{
    /// <summary>Kept so a sign-in entry written by an older build still starts something sensible.</summary>
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
                key.SetValue(ValueName, $"\"{path}\" {AgentContract.AgentArgument}");
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

    /// <summary>True when a sign-in entry exists and already points at this build's agent.</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Contains(AgentContract.AgentArgument, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
