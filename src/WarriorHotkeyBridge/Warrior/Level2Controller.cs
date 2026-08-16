using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Diagnostics;

namespace WarriorHotkeyBridge.Warrior;

/// <inheritdoc cref="ILevel2Controller"/>
internal sealed class Level2Controller : ILevel2Controller
{
    private readonly WarriorSimOptions _options;
    private readonly ILogger<Level2Controller> _logger;

    /// <summary>Last text-mismatch reported, so an unchanged page stays quiet.</summary>
    private string? _lastTextMismatch;

    public Level2Controller(IOptions<WarriorSimOptions> options, ILogger<Level2Controller> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// What counts as focus that will swallow the chord.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defined once and shared by the probe and the repair, because the first version of this had
    /// the test in one and the fix in the other and they disagreed: the probe looked only for an
    /// iframe, so a focused order-entry field passed every check and then ate the keystroke.
    /// </para>
    /// <para>
    /// Two different failures, one rule. An <c>IFRAME</c> sends the key to another document
    /// entirely - the SIM's charts are TradingView widgets, and a chord delivered there becomes
    /// the first character of a symbol search. A text field types the character into itself. In
    /// both cases the SIM's document-level hotkey handler never sees it, and the order never
    /// happens while every other signal reports success.
    /// </para>
    /// </remarks>
    private const string FocusTrapPredicate = """
        (el) => {
          if (!el) return false;
          const tag = el.tagName;
          return tag === 'IFRAME' || tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT'
            || el.isContentEditable === true;
        }
        """;

    /// <summary>
    /// Reads everything about Level 2 in a single round trip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written as one script rather than a sequence of locator calls for two reasons. Latency:
    /// each locator call is a separate CDP round trip, and the command path probes Level 2
    /// several times, so a five-call probe turns into tens of round trips on the hotkey path.
    /// Correctness: React re-renders the FlexLayout tree freely, so separate calls can describe
    /// different DOM states - the count from one render, the class attribute from the next. A
    /// single evaluation sees one consistent snapshot.
    /// </para>
    /// <para>
    /// Candidates are filtered by their text BEFORE indexing, so <c>Level2Index</c> counts
    /// actual Level 2 panels rather than everything the selector happened to match.
    /// </para>
    /// </remarks>
    private const string ProbeScript = $$"""
        ({ selectors, expectedText, selectedTabClass, tabBarClass, selectedTabsetClass, contentClass, index }) => {
          const wanted = expectedText.toLowerCase();
          let nearMiss = null;

          // Page identity travels with the Level 2 answer so a readiness check costs one round
          // trip instead of three. Neither of these forces layout.
          const pageTitle = document.title;
          const pageVisible = document.visibilityState === 'visible';

          // Whether anything will intercept the chord before the SIM's own handler sees it.
          // Selection says which component the SIM would route a key to; this says whether the key
          // reaches that routing at all. They disagree exactly when it matters, and only this one
          // is about delivery. Cheap to read and needed on the same round trip as everything else,
          // so it travels with the Level 2 answer rather than costing a second probe.
          const focusTrapped = ({{FocusTrapPredicate}})(document.activeElement);

          for (const selector of selectors) {
            let nodes;
            try {
              nodes = Array.from(document.querySelectorAll(selector));
            } catch {
              continue;                       // not valid CSS; try the next selector
            }

            if (nodes.length === 0) continue;

            // The selector alone is not proof of identity - a class name can be reused - so the
            // element must also say what it is.
            //
            // textContent, never innerText: innerText forces a synchronous layout reflow, and
            // this runs over every tab button on the page. On a busy dashboard - live scanners
            // repainting each second - that turned a ~9ms probe into ~180ms. A tab's label needs
            // no layout information to read.
            const matched = nodes.filter(el =>
              ((el.textContent || '').toLowerCase()).includes(wanted));

            if (matched.length === 0) {
              // Keep looking rather than giving up. The generic fallback selector matches every
              // tab button, so on a page that simply has no Level 2 - a scanner or alert popout -
              // this is the expected outcome, not an error.
              nearMiss ??= {
                selector,
                sample: (nodes[0].textContent || '').trim().slice(0, 60),
              };
              continue;
            }

            if (index >= matched.length) {
              return { status: 'ambiguous', pageTitle, pageVisible, focusTrapped, selector, count: matched.length };
            }

            const el = matched[index];
            const tabBar = tabBarClass ? el.closest('.' + tabBarClass) : null;
            const tabSelected = el.classList.contains(selectedTabClass);
            const tabsetActive = tabBar === null || tabBar.classList.contains(selectedTabsetClass);
            const contentChild = contentClass ? el.querySelector('.' + contentClass) : null;

            // Our own hit test, standing in for Playwright's "receives events" check so the
            // click can skip its far more expensive "stable across two animation frames" wait.
            // Only run when a click is actually going to happen: getBoundingClientRect forces a
            // layout reflow, and the already-selected path must stay free of that.
            let clickTargetOnTop = null;
            let blockedBy = null;
            let blockedByDialog = false;

            if (!(tabSelected && tabsetActive) && tabBar !== null) {
              const clickEl = contentChild || el;
              const r = clickEl.getBoundingClientRect();

              if (r.width > 0 && r.height > 0) {
                const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);

                // The topmost element at that point must be the tab itself or something inside
                // it. Anything else means an overlay covers the tab - a dialog, a dropdown - and
                // a forced click there would hit the overlay instead.
                clickTargetOnTop = top !== null && (top === el || el.contains(top));

                // Identify the obstruction while we are already here. Without it the failure
                // surfaces as Playwright's retry transcript, which says an element intercepts
                // pointer events but not that the operator simply has a dialog open.
                if (clickTargetOnTop === false && top !== null) {
                  const firstClass = (typeof top.className === 'string' && top.className)
                    ? '.' + top.className.split(/\s+/)[0]
                    : '';

                  blockedBy = (top.tagName || '?').toLowerCase() + firstClass;

                  // Walk up looking for a modal ancestor. The topmost element is usually an inert
                  // backdrop or layout div; what matters to the operator is whether a dialog is
                  // what put it there.
                  let node = top;

                  for (let depth = 0; depth < 10 && node; depth++) {
                    const role = node.getAttribute ? node.getAttribute('role') : null;
                    const cls = typeof node.className === 'string' ? node.className : '';

                    if (role === 'dialog' || role === 'alertdialog' || /dialog|modal/i.test(cls)) {
                      blockedByDialog = true;
                      break;
                    }

                    node = node.parentElement;
                  }
                }
              } else {
                clickTargetOnTop = false;      // zero-sized: scrolled out of the tab strip
                blockedBy = 'the tab is not visible in the tab strip';
              }
            }

            return {
              status: 'found',
              pageTitle,
              pageVisible,
              focusTrapped,
              selector,
              count: matched.length,
              hasTabBar: tabBar !== null,
              tabSelected,
              tabsetActive,

              // Reported here so the click path does not need a second round trip to find out
              // whether the tab has an inner label element to aim at.
              hasContentChild: contentChild !== null,
              clickTargetOnTop,
              blockedBy,
              blockedByDialog,
            };
          }

          return nearMiss
            ? { status: 'textMismatch', pageTitle, pageVisible, focusTrapped, selector: nearMiss.selector, sample: nearMiss.sample }
            : { status: 'notFound', pageTitle, pageVisible, focusTrapped };
        }
        """;

    public async Task<Level2Result> LocateAsync(IPage page, int index, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();

        Level2Probe probe;

        try
        {
            // EvaluateAsync has no timeout of its own - unlike the locator calls this replaced,
            // which defaulted to 30s. A wedged renderer would otherwise block here forever, and
            // because the command queue has a single consumer that would stall every later
            // keypress too, not just this one.
            probe = await page.EvaluateAsync<Level2Probe>(ProbeScript, new
            {
                selectors = _options.EffectiveLevel2Selectors,
                expectedText = _options.Level2TabText,
                selectedTabClass = _options.SelectedTabButtonClass,
                tabBarClass = _options.TabsetTabBarClass,
                selectedTabsetClass = _options.SelectedTabsetClass,
                contentClass = _options.TabButtonContentClass,
                index,
            }).WaitAsync(_options.ProbeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            string attempted = string.Join(", ", _options.EffectiveLevel2Selectors);
            _logger.Level2SelectorFailed(attempted, ex.Message);

            return new Level2Result
            {
                Status = Level2Status.NotFound,

                // Distinct from "the page answered and has no Level 2". The watchdog uses this
                // to tell a dead connection from a page that is simply not the SIM - without it
                // a silent connection failure looks exactly like a normal negative result.
                ProbeFailed = true,
                Reason = $"Level 2 could not be inspected: {ex.Message}",
            };
        }

        switch (probe.Status)
        {
            case "found":
                // Being the front tab of your own tabset is NOT the same as being the active
                // component. FlexLayout marks one selected tab button per tabset - a real
                // dashboard had eleven at once - while exactly one tab bar document-wide carries
                // the selected-tabset class. Only that one receives the keyboard.
                bool isSelected = probe.TabSelected && probe.TabsetActive;

                return new Level2Result
                {
                    Status = isSelected || !probe.HasTabBar ? Level2Status.Ready : Level2Status.Found,
                    MatchedSelector = probe.Selector,
                    PageTitle = probe.PageTitle,
                    PageVisible = probe.PageVisible,
                    FocusTrapped = probe.FocusTrapped,
                    MatchCount = probe.Count,
                    HasTabBar = probe.HasTabBar,
                    HasContentChild = probe.HasContentChild,
                    ClickTargetOnTop = probe.ClickTargetOnTop,
                    BlockedBy = probe.BlockedBy,
                    BlockedByDialog = probe.BlockedByDialog,
                    IsSelected = isSelected,
                };

            case "ambiguous":
                return new Level2Result
                {
                    Status = Level2Status.Ambiguous,
                    MatchedSelector = probe.Selector,
                    PageTitle = probe.PageTitle,
                    PageVisible = probe.PageVisible,
                    FocusTrapped = probe.FocusTrapped,
                    MatchCount = probe.Count,
                    Reason = $"Binding targets Level 2 panel #{index} but only {probe.Count} panel(s) matched.",
                };

            case "textMismatch":
                // Change-gated: a page that simply has no Level 2 - a scanner popout - hits this
                // on every health check, and repeating it every few seconds buries the
                // command-path lines the operator is watching.
                string mismatchSignature = $"{probe.Selector}|{probe.Sample}";

                if (!string.Equals(mismatchSignature, _lastTextMismatch, StringComparison.Ordinal))
                {
                    _lastTextMismatch = mismatchSignature;
                    _logger.Level2TextMismatch(probe.Selector ?? "?", _options.Level2TabText, probe.Sample ?? "<none>");
                }

                return new Level2Result
                {
                    Status = Level2Status.NotFound,
                    PageTitle = probe.PageTitle,
                    PageVisible = probe.PageVisible,
                    FocusTrapped = probe.FocusTrapped,
                    Reason = $"'{probe.Selector}' matched an element that does not contain "
                        + $"\"{_options.Level2TabText}\".",
                };

            default:
                return new Level2Result
                {
                    Status = Level2Status.NotFound,
                    PageTitle = probe.PageTitle,
                    PageVisible = probe.PageVisible,
                    FocusTrapped = probe.FocusTrapped,
                    Reason = "No element matched any configured Level 2 selector "
                        + $"({string.Join(", ", _options.EffectiveLevel2Selectors)}).",
                };
        }
    }

    /// <summary>Result shape of <see cref="ProbeScript"/>. Needs a parameterless constructor to deserialise.</summary>
    private sealed class Level2Probe
    {
        public string Status { get; set; } = "notFound";

        public string? Selector { get; set; }

        public string? Sample { get; set; }

        public int Count { get; set; }

        public bool HasTabBar { get; set; }

        public bool TabSelected { get; set; }

        public bool TabsetActive { get; set; }

        public string? PageTitle { get; set; }

        public bool PageVisible { get; set; }

        public bool HasContentChild { get; set; }

        public bool FocusTrapped { get; set; }

        public bool? ClickTargetOnTop { get; set; }

        /// <summary>Short identity of whatever is covering the tab, when something is.</summary>
        public string? BlockedBy { get; set; }

        /// <summary>True when the obstruction sits inside a modal dialog.</summary>
        public bool BlockedByDialog { get; set; }
    }

    /// <summary>Counts what every configured selector matches, in one round trip.</summary>
    private const string SelectorSurveyScript = """
        ({ selectors, expectedText }) => {
          const wanted = expectedText.toLowerCase();

          return selectors.map(selector => {
            try {
              const nodes = Array.from(document.querySelectorAll(selector));
              return {
                selector,
                matched: nodes.length,
                withExpectedText: nodes.filter(el =>
                  ((el.textContent || '').toLowerCase()).includes(wanted)).length,
                error: null,
              };
            } catch (e) {
              return { selector, matched: 0, withExpectedText: 0, error: String(e && e.message || e) };
            }
          });
        }
        """;

    public async Task<IReadOnlyList<SelectorMatch>> DescribeSelectorsAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        try
        {
            SelectorSurveyRow[] rows = await page
                .EvaluateAsync<SelectorSurveyRow[]>(SelectorSurveyScript, new
                {
                    selectors = _options.EffectiveLevel2Selectors,
                    expectedText = _options.Level2TabText,
                })
                .WaitAsync(_options.ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);

            return [.. rows.Select(r => new SelectorMatch(r.Selector, r.Matched, r.WithExpectedText, r.Error))];
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // Diagnostics must never throw - a failure here is itself the useful information.
            return [.. _options.EffectiveLevel2Selectors.Select(s => new SelectorMatch(s, 0, 0, ex.Message))];
        }
    }

    private sealed class SelectorSurveyRow
    {
        public string Selector { get; set; } = string.Empty;

        public int Matched { get; set; }

        public int WithExpectedText { get; set; }

        public string? Error { get; set; }
    }

    /// <summary>
    /// Explains a failed tab selection in terms the operator can act on.
    /// </summary>
    /// <remarks>
    /// Playwright's exception message is its full retry transcript - dozens of lines of "waiting
    /// for element to be visible, enabled and stable" ending in a CSS selector that intercepted
    /// pointer events. It is precise and it is unreadable, and it buries the one fact that
    /// matters: a dialog is open in the SIM and the operator needs to close it. The probe already
    /// determined what was covering the tab, so the useful answer is available without
    /// interpreting the transcript at all.
    /// </remarks>
    private static string DescribeSelectionFailure(Level2Result located, Exception ex)
    {
        if (located.BlockedByDialog)
        {
            return "A dialog is open in Warrior SIM and is covering the Level 2 tab, so it could "
                + "not be selected. Close the dialog and press the key again. Nothing was sent.";
        }

        if (located.ClickTargetOnTop == false && located.BlockedBy is { } blocker)
        {
            return $"Something is covering the Level 2 tab ({blocker}), so it could not be "
                + "selected. Close whatever is over it and press the key again. Nothing was sent.";
        }

        // Unrecognised failure: keep Playwright's first line, which names the actual error, and
        // drop the retry transcript that follows it.
        string first = ex.Message.Split('\n', 2)[0].Trim();

        return $"Could not select the Level 2 tab: {first}";
    }

    /// <summary>
    /// Returns keyboard focus from a child frame to the page itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blurring the frame element is deliberately the whole of it. Focus lands back on the page's
    /// own body, which is the state every working dispatch was measured in, and from there the SIM
    /// routes the chord by its selected component - which the caller has already established is
    /// Level 2. Focusing something inside the panel instead would mean picking an element in a
    /// component made mostly of order controls, to gain nothing.
    /// </para>
    /// <para>
    /// Returns the tag now holding focus so the caller can verify rather than assume, in keeping
    /// with the rest of this class: nothing here trusts an action to have worked.
    /// </para>
    /// </remarks>
    private const string ReturnFocusScript = $$"""
        () => {
          const trapped = ({{FocusTrapPredicate}});
          const focused = document.activeElement;

          if (trapped(focused) && typeof focused.blur === 'function') {
            focused.blur();
          }

          const now = document.activeElement;
          return (now && now.tagName || 'none').toLowerCase();
        }
        """;

    public async Task<Level2Result> EnsureSelectedAsync(IPage page, int index, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        Level2Result located = await LocateAsync(page, index, cancellationToken).ConfigureAwait(false);

        if (located.Status is Level2Status.NotFound or Level2Status.Ambiguous)
        {
            return located;
        }

        // Focus before selection, and before the IsReady shortcut. A page can be perfectly ready
        // by every other measure while the chord would still be delivered to a chart's iframe,
        // which is exactly the case that made this necessary - so returning early on IsReady
        // without checking focus is the bug, not an optimisation.
        if (located.FocusTrapped)
        {
            located = await ReturnFocusToPageAsync(page, index, located, cancellationToken).ConfigureAwait(false);

            // RefusedIfFocusTrapped, not a bare return: the re-probe can legitimately come back
            // Ready with focus still in the frame, and that combination is the bug itself - the
            // command path would see IsReady and dispatch into the chart.
            if (located.Status is Level2Status.NotFound or Level2Status.Ambiguous || located.FocusTrapped)
            {
                return located.RefusedIfFocusTrapped();
            }
        }

        if (located.IsReady)
        {
            return located.RefusedIfFocusTrapped();
        }

        // Level 2 exists but has no tab bar. This is the expected shape when the panel has been
        // popped out into its own window: there is nothing to select, and clicking a tab that
        // does not exist would be the actual bug.
        if (!located.HasTabBar)
        {
            _logger.Level2NoTabBar();

            return (located with
            {
                Status = Level2Status.Ready,
                IsSelected = true,
                Reason = "Level 2 has no FlexLayout tab bar (popped out); selection is not applicable.",
            }).RefusedIfFocusTrapped();
        }

        // Text-filtered before indexing, matching the probe exactly. Without the filter, Nth
        // would index into every element the selector matched rather than into Level 2 panels.
        ILocator target = page.Locator(located.MatchedSelector!)
            .Filter(new LocatorFilterOptions { HasText = _options.Level2TabText })
            .Nth(index);

        try
        {
            // Aims at the tab header's own label element where one exists - never an order
            // control. Whether it exists came back with the probe, so this costs no round trip.
            ILocator safeTarget = located.HasContentChild
                ? target.Locator($".{_options.TabButtonContentClass}")
                : target;

            // Our own hit test already confirmed the tab is the topmost element at the click
            // point, so Playwright's checks can be skipped - measured at ~190ms of a ~200ms
            // click on a live dashboard, almost all of it waiting for the page to stop
            // repainting. When the hit test says something IS covering the tab, fall back to the
            // fully checked click rather than punching through it.
            bool ownHitTestPassed = located.ClickTargetOnTop == true;

            await safeTarget.ClickAsync(new LocatorClickOptions
            {
                Timeout = _options.SelectionTimeoutMs,
                Force = ownHitTestPassed,
            }).ConfigureAwait(false);

            _logger.Level2Selecting(located.MatchedSelector!, ownHitTestPassed ? "fast" : "checked");
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            return located with
            {
                Status = Level2Status.Found,
                Reason = DescribeSelectionFailure(located, ex),
            };
        }

        // Re-read rather than assume the click worked. The whole point of this step is that the
        // SIM only honours shortcuts when Level 2 is genuinely the selected component.
        Level2Result verified = await LocateAsync(page, index, cancellationToken).ConfigureAwait(false);

        if (!verified.IsReady)
        {
            return verified with
            {
                Reason = verified.Reason ?? "Level 2 did not become selected after clicking its tab.",
            };
        }

        // The click itself can re-trap focus - selecting the tab hands the keyboard to something
        // inside the panel - so releasing it once before the click is not enough. Observed live:
        // the release fired, the tab was clicked, and focus was held again immediately afterwards.
        // It only reached the SIM because preparation retries once, which meant every command in
        // that state spent its entire retry budget on a condition guaranteed to recur. Releasing
        // again here keeps the retry for genuine transients.
        if (verified.FocusTrapped)
        {
            verified = await ReturnFocusToPageAsync(page, index, verified, cancellationToken).ConfigureAwait(false);
        }

        return verified.RefusedIfFocusTrapped();
    }

    /// <summary>
    /// Blurs the focused frame and re-probes, so the caller sees measured state rather than hope.
    /// </summary>
    /// <remarks>
    /// Failure is reported as <see cref="Level2Status.Found"/> with a reason, which is how a
    /// blocked tab click reports too: not ready, so the command path refuses and sends nothing.
    /// Dispatching anyway would put a trading chord into whatever holds focus, and the whole
    /// reason this method exists is that the thing holding focus was a chart.
    /// </remarks>
    private async Task<Level2Result> ReturnFocusToPageAsync(
        IPage page,
        int index,
        Level2Result located,
        CancellationToken cancellationToken)
    {
        try
        {
            string holder = await page
                .EvaluateAsync<string>(ReturnFocusScript)
                .WaitAsync(_options.ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);

            _logger.Level2FocusReturned(holder);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            return located with
            {
                Status = Level2Status.Found,
                Reason = $"Something on the page has the keyboard and it could not be released, "
                    + $"so the shortcut would have been typed into it rather than acted on: {ex.Message}",
            };
        }

        // Re-probed rather than trusting the blur, exactly as the tab click is. If focus is still
        // held the result carries it, and every return path out of EnsureSelectedAsync
        // runs it through RefusedIfFocusTrapped.
        return await LocateAsync(page, index, cancellationToken).ConfigureAwait(false);
    }
}
