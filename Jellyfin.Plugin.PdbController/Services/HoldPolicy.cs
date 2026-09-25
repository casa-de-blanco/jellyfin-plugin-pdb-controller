namespace Jellyfin.Plugin.PdbController.Services;

/// <summary>
/// What the controller remembers between ticks.
/// </summary>
/// <param name="HeldSince">
/// When the hold this process is responsible for began, or null if it holds nothing.
/// Null is load-bearing: it is what distinguishes "we are holding" from "the object
/// is held and we have no idea why", which is the state a killed process leaves.
/// </param>
/// <param name="ClearedAt">When the hold conditions last went away.</param>
/// <param name="ForceReleased">Whether the maximum hold has been broken and not yet re-armed.</param>
public readonly record struct HoldState(DateTimeOffset? HeldSince, DateTimeOffset? ClearedAt, bool ForceReleased);

/// <summary>
/// The outcome of one decision.
/// </summary>
/// <param name="MinAvailable">The value the budget should carry.</param>
/// <param name="ForceReleased">The new state of the force-release latch.</param>
/// <param name="BrokeHold">Whether this decision broke a hold that hit the cap.</param>
public readonly record struct HoldDecision(int MinAvailable, bool ForceReleased, bool BrokeHold);

/// <summary>
/// The whole hold/release decision, as a pure function.
/// </summary>
/// <remarks>
/// Separated from the service on purpose. Everything subtle about this plugin lives
/// here -- the grace window, the maximum-hold cap, the latch that stops the cap being
/// undone a second later, and the distinction between our hold and a stale one -- and
/// none of it is worth reasoning about by hand when it can be tested directly.
/// </remarks>
public static class HoldPolicy
{
    /// <summary>
    /// Decides what the budget should carry.
    /// </summary>
    /// <param name="wantHold">Whether something selected is currently running or playing.</param>
    /// <param name="observedMinAvailable">The value the budget carries right now.</param>
    /// <param name="state">What this process remembers.</param>
    /// <param name="grace">How long to wait after the conditions clear before releasing.</param>
    /// <param name="maxHold">The longest a single hold may last.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The decision.</returns>
    public static HoldDecision Decide(
        bool wantHold,
        int? observedMinAvailable,
        HoldState state,
        TimeSpan grace,
        TimeSpan maxHold,
        DateTimeOffset now)
    {
        if (!wantHold)
        {
            // A budget still held that this process never took is the crash case: the
            // server was killed mid-hold, the object outlived it, and nothing is left
            // to release it -- so it blocks every drain until someone notices. That
            // has to go back on this pass. The grace window exists to stop *our* hold
            // flapping, and has no claim on a hold we never took.
            if (state.HeldSince is null)
            {
                return new HoldDecision(0, false, false);
            }

            if (state.ClearedAt is { } cleared && now - cleared < grace)
            {
                return new HoldDecision(observedMinAvailable ?? 0, state.ForceReleased, false);
            }

            return new HoldDecision(0, false, false);
        }

        // The latch is what makes the cap mean anything. Without it the next tick sees
        // the same running task, holds again, and the maximum is never enforced.
        if (state.ForceReleased)
        {
            return new HoldDecision(0, true, false);
        }

        if (state.HeldSince is { } since && observedMinAvailable == 1 && now - since > maxHold)
        {
            return new HoldDecision(0, true, true);
        }

        return new HoldDecision(1, false, false);
    }
}
