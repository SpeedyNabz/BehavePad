using System.Diagnostics;
using Nefarius.Drivers.HidHide;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;

namespace BehavePad.Services;

public sealed record DriverInfo(bool Installed, string? Version, string? Problem)
{
    public bool Ready => Installed && Problem is null;
}

/// <summary>Checks for the two free drivers BehavePad uses to filter inside games.</summary>
public static class DriverStatus
{
    public const string VigemDownloadUrl = "https://github.com/nefarius/ViGEmBus/releases/latest";
    public const string HidHideDownloadUrl = "https://github.com/nefarius/HidHide/releases/latest";

    public static DriverInfo CheckVigem()
    {
        try
        {
            using var client = new ViGEmClient();
            return new DriverInfo(true, FileVersion("ViGEmBus.sys"), null);
        }
        catch (VigemBusNotFoundException)
        {
            return new DriverInfo(false, null, null);
        }
        catch (VigemBusVersionMismatchException)
        {
            return new DriverInfo(true, FileVersion("ViGEmBus.sys"), "This ViGEmBus version is too old. Install the latest release.");
        }
        catch (Exception ex)
        {
            return new DriverInfo(false, null, ex.Message);
        }
    }

    public static DriverInfo CheckHidHide()
    {
        try
        {
            var service = new HidHideControlService();
            if (!service.IsInstalled)
            {
                return new DriverInfo(false, null, null);
            }

            var version = service.LocalDriverVersion?.ToString();
            return service.IsOperational
                ? new DriverInfo(true, version, null)
                : new DriverInfo(true, version, "HidHide is installed but not running yet. Restart your PC to finish setup.");
        }
        catch (Exception ex)
        {
            return new DriverInfo(false, null, ex.Message);
        }
    }

    private static string? FileVersion(string driverFile)
    {
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "drivers", driverFile);
            return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
