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
            LearnZone = true,
            RightStick = new StickFilterSettings
            {
                Level = ProtectionLevel.Precise,
                Shape = ZoneShape.Fitted,
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
        Assert.Equal(new StickChoice(ProtectionLevel.Precise, ZoneShape.Fitted), loaded.RightStick.Choice);
        Assert.True(loaded.LearnZone);
        Assert.DoesNotContain("zoneShape", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Profile_from_version_1_1_gives_both_sticks_its_shared_shape_and_preset()
    {
        var path = Path.Combine(_directory, "profile.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, """
            {
              "schemaVersion": 2,
              "level": "Maximum",
              "rightStick": { "enabled": true, "centerX": 0.03, "centerY": -0.11, "deadzone": 0.2, "outline": [ { "x": 0.02, "y": -0.12 }, { "x": 0.04, "y": 0.6 } ], "outlineMargin": 0.06, "learned": [] },
              "zoneShape": "Fitted",
              "learnZone": true
            }
            """);
        var store = new JsonFileStore<FilterProfile>(path);

        var loaded = store.Load()!.Sanitized();
        store.Save(loaded);

        Assert.Equal(FilterProfile.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(new StickChoice(ProtectionLevel.Maximum, ZoneShape.Fitted), loaded.LeftStick.Choice);
        Assert.Equal(new StickChoice(ProtectionLevel.Maximum, ZoneShape.Fitted), loaded.RightStick.Choice);
        Assert.Equal(2, loaded.RightStick.Outline.Count);
        Assert.True(loaded.LearnZone);
        Assert.DoesNotContain("zoneShape", File.ReadAllText(path));
        Assert.Equal(loaded.RightStick, store.Load()!.Sanitized().RightStick);
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

        Assert.Equal(FilterProfile.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(new StickChoice(ProtectionLevel.Balanced, ZoneShape.Circle), loaded.RightStick.Choice);
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
