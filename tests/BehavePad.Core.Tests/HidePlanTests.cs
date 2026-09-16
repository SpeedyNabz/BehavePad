using BehavePad.Core.Setup;

namespace BehavePad.Core.Tests;

public sealed class HidePlanTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void The_controller_is_only_restarted_when_something_changed_and_the_user_asked_for_it(bool changed, bool allowRestart)
    {
        Assert.False(HidePlan.ShouldRestart(changed, allowRestart));
    }

    [Fact]
    public void A_run_that_changed_what_hidhide_blocks_restarts_the_controller_once_opted_in()
    {
        Assert.True(HidePlan.ShouldRestart(changed: true, allowRestart: true));
    }

    [Fact]
    public void A_run_that_changed_nothing_leaves_the_user_alone()
    {
        Assert.Equal(HideFollowUp.None, HidePlan.Decide(changed: false, allowRestart: false, restartSucceeded: null, controllerReturned: true));
        Assert.Equal(HideFollowUp.None, HidePlan.Decide(changed: false, allowRestart: true, restartSucceeded: null, controllerReturned: true));
    }

    [Fact]
    public void Without_the_opt_in_the_user_is_asked_to_replug()
    {
        // The default path: BehavePad never touches the device, so whatever already held the controller still does.
        Assert.Equal(
            HideFollowUp.ReplugRecommended,
            HidePlan.Decide(changed: true, allowRestart: false, restartSucceeded: null, controllerReturned: true));
    }

    [Fact]
    public void An_opt_in_that_never_got_as_far_as_restarting_still_asks_for_a_replug()
    {
        Assert.Equal(
            HideFollowUp.ReplugRecommended,
            HidePlan.Decide(changed: true, allowRestart: true, restartSucceeded: null, controllerReturned: false));
    }

    [Fact]
    public void A_restart_that_brings_the_controller_back_is_the_only_success()
    {
        Assert.Equal(
            HideFollowUp.Restarted,
            HidePlan.Decide(changed: true, allowRestart: true, restartSucceeded: true, controllerReturned: true));
    }

    [Fact]
    public void A_restart_that_reports_success_but_loses_the_controller_is_a_failure()
    {
        // This is the case that cost a working controller: the port cycle returned fine and the device came back
        // without the half anything can read, so reporting it as a success would be a lie.
        Assert.Equal(
            HideFollowUp.RestartFailed,
            HidePlan.Decide(changed: true, allowRestart: true, restartSucceeded: true, controllerReturned: false));
    }

    [Fact]
    public void A_restart_that_fails_outright_is_a_failure()
    {
        Assert.Equal(
            HideFollowUp.RestartFailed,
            HidePlan.Decide(changed: true, allowRestart: true, restartSucceeded: false, controllerReturned: false));
        Assert.Equal(
            HideFollowUp.RestartFailed,
            HidePlan.Decide(changed: true, allowRestart: true, restartSucceeded: false, controllerReturned: true));
    }
}
