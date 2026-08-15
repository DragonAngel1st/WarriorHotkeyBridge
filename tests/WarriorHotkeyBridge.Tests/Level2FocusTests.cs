using WarriorHotkeyBridge.Warrior;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// The rule that keeps a chord out of a chart.
/// </summary>
/// <remarks>
/// Measured on a live session: the SIM's charts are TradingView widgets in iframes, and clicking
/// inside one moves browser keyboard focus to that frame. Playwright then delivers the chord to
/// that document, so Level 2 never sees it - while every selection signal still says Level 2 is
/// the active FlexLayout component, because FlexLayout runs in the parent and never saw the click.
/// A "Buy 100" arrived in a chart as the first character of its symbol search while the bridge
/// reported the command OK.
///
/// So Ready and focus-trapped must never be dispatchable together, whichever path produced it.
/// </remarks>
public class Level2FocusTests
{
    [Fact]
    public void ReadyButFocusTrappedIsNotDispatchable()
    {
        var result = new Level2Result { Status = Level2Status.Ready, FocusTrappedInFrame = true };

        Level2Result guarded = result.RefusedIfFocusTrapped();

        Assert.False(guarded.IsReady);
        Assert.Contains("chart", guarded.Reason, StringComparison.OrdinalIgnoreCase);

        // The operator has to be told nothing was sent, because the failure they just saw looks
        // identical to one where the order went through.
        Assert.Contains("Nothing was sent", guarded.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyWithFocusOnThePageIsUntouched()
    {
        var result = new Level2Result
        {
            Status = Level2Status.Ready,
            IsSelected = true,
            FocusTrappedInFrame = false,
        };

        Assert.Same(result, result.RefusedIfFocusTrapped());
    }

    // Three Facts rather than a Theory: Level2Status is internal, so it cannot appear in the
    // signature of a public test method.

    [Fact]
    public void NotFoundIsNotOverwritten() => AssertStatusSurvives(Level2Status.NotFound);

    [Fact]
    public void AmbiguousIsNotOverwritten() => AssertStatusSurvives(Level2Status.Ambiguous);

    [Fact]
    public void FoundIsNotOverwritten() => AssertStatusSurvives(Level2Status.Found);

    /// <summary>
    /// A worse status survives. "Level 2 is not on this page" is the more useful thing to be told,
    /// and replacing it with a focus complaint sends the operator after the wrong problem.
    /// </summary>
    private static void AssertStatusSurvives(Level2Status status)
    {
        var result = new Level2Result
        {
            Status = status,
            FocusTrappedInFrame = true,
            Reason = "the original reason",
        };

        Level2Result guarded = result.RefusedIfFocusTrapped();

        Assert.Equal(status, guarded.Status);
        Assert.Equal("the original reason", guarded.Reason);
    }
}
