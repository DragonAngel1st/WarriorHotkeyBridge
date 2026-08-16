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
        var result = new Level2Result { Status = Level2Status.Ready, FocusTrapped = true };

        Level2Result guarded = result.RefusedIfFocusTrapped();

        Assert.False(guarded.IsReady);
        Assert.Contains("keyboard", guarded.Reason, StringComparison.OrdinalIgnoreCase);

        // The operator has to be told nothing was sent, because the failure they just saw looks
        // identical to one where the order went through.
        Assert.Contains("Nothing was sent", guarded.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A focused text field is the same failure as a focused chart, and the harder one to see.
    /// </summary>
    /// <remarks>
    /// Measured live: Level 2 genuinely selected and active, the tabset reporting
    /// "Level 2 &amp; Order Entry", and <c>document.activeElement</c> an <c>input</c> - an
    /// order-entry box inside the panel holding the caret. Shift+Digit3 became a "#" typed into
    /// that box instead of a Buy 100, and the command still logged as sent.
    ///
    /// The first version of this guard tested only for an iframe, so this case passed every check.
    /// The rule is about anything that consumes the chord, not about frames.
    /// </remarks>
    [Fact]
    public void AFocusedTextFieldIsAlsoRefused()
    {
        var result = new Level2Result
        {
            Status = Level2Status.Ready,
            IsSelected = true,
            FocusTrapped = true,
        };

        Assert.False(result.RefusedIfFocusTrapped().IsReady);
    }

    [Fact]
    public void ReadyWithFocusOnThePageIsUntouched()
    {
        var result = new Level2Result
        {
            Status = Level2Status.Ready,
            IsSelected = true,
            FocusTrapped = false,
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
            FocusTrapped = true,
            Reason = "the original reason",
        };

        Level2Result guarded = result.RefusedIfFocusTrapped();

        Assert.Equal(status, guarded.Status);
        Assert.Equal("the original reason", guarded.Reason);
    }
}
