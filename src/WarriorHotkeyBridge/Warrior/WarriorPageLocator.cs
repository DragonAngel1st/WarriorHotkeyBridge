using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using WarriorHotkeyBridge.Chrome;
using WarriorHotkeyBridge.Configuration;
using WarriorHotkeyBridge.Diagnostics;

namespace WarriorHotkeyBridge.Warrior;

/// <inheritdoc cref="IWarriorPageLocator"/>
internal sealed class WarriorPageLocator : IWarriorPageLocator
{
    private readonly IChromeConnectionManager _chrome;
    private readonly ILevel2Controller _level2;
    private readonly WarriorSimOptions _options;
    private readonly ILogger<WarriorPageLocator> _logger;

    /// <summary>Last candidate set logged, so an unchanged health check stays quiet.</summary>
    private string? _lastCandidateSignature;

    /// <summary>
    /// The page chosen by the last unambiguous full scan, held as one immutable unit.
    /// </summary>
    /// <remarks>
    /// A Playwright <see cref="IPage"/> survives navigation within its tab, so caching it is
    /// safe in a way that caching a DOM element handle would not be - React recreates those
    /// constantly, which is why locators are always re-resolved.
    /// </remarks>
    /// <remarks>
    /// A single volatile reference rather than several fields, because two hosted services -
    /// the watchdog and the command executor - share this locator and can scan concurrently.
    /// With separate fields a reader could observe a new page count beside an old page, which
    /// is precisely the combination the staleness guards exist to prevent.
    /// </remarks>
    private sealed record PageCache(IPage Page, WarriorPageCandidate Candidate, int PageCount);

    private volatile PageCache? _cache;

    private void Invalidate() => _cache = null;

    /// <summary>
    /// Revalidates the remembered page in a single round trip, or returns null to force a scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the readiness check the command path actually runs. The host comes from
    /// <see cref="IPage.Url"/>, which Playwright tracks from navigation events and costs
    /// nothing, and the one probe returns title, visibility and Level 2 state together. A full
    /// scan by contrast inspects every open page.
    /// </para>
    /// <para>
    /// Two guards keep it honest. The page count catches tabs appearing or disappearing, and a
    /// scan of every page's host catches an existing tab navigating onto the SIM - which the
    /// count alone cannot see. Either forces a full scan, so ambiguity is always recomputed
    /// before a command can be dispatched.
    /// </para>
    /// </remarks>
    private async Task<WarriorPageResult?> TryFastPathAsync(
        IBrowser browser,
        int pageCount,
        CancellationToken cancellationToken)
    {
        // Read once: the field can be replaced by the other service at any moment.
        PageCache? cache = _cache;

        if (cache is null || cache.Page.IsClosed || pageCount != cache.PageCount)
        {
            return null;
        }

        IPage cached = cache.Page;

        // Free: Playwright keeps the URL up to date from frame navigation events.
        if (!WarriorTargetValidator.IsAllowedHost(cached.Url, _options.AllowedHost))
        {
            Invalidate();
            return null;
        }

        // Rival check, and the reason the page-count guard alone was not enough. Counting tabs
        // only notices a tab appearing or disappearing; it says nothing about an existing tab
        // NAVIGATING onto the SIM host, or a second SIM tab that was still loading during the
        // last scan and only became eligible afterwards. Either produces a second valid target
        // while the count is unchanged, and the executor's fail-closed refusal would never fire
        // because the fast path never recomputes ambiguity.
        //
        // Any rival must at minimum share the allowed host, and page.Url costs nothing, so this
        // catches every case for free and hands off to the full scan to decide properly.
        foreach (IBrowserContext context in browser.Contexts)
        {
            foreach (IPage page in context.Pages)
            {
                if (!ReferenceEquals(page, cached)
                    && !page.IsClosed
                    && WarriorTargetValidator.IsAllowedHost(page.Url, _options.AllowedHost))
                {
                    return null;
                }
            }
        }

        Level2Result probe = await _level2.LocateAsync(cached, index: 0, cancellationToken).ConfigureAwait(false);

        bool stillValid = WarriorTargetValidator.TitleMatches(probe.PageTitle, _options.ExpectedTitle)
            && probe.Status is Level2Status.Found or Level2Status.Ready;

        if (!stillValid)
        {
            Invalidate();

            // A probe that could not run says nothing about the page; surfacing it lets the
            // watchdog tell a dead connection from a page that is genuinely not the SIM.
            return probe.ProbeFailed
                ? new WarriorPageResult
                {
                    Status = WarriorPageStatus.PageNotFound,
                    ProbeFailed = true,
                    Reason = probe.Reason,
                }
                : null;
        }

        var candidate = cache.Candidate with
        {
            Title = probe.PageTitle ?? cache.Candidate.Title,
            IsVisible = probe.PageVisible,
        };

        _cache = cache with { Candidate = candidate };

        return new WarriorPageResult
        {
            Status = WarriorPageStatus.PageFound,
            Page = cached,
            Candidates = [candidate],
        };
    }

