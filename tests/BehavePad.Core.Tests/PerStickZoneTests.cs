using BehavePad.Core.Analysis;
using BehavePad.Core.Filtering;
using BehavePad.Core.Input;

namespace BehavePad.Core.Tests;

public class PerStickZoneTests
{
    private static DriftReport WornReport()
    {
        var recorder = new RestRecorder();
        for (var i = 0; i < 2000; i++)
        {
            var wobble = 0.004 * Math.Sin(i / 7.0);
            recorder.Add(
                new GamepadState(
                    StickPoint.ToRaw(0.01 + wobble), StickPoint.ToRaw(-0.02),
                    StickPoint.ToRaw(0.03), StickPoint.ToRaw(-0.1 + wobble),
                    8, 0, GamepadButtons.None),
                i);
        }

        var snap = new SnapBackCapture(
            [new StickPoint(0.02, -0.01), new StickPoint(0, -0.03)],
            [new StickPoint(0.03, 0.05), new StickPoint(0.035, 0.2), new StickPoint(0.025, -0.14)]);
        return DriftAnalyzer.Analyze(recorder.ToCapture(), snap, "Pad");
    }

    [Fact]
    public void Each_stick_keeps_its_own_shape_and_preset()
    {
        var report = WornReport();

        var profile = FilterProfileBuilder.Build(
            report,
            new StickChoice(ProtectionLevel.Precise, ZoneShape.Circle),
            new StickChoice(ProtectionLevel.Maximum, ZoneShape.Fitted),
            ProtectionLevel.Balanced);

        Assert.Equal(new StickChoice(ProtectionLevel.Precise, ZoneShape.Circle), profile.LeftStick.Choice);
        Assert.Equal(new StickChoice(ProtectionLevel.Maximum, ZoneShape.Fitted), profile.RightStick.Choice);
        Assert.Equal(FilterProfileBuilder.BuildStick(report.LeftStick, ProtectionLevel.Precise).Deadzone, profile.LeftStick.Deadzone);
        Assert.Equal(FilterProfileBuilder.BuildStick(report.RightStick, ProtectionLevel.Maximum).OutlineMargin, profile.RightStick.OutlineMargin);
        Assert.Equal(ProtectionLevel.Balanced, profile.Level);
        Assert.Equal(FilterProfileBuilder.BuildTrigger(report.LeftTrigger, ProtectionLevel.Balanced), profile.LeftTrigger);
        Assert.True(profile.AnyStickUses(ZoneShape.Circle));
        Assert.True(profile.AnyStickUses(ZoneShape.Fitted));
    }

    [Fact]
    public void Filter_uses_each_sticks_own_shape()
    {
        var filter = new InputFilter(new FilterProfile
        {
            LearnZone = true,
            LeftStick = new StickFilterSettings { Shape = ZoneShape.Circle, Deadzone = 0.1 },
            RightStick = new StickFilterSettings
            {
                Shape = ZoneShape.Fitted,
                Deadzone = 0.1,
                Outline = [new StickPoint(0.02, -0.12), new StickPoint(0.04, 0.6)],
                OutlineMargin = 0.03,
            },
        });

        var output = filter.Apply(
            default(GamepadState).WithStick(StickSide.Left, new StickPoint(0, 0.5)).WithStick(StickSide.Right, new StickPoint(0.03, 0.5)),
            0);

        Assert.NotEqual(StickPoint.Zero, output.LeftStick);
        Assert.Equal(StickPoint.Zero, output.RightStick);
        Assert.Null(filter.Zone(StickSide.Left));
        Assert.NotNull(filter.Zone(StickSide.Right));
    }

    [Fact]
    public void Profile_with_one_shared_shape_gives_both_sticks_that_shape_when_upgraded()
    {
        var legacy = new FilterProfile
        {
            SchemaVersion = 2,
            Level = ProtectionLevel.Maximum,
            LegacyZoneShape = ZoneShape.Fitted,
        };

        var upgraded = legacy.Sanitized();

        Assert.Equal(FilterProfile.CurrentSchemaVersion, upgraded.SchemaVersion);
        Assert.Null(upgraded.LegacyZoneShape);
        Assert.Equal(new StickChoice(ProtectionLevel.Maximum, ZoneShape.Fitted), upgraded.LeftStick.Choice);
        Assert.Equal(new StickChoice(ProtectionLevel.Maximum, ZoneShape.Fitted), upgraded.RightStick.Choice);
        Assert.Equal(upgraded, upgraded.Sanitized() with { CreatedAt = upgraded.CreatedAt });
    }

    [Fact]
    public void Current_profiles_keep_their_per_stick_choices_when_sanitized()
    {
        var profile = new FilterProfile
        {
            Level = ProtectionLevel.Precise,
            RightStick = new StickFilterSettings { Level = ProtectionLevel.Maximum, Shape = ZoneShape.Fitted },
        };

        var sanitized = profile.Sanitized();

        Assert.Equal(new StickChoice(ProtectionLevel.Balanced, ZoneShape.Circle), sanitized.LeftStick.Choice);
        Assert.Equal(new StickChoice(ProtectionLevel.Maximum, ZoneShape.Fitted), sanitized.RightStick.Choice);
    }
}
