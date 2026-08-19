using WarriorHotkeyBridge.Warrior;

namespace WarriorHotkeyBridge.Tests;

/// <summary>
/// When the SIM has to be woken up, and when touching it would be pure cost.
/// </summary>
/// <remarks>
/// <para>
/// Measured on a live session, over several days of intermittent failures. The bridge did
/// everything right - correct page, chord delivered to &lt;body&gt;, document.hasFocus() true,
/// defaultPrevented false - and the SIM ignored it. The SIM decides whether it is the active
/// application from real focus events, and raising Chrome's window produces none: the renderer
/// never considered that page blurred, so nothing fires and the SIM stays asleep.
/// </para>
/// <para>
/// A synthesised FocusEvent was tried on the live page and did nothing; the SIM checks isTrusted.
/// A real Playwright click on the Level 2 tab header fixed it immediately. That also explains why
/// the fault came and went: when Level 2 was not the selected component the bridge clicked that
/// same tab to select it and woke the SIM by accident, and when it was already selected the click
/// was skipped and so was the wake-up.
/// </para>
/// </remarks>
public class Level2ReactivationTests
{
    /// <summary>
    /// Having had to bring the window forward is what identifies "the operator was elsewhere".
    /// </summary>
    [Fact]
    public void RaisingTheWindowMeansTheSimNeedsWaking()
        => Assert.True(ActivationOutcome.Raised.NeedsReactivation());

    /// <summary>
    /// A failed raise is the same situation as a successful one: the operator was still away.
    /// Whether Windows granted the foreground change says nothing about whether the SIM is asleep.
    /// </summary>
    [Fact]
    public void AFailedRaiseStillNeedsWaking()
        => Assert.True(ActivationOutcome.NotRaised.NeedsReactivation());

    /// <summary>
    /// The case that has to stay free. Repeated presses without leaving the SIM are the normal
    /// trading rhythm, and they must not each pay for a click and a hit test.
    /// </summary>
    [Fact]
    public void AWindowAlreadyInFrontIsLeftAlone()
        => Assert.False(ActivationOutcome.AlreadyInFront.NeedsReactivation());

    [Fact]
    public void ATabHeaderIsClickableWhenLevel2SitsInATabset()
    {
        var located = new Level2Result
        {
            Status = Level2Status.Ready,
            MatchedSelector = ".flexlayout__tab_button",
            HasTabBar = true,
        };

        Assert.True(located.HasClickableTab);
    }

    /// <summary>
    /// Level 2 popped out into its own window has no tab bar. Nothing gets clicked there.
    /// </summary>
    /// <remarks>
    /// The panel is made almost entirely of order controls, so "click something to wake it" has
    /// exactly one safe answer and it is the tab header. With no tab header the answer is to
    /// click nothing - a missed wake-up costs one keystroke, a stray click costs a position.
    /// </remarks>
    [Fact]
    public void APoppedOutPanelIsNeverClicked()
    {
        var poppedOut = new Level2Result
        {
            Status = Level2Status.Ready,
            MatchedSelector = ".flexlayout__tab_button",
            HasTabBar = false,
        };

        Assert.False(poppedOut.HasClickableTab);
    }

    [Fact]
    public void NothingIsClickedWhenNoSelectorMatched()
    {
        var notFound = new Level2Result { Status = Level2Status.NotFound, HasTabBar = true };

        Assert.False(notFound.HasClickableTab);
    }
}
