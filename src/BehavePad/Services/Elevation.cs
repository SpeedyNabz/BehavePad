using System.Security.Principal;

namespace BehavePad.Services;

/// <summary>Whether this process can change HidHide's settings without Windows asking for permission.</summary>
public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
