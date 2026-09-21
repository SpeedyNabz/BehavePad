using System.Runtime.InteropServices;

namespace BehavePad.Setup;

/// <summary>
/// Writes Windows shortcuts. This talks to the shell's own IShellLink rather than a scripting host, so it
/// keeps working in a single-file build where late-bound COM would not.
/// </summary>
public static class Shortcuts
{
    public static string DesktopPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "BehavePad.lnk");

    public static string StartMenuPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "BehavePad.lnk");

    /// <summary>Creates or replaces a shortcut. Returns false when Windows refused, which is never fatal.</summary>
    public static bool Create(string shortcutPath, string target, string description, string? arguments = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(target);
            link.SetArguments(arguments ?? string.Empty);
            link.SetDescription(Truncate(description, 259));
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? string.Empty);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(shortcutPath, fRemember: true);
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not create {shortcutPath}: {ex.Message}");
            return false;
        }
    }

    public static void Remove(string shortcutPath)
    {
        try
        {
            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            System.Diagnostics.Trace.TraceWarning($"Could not remove {shortcutPath}: {ex.Message}");
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int maxPath, nint findData, int flags);

        void GetIDList(out nint idList);

        void SetIDList(nint idList);

        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int maxArgs);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int iconPathLength, out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);

        void Resolve(nint hwnd, int flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
