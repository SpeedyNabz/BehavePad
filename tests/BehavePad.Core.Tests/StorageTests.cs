using BehavePad.Core.Analysis;
using BehavePad.Core.Filtering;
using BehavePad.Core.Input;
using BehavePad.Core.Storage;

namespace BehavePad.Core.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "BehavePadTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void Profile_round_trips_through_json()
    {
        var store = new JsonFileStore<FilterProfile>(Path.Combine(_directory, "profile.json"));
        var profile = new FilterProfile
        {
            Name = "Pad",
            Level = ProtectionLevel.Maximum,
            AdaptiveCentering = true,
            ZoneShape = ZoneShape.Fitted,
            LearnZone = true,
            RightStick = new StickFilterSettings
            {
                CenterX = 0.03,
                CenterY = -0.11,
                Deadzone = 0.14,
                NoiseGate = 0.01,
                Outline = [new StickPoint(0.01, -0.13), new StickPoint(0.05, 0.6)],
                OutlineMargin = 0.04,
                Learned = [new StickPoint(0.05, 0.64)],
            },
            LeftTrigger = new TriggerFilterSettings { Deadzone = 0.06 },
            Buttons = new ButtonFilterSettings
            {
                Debounce = [new ButtonDebounce(GamepadButtons.Y, 32)],
                Blocked = GamepadButtons.LeftShoulder | GamepadButtons.Back,
            },
        };

        store.Save(profile);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(profile.RightStick, loaded.RightStick);
        Assert.Equal(profile.LeftTrigger, loaded.LeftTrigger);
        Assert.Equal(profile.Buttons.Blocked, loaded.Buttons.Blocked);
        Assert.Equal(32, loaded.Buttons.DebounceFor(GamepadButtons.Y));
        Assert.Equal(ProtectionLevel.Maximum, loaded.Level);
        Assert.True(loaded.AdaptiveCentering);
        Assert.Equal(ZoneShape.Fitted, loaded.ZoneShape);
        Assert.True(loaded.LearnZone);
    }

    [Fact]
    public void Profile_saved_before_fitted_zones_loads_as_a_circle()
    {
        var path = Path.Combine(_directory, "profile.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "level": "Balanced",
              "rightStick": { "enabled": true, "centerX": 0.03, "centerY": -0.11, "deadzone": 0.14, "outerDeadzone": 0.97, "noiseGate": 0, "hysteresis": 0.006 },
              "adaptiveCentering": false
            }
            """);

        var loaded = new JsonFileStore<FilterProfile>(path).Load()!.Sanitized();

        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(ZoneShape.Circle, loaded.ZoneShape);
        Assert.Equal(0.14, loaded.RightStick.Deadzone, 6);
        Assert.Equal([new StickPoint(0.03, -0.11)], loaded.RightStick.Outline);
        Assert.Equal(0.14, loaded.RightStick.OutlineMargin, 6);
    }

    [Fact]
    public void Report_round_trips_through_json()
    {
        var recorder = new RestRecorder();
        for (var i = 0; i < 500; i++)
        {
            recorder.Add(new GamepadState(0, 0, (short)(i % 7), -3638, 12, 0, i % 100 < 5 ? GamepadButtons.Y : GamepadButtons.None), i);
        }

        var report = DriftAnalyzer.Analyze(recorder.ToCapture(), new SnapBackCapture([new StickPoint(0.01, 0)], []), "Pad");
        var store = new JsonFileStore<DriftReport>(Path.Combine(_directory, "report.json"));

        store.Save(report);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(report.RightStick.RestCenter, loaded.RightStick.RestCenter);
        Assert.Equal(report.RightStick.RestPoints.Count, loaded.RightStick.RestPoints.Count);
        Assert.Equal(report.LeftStick.SettlePoints, loaded.LeftStick.SettlePoints);
        Assert.NotEmpty(loaded.RightStick.RestOutline);
        Assert.Equal(report.RightStick.RestOutline, loaded.RightStick.RestOutline);
        Assert.Equal(report.ButtonGlitches, loaded.ButtonGlitches);
        Assert.Equal(report.LeftTrigger, loaded.LeftTrigger);
        Assert.Equal(report.Overall, loaded.Overall);
    }

    [Fact]
    public void Unreadable_file_loads_as_nothing_and_is_set_aside()
    {
        var path = Path.Combine(_directory, "profile.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{ not json");

        Assert.Null(new JsonFileStore<FilterProfile>(path).Load());
        Assert.True(File.Exists(path + ".corrupt"));
    }
}
