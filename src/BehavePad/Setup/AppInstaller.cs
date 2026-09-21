using System.Diagnostics;
using System.Reflection;
using BehavePad.Core.Storage;
using BehavePad.Ipc;
using BehavePad.Services;
using Microsoft.Win32;

namespace BehavePad.Setup;

/// <summary>What the person chose on the installer window.</summary>
public sealed record InstallChoices(bool DesktopShortcut, bool StartMenuShortcut, bool StartAtSignIn, bool InstallDrivers);

/// <summary>
/// Puts BehavePad on the PC and takes it off again. BehavePad ships as one self-contained executable, so
/// installing is mostly copying that file somewhere sensible, adding shortcuts and registering an entry in
/// Add or remove programs. It installs per user, so nothing here needs administrator rights; only the
/// drivers ask, and Windows asks for those once.
/// </summary>
public static class AppInstaller
{
    public const string InstallArgument = "--install";
    public const string UninstallArgument = "--uninstall";

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BehavePad";
    private const string ExeName = "BehavePad.exe";

    /// <summary>Per user, under Programs, which is where a user-scope install belongs and needs no permission.</summary>
    public static string DefaultLocation => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BehavePad");

    public static string InstalledExe => Path.Combine(DefaultLocation, ExeName);

    public static string CurrentVersionText =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>True when this copy is already the installed one, so it should just run rather than offer to install.</summary>
    public static bool IsRunningInstalled =>
        Environment.ProcessPath is { } path &&
        Path.GetFullPath(path).Equals(Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>
    /// True when this executable was started to set BehavePad up: either it was asked to, or it is a copy
    /// named like a setup download sitting outside the install folder.
    /// </summary>
    public static bool ShouldRunInstaller(string[] args)
    {
        if (args.Contains(InstallArgument))
        {
            return true;
        }

        if (IsRunningInstalled || Environment.ProcessPath is not { } path)
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("BehavePadSetup", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies this executable into place, adds the chosen shortcuts and registers the uninstaller.
    /// The driver install is separate, because that is the part Windows asks permission for.
    /// </summary>
    public static async Task InstallAsync(InstallChoices choices, IProgress<string> progress)
    {
        progress.Report("Stopping anything already running");
        await StopRunningCopiesAsync();

        progress.Report("Copying BehavePad");
        Directory.CreateDirectory(DefaultLocation);
        var source = Environment.ProcessPath ?? throw new InvalidOperationException("BehavePad could not find its own file.");
        var target = InstalledExe;
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            await CopyWithRetryAsync(source, target);
        }

        progress.Report("Adding shortcuts");
        if (choices.StartMenuShortcut)
        {
            Shortcuts.Create(Shortcuts.StartMenuPath, target, "Your controller, on its best behavior");
        }
        else
        {
            Shortcuts.Remove(Shortcuts.StartMenuPath);
        }

        if (choices.DesktopShortcut)
        {
            Shortcuts.Create(Shortcuts.DesktopPath, target, "Your controller, on its best behavior");
        }
        else
        {
            Shortcuts.Remove(Shortcuts.DesktopPath);
        }

        progress.Report("Registering BehavePad with Windows");
        WriteUninstallEntry(target);
        SetStartAtSignIn(choices.StartAtSignIn, target);
    }

    /// <summary>Starts the installed copy's background agent, so filtering is live the moment setup finishes.</summary>
    public static void StartInstalledAgent()
    {
        try
        {
            Process.Start(new ProcessStartInfo(InstalledExe, AgentContract.AgentArgument) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Could not start the BehavePad agent: {ex.Message}");
        }
    }

    public static void OpenInstalledWindow()
    {
        try
        {
            Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Could not open BehavePad: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the shortcuts, the sign-in entry and the Add or remove programs entry, then schedules the
    /// install folder to go. The running executable cannot delete itself, so a short script does it after exit.
    /// </summary>
    public static async Task UninstallAsync(bool keepData, IProgress<string> progress)
    {
        progress.Report("Stopping BehavePad");
        await StopRunningCopiesAsync();

        progress.Report("Removing shortcuts");
        Shortcuts.Remove(Shortcuts.DesktopPath);
        Shortcuts.Remove(Shortcuts.StartMenuPath);
        SetStartAtSignIn(false, InstalledExe);

        progress.Report("Removing BehavePad from Windows");
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }

        if (!keepData)
        {
            progress.Report("Removing your settings and test");
            TryDeleteDirectory(AppPaths.DataDirectory);
        }

        progress.Report("Cleaning up");
        ScheduleFolderDelete(DefaultLocation);
    }

    /// <summary>Asks any running agent or window to exit, then waits for the files to be free.</summary>
    private static async Task StopRunningCopiesAsync()
    {
        foreach (var process in Process.GetProcessesByName("BehavePad"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                try
                {
                    if (!process.CloseMainWindow())
                    {
                        process.Kill();
                    }

                    await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
                }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
                {
                    TryKill(process);
                }
            }
        }

        // The agent restores a hidden controller on its way out, which is worth a moment's patience.
        await Task.Delay(700);

        static void TryKill(Process process)
        {
            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch (Exception)
            {
            }
        }
    }

    private static async Task CopyWithRetryAsync(string source, string target)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                await Task.Delay(400);
            }
        }
    }

    private static void WriteUninstallEntry(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, writable: true);
            key.SetValue("DisplayName", "BehavePad");
            key.SetValue("DisplayVersion", CurrentVersionText);
            key.SetValue("Publisher", "SpeedyNabz");
            key.SetValue("DisplayIcon", exe);
            key.SetValue("InstallLocation", DefaultLocation);
            key.SetValue("UninstallString", $"\"{exe}\" {UninstallArgument}");
            key.SetValue("QuietUninstallString", $"\"{exe}\" {UninstallArgument} --quiet");
            key.SetValue("URLInfoAbout", "https://github.com/SpeedyNabz/BehavePad");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Trace.TraceWarning($"Could not register BehavePad with Windows: {ex.Message}");
        }
    }

    /// <summary>Points the sign-in entry at the installed copy's agent, not at whatever ran the installer.</summary>
    private static void SetStartAtSignIn(bool enabled, string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (enabled)
            {
                key.SetValue("BehavePad", $"\"{exe}\" {AgentContract.AgentArgument}");
            }
            else if (key.GetValue("BehavePad") is not null)
            {
                key.DeleteValue("BehavePad", throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Trace.TraceWarning($"Could not update sign-in launch: {ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Trace.TraceWarning($"Could not remove {path}: {ex.Message}");
        }
    }

    /// <summary>Leaves a short script that waits for this process to exit and then removes the folder.</summary>
    private static void ScheduleFolderDelete(string folder)
    {
        try
        {
            var script = Path.Combine(Path.GetTempPath(), $"behavepad-cleanup-{Environment.ProcessId}.cmd");
            File.WriteAllText(script, $"""
                @echo off
                :wait
                timeout /t 1 /nobreak >nul
                tasklist /fi "PID eq {Environment.ProcessId}" | find "{Environment.ProcessId}" >nul && goto wait
                rmdir /s /q "{folder}"
                del /q "%~f0"
                """);

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Could not schedule cleanup: {ex.Message}");
        }
    }
}
