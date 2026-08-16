using Microsoft.Playwright;

namespace WarriorHotkeyBridge.Warrior;

internal enum Level2Status
{
    /// <summary>No element matched any configured selector, or the match failed text verification.</summary>
    NotFound,

    /// <summary>Level 2 is present but its FlexLayout tabset is not the selected one.</summary>
    Found,

    /// <summary>Level 2 is present and selected; the page will act on a shortcut.</summary>
    Ready,

    /// <summary>More panels matched than the binding can address. Fails closed.</summary>
    Ambiguous,
}

/// <param name="Matched">Elements the selector matched, before any text filtering.</param>
/// <param name="WithExpectedText">Of those, how many actually contain the Level 2 label.</param>
internal sealed record SelectorMatch(string Selector, int Matched, int WithExpectedText, string? Error = null)
{
    public string Describe() => Error is not null
        ? $"{Selector} -> ERROR: {Error}"
        : $"{Selector} -> {Matched} matched, {WithExpectedText} with the expected text";
}

internal sealed record Level2Result
{
    public required Level2Status Status { get; init; }

    /// <summary>Which configured selector matched, for diagnostics.</summary>
    public string? MatchedSelector { get; init; }

    /// <summary>How many Level 2 panels the winning selector matched.</summary>
    public int MatchCount { get; init; }

    /// <summary>
    /// False when Level 2 exists without a FlexLayout tab bar - the expected shape when the
    /// panel has been popped out into its own window. Selection is a no-op in that case.
    /// </summary>
    public bool HasTabBar { get; init; }

    /// <summary>Whether the tab has an inner label element to aim a click at.</summary>
    public bool HasContentChild { get; init; }

    /// <summary>
    /// Whether the tab is the topmost element at its own click point. Null when no click was
    /// going to be needed, so the test was skipped.
    /// </summary>
    public bool? ClickTargetOnTop { get; init; }

    /// <summary>
    /// Short identity of whatever is covering the tab, e.g. <c>div.MuiDialog-container</c>.
    /// </summary>
    /// <remarks>
    /// Captured by the same probe that performs the hit test, so explaining a blocked click costs
    /// no extra round trip - and the explanation is available before Playwright has finished
    /// producing the retry transcript that would otherwise be all the operator saw.
    /// </remarks>
    public string? BlockedBy { get; init; }

    /// <summary>True when that obstruction sits inside a modal dialog.</summary>
    public bool BlockedByDialog { get; init; }

    /// <summary>Title of the page the probe ran on. Carried here so a readiness check is one round trip.</summary>
    public string? PageTitle { get; init; }

    /// <summary>Whether that page is the active tab in its window.</summary>
    public bool PageVisible { get; init; }

    /// <summary>
    /// True when whatever holds keyboard focus will consume the chord before the SIM sees it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This decides whether the chord reaches the SIM's own handler at all, and it outranks every
    /// other signal here. Two shapes, both measured on a live session, both while the bridge
    /// correctly reported Level 2 ready:
    /// </para>
    /// <para>
    /// A focused <b>iframe</b> sends the key to another document entirely. The SIM's charts are
    /// TradingView widgets, and clicking a graph line moves focus into one; the chord then arrives
    /// there as the first character of a symbol search. FlexLayout runs in the parent document and
    /// never sees a click inside a frame, so its selection state goes on pointing at Level 2.
    /// </para>
    /// <para>
    /// A focused <b>text field</b> types the character into itself. This one is worse to diagnose,
    /// because it happens with Level 2 genuinely selected and active - an order-entry input inside
    /// the panel holds the caret, and <c>Shift+Digit3</c> becomes a <c>#</c> in that box instead of
    /// a command.
    /// </para>
    /// <para>
    /// Selection answers which component the SIM would route a key to. This answers whether the
    /// key ever gets that far. They disagree exactly when it matters.
    /// </para>
    /// </remarks>
    public bool FocusTrapped { get; init; }

    /// <summary>
    /// True when the probe could not be executed at all, as opposed to running and reporting
    /// that Level 2 is absent. Only the former says anything about connection health.
    /// </summary>
    public bool ProbeFailed { get; init; }

    public bool IsSelected { get; init; }

    public string? Reason { get; init; }

    public bool IsReady => Status is Level2Status.Ready;

    /// <summary>
    /// This result, refused if a chord dispatched now would be swallowed before the SIM saw it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last gate before the command path treats a page as dispatchable, so the guarantee holds
    /// however readiness was arrived at rather than only on the path that repairs focus. Ready and
    /// focus-trapped is precisely the combination that produced the bug: every selection signal
    /// correct, and the keystroke delivered somewhere else entirely.
    /// </para>
    /// <para>
    /// Leaves a worse status alone. "Level 2 is not on this page" is the more useful thing to be
    /// told, and overwriting it with a focus complaint would send the operator after the wrong
    /// problem.
    /// </para>
    /// </remarks>
    public Level2Result RefusedIfFocusTrapped() =>
        IsReady && FocusTrapped
            ? this with
            {
                Status = Level2Status.Found,
                Reason = "Something on the page still has the keyboard - a chart or a text field - "
                    + "so the shortcut would have been typed into it rather than acted on. Click "
                    + "an empty part of the Level 2 panel and press the key again. Nothing was sent.",
            }
            : this;
}

/// <summary>
/// Makes the page ready to receive a chord: nothing holding the keyboard that would consume it,
/// and Level 2 the selected FlexLayout component.
/// </summary>
/// <remarks>
/// <para>
/// Both conditions are required and they are independent. Selection decides which component the
/// SIM routes a key to; focus decides whether the key reaches that routing at all. Satisfying only
/// the second is how a chord came to be typed into a chart's symbol search, and then into an
/// order-entry field, while every reported signal said Level 2 was ready.
/// </para>
/// <para>
/// Works exclusively through the DOM. No screen coordinates, no mouse movement, no
/// <c>SendInput</c> - a click here is a Playwright DOM click on the tab header, which cannot
/// land on an order button even if the layout moves.
/// </para>
/// </remarks>
internal interface ILevel2Controller
{
    /// <summary>Reports where Level 2 is without changing anything.</summary>
    Task<Level2Result> LocateAsync(IPage page, int index, CancellationToken cancellationToken);

    /// <summary>
    /// Per-selector match counts, for the diagnostics report.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="LocateAsync"/>, which stops at the first selector that works.
    /// Troubleshooting needs to know what every configured selector matched - particularly
    /// whether the primary matched and the fallback was simply never reached, or whether the
    /// primary is now dead and the fallback is carrying the load.
    /// </remarks>
    Task<IReadOnlyList<SelectorMatch>> DescribeSelectorsAsync(IPage page, CancellationToken cancellationToken);

    /// <summary>
    /// Locates Level 2 and selects it if it is not already selected, then re-verifies.
    /// </summary>
    /// <remarks>
    /// Preparation is safe to retry, which is why it is separated from dispatch: a keystroke
    /// that may already have been delivered must never be repeated automatically.
    /// </remarks>
    Task<Level2Result> EnsureSelectedAsync(IPage page, int index, CancellationToken cancellationToken);
}
