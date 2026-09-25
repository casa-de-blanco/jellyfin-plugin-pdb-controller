using Jellyfin.Plugin.PdbController.Services;
using Xunit;

namespace Jellyfin.Plugin.PdbController.Tests;

public class HoldPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxHold = TimeSpan.FromHours(4);

    private static HoldDecision Decide(bool wantHold, int? observed, HoldState state) =>
        HoldPolicy.Decide(wantHold, observed, state, Grace, MaxHold, Now);

    [Fact]
    public void TakesHoldImmediately()
    {
        var decision = Decide(true, 0, new HoldState(null, null, false));
        Assert.Equal(1, decision.MinAvailable);
    }

    [Fact]
    public void ReleasesWhenNothingIsHappeningAndNothingWasHeld()
    {
        var decision = Decide(false, 0, new HoldState(null, null, false));
        Assert.Equal(0, decision.MinAvailable);
    }

    /// <summary>
    /// The crash case, and the most important one. A killed process leaves the budget
    /// at 1 with nothing left to release it, which blocks every drain. The grace
    /// window protects our own hold from flapping and must not delay this.
    /// </summary>
    [Fact]
    public void ReleasesAStaleHoldImmediatelyOnStartup()
    {
        var decision = Decide(false, 1, new HoldState(HeldSince: null, ClearedAt: Now, ForceReleased: false));

        Assert.Equal(0, decision.MinAvailable);
    }

    [Fact]
    public void KeepsOurOwnHoldThroughTheGracePeriod()
    {
        var state = new HoldState(Now.AddMinutes(-30), Now.AddMinutes(-1), false);

        Assert.Equal(1, Decide(false, 1, state).MinAvailable);
    }

    [Fact]
    public void ReleasesOurOwnHoldOnceTheGracePeriodElapses()
    {
        var state = new HoldState(Now.AddMinutes(-30), Now.AddMinutes(-6), false);

        Assert.Equal(0, Decide(false, 1, state).MinAvailable);
    }

    [Fact]
    public void BreaksAHoldThatOutlastsTheCap()
    {
        var state = new HoldState(Now.AddHours(-5), null, false);
        var decision = Decide(true, 1, state);

        Assert.Equal(0, decision.MinAvailable);
        Assert.True(decision.BrokeHold);
        Assert.True(decision.ForceReleased);
    }

    [Fact]
    public void DoesNotBreakAHoldInsideTheCap()
    {
        var state = new HoldState(Now.AddHours(-3), null, false);

        Assert.Equal(1, Decide(true, 1, state).MinAvailable);
    }

    /// <summary>
    /// Without the latch the cap is decorative: the next tick sees the same running
    /// task and holds again a second later.
    /// </summary>
    [Fact]
    public void StaysReleasedWhileTheConditionPersistsAfterBreaking()
    {
        var state = new HoldState(Now.AddHours(-5), null, ForceReleased: true);
        var decision = Decide(true, 0, state);

        Assert.Equal(0, decision.MinAvailable);
        Assert.True(decision.ForceReleased);
        Assert.False(decision.BrokeHold);
    }

    [Fact]
    public void ClearsTheLatchOnceTheConditionGoesAway()
    {
        var state = new HoldState(HeldSince: null, ClearedAt: Now, ForceReleased: true);
        var decision = Decide(false, 0, state);

        Assert.Equal(0, decision.MinAvailable);
        Assert.False(decision.ForceReleased);
    }

    /// <summary>
    /// A budget with no minAvailable at all should not be read as "held".
    /// </summary>
    [Fact]
    public void TreatsAnUnsetBudgetAsReleased()
    {
        var decision = Decide(false, null, new HoldState(null, null, false));

        Assert.Equal(0, decision.MinAvailable);
    }
}