    public WarriorPageLocator(
        IChromeConnectionManager chrome,
        ILevel2Controller level2,
        IOptions<WarriorSimOptions> options,
        ILogger<WarriorPageLocator> logger)
    {
        _chrome = chrome;
        _level2 = level2;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<WarriorPageResult> LocateAsync(CancellationToken cancellationToken)
    {
        IBrowser? browser = _chrome.Browser;

        if (browser is null || !browser.IsConnected)
        {
            Invalidate();

            return new WarriorPageResult
            {
                Status = WarriorPageStatus.NotConnected,
                Reason = "Chrome is not connected.",
            };
        }

        int pageCount = browser.Contexts.Sum(c => c.Pages.Count);

        WarriorPageResult? fast = await TryFastPathAsync(browser, pageCount, cancellationToken).ConfigureAwait(false);

        if (fast is not null)
        {
            return fast;
        }

        List<WarriorPageCandidate> candidates = [];
        List<(IPage Page, WarriorPageCandidate Candidate)> eligible = [];
        bool anyProbeFailed = false;

        foreach (IBrowserContext context in browser.Contexts)
        {
            foreach (IPage page in context.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (page.IsClosed)
                {
                    continue;
                }

                (WarriorPageCandidate? candidate, bool probeFailed) =
                    await InspectAsync(page, cancellationToken).ConfigureAwait(false);

                anyProbeFailed |= probeFailed;

                if (candidate is null)
                {
                    continue;
                }

                candidates.Add(candidate);

                if (candidate.IsEligible)
                {
                    eligible.Add((page, candidate));
                }
            }
        }

        LogCandidates(candidates);

        if (eligible.Count == 0)
        {
            Invalidate();

            return new WarriorPageResult
            {
                Status = WarriorPageStatus.PageNotFound,
                Candidates = candidates,
                ProbeFailed = anyProbeFailed,
                Reason = candidates.Any(c => c.HostMatches)
                    ? $"A page on {_options.AllowedHost} is open but its title does not contain "
                        + $"\"{_options.ExpectedTitle}\"."
                    : $"No open page has host {_options.AllowedHost}.",
            };
        }

        // Deterministic selection. The visible tab wins because that is the one the operator is
        // actually looking at and therefore the one they mean. Enumeration order breaks
        // remaining ties so the same layout always resolves to the same page rather than
        // flipping between two identical SIM tabs from one keypress to the next.
        (IPage selectedPage, WarriorPageCandidate winner) = eligible
            .OrderByDescending(e => e.Candidate.IsVisible)
            .First();

        bool ambiguous = eligible.Count > 1;

        if (ambiguous)
        {
            _logger.WarriorPageAmbiguous(eligible.Count, winner.Describe());
        }

        // Only remember an unambiguous winner. Caching one of several equally valid pages would
        // make an arbitrary choice sticky, and the fast path would then keep confirming it
        // without ever noticing the other one. Published as one write so no reader can see a
        // fresh page count beside a stale page.
        _cache = ambiguous ? null : new PageCache(selectedPage, winner, pageCount);

        return new WarriorPageResult
        {
            Status = WarriorPageStatus.PageFound,
            Page = selectedPage,
            Candidates = candidates,
            WasAmbiguous = ambiguous,
            Reason = ambiguous
                ? $"{eligible.Count} pages passed validation; selected the {(winner.IsVisible ? "visible" : "first")} one."
                : null,
        };
    }

    /// <summary>
    /// Reads the identity of one page, or null if it cannot be classified.
    /// </summary>
    private async Task<(WarriorPageCandidate? Candidate, bool ProbeFailed)> InspectAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Uri.TryCreate(page.Url, UriKind.Absolute, out Uri? uri))
            {
                return (null, false);
            }

            // about:blank, devtools:// and chrome-extension:// pages are not targets.
            if (uri.Scheme is not ("http" or "https"))
            {
                return (null, false);
            }

            bool hostMatches = WarriorTargetValidator.IsAllowedHost(page.Url, _options.AllowedHost);

            // Only interrogate pages that already passed the host gate: calling into an
            // arbitrary page costs a round trip and tells us nothing we are allowed to act on.
            if (!hostMatches)
            {
                return (new WarriorPageCandidate(uri.Host, uri.AbsolutePath, Title: string.Empty,
                    HostMatches: false, TitleMatches: false, HasLevel2: false, IsVisible: false), false);
            }

            // One probe answers title, visibility and Level 2 together. Asking separately cost
            // three round trips per candidate page, and a normal session has two SIM pages.
            Level2Result probe = await _level2
                .LocateAsync(page, index: 0, cancellationToken)
                .ConfigureAwait(false);

            string title = probe.PageTitle ?? string.Empty;
            bool titleMatches = WarriorTargetValidator.TitleMatches(title, _options.ExpectedTitle);
            bool hasLevel2 = titleMatches && probe.Status is Level2Status.Found or Level2Status.Ready;

            return (new WarriorPageCandidate(
                uri.Host, uri.AbsolutePath, title, hostMatches, titleMatches, hasLevel2, probe.PageVisible), probe.ProbeFailed);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // The page navigated or closed while we were inspecting it. Skipping it is correct:
            // a page we cannot positively identify must never be selected. Reported as a probe
            // failure so a connection that has silently died is distinguishable from a page
            // that simply is not the SIM.
            _logger.WarriorPageInspectFailed(ex.Message);
            return (null, true);
        }
    }

    /// <summary>
    /// Dumps the candidate list only when it differs from the last one.
    /// </summary>
    /// <remarks>
    /// The health check runs every few seconds and almost always sees exactly the same pages.
    /// Logging the full list each time produced roughly two hundred lines a minute and buried
    /// the one line an operator actually looks for - the hotkey they just pressed. Gating on
    /// change keeps the detail available for the moment it becomes interesting.
    /// </remarks>
    private void LogCandidates(List<WarriorPageCandidate> candidates)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        string signature = string.Join('|', candidates.Select(c => c.Describe()));

        if (signature == _lastCandidateSignature)
        {
            return;
        }

        _lastCandidateSignature = signature;

        _logger.WarriorPageCandidateCount(candidates.Count);

        foreach (WarriorPageCandidate candidate in candidates)
        {
            _logger.WarriorPageCandidate(candidate.Describe());
        }
    }
}
