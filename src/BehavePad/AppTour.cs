using System.Diagnostics;
using BehavePad.Core.Filtering;
using BehavePad.ViewModels;

namespace BehavePad;

public partial class App
{
    /// <summary>
    /// Walks through every screen and saves a screenshot of each step. Run with --capture-tour &lt;folder&gt; and
    /// BEHAVEPAD_DATA_DIR set so real settings stay untouched. Set BEHAVEPAD_TOUR=filter to instead turn the
    /// filter on with the installed drivers and record which controller slots BehavePad reads and writes.
    /// </summary>
    private async Task CaptureTourAsync(ShellViewModel shell, string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            if (Environment.GetEnvironmentVariable("BEHAVEPAD_TOUR") == "filter")
            {
                await CaptureFilterCheckAsync(shell, directory);
                return;
            }

            await Task.Delay(1800);
            Capture(directory, "01-overview.png");

            shell.CurrentPage = AppPage.Test;
            await Task.Delay(900);
            Capture(directory, "02-test-intro.png");

            shell.Test.Length = RestLength.Quick;
            shell.Test.BeginCommand.Execute(null);
            await WaitUntilAsync(() => shell.Test.Step == TestStep.Countdown, TimeSpan.FromSeconds(5));
            await Task.Delay(600);
            Capture(directory, "03-test-countdown.png");

            await WaitUntilAsync(() => shell.Test.Step == TestStep.Resting && shell.Test.RestProgress > 0.6, TimeSpan.FromSeconds(20));
            Capture(directory, "04-test-resting.png");

            if (shell.Controller.IsDemo)
            {
                await WaitUntilAsync(() => shell.Test.Step == TestStep.SnapBack && shell.Test.RightReleases >= 3, TimeSpan.FromSeconds(30));
                Capture(directory, "05-test-snapback.png");
            }
            else
            {
                // A real controller needs a person to flick the sticks, so the tour skips that step.
                await WaitUntilAsync(() => shell.Test.Step == TestStep.SnapBack, TimeSpan.FromSeconds(30));
                await Task.Delay(1500);
                Capture(directory, "05-test-snapback.png");
                shell.Test.SkipSnapBackCommand.Execute(null);
            }

            await WaitUntilAsync(() => shell.Test.Step == TestStep.Results, TimeSpan.FromSeconds(30));
            await Task.Delay(900);
            CaptureTall(directory, "06-test-results.png", 1900);

            shell.Test.ZoneShape = ZoneShape.Fitted;
            await Task.Delay(500);
            CaptureTall(directory, "06b-test-results-shaped.png", 1900);
            shell.Test.ZoneShape = ZoneShape.Circle;

            shell.Test.Level = ProtectionLevel.Maximum;
            await Task.Delay(400);
            shell.Test.Level = ProtectionLevel.Balanced;
            shell.Test.SaveOnlyCommand.Execute(null);
            await Task.Delay(1500);
            Capture(directory, "07-overview-tested.png");

            shell.CurrentPage = AppPage.Live;
            await Task.Delay(4200);
            CaptureTall(directory, "08-live.png", 1600);

            shell.Live.ZoneShape = ZoneShape.Fitted;
            await Task.Delay(2500);
            CaptureTall(directory, "08b-live-shaped.png", 1720);

            shell.CurrentPage = AppPage.Setup;
            await Task.Delay(1200);
            CaptureTall(directory, "09-setup.png", 1500);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "tour-error.txt"), ex.ToString());
            Trace.TraceError(ex.ToString());
        }
        finally
        {
            ExitApplication();
        }
    }

    private async Task CaptureFilterCheckAsync(ShellViewModel shell, string directory)
    {
        var controller = _controller!;
        var filter = _filter!;

        await Task.Delay(2000);
        shell.CurrentPage = AppPage.Live;
        await Task.Delay(500);

        var before = controller.Pump.Latest;
        var started = await filter.StartAsync();
        await Task.Delay(3000);
        var frame = controller.Pump.Latest;

        var lines = new List<string>
        {
            $"started: {started}",
            $"message: {filter.Message}",
            $"real controller slot before: {before.Slot} (connected {before.Connected})",
            $"virtual controller slot found: {filter.VirtualSlot?.ToString() ?? "none"}",
            $"pump reads slot: {frame.Slot} (connected {frame.Connected}, forwarding {frame.Forwarding})",
            $"raw: {frame.Raw}",
            $"output: {frame.Output}",
        };

        for (var slot = 0; slot < 4; slot++)
        {
            var state = controller.XInput is { } xinput && xinput.TryRead(slot, out var reading)
                ? reading.State.ToString()
                : "empty";
            lines.Add($"xinput slot {slot}: {state}");
        }

        File.WriteAllLines(Path.Combine(directory, "filter-check.txt"), lines);
        CaptureTall(directory, "filter-on-live.png", 1480);

        await filter.StopAsync();
        await Task.Delay(500);
    }

    private void Capture(string directory, string name) => _window!.SaveScreenshot(Path.Combine(directory, name));

    private void CaptureTall(string directory, string name, double height)
    {
        var original = _window!.Height;
        _window.Height = height;
        _window.UpdateLayout();
        Capture(directory, name);
        _window.Height = original;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException("The capture tour waited too long for the next step.");
            }

            await Task.Delay(100);
        }
    }
}
